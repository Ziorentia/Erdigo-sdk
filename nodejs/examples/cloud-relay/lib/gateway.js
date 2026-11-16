// The gateway of docs/cloud-relay.md: it runs on the Erdigo app's local network, fetches operations from a relay with
// outgoing HTTPS requests, and writes them to the app through the SDK, as a paired device.

import { randomBytes, randomInt } from 'node:crypto';
import { linkSync, renameSync, unlinkSync } from 'node:fs';
import { request as httpRequest } from 'node:http';
import { request as httpsRequest } from 'node:https';
import { join } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';
import {
  ErdigoClient,
  ErdigoError,
  ErdigoErrorKind,
  FileDeviceStore,
  ITEM_KINDS,
  PairingTicket,
  ReceiverTarget,
  RetryPolicy,
  WireError,
  isFingerprint,
  pair,
  pairingFingerprint,
  shortFingerprint,
} from '../../../src/index.js';
import { readCodeFile } from '../../../src/cli/files.js';
import { Interrupted, UsageError, ask, personIsThere, readSecretLine } from '../../../src/cli/terminal.js';
import { trimWhiteSpace } from '../../../src/model.js';
import { PrivateFileError, PrivateFileProblem, readPrivateFile, writePrivateFileAtomically } from '../../../src/private-file.js';
import { MAX_SEQUENCE, OpError, isEpoch, isOpName, isToken, parseJson, readOperation } from './operations.js';

const USAGE = `gateway: fetches operations from an Erdigo cloud relay and writes them to the Erdigo app on this local network,
as a paired device.

Usage
  gateway pair --name NAME [--kinds LIST] [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
               [--profile NAME] [--state-dir DIR]
      Pairs once. The code or the QR text is read from the keyboard, or from a file only you can read.
      It is never accepted as a command-line argument. --kinds is pin,marker,note,photo,track by default.
      A bare code means you compare the receiver's key and type yes before the app is asked to pair.
  gateway run  --relay URL [--host HOST --port PORT] [--once] [--profile NAME] [--state-dir DIR]
      Fetches from the relay and writes to the app until it is stopped. With --once, it stops when the relay
      has nothing more. URL starts with https://; http:// is accepted only for 127.0.0.1, [::1] and localhost.

Environment
  ERDIGO_RELAY_TOKEN  The gateway token, for run.

--profile is "cloud-gateway" by default. Without --host and --port, the app is found on the local network.
run exits with 0 when stopped or, with --once, when nothing more is waiting; 1 for a mistake in the command
line or the environment, no pairing, or a profile another run is using; 2 when the relay refuses the token or
breaks the relay API; 3 when, with --once, the app or the relay could not be reached for about 30 seconds.
See docs/cloud-relay.md.`;

const DEFAULT_PROFILE = 'cloud-gateway';
const PLACE_FILE = 'relay-state.json';
/** Holds the process id of the `run` that is using the profile. */
const LOCK_FILE = '.gateway.lock';
const FETCH_WAIT_SECONDS = 25;
const FETCH_LIMIT = 100;
/** Comfortably above the 25 seconds a relay may hold a fetch. */
const FETCH_TIMEOUT_MS = 60_000;
/** With --once the relay is asked not to wait, so an answer that has not started by then is not coming. */
const ONCE_ANSWER_TIMEOUT_MS = 10_000;
/** The least time between two fetches that found nothing, for a relay that answers at once instead of waiting. */
const EMPTY_FETCH_INTERVAL_MS = 2_000;
/** A connection to the app unused for this long is closed, well before the app closes it after five minutes. */
const IDLE_CLOSE_MS = 60_000;
/**
 * How long an operation may go without a connection to the app, or in trouble with it, before the gateway says so, and
 * how often after that.
 */
const OUTAGE_FIRST_REPORT_MS = 2_000;
const OUTAGE_REPORT_EVERY_MS = 60_000;
const MAX_ANSWER_BYTES = 8 * 1024 * 1024;
/** How long `run --once` keeps trying an app or a relay it cannot reach. */
const ONCE_GIVE_UP_MS = 30_000;
const LOOPBACK_HOSTS = new Set(['127.0.0.1', '[::1]', 'localhost']);
const KNOWN_OPTIONS = new Set(['code-file', 'fingerprint', 'host', 'kinds', 'name', 'port', 'profile', 'relay', 'state-dir']);

/** The relay refused the token. Exit status 2. */
class RelayRefused extends Error {}
/** The relay answered something that does not follow the relay API. Exit status 2. */
class RelayBroken extends Error {}
/** The relay could not be reached, or answered with a server error. Tried again later. */
class RelayUnreachable extends Error {}
/** With --once, the app or the relay could not be reached. Its message is what to print. Exit status 3. */
class GaveUp extends Error {}

/**
 * How long to wait before the next try, 1, 2, 4 ... up to 60 seconds, or null when --once has tried for long enough.
 * Like the SDK, it uses all the time allowed, with a last try at the end of it.
 */
class Retries {
  /** @param {boolean} once */
  constructor(once) {
    this.once = once;
    this.attempt = 0;
    /** @type {number | null} */
    this.since = null;
  }

  /** @param {number} [startedAt] when the attempt that failed began, so that a slow attempt counts in full */
  next(startedAt = Date.now()) {
    const now = Date.now();
    this.since ??= startedAt;
    let wait = backoff(this.attempt);
    this.attempt += 1;
    if (this.once) {
      const remaining = (ONCE_GIVE_UP_MS - (now - this.since)) / 1000;
      if (remaining <= 0) {
        return null;
      }
      wait = Math.min(wait, remaining);
    }
    return wait;
  }

  /** With --once, how much of the time allowed is left, in milliseconds. */
  remainingMs() {
    return this.since === null ? ONCE_GIVE_UP_MS : ONCE_GIVE_UP_MS - (Date.now() - this.since);
  }

  reset() {
    this.attempt = 0;
    this.since = null;
  }
}

/** A deliberately small option parser, as erdigo-send has: `--name value` pairs and bare flags, nothing else. */
class Options {
  /**
   * @param {string[]} args
   * @param {string[]} allowedFlags
   */
  constructor(args, allowedFlags) {
    /** @type {Map<string, string>} */
    this.values = new Map();
    /** @type {Set<string>} */
    this.flags = new Set();
    /** @type {Set<string>} */
    this.used = new Set();
    for (let index = 0; index < args.length; index += 1) {
      const argument = args[index];
      // Errors here never repeat what was typed, because it might be a pairing code.
      if (!argument.startsWith('--')) {
        throw new UsageError('unexpected argument. Options start with --, and a pairing code is never given as an argument.');
      }
      const name = argument.slice(2);
      if (name === 'code' || name.startsWith('code=')) {
        throw new UsageError('the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. Type it when asked, or use --code-file.');
      }
      if (name === 'token' || name.startsWith('token=')) {
        throw new UsageError('a token is not accepted as an argument, because arguments appear in the process list. Set ERDIGO_RELAY_TOKEN.');
      }
      if (allowedFlags.includes(name)) {
        this.flags.add(name);
        continue;
      }
      if (!KNOWN_OPTIONS.has(name)) {
        throw new UsageError("an unknown option was given. See 'gateway help'.");
      }
      if (index + 1 >= args.length) {
        throw new UsageError(`--${name} needs a value`);
      }
      if (this.values.has(name)) {
        throw new UsageError(`--${name} was given twice`);
      }
      this.values.set(name, args[index + 1]);
      index += 1;
    }
  }

  /** @param {string} name */
  flag(name) {
    this.used.add(name);
    return this.flags.has(name);
  }

  /** @param {string} name @returns {string | null} */
  optional(name) {
    this.used.add(name);
    return this.values.get(name) ?? null;
  }

  /** @param {string} name */
  required(name) {
    const value = this.optional(name);
    if (value === null) {
      throw new UsageError(`--${name} is required`);
    }
    return value;
  }

  finish() {
    const unknown = [...this.values.keys(), ...this.flags].filter((name) => !this.used.has(name)).sort()[0];
    if (unknown !== undefined) {
      throw new UsageError(`--${unknown} does not apply to this command`);
    }
  }
}

/**
 * The folder that holds the gateway's key, its pairing and its place in the relay's queue.
 * @param {Options} options
 */
function stateDirectory(options) {
  const profile = options.optional('profile');
  const directory = options.optional('state-dir');
  if (profile !== null && directory !== null) {
    throw new UsageError('--state-dir and --profile cannot be used together, because a state folder holds one identity');
  }
  if (profile !== null && !/^[\p{L}\p{M}\p{Nd}\p{Nl}\p{No}_-]+$/u.test(profile)) {
    throw new UsageError("--profile may contain only letters, digits, '-' and '_'");
  }
  return directory ?? FileDeviceStore.defaultDirectory(profile ?? DEFAULT_PROFILE);
}

/** @param {Options} options */
function targetOf(options) {
  const host = options.optional('host');
  const port = options.optional('port');
  if (host === null && port === null) {
    return ReceiverTarget.discover;
  }
  if (host === null || port === null) {
    throw new UsageError('--host and --port go together');
  }
  if (!/^\d{1,5}$/.test(port) || Number(port) < 1 || Number(port) > 65535) {
    throw new UsageError('--port must be between 1 and 65535');
  }
  return ReceiverTarget.address(host, Number(port));
}

/** @param {string} text */
function parseKinds(text) {
  /** @type {Set<string>} */
  const kinds = new Set();
  for (const piece of text.split(',')) {
    if (piece.length === 0) {
      continue;
    }
    const kind = trimWhiteSpace(piece);
    if (!ITEM_KINDS.includes(kind)) {
      throw new UsageError('--kinds takes pin, marker, note, photo and track, separated by commas.');
    }
    kinds.add(kind);
  }
  if (kinds.size === 0) {
    throw new UsageError('--kinds needs at least one item kind');
  }
  return /** @type {import('../../../src/model.js').ItemKind[]} */ ([...kinds]);
}

async function readCodeFromKeyboard() {
  if (process.platform === 'win32' && !process.stdin.isTTY) {
    throw new UsageError('no terminal to read the code from. Use --code-file with a file only you can read.');
  }
  const code = await readSecretLine('Pairing code: ', 1024);
  if (code === null) {
    throw new UsageError(process.platform === 'win32'
      ? 'no terminal to read the code from. Use --code-file with a file only you can read.'
      : 'no terminal to read the code from. Use --code-file with a file of mode 0600.');
  }
  return code;
}

/**
 * `gateway pair`: the same steps as `erdigo-send pair`, into a profile of its own.
 * @param {string[]} args
 */
async function runPair(args) {
  const options = new Options(args, []);
  const directory = stateDirectory(options);
  const name = options.required('name');
  const kinds = parseKinds(options.optional('kinds') ?? ITEM_KINDS.join(','));
  const codeFile = options.optional('code-file');
  const fingerprint = options.optional('fingerprint')?.toLowerCase() ?? null;
  const target = targetOf(options);
  options.finish();
  if (fingerprint !== null && !isFingerprint(fingerprint)) {
    throw new UsageError('--fingerprint must be 64 hexadecimal digits');
  }
  const store = new FileDeviceStore(directory);
  if (store.pairings().length > 0) {
    throw new UsageError('this profile already holds a pairing, and a gateway uses exactly one. Use another --profile or --state-dir.');
  }
  const text = codeFile !== null ? readCodeFile(codeFile) : await readCodeFromKeyboard();
  let ticket;
  try {
    ticket = PairingTicket.parse(text);
  } catch (error) {
    if (error instanceof WireError) {
      throw new UsageError(error.detail);
    }
    throw error;
  }
  if (fingerprint !== null) {
    if (ticket.fingerprint !== null && ticket.fingerprint !== fingerprint) {
      throw new ErdigoError(ErdigoErrorKind.fingerprintMismatch);
    }
    ticket = new PairingTicket(ticket.code, fingerprint);
  }
  // Only a person can compare the key, so a bare code is refused before the app is contacted when nobody can answer.
  if (ticket.fingerprint === null && !personIsThere()) {
    throw new UsageError("a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.");
  }
  const waiting = 'Waiting for the request to be confirmed in the Erdigo app...';
  const personCompares = ticket.fingerprint === null;
  console.log(personCompares ? 'Checking the code with the Erdigo app...' : waiting);
  const identity = store.loadOrCreateIdentity();
  try {
    const pairing = await pair({
      target,
      ticket,
      identity,
      deviceName: name,
      kinds,
      confirmKey: async (key) => {
        const answer = await ask([`Receiver key: ${key}`, 'If this matches the key shown in the Erdigo app, type yes: ']);
        if (answer === null || trimWhiteSpace(answer).toLowerCase() !== 'yes') {
          console.log();
          return false;
        }
        console.log(waiting);
        return true;
      },
    });
    store.save(pairing);
    console.log(`Paired. Allowed: ${pairing.kinds.join(', ')}.`);
    if (!personCompares) {
      console.log(`Receiver key: ${shortFingerprint(pairingFingerprint(pairing))}`);
    }
  } finally {
    identity.dispose();
  }
}

/**
 * The relay's address, checked: https://, or http:// on this computer only, with no user, query or fragment.
 * @param {string} text
 */
function relayAddress(text) {
  let url;
  try {
    url = new URL(text);
  } catch {
    throw new UsageError('--relay must be an address such as https://relay.example.com');
  }
  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && LOOPBACK_HOSTS.has(url.hostname))) {
    throw new UsageError('--relay must start with https://. Plain http:// is accepted only for 127.0.0.1, [::1] and localhost.');
  }
  if (url.username !== '' || url.password !== '' || url.search !== '' || url.hash !== '') {
    throw new UsageError('--relay cannot hold a user name, a password, a query or a fragment');
  }
  const base = `${url.protocol}//${url.host}${url.pathname.replace(/\/+$/, '')}`;
  return { key: base, ops: new URL(`${base}/v1/ops`) };
}

/**
 * Where the gateway is in the relay's queue: the relay's epoch and the last sequence number it is done with, and
 * whether it has already said that operations expired before this place. The relay says so at every fetch until the
 * place moves past them, so it is said once for each place, also across `--once` runs.
 * @typedef {{ epoch: string | null, after: number, missedReported: boolean }} Place
 */

/** @type {Place} */
const NO_PLACE = Object.freeze({ epoch: null, after: 0, missedReported: false });

/**
 * @param {string} directory
 * @param {string} relay
 * @returns {Place}
 */
function loadPlace(directory, relay) {
  let data;
  try {
    data = readPrivateFile(join(directory, PLACE_FILE), 4096);
  } catch (error) {
    if (error instanceof PrivateFileError && error.problem === PrivateFileProblem.missing) {
      return NO_PLACE;
    }
    if (error instanceof PrivateFileError) {
      throw new UsageError(`${PLACE_FILE} could not be used: ${error.message}`);
    }
    throw error;
  }
  const root = parseJson(data);
  const fields = root?.type === 'obj' ? root.value : null;
  const address = fields?.get('relay');
  const epoch = fields?.get('epoch');
  const after = fields?.get('after');
  const reported = fields?.get('missed_reported');
  // missed_reported may be left out, so a file with just the three fields of docs/cloud-relay.md is read too.
  if (fields === null || fields === undefined || fields.size !== (reported === undefined ? 3 : 4) || address?.type !== 'str'
    || !(epoch?.type === 'null' || (epoch?.type === 'str' && isEpoch(epoch.value)))
    || after?.type !== 'int' || after.value < 0n || after.value > BigInt(MAX_SEQUENCE)
    || !(reported === undefined || reported.type === 'bool')) {
    throw new UsageError(`${PLACE_FILE} is damaged. Move it away to start again from the relay's oldest operation.`);
  }
  // Another relay has its own numbering, so the gateway starts again from that relay's oldest operation.
  if (address.value !== relay) {
    return NO_PLACE;
  }
  return {
    epoch: epoch.type === 'str' ? epoch.value : null,
    after: Number(after.value),
    missedReported: reported?.type === 'bool' && reported.value,
  };
}

/**
 * Replaces the file whole, through a temporary file and a rename, readable by this user only.
 * @param {string} directory
 * @param {string} relay
 * @param {Place} place
 */
function savePlace(directory, relay, place) {
  const text = `${JSON.stringify({ relay, epoch: place.epoch, after: place.after, missed_reported: place.missedReported })}\n`;
  writePrivateFileAtomically(join(directory, PLACE_FILE), Buffer.from(text, 'utf8'));
}

/** @param {unknown} error */
const codeOf = (error) => /** @type {NodeJS.ErrnoException} */ (error)?.code;

/**
 * The process id in a lock file, 0 if it holds none, or null if there is no lock file.
 * @param {string} path
 * @returns {number | null}
 */
function lockHolder(path) {
  let data;
  try {
    // A link, another kind of file, or a file someone else could write is refused, not followed or taken over.
    data = readPrivateFile(path, 64);
  } catch (error) {
    if (error instanceof PrivateFileError && error.problem === PrivateFileProblem.missing) {
      return null;
    }
    if (error instanceof PrivateFileError) {
      throw new UsageError(`${error.message}, so it was not used. Remove it from the state folder if no gateway is running.`);
    }
    throw error;
  }
  const text = data.toString('latin1');
  return /^[1-9]\d{0,15}\n$/.test(text) && Number.isSafeInteger(Number(text.trim())) ? Number(text.trim()) : 0;
}

/**
 * Whether a process with this id is running. Some other user's process counts too.
 * @param {number} pid
 */
function isRunning(pid) {
  if (pid <= 0 || pid === process.pid) {
    return false;
  }
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return codeOf(error) !== 'ESRCH';
  }
}

/**
 * Makes sure that only one `run` at a time uses a state folder: two gateways with one key would take each other's
 * connection to the app and repeat each other's work. Node.js has no file locks, so the lock is a private file that
 * holds the process id, put in place whole or not at all, and removed when the run ends. A lock left behind by a
 * process that has ended, for example one that was killed, is taken over.
 * @param {string} directory
 * @returns {() => void} gives the lock back
 */
function lockStateFolder(directory) {
  const path = join(directory, LOCK_FILE);
  const busy = (/** @type {number} */ pid) => new UsageError(`another gateway (process ${pid}) is already running with this profile`);
  for (let attempt = 0; attempt < 5; attempt += 1) {
    try {
      // Written to a temporary file and linked into place, which fails if anything at all is at the path.
      writePrivateFileAtomically(path, Buffer.from(`${process.pid}\n`, 'latin1'), false);
      return () => {
        try {
          if (lockHolder(path) === process.pid) {
            unlinkSync(path);
          }
        } catch {
          // Left behind, it is taken over by the next run, because this process will have ended.
        }
      };
    } catch (error) {
      if (codeOf(error) !== 'EEXIST') {
        throw new UsageError(`${LOCK_FILE} could not be created in the state folder: ${error instanceof Error ? error.message : String(error)}`);
      }
    }
    const holder = lockHolder(path);
    if (holder === null) {
      continue;
    }
    if (isRunning(holder)) {
      throw busy(holder);
    }
    // Moved aside before it is removed, so that of two gateways starting at once, one cannot remove the lock the
    // other has just taken: whichever moved a live lock puts it back.
    const aside = join(directory, `${LOCK_FILE}.${randomBytes(8).toString('hex')}`);
    try {
      renameSync(path, aside);
    } catch (error) {
      if (codeOf(error) === 'ENOENT') {
        continue;
      }
      throw new UsageError(`${LOCK_FILE} could not be taken over: ${error instanceof Error ? error.message : String(error)}`);
    }
    let moved = 0;
    try {
      moved = lockHolder(aside) ?? 0;
      if (moved !== holder && isRunning(moved)) {
        try {
          linkSync(aside, path);
        } catch {
          // A third gateway took the place in the meantime, and holds a lock of its own.
        }
        throw busy(moved);
      }
    } finally {
      try {
        unlinkSync(aside);
      } catch {
        // Already gone.
      }
    }
  }
  throw new UsageError('another gateway is starting with this profile');
}

/**
 * One GET, with the answer's size capped. Resolves with the status and, for a 200, the body.
 * @param {URL} url
 * @param {string} token
 * @param {AbortSignal} signal
 * @param {number} answerMs how long the answer may take to start; the whole request is limited to FETCH_TIMEOUT_MS
 * @returns {Promise<{ status: number, type: string | undefined, body: Buffer }>}
 */
function get(url, token, signal, answerMs) {
  return new Promise((resolve, reject) => {
    const send = url.protocol === 'https:' ? httpsRequest : httpRequest;
    const request = send(url, { method: 'GET', headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' }, signal });
    let settled = false;
    const settle = (/** @type {() => void} */ how) => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        clearTimeout(answerTimer);
        how();
      }
    };
    // Settled here first, so that whatever error destroying the request raises afterwards changes nothing.
    const fail = (/** @type {Error} */ error) => {
      settle(() => reject(signal.aborted ? signal.reason : error));
      request.destroy();
    };
    const timer = setTimeout(() => fail(new RelayUnreachable('the relay did not answer in time')), FETCH_TIMEOUT_MS);
    const answerTimer = setTimeout(() => fail(new RelayUnreachable('the relay did not answer in time')), answerMs);
    request.on('error', (error) => fail(new RelayUnreachable(error.message)));
    request.on('response', (response) => {
      clearTimeout(answerTimer);
      const status = response.statusCode ?? 0;
      if (status !== 200) {
        // Anything but a 200 is told by its status alone, so its body is not read.
        settle(() => resolve({ status, type: undefined, body: Buffer.alloc(0) }));
        request.destroy();
        return;
      }
      const tooLarge = () => fail(new RelayBroken('the answer is larger than 8 MiB'));
      if (Number(response.headers['content-length'] ?? 0) > MAX_ANSWER_BYTES) {
        tooLarge();
        return;
      }
      /** @type {Buffer[]} */
      const chunks = [];
      let size = 0;
      response.on('data', (/** @type {Buffer} */ chunk) => {
        if (settled) {
          return;
        }
        size += chunk.length;
        if (size > MAX_ANSWER_BYTES) {
          tooLarge();
          return;
        }
        chunks.push(chunk);
      });
      response.on('end', () => settle(() => resolve({ status, type: response.headers['content-type'], body: Buffer.concat(chunks, size) })));
      response.on('error', (error) => fail(new RelayUnreachable(error.message)));
      response.on('close', () => {
        if (!response.complete) {
          fail(new RelayUnreachable('the answer was cut off'));
        }
      });
    });
    request.end();
  });
}

/**
 * Checks an answer to GET /v1/ops strictly. Only the operations themselves are left to be checked one by one, so
 * that one the gateway cannot use is skipped rather than holding up the rest.
 * @param {Buffer} body
 * @param {string | undefined} type
 * @param {Place} sent
 */
function readAnswer(body, type, sent) {
  const broken = (/** @type {string} */ reason) => new RelayBroken(reason);
  if (!/^application\/json(\s*;.*)?$/i.test(type ?? '')) {
    throw broken('the answer is not JSON');
  }
  const root = parseJson(body);
  if (root === null || root.type !== 'obj') {
    throw broken('the answer is not one JSON object');
  }
  const fields = root.value;
  const epoch = fields.get('epoch');
  const reset = fields.get('reset');
  const missed = fields.get('missed');
  const ops = fields.get('ops');
  if (fields.size !== 4 || epoch?.type !== 'str' || reset?.type !== 'bool' || missed?.type !== 'bool' || ops?.type !== 'arr') {
    throw broken('the answer is not {"epoch":...,"reset":...,"missed":...,"ops":[...]}');
  }
  if (!isEpoch(epoch.value)) {
    throw broken('the epoch is not 16 to 64 letters, digits, - or _');
  }
  if (!reset.value && (sent.epoch === null || epoch.value !== sent.epoch)) {
    throw broken('the relay changed its epoch without a reset');
  }
  if (ops.value.length > FETCH_LIMIT) {
    throw broken('the answer holds more operations than asked for');
  }
  let last = reset.value ? 0 : sent.after;
  const entries = ops.value.map((value) => {
    const entry = value.type === 'obj' ? value.value : null;
    const seq = entry?.get('seq');
    const received = entry?.get('received_ms');
    const op = entry?.get('op');
    if (entry === null || entry.size !== 3 || seq?.type !== 'int' || received?.type !== 'int' || op?.type !== 'obj') {
      throw broken('an entry is not {"seq":...,"received_ms":...,"op":{...}}');
    }
    if (seq.value <= BigInt(last) || seq.value > BigInt(MAX_SEQUENCE)) {
      throw broken('the sequence numbers do not go up from the last one finished');
    }
    if (received.value < 0n || received.value > BigInt(MAX_SEQUENCE)) {
      throw broken('received_ms is out of range');
    }
    last = Number(seq.value);
    return { seq: last, op };
  });
  return { epoch: epoch.value, reset: reset.value, missed: missed.value, entries };
}

/**
 * 1, 2, 4 ... up to 60 seconds, with 20 percent random variation (spec section 13). In seconds.
 * @param {number} attempt
 */
function backoff(attempt) {
  return Math.min(60, 2 ** Math.min(attempt, 6)) * (0.8 + 0.4 * (randomInt(0, 1_000_001) / 1_000_000));
}

/** @param {number} seconds */
function inSeconds(seconds) {
  const whole = Math.max(1, Math.round(seconds));
  return whole === 1 ? '1 second' : `${whole} seconds`;
}

/**
 * Writes one operation through the SDK.
 * @param {ErdigoClient} client
 * @param {Record<string, any>} op
 * @param {Buffer | null} photo
 * @param {AbortSignal} signal
 */
function apply(client, op, photo, signal) {
  const at = { lat: op.lat, lon: op.lon };
  const title = op.title ?? null;
  switch (op.op) {
    case 'pin':
      return client.putPin(op.id, at, { title, signal });
    case 'marker':
      return client.putMarker(op.id, at, { title, color: op.color ?? null, signal });
    case 'note':
      return client.putNote(op.id, at, op.text, { signal });
    case 'photo':
      return client.putPhoto(op.id, at, /** @type {Buffer} */ (photo), { title, signal });
    case 'track':
      return client.appendTrack(op.id, op.points, { title, signal });
    case 'finish_track':
      return client.finishTrack(op.id, { signal });
    case 'move':
      return client.move(op.id, at, { signal });
    case 'delete':
      return client.delete(op.id, { signal });
    default:
      throw new OpError('unknown operation');
  }
}

/**
 * What an error from the SDK means for the operation: refused for good, worth trying again, or the end of the run.
 * @param {unknown} error
 * @returns {{ refused: string } | { retry: true } | { gaveUp: true } | { fatal: unknown }}
 */
function outcomeOf(error) {
  if (!(error instanceof ErdigoError)) {
    return { fatal: error };
  }
  switch (error.kind) {
    case ErdigoErrorKind.noAnswer:
      // Only with --once: the SDK has tried for the time it was given.
      return { gaveUp: true };
    case ErdigoErrorKind.protocolViolation:
      return { retry: true };
    case ErdigoErrorKind.notPermitted:
      return { refused: 'not_permitted' };
    case ErdigoErrorKind.invalidArgument:
      return { refused: error.kind };
    case ErdigoErrorKind.rejected: {
      const code = error.receiverError?.code ?? '';
      if (code === 'internal_error' || code === 'rate_limited' || code === 'too_many_errors') {
        return { retry: true };
      }
      // A code this gateway does not know is a permanent failure of that request (spec section 10).
      return { refused: /^[a-z0-9_]{1,40}$/.test(code) ? code : 'unknown' };
    }
    default:
      // Another program using this key, a receiver that speaks no version this SDK does, or unusable stored state.
      return { fatal: error };
  }
}

/**
 * What an error from connecting means. Nothing about the operation was sent yet, so nothing here can refuse it.
 * @param {unknown} error
 * @returns {{ retry: true } | { gaveUp: true } | { fatal: unknown }}
 */
function connectionOutcomeOf(error) {
  if (error instanceof ErdigoError && error.kind === ErdigoErrorKind.noAnswer) {
    return { gaveUp: true };
  }
  if (!(error instanceof ErdigoError) || error.kind === ErdigoErrorKind.versionUnsupported || error.kind === ErdigoErrorKind.storage) {
    return { fatal: error };
  }
  return { retry: true };
}

/**
 * `gateway run`.
 * @param {string[]} args
 * @param {Record<string, string | undefined>} env
 * @returns {Promise<number>}
 */
async function runGateway(args, env) {
  const options = new Options(args, ['once']);
  const relay = relayAddress(options.required('relay'));
  const once = options.flag('once');
  const target = targetOf(options);
  const directory = stateDirectory(options);
  options.finish();
  const token = env.ERDIGO_RELAY_TOKEN?.trim();
  if (!token) {
    throw new UsageError('set ERDIGO_RELAY_TOKEN to the gateway token. It is never taken as an argument.');
  }
  if (!isToken(token)) {
    throw new UsageError('ERDIGO_RELAY_TOKEN must be 32 to 256 characters from A-Z, a-z, 0-9, -, _, . and ~');
  }
  const store = new FileDeviceStore(directory);
  const pairings = store.pairings();
  if (pairings.length !== 1) {
    throw new UsageError(pairings.length === 0
      ? "this profile holds no pairing. Run 'gateway pair' first."
      : 'this profile holds several pairings, and a gateway uses exactly one. Pair the gateway in a profile of its own.');
  }
  const unlock = lockStateFolder(store.directory);
  try {
    return await fetchAndApply(store, pairings[0], relay, target, once, token);
  } finally {
    unlock();
  }
}

/**
 * The work of `gateway run`, while it holds the state folder.
 * @param {FileDeviceStore} store
 * @param {import('../../../src/identity.js').ReceiverPairing} pairing
 * @param {{ key: string, ops: URL }} relay
 * @param {import('../../../src/discovery.js').ReceiverTarget} target
 * @param {boolean} once
 * @param {string} token
 * @returns {Promise<number>}
 */
async function fetchAndApply(store, pairing, relay, target, once, token) {
  let place = loadPlace(store.directory, relay.key);

  const stop = new AbortController();
  const { signal } = stop;
  const onSignal = () => {
    process.off('SIGINT', onSignal);
    process.off('SIGTERM', onSignal);
    stop.abort();
  };
  process.on('SIGINT', onSignal);
  process.on('SIGTERM', onSignal);

  const identity = store.loadOrCreateIdentity();
  // The SDK waits out an app it cannot reach, and sends a request whose answer was lost again with the same sequence
  // number, so that the app applies it once (spec sections 7 and 13). With --once it gives up after about 30 seconds
  // without a connection, a count that starts again at each new connection, so the gateway also keeps its own.
  const client = new ErdigoClient({ identity, pairing, store, target, retry: once ? RetryPolicy.upTo(ONCE_GIVE_UP_MS) : RetryPolicy.forever });
  try {
    const relayRetries = new Retries(once);
    let lastUsed = Date.now();
    for (;;) {
      let answer;
      const startedAt = Date.now();
      try {
        const url = new URL(relay.ops);
        url.search = new URLSearchParams({
          ...(place.epoch !== null ? { epoch: place.epoch } : {}),
          after: String(place.after),
          limit: String(FETCH_LIMIT),
          // With --once there is no point waiting for more: it stops as soon as nothing is waiting.
          wait: String(once ? 0 : FETCH_WAIT_SECONDS),
        }).toString();
        const answerMs = once ? Math.max(1000, Math.min(ONCE_ANSWER_TIMEOUT_MS, relayRetries.remainingMs())) : FETCH_TIMEOUT_MS;
        const { status, type, body } = await get(url, token, signal, answerMs);
        if (status === 401) {
          throw new RelayRefused('the relay refused the token');
        }
        if (status === 408 || status === 429 || status >= 500) {
          throw new RelayUnreachable(`the relay answered ${status}`);
        }
        if (status !== 200) {
          throw new RelayBroken(`the relay answered ${status}`);
        }
        answer = readAnswer(body, type, place);
        relayRetries.reset();
      } catch (error) {
        if (!(error instanceof RelayUnreachable)) {
          throw error;
        }
        const wait = relayRetries.next(startedAt);
        if (wait === null) {
          throw new GaveUp('The relay could not be reached.');
        }
        console.log(`The relay could not be reached; trying again in ${inSeconds(wait)}.`);
        await delay(wait * 1000, undefined, { signal });
        continue;
      }
      if (answer.reset) {
        if (place.epoch !== null) {
          console.log('The relay started a new numbering; starting from its oldest operation.');
        }
        place = { epoch: answer.epoch, after: 0, missedReported: false };
        savePlace(store.directory, relay.key, place);
      }
      if (answer.missed && !place.missedReported) {
        console.log('Some operations expired on the relay before they were fetched.');
        place = { ...place, missedReported: true };
        savePlace(store.directory, relay.key, place);
      }
      if (answer.entries.length === 0) {
        if (once) {
          return 0;
        }
        // The app closes a connection that stays idle for five minutes (spec section 11), so close it first.
        if (Date.now() - lastUsed >= IDLE_CLOSE_MS) {
          client.close();
        }
        // A relay that cannot hold a fetch open answers at once, and is not asked again straight away.
        const spent = Date.now() - startedAt;
        if (spent < EMPTY_FETCH_INTERVAL_MS) {
          await delay(EMPTY_FETCH_INTERVAL_MS - spent, undefined, { signal });
        }
        continue;
      }
      let refused = 0;
      for (const entry of answer.entries) {
        const code = await applyWithRetries(client, entry, once, signal);
        if (code !== null) {
          refused += 1;
          const name = entry.op.value.get('op');
          const shown = name?.type === 'str' && isOpName(name.value) ? name.value : 'unknown';
          console.log(`Refused operation ${entry.seq} (${shown}): ${code}`);
        }
        // Done with it, accepted or refused: the place is saved before going on, and the relay is told at the next
        // fetch, so an operation the gateway was stopped in the middle of comes again.
        place = { epoch: answer.epoch, after: entry.seq, missedReported: false };
        savePlace(store.directory, relay.key, place);
      }
      lastUsed = Date.now();
      const count = answer.entries.length;
      console.log(`Done with ${count} ${count === 1 ? 'operation' : 'operations'}, up to ${place.after}${refused > 0 ? ` (${refused} refused)` : ''}`);
    }
  } catch (error) {
    if (signal.aborted) {
      return 0;
    }
    throw error;
  } finally {
    process.off('SIGINT', onSignal);
    process.off('SIGTERM', onSignal);
    client.close();
    identity.dispose();
  }
}

const APP_GAVE_UP = 'The Erdigo app could not be reached. The operations stay on the relay.';

/**
 * One operation's trouble reaching the app, across all its tries. The SDK counts its own time from the last
 * connection it made, so an app that takes every connection but never answers, or whose answers are all lost, would
 * keep it trying for ever. This counts from the first sign of trouble until the app takes the operation, however many
 * connections that takes. Time the SDK spends pacing points before it sends is not trouble, so it is not counted.
 */
class Trouble {
  constructor() {
    /** When the first sign of trouble showed, or null while there has been none. @type {number | null} */
    this.since = null;
    /** Until when the gateway says no more that the app does not answer. */
    this.quietUntil = 0;
  }

  /** Notes trouble that shows now, and returns when the trouble began. */
  note(now = Date.now()) {
    this.since ??= now;
    return this.since;
  }
}

/**
 * One try at an operation. Returns null once the app has taken it, or what the error means.
 *
 * A watcher looks at the SDK's connection from outside while it runs, because the SDK does not say how it is doing.
 * Once the SDK has connected and is sending the operation, it drops that connection only when sending failed: the
 * answer was lost, or did not come in time. That is trouble. The SDK then connects again and sends the same request
 * with the same number. A request waiting for the app's rate limit keeps its connection, so that wait is not mistaken
 * for this. While the try goes on with no connection, the watcher says so, and the SDK counts that time itself.
 *
 * With --once the watcher ends the try once the trouble has lasted ONCE_GIVE_UP_MS. A request stopped half way may
 * have been applied, which at-least-once delivery allows: the operation stays on the relay.
 * @param {ErdigoClient} client
 * @param {{ op: Record<string, any>, photo: Buffer | null }} checked
 * @param {boolean} once
 * @param {Trouble} trouble
 * @param {AbortSignal} signal Ctrl+C or SIGTERM
 * @returns {Promise<null | ReturnType<typeof outcomeOf>>}
 */
async function attempt(client, checked, once, trouble, signal) {
  const ending = new AbortController();
  const stop = () => ending.abort(signal.reason);
  signal.addEventListener('abort', stop, { once: true });
  if (signal.aborted) {
    stop();
  }
  // The limits the app gave on the connection the operation is sent over, once there is one. Each connection brings
  // limits of its own, so other limits mean another connection, even one made between two looks.
  /** @type {object | null | undefined} */
  let sendingOn;
  /** @type {number | null} */
  let unconnectedSince = null;
  const watch = setInterval(() => {
    const now = Date.now();
    const limits = client.limits;
    if (sendingOn !== undefined && limits !== sendingOn) {
      trouble.note(now);
    }
    unconnectedSince = limits === null ? (unconnectedSince ?? now) : null;
    const troubled = trouble.since === null ? 0 : now - trouble.since;
    const unconnected = unconnectedSince === null ? 0 : now - unconnectedSince;
    // The SDK does not say when it tries next, so the line does not say either.
    if (Math.max(troubled, unconnected) >= OUTAGE_FIRST_REPORT_MS && now >= trouble.quietUntil) {
      console.log('No answer from the Erdigo app; trying again.');
      trouble.quietUntil = now + OUTAGE_REPORT_EVERY_MS;
    }
    if (once && troubled >= ONCE_GIVE_UP_MS) {
      ending.abort(new GaveUp(APP_GAVE_UP));
    }
  }, 250);
  // Stopping, which may come at the same moment as the time running out, ends the run with 0 rather than 3.
  const ended = (/** @type {unknown} */ error) => {
    if (signal.aborted) {
      throw error;
    }
    return /** @type {{ gaveUp: true }} */ ({ gaveUp: true });
  };
  try {
    // Connecting first keeps a failure of the connection apart from an answer about this operation, which alone
    // may refuse it, and keeps the time before the app is reached out of the count: the SDK counts that itself.
    try {
      await client.connect({ signal: ending.signal });
    } catch (error) {
      return ending.signal.aborted ? ended(error) : connectionOutcomeOf(error);
    }
    sendingOn = client.limits;
    try {
      await apply(client, checked.op, checked.photo, ending.signal);
      return null;
    } catch (error) {
      return ending.signal.aborted ? ended(error) : outcomeOf(error);
    }
  } finally {
    clearInterval(watch);
    signal.removeEventListener('abort', stop);
  }
}

/**
 * Applies one operation. Returns null once the app has taken it, or the code it was refused with.
 *
 * While the app cannot be reached the SDK waits 1, 2, 4 ... up to 60 seconds and sends the same request again with
 * the same sequence number, so an operation the app applied but whose answer was lost is not applied twice (spec
 * sections 7 and 13). With --once the gateway gives up when the app has not taken the operation ONCE_GIVE_UP_MS after
 * the first sign of trouble, even if the app takes every new connection.
 * @param {ErdigoClient} client
 * @param {{ seq: number, op: import('../../../src/json.js').JsonValue }} entry
 * @param {boolean} once
 * @param {AbortSignal} signal
 * @returns {Promise<string | null>}
 */
async function applyWithRetries(client, entry, once, signal) {
  let checked;
  try {
    checked = readOperation(entry.op);
  } catch (error) {
    if (error instanceof OpError) {
      return 'invalid_op';
    }
    throw error;
  }
  const trouble = new Trouble();
  // Only for the answers that say "not now" about this request, which the SDK does not wait out itself.
  const retries = new Retries(once);
  for (;;) {
    const outcome = await attempt(client, checked, once, trouble, signal);
    if (outcome === null) {
      return null;
    }
    if ('refused' in outcome) {
      return outcome.refused;
    }
    if ('gaveUp' in outcome) {
      throw new GaveUp(APP_GAVE_UP);
    }
    if ('fatal' in outcome) {
      throw outcome.fatal;
    }
    // Counted from when a try failed rather than when it began, so that time spent pacing points is not counted.
    const wait = retries.next(trouble.note());
    if (wait === null) {
      throw new GaveUp(APP_GAVE_UP);
    }
    console.log(`No answer from the Erdigo app; trying again in ${inSeconds(wait)}.`);
    // This line says when, so the one without a time is not needed for a while.
    trouble.quietUntil = Date.now() + OUTAGE_REPORT_EVERY_MS;
    await delay(wait * 1000, undefined, { signal });
  }
}

/**
 * Runs the gateway and returns its exit status.
 * @param {string[]} args
 * @param {Record<string, string | undefined>} env
 * @returns {Promise<number>}
 */
export async function main(args, env) {
  if (args.length === 0 || args[0] === 'help' || args[0] === '--help' || args[0] === '-h') {
    console.log(USAGE);
    return 0;
  }
  try {
    switch (args[0]) {
      case 'pair':
        await runPair(args.slice(1));
        return 0;
      case 'run':
        return await runGateway(args.slice(1), env);
      default:
        console.log(USAGE);
        throw new UsageError('unknown command');
    }
  } catch (error) {
    if (error instanceof Interrupted) {
      process.stderr.write('\n');
      return 130;
    }
    if (error instanceof GaveUp) {
      console.log(error.message);
      return 3;
    }
    if (error instanceof RelayRefused || error instanceof RelayBroken) {
      process.stderr.write(`gateway: ${error instanceof RelayRefused ? error.message : `the relay's answer does not follow the relay API: ${error.message}`}\n`);
      return 2;
    }
    const known = error instanceof UsageError || error instanceof ErdigoError || error instanceof WireError
      || (error instanceof Error && typeof (/** @type {NodeJS.ErrnoException} */ (error).code) === 'string');
    const message = error instanceof Error ? error.message : String(error);
    process.stderr.write(`gateway: ${known ? message : `unexpected error: ${message}`}\n`);
    return 1;
  }
}
