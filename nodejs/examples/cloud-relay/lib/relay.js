// The relay of docs/cloud-relay.md: devices POST operations to it, and one gateway fetches them. It keeps its queue in
// memory, speaks plain HTTP, and is meant to sit behind an HTTPS reverse proxy.

import { createHmac, randomBytes, timingSafeEqual } from 'node:crypto';
import { createServer } from 'node:http';
import { isIP } from 'node:net';
import { MAX_SEQUENCE, OpError, isEpoch, isToken, parseJson, readOperation } from './operations.js';

const USAGE = `relay: holds operations that devices send over HTTPS until an Erdigo cloud gateway fetches them.

Usage
  relay [--listen HOST:PORT] [--keep-hours HOURS] [--behind-proxy]

  --listen HOST:PORT  Where to accept plain HTTP. 127.0.0.1:8080 by default. Port 0 picks a free port.
                      A host other than 127.0.0.1, ::1 or localhost needs --behind-proxy. Write IPv6 as [::1]:8080.
  --keep-hours HOURS  How long an operation waits for the gateway before it is dropped: 1 to 168, 24 by default.
  --behind-proxy      HTTPS ends in a reverse proxy in front of this relay, so it may listen beyond this computer.

Environment
  ERDIGO_RELAY_SOURCE_TOKENS  One or more source tokens, separated by commas, for the devices that send.
  ERDIGO_RELAY_GATEWAY_TOKEN  The gateway token. It must differ from every source token.
  A token is 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.

See docs/cloud-relay.md for the HTTP API.`;

/** The limits of the example relays (docs/cloud-relay.md). Tests pass smaller ones; the command line does not. */
export const DEFAULT_LIMITS = Object.freeze({
  bodyBytes: 2 * 1024 * 1024,
  opsPerPost: 100,
  queuedOps: 10_000,
  queuedBytes: 64 * 1024 * 1024,
  answerOps: 100,
  answerBytes: 4 * 1024 * 1024,
  waitSeconds: 30,
});

const LOOPBACK_HOSTS = new Set(['127.0.0.1', '::1', 'localhost']);

/** A mistake in the command line or the environment. Its message never holds a token. */
export class ConfigError extends Error {
  /** @param {string} message */
  constructor(message) {
    super(message);
    this.name = 'ConfigError';
  }
}

/**
 * Reads and checks the tokens from the environment.
 * @param {Record<string, string | undefined>} env
 * @returns {{ sourceTokens: string[], gatewayToken: string }}
 */
export function tokensFrom(env) {
  const sources = env.ERDIGO_RELAY_SOURCE_TOKENS;
  const gatewayToken = env.ERDIGO_RELAY_GATEWAY_TOKEN?.trim();
  if (!sources || sources.trim().length === 0) {
    throw new ConfigError('ERDIGO_RELAY_SOURCE_TOKENS must hold at least one source token');
  }
  const sourceTokens = sources.split(',').map((piece) => piece.trim());
  if (!sourceTokens.every(isToken)) {
    throw new ConfigError('every token in ERDIGO_RELAY_SOURCE_TOKENS must be 32 to 256 characters from A-Z, a-z, 0-9, -, _, . and ~, separated by commas');
  }
  if (!gatewayToken) {
    throw new ConfigError('ERDIGO_RELAY_GATEWAY_TOKEN must hold the gateway token');
  }
  if (!isToken(gatewayToken)) {
    throw new ConfigError('ERDIGO_RELAY_GATEWAY_TOKEN must be 32 to 256 characters from A-Z, a-z, 0-9, -, _, . and ~');
  }
  if (sourceTokens.includes(gatewayToken)) {
    throw new ConfigError('the gateway token must differ from every source token');
  }
  return { sourceTokens, gatewayToken };
}

/**
 * Compares tokens in constant time. Each token is reduced to a keyed hash first, so neither its content nor its
 * length shows in how long a comparison takes.
 */
class TokenSet {
  /** @param {string[]} tokens */
  constructor(tokens) {
    this.key = randomBytes(32);
    this.digests = tokens.map((token) => this.digest(token));
  }

  /** @param {string} token */
  digest(token) {
    return createHmac('sha256', this.key).update(token, 'utf8').digest();
  }

  /** @param {string} token */
  has(token) {
    const presented = this.digest(token);
    let found = false;
    for (const digest of this.digests) {
      // No early exit, so the position of a match does not show either.
      found = timingSafeEqual(presented, digest) || found;
    }
    return found;
  }
}

/** The answer is a 400 with this body. */
class BadRequest extends Error {
  /** @param {Record<string, unknown>} body */
  constructor(body) {
    super(String(body.error));
    this.body = body;
  }
}

/**
 * The queue and its numbering, apart from HTTP. JavaScript runs one piece of code at a time, and every method here is
 * synchronous, so it needs no lock.
 */
export class RelayQueue {
  /**
   * @param {object} options
   * @param {number} options.keepMs how long an operation waits before it is dropped
   * @param {typeof DEFAULT_LIMITS} options.limits
   * @param {() => number} options.now milliseconds since 1970
   */
  constructor({ keepMs, limits, now }) {
    this.keepMs = keepMs;
    this.limits = limits;
    this.now = now;
    // 16 random bytes in base64url: 22 characters from the epoch alphabet.
    this.epoch = randomBytes(16).toString('base64url');
    this.lastSeq = 0;
    /** @type {Array<{ seq: number, receivedMs: number, json: string, bytes: number }>} */
    this.items = [];
    this.head = 0;
    this.bytes = 0;
    /** The highest sequence number dropped because it waited too long. */
    this.expiredUpTo = 0;
  }

  get count() {
    return this.items.length - this.head;
  }

  /** Drops operations that have waited longer than the relay keeps them. */
  expire() {
    const oldest = this.now() - this.keepMs;
    while (this.head < this.items.length && this.items[this.head].receivedMs <= oldest) {
      this.expiredUpTo = Math.max(this.expiredUpTo, this.items[this.head].seq);
      this.drop();
    }
  }

  /** Forgets every operation up to `after`, which the gateway is done with. @param {number} after */
  forget(after) {
    while (this.head < this.items.length && this.items[this.head].seq <= after) {
      this.drop();
    }
  }

  drop() {
    this.bytes -= this.items[this.head].bytes;
    this.items[this.head] = /** @type {any} */ (undefined);
    this.head += 1;
    if (this.head > 1024 && this.head * 2 > this.items.length) {
      this.items = this.items.slice(this.head);
      this.head = 0;
    }
  }

  /**
   * Takes a whole batch, or nothing.
   * @param {string[]} operations each as JSON text
   * @returns {{ accepted: number, last_seq: number } | null} null when the queue has no room
   */
  add(operations) {
    this.expire();
    const sizes = operations.map((json) => Buffer.byteLength(json, 'utf8'));
    const total = sizes.reduce((sum, size) => sum + size, 0);
    if (this.count + operations.length > this.limits.queuedOps || this.bytes + total > this.limits.queuedBytes) {
      return null;
    }
    const receivedMs = this.now();
    operations.forEach((json, index) => {
      this.lastSeq += 1;
      this.items.push({ seq: this.lastSeq, receivedMs, json, bytes: sizes[index] });
    });
    this.bytes += total;
    return { accepted: operations.length, last_seq: this.lastSeq };
  }

  /**
   * The answer to a fetch, as JSON text, and whether it holds any operations.
   * @param {{ epoch: string | null, after: number, limit: number }} query
   * @returns {{ json: string, empty: boolean }}
   */
  fetch({ epoch, after, limit }) {
    this.expire();
    let from = 0;
    const reset = epoch !== this.epoch;
    if (!reset) {
      if (after > this.lastSeq) {
        throw new BadRequest({ error: 'invalid_query' });
      }
      this.forget(after);
      from = after;
    }
    const missed = this.expiredUpTo > from;
    const start = `{"epoch":"${this.epoch}","reset":${reset},"missed":${missed},"ops":[`;
    const end = ']}';
    let size = start.length + end.length;
    /** @type {string[]} */
    const entries = [];
    for (let index = this.head; index < this.items.length && entries.length < limit; index += 1) {
      const item = this.items[index];
      if (item.seq <= from) {
        continue;
      }
      const entry = `{"seq":${item.seq},"received_ms":${item.receivedMs},"op":${item.json}}`;
      const added = item.bytes + entry.length - item.json.length + (entries.length > 0 ? 1 : 0);
      if (entries.length > 0 && size + added > this.limits.answerBytes) {
        break;
      }
      entries.push(entry);
      size += added;
    }
    return { json: `${start}${entries.join(',')}${end}`, empty: entries.length === 0 };
  }
}

/**
 * Reads the query of a fetch. Each parameter at most once, none but these four, each in its range.
 * @param {string} text the part of the URL after '?'
 * @param {typeof DEFAULT_LIMITS} limits
 */
function readFetchQuery(text, limits) {
  /** @type {{ epoch: string | null, after: number, limit: number, wait: number }} */
  const query = { epoch: null, after: 0, limit: limits.answerOps, wait: 0 };
  const seen = new Set();
  const whole = (/** @type {string} */ value, /** @type {number} */ low, /** @type {number} */ high) => {
    if (!/^[0-9]{1,20}$/.test(value) || Number(value) < low || Number(value) > high) {
      throw new BadRequest({ error: 'invalid_query' });
    }
    return Number(value);
  };
  for (const [name, value] of new URLSearchParams(text)) {
    if (seen.has(name)) {
      throw new BadRequest({ error: 'invalid_query' });
    }
    seen.add(name);
    switch (name) {
      case 'epoch':
        if (!isEpoch(value)) {
          throw new BadRequest({ error: 'invalid_query' });
        }
        query.epoch = value;
        break;
      case 'after':
        query.after = whole(value, 0, MAX_SEQUENCE);
        break;
      case 'limit':
        query.limit = whole(value, 1, limits.answerOps);
        break;
      case 'wait':
        query.wait = whole(value, 0, limits.waitSeconds);
        break;
      default:
        throw new BadRequest({ error: 'invalid_query' });
    }
  }
  return query;
}

/**
 * Reads a POST body: one JSON object holding 1 to 100 valid operations and nothing else.
 * @param {Buffer} body
 * @param {typeof DEFAULT_LIMITS} limits
 * @returns {string[]} the operations as JSON text
 */
function readBatch(body, limits) {
  const root = parseJson(body);
  if (root === null || root.type !== 'obj') {
    throw new BadRequest({ error: 'invalid_json' });
  }
  const ops = root.value.get('ops');
  if (root.value.size !== 1 || ops?.type !== 'arr') {
    throw new BadRequest({ error: 'invalid_body', reason: 'the body is {"ops":[...]} and nothing else' });
  }
  if (ops.value.length < 1 || ops.value.length > limits.opsPerPost) {
    throw new BadRequest({ error: 'invalid_body', reason: `ops holds 1 to ${limits.opsPerPost} operations` });
  }
  return ops.value.map((value, index) => {
    try {
      return JSON.stringify(readOperation(value).op);
    } catch (error) {
      if (error instanceof OpError) {
        throw new BadRequest({ error: 'invalid_op', index, reason: error.reason });
      }
      throw error;
    }
  });
}

/**
 * True for `application/json`, alone or with `charset=utf-8`.
 * @param {string | undefined} header
 */
function isJsonContentType(header) {
  if (header === undefined) {
    return false;
  }
  const [type, ...parameters] = header.split(';').map((part) => part.trim().toLowerCase());
  if (type !== 'application/json') {
    return false;
  }
  return parameters.every((parameter) => parameter === 'charset=utf-8' || parameter === 'charset="utf-8"');
}

/**
 * The token of an `Authorization: Bearer TOKEN` header, or null.
 * @param {string | undefined} header
 */
function bearerToken(header) {
  const match = /^Bearer +([^ ]+) *$/i.exec(header ?? '');
  return match ? match[1] : null;
}

const KNOWN_PATHS = new Set(['/v1/ops', '/v1/health']);

/**
 * @typedef {object} RelayOptions
 * @property {string} host
 * @property {number} port
 * @property {string[]} sourceTokens
 * @property {string} gatewayToken
 * @property {number} [keepHours]
 * @property {typeof DEFAULT_LIMITS} [limits]
 * @property {() => number} [now] milliseconds since 1970, for tests
 * @property {(line: string) => void} [log]
 */

/**
 * Starts a relay and resolves once it listens.
 * @param {RelayOptions} options
 */
export async function startRelay({
  host,
  port,
  sourceTokens,
  gatewayToken,
  keepHours = 24,
  limits = DEFAULT_LIMITS,
  now = Date.now,
  log = () => {},
}) {
  const sources = new TokenSet(sourceTokens);
  const gateways = new TokenSet([gatewayToken]);
  const queue = new RelayQueue({ keepMs: keepHours * 3_600_000, limits, now });
  /** Fetches waiting for an operation to arrive. */
  const waiting = new Set();
  let stopping = false;

  const server = createServer({
    maxHeaderSize: 8192,
    headersTimeout: 10_000,
    requestTimeout: 60_000,
    keepAliveTimeout: 5_000,
    connectionsCheckingInterval: 2_000,
  });

  /**
   * @param {import('node:http').ServerResponse} response
   * @param {number} status
   * @param {string} json
   * @param {Record<string, string>} [headers]
   */
  const send = (response, status, json, headers = {}) => {
    if (response.headersSent || response.destroyed) {
      return;
    }
    response.writeHead(status, {
      'Content-Type': 'application/json',
      'Content-Length': Buffer.byteLength(json, 'utf8'),
      'Cache-Control': 'no-store',
      ...headers,
    });
    response.end(json);
  };

  /**
   * @param {import('node:http').IncomingMessage} request
   * @param {import('node:http').ServerResponse} response
   * @param {boolean} expectsContinue
   */
  const handle = (request, response, expectsContinue) => {
    const started = process.hrtime.bigint();
    const target = request.url ?? '';
    const mark = target.indexOf('?');
    const path = mark >= 0 ? target.slice(0, mark) : target;
    const queryText = mark >= 0 ? target.slice(mark + 1) : '';
    // Only the method, a known path and the status are logged: never a token, a query or what an operation holds.
    response.once('close', () => {
      const ms = Number((process.hrtime.bigint() - started) / 1_000_000n);
      const status = response.writableFinished ? String(response.statusCode) : 'aborted';
      log(`${new Date(now()).toISOString()} ${request.method} ${KNOWN_PATHS.has(path) ? path : '(other)'} ${status} ${ms}ms`);
    });
    // A body this relay does not read is not drained either: the connection is closed after the answer.
    const hasBody = (request.headers['content-length'] ?? '0') !== '0' || request.headers['transfer-encoding'] !== undefined;
    if (hasBody) {
      response.setHeader('Connection', 'close');
    }

    if (!KNOWN_PATHS.has(path)) {
      send(response, 404, '{"error":"not_found"}');
      return;
    }
    if (path === '/v1/health') {
      if (request.method !== 'GET') {
        send(response, 405, '{"error":"method_not_allowed"}', { Allow: 'GET' });
        return;
      }
      send(response, 200, '{"ok":true}');
      return;
    }
    if (request.method !== 'GET' && request.method !== 'POST') {
      send(response, 405, '{"error":"method_not_allowed"}', { Allow: 'GET, POST' });
      return;
    }
    const token = bearerToken(request.headers.authorization);
    const allowed = request.method === 'POST' ? sources : gateways;
    if (token === null || !allowed.has(token)) {
      send(response, 401, '{"error":"unauthorized"}', { 'WWW-Authenticate': 'Bearer' });
      return;
    }
    if (request.method === 'GET') {
      fetchOps(queryText, response);
      return;
    }
    if (!isJsonContentType(request.headers['content-type'])) {
      send(response, 415, '{"error":"unsupported_media_type"}');
      return;
    }
    const announced = request.headers['content-length'];
    if (announced !== undefined && Number(announced) > limits.bodyBytes) {
      send(response, 413, '{"error":"too_large"}');
      return;
    }
    response.removeHeader('Connection');
    if (expectsContinue) {
      response.writeContinue();
    }
    /** @type {Buffer[]} */
    const chunks = [];
    let received = 0;
    let tooLarge = false;
    request.on('data', (/** @type {Buffer} */ chunk) => {
      if (tooLarge) {
        return;
      }
      received += chunk.length;
      if (received > limits.bodyBytes) {
        // A body without Content-Length is read up to the limit, and no further.
        tooLarge = true;
        chunks.length = 0;
        send(response, 413, '{"error":"too_large"}', { Connection: 'close' });
        return;
      }
      chunks.push(chunk);
    });
    request.on('end', () => {
      if (tooLarge) {
        return;
      }
      try {
        const taken = queue.add(readBatch(Buffer.concat(chunks, received), limits));
        if (taken === null) {
          send(response, 503, '{"error":"queue_full"}', { 'Retry-After': '30' });
          return;
        }
        send(response, 200, JSON.stringify(taken));
        for (const wake of [...waiting]) {
          wake();
        }
      } catch (error) {
        if (error instanceof BadRequest) {
          send(response, 400, JSON.stringify(error.body));
        } else {
          send(response, 500, '{"error":"internal_error"}', { Connection: 'close' });
        }
      }
    });
  };

  /**
   * @param {string} queryText
   * @param {import('node:http').ServerResponse} response
   */
  const fetchOps = (queryText, response) => {
    let query;
    let answer;
    try {
      query = readFetchQuery(queryText, limits);
      answer = queue.fetch(query);
    } catch (error) {
      if (error instanceof BadRequest) {
        send(response, 400, JSON.stringify(error.body));
        return;
      }
      throw error;
    }
    if (!answer.empty || query.wait === 0 || stopping) {
      send(response, 200, answer.json);
      return;
    }
    // Nothing is waiting: hold the request until something arrives, the wait ends, or the relay stops.
    const finish = () => {
      clearTimeout(timer);
      waiting.delete(finish);
      response.off('close', abandon);
      try {
        send(response, 200, queue.fetch(/** @type {typeof query} */ (query)).json);
      } catch {
        send(response, 500, '{"error":"internal_error"}', { Connection: 'close' });
      }
    };
    const abandon = () => {
      clearTimeout(timer);
      waiting.delete(finish);
    };
    const timer = setTimeout(finish, query.wait * 1000);
    waiting.add(finish);
    response.once('close', abandon);
  };

  /**
   * @param {import('node:http').IncomingMessage} request
   * @param {import('node:http').ServerResponse} response
   * @param {boolean} expectsContinue
   */
  const guarded = (request, response, expectsContinue) => {
    try {
      handle(request, response, expectsContinue);
    } catch {
      send(response, 500, '{"error":"internal_error"}', { Connection: 'close' });
    }
  };
  server.on('request', (request, response) => guarded(request, response, false));
  // Answered here, so that a body that would be refused is never asked for.
  server.on('checkContinue', (request, response) => guarded(request, response, true));
  // Node.js would answer any other expectation with a bare 417 of its own. HTTP allows serving the request as usual,
  // which keeps the order of the checks, a JSON answer and the log line.
  server.on('checkExpectation', (request, response) => guarded(request, response, false));
  server.on('clientError', (error, socket) => {
    const code = /** @type {NodeJS.ErrnoException} */ (error).code;
    if (socket.writable && !socket.destroyed) {
      const [status, text] = code === 'HPE_HEADER_OVERFLOW' ? [431, 'Request Header Fields Too Large']
        : code === 'ERR_HTTP_REQUEST_TIMEOUT' ? [408, 'Request Timeout'] : [400, 'Bad Request'];
      const body = '{"error":"bad_request"}';
      socket.end(`HTTP/1.1 ${status} ${text}\r\nContent-Type: application/json\r\nContent-Length: ${body.length}\r\nConnection: close\r\n\r\n${body}`);
      log(`${new Date(now()).toISOString()} - (malformed request) ${status}`);
    } else {
      socket.destroy();
    }
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, host, () => {
      server.off('error', reject);
      resolve(undefined);
    });
  });
  const address = /** @type {import('node:net').AddressInfo} */ (server.address());
  const url = `http://${address.family === 'IPv6' || isIP(address.address) === 6 ? `[${address.address}]` : address.address}:${address.port}`;

  return {
    url,
    port: address.port,
    queue,
    /** Stops taking connections, answers the fetches that are waiting, and closes every connection. */
    async close() {
      stopping = true;
      for (const wake of [...waiting]) {
        wake();
      }
      const closed = new Promise((resolve) => server.close(() => resolve(undefined)));
      server.closeIdleConnections();
      const deadline = setTimeout(() => server.closeAllConnections(), 2000);
      await closed;
      clearTimeout(deadline);
    },
  };
}

/**
 * Reads `HOST:PORT`, with an IPv6 host in brackets.
 * @param {string} text
 */
function parseListen(text) {
  const match = /^(?:\[([0-9A-Fa-f:.]+)\]|([^:[\]]+)):([0-9]{1,5})$/.exec(text);
  if (!match || Number(match[3]) > 65535) {
    throw new ConfigError('--listen takes HOST:PORT, such as 127.0.0.1:8080 or [::1]:8080');
  }
  const host = match[1] ?? match[2];
  if (match[1] !== undefined && isIP(host) !== 6) {
    throw new ConfigError('--listen takes HOST:PORT, such as 127.0.0.1:8080 or [::1]:8080');
  }
  return { host, port: Number(match[3]) };
}

/** @param {string} host */
function isLoopbackHost(host) {
  return LOOPBACK_HOSTS.has(host.toLowerCase());
}

/**
 * Reads the command line.
 * @param {string[]} args
 */
export function parseArguments(args) {
  /** @type {Map<string, string>} */
  const values = new Map();
  let behindProxy = false;
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (argument === '--behind-proxy') {
      behindProxy = true;
      continue;
    }
    if (argument !== '--listen' && argument !== '--keep-hours') {
      throw new ConfigError("an unknown argument was given. See 'relay --help'.");
    }
    if (index + 1 >= args.length) {
      throw new ConfigError(`${argument} needs a value`);
    }
    if (values.has(argument)) {
      throw new ConfigError(`${argument} was given twice`);
    }
    values.set(argument, args[index + 1]);
    index += 1;
  }
  const { host, port } = parseListen(values.get('--listen') ?? '127.0.0.1:8080');
  if (!isLoopbackHost(host) && !behindProxy) {
    throw new ConfigError('--listen beyond this computer needs --behind-proxy, with HTTPS ending in a reverse proxy in front of the relay');
  }
  const hoursText = values.get('--keep-hours') ?? '24';
  if (!/^[0-9]{1,3}$/.test(hoursText) || Number(hoursText) < 1 || Number(hoursText) > 168) {
    throw new ConfigError('--keep-hours is a whole number of hours from 1 to 168');
  }
  return { host, port, keepHours: Number(hoursText) };
}

/**
 * Runs the relay until Ctrl+C or SIGTERM, and returns its exit status.
 * @param {string[]} args
 * @param {Record<string, string | undefined>} env
 */
export async function main(args, env) {
  if (args[0] === 'help' || args[0] === '--help' || args[0] === '-h') {
    console.log(USAGE);
    return 0;
  }
  let relay;
  try {
    const { host, port, keepHours } = parseArguments(args);
    const { sourceTokens, gatewayToken } = tokensFrom(env);
    relay = await startRelay({ host, port, keepHours, sourceTokens, gatewayToken, log: (line) => console.log(line) });
  } catch (error) {
    const code = /** @type {NodeJS.ErrnoException} */ (error).code;
    if (error instanceof ConfigError) {
      process.stderr.write(`relay: ${error.message}\n`);
    } else if (typeof code === 'string') {
      process.stderr.write(`relay: could not listen there (${code})\n`);
    } else {
      throw error;
    }
    return 1;
  }
  console.log(`Relay listening on ${relay.url}`);
  await new Promise((resolve) => {
    process.once('SIGINT', resolve);
    process.once('SIGTERM', resolve);
  });
  await relay.close();
  return 0;
}
