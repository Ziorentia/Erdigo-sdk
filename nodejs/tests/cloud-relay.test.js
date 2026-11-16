// The cloud relay example (examples/cloud-relay) against docs/cloud-relay.md: every case in
// docs/cloud-relay-cases.json, and the rest of the API. Most tests run the relay in this process, so that they can
// give it small limits and a clock of their own. The last ones run its command line.

import assert from 'node:assert/strict';
import { connect, createServer } from 'node:net';
import { test } from 'node:test';
import { DEFAULT_LIMITS, RelayQueue, parseArguments, startRelay, tokensFrom } from '../examples/cloud-relay/lib/relay.js';
import { RELAY, call, raw, run, startProgram, token } from './support/cloud-relay.js';
import { loadJson } from './support/vectors.js';

const cases = loadJson('docs/cloud-relay-cases.json');
const PNG_1X1 = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==';
const pin = (/** @type {string} */ id) => ({ op: 'pin', id, lat: 37.5663, lon: 126.9779 });

/**
 * A relay in this process, with tokens made for it.
 * @param {{ limits?: Partial<typeof DEFAULT_LIMITS>, now?: () => number, keepHours?: number, log?: (line: string) => void }} [options]
 */
async function relay({ limits = {}, now, keepHours, log } = {}) {
  const source = token();
  const otherSource = token();
  const gateway = token();
  const started = await startRelay({
    host: '127.0.0.1',
    port: 0,
    sourceTokens: [source, otherSource],
    gatewayToken: gateway,
    keepHours,
    limits: { ...DEFAULT_LIMITS, ...limits },
    now,
    log,
  });
  const ops = `${started.url}/v1/ops`;
  return {
    ...started,
    source,
    otherSource,
    gateway,
    ops,
    /** @param {unknown} body */
    post: (body, type = 'application/json') => call(ops, { method: 'POST', token: source, body, type }),
    /** @param {string} [query] */
    fetch: (query = '') => call(`${ops}${query ? `?${query}` : ''}`, { token: gateway }),
  };
}

/**
 * Every answer is JSON, whatever its status.
 * @param {import('./support/cloud-relay.js').Answer} answer
 * @param {number} status
 */
function expectAnswer(answer, status, name = '') {
  assert.equal(answer.status, status, `${name}: ${answer.text}`);
  assert.equal(answer.headers['content-type'], 'application/json', name);
  assert.notEqual(answer.json, null, name);
  return answer.json;
}

test('takes every valid operation in the cases, and keeps it exactly as sent', async () => {
  const r = await relay();
  try {
    for (const [index, sample] of cases.valid_ops.entries()) {
      const taken = expectAnswer(await r.post({ ops: [sample.op] }), 200, sample.name);
      assert.deepEqual(taken, { accepted: 1, last_seq: index + 1 }, sample.name);
    }
    const answer = expectAnswer(await r.fetch(), 200);
    assert.equal(answer.ops.length, cases.valid_ops.length);
    answer.ops.forEach((/** @type {any} */ entry, /** @type {number} */ index) => {
      assert.equal(entry.seq, index + 1);
      assert.ok(Number.isSafeInteger(entry.received_ms));
      assert.deepEqual(entry.op, cases.valid_ops[index].op, cases.valid_ops[index].name);
    });
  } finally {
    await r.close();
  }
});

test('refuses every invalid operation in the cases, and takes nothing', async () => {
  const r = await relay();
  try {
    for (const sample of cases.invalid_ops) {
      const refused = expectAnswer(await r.post({ ops: [sample.op] }), 400, sample.name);
      assert.equal(refused.error, 'invalid_op', sample.name);
      assert.equal(refused.index, 0, sample.name);
      assert.equal(typeof refused.reason, 'string', sample.name);
      assert.ok(refused.reason.length > 0, sample.name);
    }
    const answer = expectAnswer(await r.fetch(), 200);
    assert.deepEqual(answer.ops, []);
    assert.equal(r.queue.lastSeq, 0);
  } finally {
    await r.close();
  }
});

test('refuses every raw body in the cases as they say', async () => {
  const r = await relay();
  try {
    for (const sample of cases.raw_bodies) {
      const body = sample.body_base64 !== undefined ? Buffer.from(sample.body_base64, 'base64') : Buffer.from(sample.body, 'utf8');
      const refused = expectAnswer(await r.post(body), 400, sample.name);
      assert.ok(sample.errors.includes(refused.error), `${sample.name}: ${refused.error}`);
    }
    assert.equal(r.queue.lastSeq, 0);
  } finally {
    await r.close();
  }
});

test('takes 100 operations in one request, and not 101', async () => {
  const r = await relay();
  try {
    const hundred = Array.from({ length: 100 }, (_, index) => pin(`p${index}`));
    assert.deepEqual(expectAnswer(await r.post({ ops: hundred }), 200), { accepted: 100, last_seq: 100 });
    const refused = expectAnswer(await r.post({ ops: [...hundred, pin('one-more')] }), 400);
    assert.equal(refused.error, 'invalid_body');
    assert.equal(r.queue.lastSeq, 100);
  } finally {
    await r.close();
  }
});

test('takes a photo of 1048576 bytes, and not of 1048577', async () => {
  const r = await relay();
  try {
    const photo = (/** @type {number} */ size) => {
      const data = Buffer.alloc(size);
      Buffer.from(PNG_1X1, 'base64').copy(data, 0, 0, 8);
      return { op: 'photo', id: 'big', lat: 0, lon: 0, type: 'image/png', data: data.toString('base64') };
    };
    assert.deepEqual(expectAnswer(await r.post({ ops: [photo(1_048_576)] }), 200), { accepted: 1, last_seq: 1 });
    const refused = expectAnswer(await r.post({ ops: [photo(1_048_577)] }), 400);
    assert.equal(refused.error, 'invalid_op');
    assert.equal(refused.index, 0);
  } finally {
    await r.close();
  }
});

test('takes a batch whole or not at all', async () => {
  const r = await relay();
  try {
    const refused = expectAnswer(await r.post({ ops: [pin('a'), pin('b'), { op: 'pin', id: 'c', lat: 91, lon: 0 }] }), 400);
    assert.equal(refused.error, 'invalid_op');
    assert.equal(refused.index, 2);
    assert.deepEqual(expectAnswer(await r.fetch(), 200).ops, []);
    expectAnswer(await r.post({ ops: [pin('a'), pin('b')] }), 200);
    const answer = expectAnswer(await r.fetch(), 200);
    assert.deepEqual(answer.ops.map((/** @type {any} */ entry) => [entry.seq, entry.op.id]), [[1, 'a'], [2, 'b']]);
  } finally {
    await r.close();
  }
});

test('answers queue_full with Retry-After when a batch would pass a limit, and takes nothing of it', async () => {
  const byCount = await relay({ limits: { queuedOps: 3 } });
  try {
    expectAnswer(await byCount.post({ ops: [pin('a'), pin('b')] }), 200);
    const full = await byCount.post({ ops: [pin('c'), pin('d')] });
    assert.equal(expectAnswer(full, 503).error, 'queue_full');
    assert.equal(full.headers['retry-after'], '30');
    assert.deepEqual(expectAnswer(await byCount.post({ ops: [pin('c')] }), 200), { accepted: 1, last_seq: 3 });
    // Forgetting what the gateway is done with makes room again.
    const { epoch } = expectAnswer(await byCount.fetch(), 200);
    expectAnswer(await byCount.fetch(`epoch=${epoch}&after=3`), 200);
    expectAnswer(await byCount.post({ ops: [pin('d'), pin('e'), pin('f')] }), 200);
  } finally {
    await byCount.close();
  }
  const opBytes = Buffer.byteLength(JSON.stringify(pin('a')));
  const byBytes = await relay({ limits: { queuedBytes: opBytes * 2 } });
  try {
    expectAnswer(await byBytes.post({ ops: [pin('a'), pin('b')] }), 200);
    assert.equal(expectAnswer(await byBytes.post({ ops: [pin('c')] }), 503).error, 'queue_full');
  } finally {
    await byBytes.close();
  }
});

test('answers from the oldest operation with reset when the epoch is missing or another, and forgets nothing', async () => {
  const r = await relay();
  try {
    expectAnswer(await r.post({ ops: [pin('a'), pin('b'), pin('c')] }), 200);
    const first = expectAnswer(await r.fetch(), 200);
    assert.match(first.epoch, /^[A-Za-z0-9_-]{16,64}$/);
    assert.equal(first.reset, true);
    assert.equal(first.missed, false);
    assert.equal(first.ops.length, 3);
    // Another epoch, even with an after, forgets nothing.
    const other = expectAnswer(await r.fetch(`epoch=${'x'.repeat(22)}&after=2`), 200);
    assert.equal(other.reset, true);
    assert.equal(other.epoch, first.epoch);
    assert.deepEqual(other.ops.map((/** @type {any} */ entry) => entry.seq), [1, 2, 3]);
    // The same epoch forgets everything up to after, and answers with what follows.
    const matched = expectAnswer(await r.fetch(`epoch=${first.epoch}&after=2`), 200);
    assert.equal(matched.reset, false);
    assert.deepEqual(matched.ops.map((/** @type {any} */ entry) => entry.seq), [3]);
    const again = expectAnswer(await r.fetch(), 200);
    assert.equal(again.reset, true);
    assert.deepEqual(again.ops.map((/** @type {any} */ entry) => entry.seq), [3]);
    // Up to the last one: nothing is left.
    assert.deepEqual(expectAnswer(await r.fetch(`epoch=${first.epoch}&after=3`), 200).ops, []);
    assert.deepEqual(expectAnswer(await r.fetch(), 200).ops, []);
    // An after past the last number given is a bad parameter, and only with the relay's own epoch.
    assert.equal(expectAnswer(await r.fetch(`epoch=${first.epoch}&after=4`), 400).error, 'invalid_query');
    expectAnswer(await r.fetch(`epoch=${'y'.repeat(16)}&after=4`), 200);
    // Numbering goes on from where it was.
    assert.deepEqual(expectAnswer(await r.post({ ops: [pin('d')] }), 200), { accepted: 1, last_seq: 4 });
  } finally {
    await r.close();
  }
});

test('a restarted relay has a new epoch', async () => {
  const first = await relay();
  const second = await relay();
  try {
    const one = expectAnswer(await first.fetch(), 200);
    const two = expectAnswer(await second.fetch(), 200);
    assert.notEqual(one.epoch, two.epoch);
    // Its numbering starts at 1 again.
    assert.deepEqual(expectAnswer(await second.post({ ops: [pin('a')] }), 200), { accepted: 1, last_seq: 1 });
  } finally {
    await first.close();
    await second.close();
  }
});

test('refuses a query with a parameter twice, an unknown one, or one out of range', async () => {
  const r = await relay();
  try {
    const { epoch } = expectAnswer(await r.fetch(), 200);
    for (const query of [
      'after=0&after=0', 'epoch=AAAAAAAAAAAAAAAA&epoch=AAAAAAAAAAAAAAAA', 'cursor=1', 'after', 'after=', 'after=-1', 'after=1.5',
      'after=1e3', 'after=+1', 'after=9007199254740992', 'limit=0', 'limit=101', 'limit=x', 'wait=31', 'wait=-1', 'wait=1.0',
      'epoch=short', `epoch=${'a'.repeat(65)}`, 'epoch=has.a.dot.in.it.xxxxxxxx', `epoch=${epoch}&after=1`,
    ]) {
      assert.equal(expectAnswer(await r.fetch(query), 400, query).error, 'invalid_query', query);
    }
    for (const query of ['after=0', 'limit=1', 'limit=100', 'wait=0', `epoch=${epoch}&after=0&limit=5&wait=0`, `epoch=${'a'.repeat(64)}`]) {
      expectAnswer(await r.fetch(query), 200, query);
    }
  } finally {
    await r.close();
  }
});

test('reports operations that expired before the gateway was done with them, and no others', async () => {
  let clock = 1_700_000_000_000;
  const hour = 3_600_000;
  const r = await relay({ now: () => clock, keepHours: 1 });
  try {
    expectAnswer(await r.post({ ops: [pin('a'), pin('b')] }), 200);
    const first = expectAnswer(await r.fetch(), 200);
    assert.equal(first.ops[0].received_ms, clock);
    // The gateway has them, and is done with them before they expire: not missed.
    clock += 2 * hour;
    const done = expectAnswer(await r.fetch(`epoch=${first.epoch}&after=2`), 200);
    assert.equal(done.missed, false);
    // One more waits too long before the gateway fetches it.
    expectAnswer(await r.post({ ops: [pin('c')] }), 200);
    clock += hour - 1;
    assert.equal(expectAnswer(await r.fetch(`epoch=${first.epoch}&after=2`), 200).ops.length, 1);
    clock += 1;
    const missed = expectAnswer(await r.fetch(`epoch=${first.epoch}&after=2`), 200);
    assert.equal(missed.missed, true);
    assert.deepEqual(missed.ops, []);
    // With a reset, anything that expired in this epoch counts.
    assert.equal(expectAnswer(await r.fetch(), 200).missed, true);
    // Once the gateway is past it, it is no longer reported.
    expectAnswer(await r.post({ ops: [pin('d')] }), 200);
    const later = expectAnswer(await r.fetch(`epoch=${first.epoch}&after=3`), 200);
    assert.equal(later.missed, false);
    assert.deepEqual(later.ops.map((/** @type {any} */ entry) => entry.seq), [4]);
  } finally {
    await r.close();
  }
});

test('pages by limit, and by the 4 MiB cap with at least one operation', async () => {
  const r = await relay();
  try {
    expectAnswer(await r.post({ ops: Array.from({ length: 5 }, (_, index) => pin(`p${index}`)) }), 200);
    const page = expectAnswer(await r.fetch('limit=2'), 200);
    assert.deepEqual(page.ops.map((/** @type {any} */ entry) => entry.seq), [1, 2]);
    const next = expectAnswer(await r.fetch(`epoch=${page.epoch}&after=2&limit=2`), 200);
    assert.deepEqual(next.ops.map((/** @type {any} */ entry) => entry.seq), [3, 4]);
    expectAnswer(await r.fetch(`epoch=${page.epoch}&after=5`), 200);

    // Three photos of 1 MiB each: two fit in 4 MiB, the third comes on its own.
    const data = Buffer.alloc(1_048_576);
    Buffer.from(PNG_1X1, 'base64').copy(data, 0, 0, 8);
    for (let index = 0; index < 3; index += 1) {
      expectAnswer(await r.post({ ops: [{ op: 'photo', id: `ph${index}`, lat: 0, lon: 0, type: 'image/png', data: data.toString('base64') }] }), 200);
    }
    const big = await r.fetch(`epoch=${page.epoch}&after=5`);
    assert.ok(Buffer.byteLength(big.text) <= 4 * 1024 * 1024);
    assert.deepEqual(expectAnswer(big, 200).ops.map((/** @type {any} */ entry) => entry.seq), [6, 7]);
    const rest = expectAnswer(await r.fetch(`epoch=${page.epoch}&after=7`), 200);
    assert.deepEqual(rest.ops.map((/** @type {any} */ entry) => entry.seq), [8]);
  } finally {
    await r.close();
  }
  // A single operation larger than the cap still comes, alone.
  const small = await relay({ limits: { answerBytes: 100 } });
  try {
    expectAnswer(await small.post({ ops: [pin('a'), pin('b')] }), 200);
    assert.equal(expectAnswer(await small.fetch(), 200).ops.length, 1);
  } finally {
    await small.close();
  }
});

test('holds a fetch open until something arrives, or until the wait ends', async () => {
  const r = await relay();
  try {
    const { epoch } = expectAnswer(await r.fetch(), 200);
    let started = Date.now();
    const empty = expectAnswer(await r.fetch(`epoch=${epoch}&after=0&wait=1`), 200);
    assert.deepEqual(empty.ops, []);
    assert.ok(Date.now() - started >= 900, 'it waited');
    started = Date.now();
    const waiting = r.fetch(`epoch=${epoch}&after=0&wait=20`);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expectAnswer(await r.post({ ops: [pin('a')] }), 200);
    const arrived = expectAnswer(await waiting, 200);
    assert.deepEqual(arrived.ops.map((/** @type {any} */ entry) => entry.op.id), ['a']);
    assert.ok(Date.now() - started < 5000, 'it answered when the operation arrived');
    // Something already waiting is answered at once.
    started = Date.now();
    assert.equal(expectAnswer(await r.fetch(`epoch=${epoch}&after=0&wait=20`), 200).ops.length, 1);
    assert.ok(Date.now() - started < 2000);
    // Another request is served while one waits.
    const held = r.fetch(`epoch=${epoch}&after=1&wait=3`);
    expectAnswer(await call(`${r.url}/v1/health`), 200);
    assert.deepEqual(expectAnswer(await held, 200).ops, []);
  } finally {
    await r.close();
  }
});

test('answers a waiting fetch when it stops', async () => {
  const r = await relay();
  const waiting = r.fetch('wait=30');
  await new Promise((resolve) => setTimeout(resolve, 200));
  const started = Date.now();
  await r.close();
  assert.deepEqual(expectAnswer(await waiting, 200).ops, []);
  assert.ok(Date.now() - started < 5000);
});

test('checks the token for each role, and the path and method before it', async () => {
  const r = await relay();
  try {
    const body = { ops: [pin('a')] };
    // An unknown path, then a wrong method, come before any token.
    assert.equal(expectAnswer(await call(`${r.url}/v1/nothing`), 404).error, 'not_found');
    assert.equal(expectAnswer(await call(`${r.url}/`, { method: 'POST', body }), 404).error, 'not_found');
    assert.equal(expectAnswer(await call(`${r.url}/v1/ops/`, { token: r.gateway }), 404).error, 'not_found');
    assert.equal(expectAnswer(await call(r.ops, { method: 'DELETE' }), 405).error, 'method_not_allowed');
    assert.equal(expectAnswer(await call(r.ops, { method: 'PUT', body }), 405).error, 'method_not_allowed');
    assert.equal(expectAnswer(await call(`${r.url}/v1/health`, { method: 'POST', body }), 405).error, 'method_not_allowed');
    // Then the token: none, an unknown one, or one of the wrong kind.
    const unauthorized = async (/** @type {Promise<import('./support/cloud-relay.js').Answer>} */ answer) => {
      assert.equal(expectAnswer(await answer, 401).error, 'unauthorized');
    };
    await unauthorized(call(r.ops));
    await unauthorized(call(r.ops, { method: 'POST', body }));
    await unauthorized(call(r.ops, { token: token() }));
    await unauthorized(call(r.ops, { method: 'POST', token: token(), body }));
    await unauthorized(call(r.ops, { token: r.source }));
    await unauthorized(call(r.ops, { method: 'POST', token: r.gateway, body }));
    await unauthorized(call(r.ops, { headers: { Authorization: r.gateway } }));
    await unauthorized(call(r.ops, { headers: { Authorization: `Basic ${r.gateway}` } }));
    await unauthorized(call(r.ops, { headers: { Authorization: `Bearer ${r.gateway.slice(0, -1)}` } }));
    // The token is checked before what is sent, so a bad body without one is still unauthorized.
    await unauthorized(call(r.ops, { method: 'POST', body: 'not json', type: 'text/plain' }));
    // Every source token may send, and only the gateway token may fetch.
    expectAnswer(await r.post(body), 200);
    expectAnswer(await call(r.ops, { method: 'POST', token: r.otherSource, body }), 200);
    expectAnswer(await r.fetch(), 200);
    expectAnswer(await call(r.ops, { headers: { Authorization: `bearer ${r.gateway}` } }), 200);
  } finally {
    await r.close();
  }
});

test('serves a request that expects something other than 100-continue as usual, with a JSON answer and a log line', async () => {
  /** @type {string[]} */
  const lines = [];
  const r = await relay({ log: (line) => lines.push(line) });
  try {
    const request = (/** @type {string} */ head, body = '') => raw(r.port, `${head}Host: relay\r\nExpect: something-else\r\nConnection: close\r\n`
      + `${body ? `Content-Type: application/json\r\nContent-Length: ${Buffer.byteLength(body)}\r\n` : ''}\r\n${body}`);
    const notFound = await request('GET /v1/nothing HTTP/1.1\r\n');
    assert.match(notFound, /^HTTP\/1\.1 404 /);
    assert.match(notFound, /\r\nContent-Type: application\/json\r\n/);
    assert.match(notFound, /\{"error":"not_found"\}$/);
    assert.match(await request('PUT /v1/ops HTTP/1.1\r\n'), /^HTTP\/1\.1 405 [^]*\{"error":"method_not_allowed"\}$/);
    assert.match(await request(`GET /v1/ops HTTP/1.1\r\nAuthorization: Bearer ${r.source}\r\n`), /^HTTP\/1\.1 401 [^]*\{"error":"unauthorized"\}$/);
    const taken = await request(`POST /v1/ops HTTP/1.1\r\nAuthorization: Bearer ${r.source}\r\n`, JSON.stringify({ ops: [pin('a')] }));
    assert.match(taken, /^HTTP\/1\.1 200 /);
    assert.match(taken, /\{"accepted":1,"last_seq":1\}$/);
  } finally {
    await r.close();
  }
  assert.equal(lines.length, 4, lines.join('\n'));
});

test('takes JSON only, with or without charset=utf-8', async () => {
  const r = await relay();
  try {
    const body = { ops: [pin('a')] };
    for (const type of ['text/plain', 'application/x-www-form-urlencoded', 'application/json; charset=latin1', 'application/jsonx', 'application/json; x=1']) {
      assert.equal(expectAnswer(await r.post(body, type), 415, type).error, 'unsupported_media_type', type);
    }
    assert.equal(expectAnswer(await call(r.ops, { method: 'POST', token: r.source, body, type: null }), 415).error, 'unsupported_media_type');
    for (const type of ['application/json', 'application/json; charset=utf-8', 'Application/JSON;charset=UTF-8']) {
      expectAnswer(await r.post(body, type), 200, type);
    }
  } finally {
    await r.close();
  }
});

test('refuses a body announced as larger than 2 MiB without reading it, and takes one of exactly 2 MiB', async () => {
  const r = await relay();
  try {
    const head = (/** @type {number} */ length) => `POST /v1/ops HTTP/1.1\r\nHost: relay\r\nAuthorization: Bearer ${r.source}\r\n`
      + `Content-Type: application/json\r\nContent-Length: ${length}\r\n\r\n`;
    // Nothing of the body is sent, so an answer can only come from the announced length.
    const answer = await raw(r.port, head(2 * 1024 * 1024 + 1));
    assert.match(answer, /^HTTP\/1\.1 413 /);
    assert.match(answer, /\{"error":"too_large"\}$/);
    const expectsContinue = await raw(r.port, head(10 * 1024 * 1024).replace('\r\n\r\n', '\r\nExpect: 100-continue\r\n\r\n'));
    assert.match(expectsContinue, /^HTTP\/1\.1 413 /, 'no 100 Continue is sent for a body that would be refused');
    // A body of exactly 2 MiB is read: white space after the object fills it up.
    const json = JSON.stringify({ ops: [pin('a')] });
    const exact = Buffer.alloc(2 * 1024 * 1024, 0x20);
    exact.write(json);
    expectAnswer(await r.post(exact), 200);
    // A body without Content-Length is read up to the limit.
    const chunked = await raw(r.port, `POST /v1/ops HTTP/1.1\r\nHost: relay\r\nAuthorization: Bearer ${r.source}\r\n`
      + `Content-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n${json.length.toString(16)}\r\n${json}\r\n0\r\n\r\n`);
    assert.match(chunked, /^HTTP\/1\.1 200 /);
    assert.match(chunked, /\{"accepted":1,"last_seq":2\}$/);
  } finally {
    await r.close();
  }
});

test('refuses headers that are too large, and closes a connection whose headers never end', async () => {
  const r = await relay();
  try {
    const large = await raw(r.port, `GET /v1/health HTTP/1.1\r\nHost: relay\r\nX-Filler: ${'a'.repeat(16 * 1024)}\r\n\r\n`);
    assert.match(large, /^HTTP\/1\.1 431 /);
    assert.match(large, /\r\nContent-Type: application\/json\r\n/);
    // A request that starts and never finishes its headers is cut off after about ten seconds.
    const started = Date.now();
    const slow = await new Promise((resolve, reject) => {
      const socket = connect(r.port, '127.0.0.1');
      let text = '';
      socket.on('data', (chunk) => {
        text += chunk.toString('latin1');
      });
      socket.on('close', () => resolve(text));
      socket.on('error', reject);
      socket.write('GET /v1/health HTTP/1.1\r\nHost: relay\r\n');
      setTimeout(() => socket.destroy(), 30_000);
    });
    const seconds = (Date.now() - started) / 1000;
    assert.ok(seconds >= 9 && seconds < 20, `cut off after ${seconds} s`);
    // Node.js answers 408 here; what matters is that the connection is closed.
    assert.ok(slow === '' || /^HTTP\/1\.1 408 /.test(String(slow)), String(slow));
  } finally {
    await r.close();
  }
});

test('answers health without a token', async () => {
  const r = await relay();
  try {
    assert.deepEqual(expectAnswer(await call(`${r.url}/v1/health`), 200), { ok: true });
    assert.deepEqual(expectAnswer(await call(`${r.url}/v1/health?probe=1`), 200), { ok: true });
  } finally {
    await r.close();
  }
});

test('logs one line per request, with no token, query or operation in it', async () => {
  /** @type {string[]} */
  const lines = [];
  const r = await relay({ log: (line) => lines.push(line) });
  try {
    expectAnswer(await r.post({ ops: [{ op: 'note', id: 'secret-id', lat: 12.345678, lon: 98.765432, text: 'secret text' }] }), 200);
    const { epoch } = expectAnswer(await r.fetch(), 200);
    expectAnswer(await r.fetch(`epoch=${epoch}&after=1`), 200);
    expectAnswer(await call(`${r.url}/secret-path`), 404);
    expectAnswer(await call(r.ops, { token: token() }), 401);
  } finally {
    await r.close();
  }
  assert.equal(lines.length, 5, lines.join('\n'));
  const all = lines.join('\n');
  for (const secret of ['secret', '12.34', '98.76', 'epoch', 'after']) {
    assert.ok(!all.includes(secret), `${secret} is not logged: ${all}`);
  }
  assert.match(lines[0], / POST \/v1\/ops 200 \d+ms$/);
  assert.match(lines[3], / GET \(other\) 404 \d+ms$/);
});

test('reads its tokens from the environment, and refuses ones that break the rules', () => {
  const a = token();
  const b = token();
  const g = token();
  assert.deepEqual(tokensFrom({ ERDIGO_RELAY_SOURCE_TOKENS: `${a}, ${b}`, ERDIGO_RELAY_GATEWAY_TOKEN: g }), { sourceTokens: [a, b], gatewayToken: g });
  const allowed = 'Az09-_.~'.repeat(4);
  assert.deepEqual(tokensFrom({ ERDIGO_RELAY_SOURCE_TOKENS: allowed, ERDIGO_RELAY_GATEWAY_TOKEN: 'x'.repeat(256) }).sourceTokens, [allowed]);
  for (const env of [
    {},
    { ERDIGO_RELAY_GATEWAY_TOKEN: g },
    { ERDIGO_RELAY_SOURCE_TOKENS: '', ERDIGO_RELAY_GATEWAY_TOKEN: g },
    { ERDIGO_RELAY_SOURCE_TOKENS: a },
    { ERDIGO_RELAY_SOURCE_TOKENS: a, ERDIGO_RELAY_GATEWAY_TOKEN: a },
    { ERDIGO_RELAY_SOURCE_TOKENS: `${b},${a}`, ERDIGO_RELAY_GATEWAY_TOKEN: a },
    { ERDIGO_RELAY_SOURCE_TOKENS: 'x'.repeat(31), ERDIGO_RELAY_GATEWAY_TOKEN: g },
    { ERDIGO_RELAY_SOURCE_TOKENS: a, ERDIGO_RELAY_GATEWAY_TOKEN: 'x'.repeat(257) },
    { ERDIGO_RELAY_SOURCE_TOKENS: `${a}+`, ERDIGO_RELAY_GATEWAY_TOKEN: g },
    { ERDIGO_RELAY_SOURCE_TOKENS: `${a},,${b}`, ERDIGO_RELAY_GATEWAY_TOKEN: g },
    { ERDIGO_RELAY_SOURCE_TOKENS: a, ERDIGO_RELAY_GATEWAY_TOKEN: `${g}=` },
  ]) {
    assert.throws(() => tokensFrom(env), (error) => error instanceof Error && error.name === 'ConfigError'
      && !error.message.includes(a) && !error.message.includes(g), JSON.stringify(Object.keys(env)));
  }
});

test('listens on this computer only, unless it is behind a proxy', () => {
  assert.deepEqual(parseArguments([]), { host: '127.0.0.1', port: 8080, keepHours: 24 });
  assert.deepEqual(parseArguments(['--listen', '[::1]:0', '--keep-hours', '168']), { host: '::1', port: 0, keepHours: 168 });
  assert.deepEqual(parseArguments(['--listen', 'localhost:9000', '--keep-hours', '1']), { host: 'localhost', port: 9000, keepHours: 1 });
  assert.deepEqual(parseArguments(['--listen', '0.0.0.0:8080', '--behind-proxy']), { host: '0.0.0.0', port: 8080, keepHours: 24 });
  for (const args of [
    ['--listen', '0.0.0.0:8080'], ['--listen', '[::]:8080'], ['--listen', '192.168.1.10:8080'], ['--listen', 'example.com:80'],
    ['--listen', '127.0.0.1'], ['--listen', '::1:8080'], ['--listen', '127.0.0.1:65536'], ['--listen', ':8080'],
    ['--keep-hours', '0'], ['--keep-hours', '169'], ['--keep-hours', '1.5'], ['--keep-hours', 'x'], ['--keep-hours'],
    ['--listen', '127.0.0.1:1', '--listen', '127.0.0.1:2'], ['--port', '1'], ['extra'],
  ]) {
    assert.throws(() => parseArguments(args), (error) => error instanceof Error && error.name === 'ConfigError', args.join(' '));
  }
});

test('a queue on its own numbers from 1 and never answers past the limit', () => {
  const queue = new RelayQueue({ keepMs: 3_600_000, limits: DEFAULT_LIMITS, now: () => 0 });
  assert.match(queue.epoch, /^[A-Za-z0-9_-]{16,64}$/);
  assert.deepEqual(queue.add(['{"op":"delete","id":"a"}']), { accepted: 1, last_seq: 1 });
  const answer = JSON.parse(queue.fetch({ epoch: null, after: 0, limit: 1 }).json);
  assert.deepEqual(answer.ops, [{ seq: 1, received_ms: 0, op: { op: 'delete', id: 'a' } }]);
});

const environment = () => ({ ERDIGO_RELAY_SOURCE_TOKENS: token(), ERDIGO_RELAY_GATEWAY_TOKEN: token() });

test('the command line prints where it listens, serves, and stops on SIGTERM', async () => {
  const started = await startProgram(RELAY, ['--listen', '127.0.0.1:0'], /^Relay listening on (http:\/\/127\.0\.0\.1:(\d+))$/m, environment());
  try {
    assert.deepEqual(expectAnswer(await call(`${started.match[1]}/v1/health`), 200), { ok: true });
    for (let tries = 0; tries < 200 && !/GET \/v1\/health 200/.test(started.state.stdout); tries += 1) {
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    assert.match(started.state.stdout, /^\S+ GET \/v1\/health 200 \d+ms$/m);
  } finally {
    started.child.kill('SIGTERM');
  }
  const { code, signal } = /** @type {{ code: number | null, signal: string | null }} */ (await started.exited);
  if (process.platform !== 'win32') {
    // Windows has no SIGTERM to catch: kill() ends the process there.
    assert.equal(code, 0, `${signal} ${started.state.stderr}`);
  }
});

test('the command line refuses tokens and addresses that break the rules, with exit status 1', async () => {
  const good = environment();
  const cases_ = [
    { args: ['--listen', '127.0.0.1:0'], env: {} },
    { args: ['--listen', '127.0.0.1:0'], env: { ...good, ERDIGO_RELAY_GATEWAY_TOKEN: good.ERDIGO_RELAY_SOURCE_TOKENS } },
    { args: ['--listen', '127.0.0.1:0'], env: { ...good, ERDIGO_RELAY_SOURCE_TOKENS: 'short' } },
    { args: ['--listen', '0.0.0.0:0'], env: good },
    { args: ['--keep-hours', '200', '--listen', '127.0.0.1:0'], env: good },
    { args: ['--token', 'x'], env: good },
  ];
  for (const { args, env } of cases_) {
    const result = await run(RELAY, args, { env, timeoutMs: 20_000 });
    assert.equal(result.code, 1, `${args.join(' ')}: ${result.stdout}${result.stderr}`);
    assert.match(result.stderr, /^relay: /);
    for (const value of Object.values(env)) {
      assert.ok(!result.stderr.includes(String(value)), 'no token is printed');
    }
  }
});

test('the command line refuses a port in use, with exit status 1', async () => {
  const blocker = createServer();
  await new Promise((resolve) => blocker.listen(0, '127.0.0.1', () => resolve(undefined)));
  try {
    const port = /** @type {import('node:net').AddressInfo} */ (blocker.address()).port;
    const result = await run(RELAY, ['--listen', `127.0.0.1:${port}`], { env: environment(), timeoutMs: 20_000 });
    assert.equal(result.code, 1, result.stderr);
  } finally {
    blocker.close();
  }
});

/** Whether this machine can listen on the IPv6 loopback address. */
async function hasIpv6Loopback() {
  const probe = createServer();
  try {
    await new Promise((resolve, reject) => {
      probe.once('error', reject);
      probe.listen(0, '::1', () => resolve(undefined));
    });
    return true;
  } catch {
    return false;
  } finally {
    probe.close();
  }
}

test('the command line writes an IPv6 address in brackets', { skip: !(await hasIpv6Loopback()) && 'no IPv6 loopback here' }, async () => {
  const started = await startProgram(RELAY, ['--listen', '[::1]:0'], /^Relay listening on (http:\/\/\[::1\]:(\d+))$/m, environment());
  try {
    assert.deepEqual(expectAnswer(await call(`${started.match[1]}/v1/health`), 200), { ok: true });
  } finally {
    started.child.kill('SIGTERM');
    await started.exited;
  }
});
