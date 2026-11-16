// The cloud gateway example (examples/cloud-relay/gateway.js) end to end: a relay in this process, the test receiver
// of tests/support, and the gateway run as a program, as a person would run it. tests/cloud-gateway-mock.test.js does
// the same against another edition's mock receiver.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { chmodSync, existsSync, mkdtempSync, readFileSync, realpathSync, rmSync, statSync, symlinkSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { connect, createServer as createTcpServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, describe, test } from 'node:test';
import { DEFAULT_LIMITS, startRelay } from '../examples/cloud-relay/lib/relay.js';
import { GATEWAY, call, run, startProgram, token } from './support/cloud-relay.js';
import { errorAnswer, TestReceiver } from './support/receiver.js';
import { loadJson } from './support/vectors.js';

const cases = loadJson('docs/cloud-relay-cases.json');
const PNG_1X1 = cases.valid_ops.find((/** @type {any} */ sample) => sample.name === 'photo, a 1x1 PNG').op.data;
const isWindows = process.platform === 'win32';

// The long name of the temporary folder, because on Windows a code file must be inside the user's profile folder,
// and the short name of the folder would not look like it is.
const work = mkdtempSync(join(realpathSync.native(tmpdir()), 'erdigo-gateway-'));
after(() => rmSync(work, { recursive: true, force: true }));
let folders = 0;

/** A new folder for one gateway's state. */
function stateDir() {
  folders += 1;
  return join(work, `state-${folders}`);
}

/** Every kind of operation, in an order the app accepts. */
const EVERY_KIND = [
  { op: 'pin', id: 'p1', lat: 37.5663, lon: 126.9779, title: 'Seoul City Hall' },
  { op: 'marker', id: 'm1', lat: 1.5, lon: 2.5, title: 'Gate', color: 'red' },
  { op: 'note', id: 'n1', lat: 0, lon: 0, text: 'line one\nline\ttwo' },
  { op: 'photo', id: 'ph1', lat: 0, lon: 0, type: 'image/png', data: PNG_1X1, title: 'View' },
  { op: 'track', id: 't1', title: 'Flight 7', points: [{ lat: 37.5663, lon: 126.9779, ts: 1_700_000_000_000, alt: 120 }, { lat: 37.567, lon: 126.978 }] },
  { op: 'finish_track', id: 't1' },
  { op: 'move', id: 'p1', lat: 37.5, lon: 127 },
  { op: 'pin', id: 'gone', lat: 0, lon: 0 },
  { op: 'delete', id: 'gone' },
];
const EVERY_KIND_REQUESTS = [
  'put:pin', 'put:marker', 'put:note', 'photo_begin', 'photo_chunk', 'photo_end', 'track_append', 'track_finish', 'move', 'put:pin', 'delete',
];

/** @param {TestReceiver} receiver */
const requestsOf = (receiver) => receiver.requests.map((envelope) => {
  const request = /** @type {any} */ (envelope.request);
  return request.kind ? `${request.t}:${request.kind}` : request.t;
});

/**
 * A relay in this process.
 * @param {{ port?: number, now?: () => number, keepHours?: number, tokens?: { source: string, gateway: string } }} [options]
 */
async function relay({ port = 0, now, keepHours, tokens = { source: token(), gateway: token() } } = {}) {
  const { source, gateway } = tokens;
  const started = await startRelay({ host: '127.0.0.1', port, sourceTokens: [source], gatewayToken: gateway, limits: DEFAULT_LIMITS, now, keepHours });
  return {
    ...started,
    source,
    gateway,
    /** @param {object[]} ops */
    async post(ops) {
      const answer = await call(`${started.url}/v1/ops`, { method: 'POST', token: source, body: { ops } });
      assert.equal(answer.status, 200, answer.text);
      return answer.json;
    },
  };
}

/**
 * Pairs a gateway with the test receiver through a code file holding the QR text.
 * @param {TestReceiver} receiver
 * @param {string[]} [extra]
 */
async function pairGateway(receiver, extra = []) {
  const state = stateDir();
  const codeFile = join(work, `code-${folders}`);
  writeFileSync(codeFile, `${receiver.qrText}\n`, { mode: 0o600 });
  const result = await run(GATEWAY, ['pair', '--name', 'Cloud gateway', '--host', '127.0.0.1', '--port', String(receiver.port),
    '--code-file', codeFile, '--state-dir', state, ...extra]);
  assert.equal(result.code, 0, result.stdout + result.stderr);
  assert.match(result.stdout, /^Paired\. Allowed: /m);
  return state;
}

/**
 * Runs `gateway run`.
 * @param {string} state
 * @param {string} url the relay
 * @param {string | undefined} gatewayToken
 * @param {TestReceiver | number} receiver or its port
 * @param {string[]} [extra]
 */
function runGateway(state, url, gatewayToken, receiver, extra = ['--once']) {
  const port = typeof receiver === 'number' ? receiver : receiver.port;
  return run(GATEWAY, ['run', '--relay', url, '--host', '127.0.0.1', '--port', String(port), '--state-dir', state, ...extra], {
    env: { ERDIGO_RELAY_TOKEN: gatewayToken },
  });
}

/** @param {string} state */
const placeOf = (state) => JSON.parse(readFileSync(join(state, 'relay-state.json'), 'utf8'));

const GAVE_UP_ON_APP = 'The Erdigo app could not be reached. The operations stay on the relay.\n';

/**
 * A TCP proxy in front of the test receiver that loses the receiver's answer to the first request on every connection,
 * its third frame after the handshake reply and the welcome. With 'drop' it cuts the connection there, as when every
 * answer is lost on the way back. With 'hold' it keeps the connection open and passes nothing more on, as an app that
 * takes the connection and never answers.
 * @param {number} receiverPort
 * @param {'drop' | 'hold'} mode
 */
async function losingProxy(receiverPort, mode) {
  /** @type {Set<import('node:net').Socket>} */
  const sockets = new Set();
  let lost = 0;
  const server = createTcpServer((device) => {
    const receiver = connect(receiverPort, '127.0.0.1');
    const end = () => {
      device.destroy();
      receiver.destroy();
    };
    for (const socket of [device, receiver]) {
      sockets.add(socket);
      socket.on('close', () => {
        sockets.delete(socket);
        end();
      });
      socket.on('error', end);
    }
    device.on('data', (chunk) => receiver.write(chunk));
    let pending = Buffer.alloc(0);
    let frames = 0;
    receiver.on('data', (chunk) => {
      pending = Buffer.concat([pending, chunk]);
      while (frames < 3 && pending.length >= 2 && pending.length >= 2 + pending.readUInt16BE(0)) {
        const size = 2 + pending.readUInt16BE(0);
        const frame = pending.subarray(0, size);
        pending = pending.subarray(size);
        frames += 1;
        if (frames === 3) {
          lost += 1;
          if (mode === 'drop') {
            end();
          }
          return;
        }
        device.write(frame);
      }
    });
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
  return {
    port: /** @type {import('node:net').AddressInfo} */ (server.address()).port,
    get lost() {
      return lost;
    },
    async close() {
      for (const socket of sockets) {
        socket.destroy();
      }
      await new Promise((resolve) => server.close(() => resolve(undefined)));
    },
  };
}

/** @param {TestReceiver} receiver */
const appliedOf = (receiver) => receiver.events.filter((event) => event.type === 'applied').map((event) => event.request);

test('pairs through a code file, writes every kind of operation, and applies nothing twice', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    assert.deepEqual(await r.post(EVERY_KIND), { accepted: 9, last_seq: 9 });
    const first = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(first.code, 0, first.stdout + first.stderr);
    assert.equal(first.stdout, 'Done with 9 operations, up to 9\n');
    assert.equal(first.stderr, '');
    assert.deepEqual(requestsOf(receiver), EVERY_KIND_REQUESTS);
    const track = /** @type {any} */ (receiver.requests[6].request);
    assert.equal(track.title, 'Flight 7');
    assert.deepEqual(track.points, EVERY_KIND[4].points);
    const photo = /** @type {any} */ (receiver.requests[4].request);
    assert.equal(Buffer.from(photo.data).toString('base64'), PNG_1X1);
    assert.deepEqual(placeOf(state), { relay: r.url, epoch: r.queue.epoch, after: 9, missed_reported: false });
    if (!isWindows) {
      assert.equal(statSync(join(state, 'relay-state.json')).mode & 0o777, 0o600);
    }
    // The relay forgot them once the gateway said it was done.
    assert.equal(r.queue.count, 0);

    const second = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(second.code, 0, second.stderr);
    assert.equal(second.stdout, '');
    assert.equal(receiver.requests.length, EVERY_KIND_REQUESTS.length, 'nothing was sent again');

    // What comes later is applied after it.
    await r.post([{ op: 'delete', id: 'p1' }]);
    const third = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(third.stdout, 'Done with 1 operation, up to 10\n');
    assert.deepEqual(requestsOf(receiver).slice(EVERY_KIND_REQUESTS.length), ['delete']);
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('prints what the app refuses, and goes on', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver, ['--kinds', 'pin,track']);
    receiver.answerOverride = (envelope) => (envelope.request.t === 'move'
      ? errorAnswer({ code: 'not_found', seq: envelope.seq })
      : null);
    await r.post([
      { op: 'marker', id: 'm1', lat: 0, lon: 0 },
      { op: 'pin', id: 'p1', lat: 0, lon: 0 },
      { op: 'move', id: 'missing', lat: 1, lon: 1 },
      { op: 'note', id: 'n1', lat: 0, lon: 0, text: 'hello' },
      { op: 'pin', id: 'p2', lat: 0, lon: 0 },
    ]);
    const result = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.stdout, [
      'Refused operation 1 (marker): not_permitted',
      'Refused operation 3 (move): not_found',
      'Refused operation 4 (note): not_permitted',
      'Done with 5 operations, up to 5 (3 refused)',
      '',
    ].join('\n'));
    assert.deepEqual(requestsOf(receiver), ['put:pin', 'move', 'put:pin']);
    assert.equal(placeOf(state).after, 5);
    // Nothing comes again.
    const again = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(again.stdout, '');
    assert.equal(receiver.requests.length, 3);
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('saves its place after each operation, so a run stopped in the middle of a batch does nothing twice', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    // Another program taking over the gateway's key stops it at the third operation.
    receiver.answerOverride = (envelope) => (/** @type {any} */ (envelope.request).id === 'p3'
      ? errorAnswer({ code: 'replaced', fatal: true })
      : null);
    await r.post(['p1', 'p2', 'p3', 'p4'].map((id) => ({ op: 'pin', id, lat: 0, lon: 0 })));
    const stopped = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(stopped.code, 1, stopped.stdout + stopped.stderr);
    assert.match(stopped.stderr, /^gateway: Another connection using this device key took over\.$/m);
    assert.equal(placeOf(state).after, 2);
    receiver.answerOverride = null;
    const resumed = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(resumed.code, 0, resumed.stderr);
    assert.equal(resumed.stdout, 'Done with 2 operations, up to 4\n');
    assert.deepEqual(receiver.requests.map((envelope) => /** @type {any} */ (envelope.request).id), ['p1', 'p2', 'p3', 'p3', 'p4']);
  } finally {
    await r.close();
    await receiver.stop();
  }
});

// These take 30 to 50 seconds each, so they run side by side.
describe('with --once, gives up after about 30 seconds of trouble, and only then', { concurrency: true }, () => {
  test('with the app down: status 3, and the operations stay on the relay', async () => {
    const receiver = new TestReceiver();
    await receiver.start();
    const r = await relay();
    try {
      const state = await pairGateway(receiver);
      await r.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
      const first = await runGateway(state, r.url, r.gateway, receiver);
      assert.equal(first.code, 0, first.stderr);
      await r.post([{ op: 'pin', id: 'p2', lat: 0, lon: 0 }, { op: 'pin', id: 'p3', lat: 0, lon: 0 }]);
      await receiver.stop();
      const started = Date.now();
      const result = await runGateway(state, r.url, r.gateway, receiver);
      const seconds = (Date.now() - started) / 1000;
      assert.equal(result.code, 3, result.stdout + result.stderr);
      assert.ok(seconds > 25 && seconds < 45, `gave up after ${seconds} s`);
      // Said once when the app could not be reached for two seconds, and next only after a minute.
      assert.equal(result.stdout, 'No answer from the Erdigo app; trying again.\nThe Erdigo app could not be reached. The operations stay on the relay.\n');
      // Both are still on the relay, and the gateway's place did not move.
      assert.equal(r.queue.count, 2);
      assert.equal(placeOf(state).after, 1);
    } finally {
      await r.close();
    }
  });

  test('with a relay that takes the request and never answers: status 3', async () => {
    const receiver = new TestReceiver();
    await receiver.start();
    /** @type {Set<import('node:http').ServerResponse>} */
    const held = new Set();
    const silent = createServer((_request, response) => {
      held.add(response);
    });
    await new Promise((resolve) => silent.listen(0, '127.0.0.1', () => resolve(undefined)));
    const url = `http://127.0.0.1:${/** @type {import('node:net').AddressInfo} */ (silent.address()).port}`;
    try {
      const state = await pairGateway(receiver);
      const started = Date.now();
      const result = await runGateway(state, url, token(), receiver);
      const seconds = (Date.now() - started) / 1000;
      assert.equal(result.code, 3, result.stdout + result.stderr);
      assert.ok(seconds > 25 && seconds < 45, `gave up after ${seconds} s`);
      const lines = result.stdout.trim().split('\n');
      assert.equal(lines[0], 'The relay could not be reached; trying again in 1 second.');
      assert.ok(lines.slice(0, -1).every((line) => /^The relay could not be reached; trying again in \d+ seconds?\.$/.test(line)), result.stdout);
      assert.equal(lines.at(-1), 'The relay could not be reached.');
      assert.ok(held.size >= 2, `asked ${held.size} times`);
    } finally {
      silent.closeAllConnections();
      await new Promise((resolve) => silent.close(() => resolve(undefined)));
      await receiver.stop();
    }
  });

  // The SDK counts its 30 seconds from the last connection it made, so here it alone would try for ever.
  test('with every answer from the app lost on the way: status 3, the operation applied once and still on the relay', async () => {
    const receiver = new TestReceiver();
    await receiver.start();
    const r = await relay();
    const proxy = await losingProxy(receiver.port, 'drop');
    try {
      const state = await pairGateway(receiver);
      await r.post([{ op: 'track', id: 't1', points: [{ lat: 1, lon: 2 }, { lat: 1.5, lon: 2.5 }] }]);
      const started = Date.now();
      const result = await runGateway(state, r.url, r.gateway, proxy.port);
      const seconds = (Date.now() - started) / 1000;
      assert.equal(result.code, 3, result.stdout + result.stderr);
      assert.ok(seconds > 25 && seconds < 45, `gave up after ${seconds} s`);
      assert.equal(result.stdout, `No answer from the Erdigo app; trying again.\n${GAVE_UP_ON_APP}`);
      assert.ok(proxy.lost >= 5, `lost ${proxy.lost} answers`);
      // Sent again and again with the same number, and applied once.
      const appends = receiver.requests.filter((envelope) => envelope.request.t === 'track_append');
      assert.equal(appends.length, proxy.lost);
      assert.ok(appends.every((envelope) => envelope.seq === appends[0].seq));
      assert.deepEqual(appliedOf(receiver), ['track_append']);
      assert.equal(r.queue.count, 1);
      assert.equal(placeOf(state).after, 0);
    } finally {
      await proxy.close();
      await r.close();
      await receiver.stop();
    }
  });

  test('with an app that takes the connection and never answers: status 3 once the SDK has stopped waiting and 30 seconds more passed', async () => {
    const receiver = new TestReceiver();
    await receiver.start();
    const r = await relay();
    const proxy = await losingProxy(receiver.port, 'hold');
    try {
      const state = await pairGateway(receiver);
      await r.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
      const started = Date.now();
      const result = await runGateway(state, r.url, r.gateway, proxy.port);
      const seconds = (Date.now() - started) / 1000;
      assert.equal(result.code, 3, result.stdout + result.stderr);
      // The SDK waits 15 seconds for an answer before it counts the connection as lost.
      assert.ok(seconds > 40 && seconds < 60, `gave up after ${seconds} s`);
      assert.equal(result.stdout, `No answer from the Erdigo app; trying again.\n${GAVE_UP_ON_APP}`);
      assert.ok(proxy.lost >= 2, `held ${proxy.lost} answers`);
      assert.deepEqual(appliedOf(receiver), ['put']);
      assert.equal(r.queue.count, 1);
    } finally {
      await proxy.close();
      await r.close();
      await receiver.stop();
    }
  });

  // The app allows 100 points at once and then 1 a second, so the SDK waits about 35 seconds before it sends the
  // second track. That is not trouble, and neither is it once an answer is lost afterwards.
  test('a slow track is not given up: time spent pacing points is not counted', async () => {
    const receiver = new TestReceiver();
    await receiver.start();
    const r = await relay();
    try {
      const state = await pairGateway(receiver);
      const points = (/** @type {number} */ count) => Array.from({ length: count }, (_, index) => ({ lat: index / 1000, lon: 0 }));
      let dropped = false;
      receiver.dropBeforeAnswer = (envelope) => {
        if (dropped || envelope.request.id !== 'slow') {
          return false;
        }
        dropped = true;
        return true;
      };
      await r.post([{ op: 'track', id: 'fast', points: points(100) }, { op: 'track', id: 'slow', points: points(35) }]);
      const started = Date.now();
      const result = await runGateway(state, r.url, r.gateway, receiver);
      const seconds = (Date.now() - started) / 1000;
      assert.equal(result.code, 0, result.stdout + result.stderr);
      assert.ok(seconds > 33, `done after ${seconds} s`);
      assert.match(result.stdout, /^Done with 2 operations, up to 2$/m);
      assert.ok(dropped);
      assert.deepEqual(appliedOf(receiver), ['track_append', 'track_append']);
      assert.equal(r.queue.count, 0);
    } finally {
      await r.close();
      await receiver.stop();
    }
  });
});

test('only one run at a time uses a state folder, and a lock left by a process that has ended is taken over', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    const lock = join(state, '.gateway.lock');
    await r.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
    const first = await startProgram(GATEWAY, ['run', '--relay', r.url, '--host', '127.0.0.1', '--port', String(receiver.port), '--state-dir', state],
      /^Done with 1 operation, up to 1$/m, { ERDIGO_RELAY_TOKEN: r.gateway });
    try {
      assert.equal(readFileSync(lock, 'latin1'), `${first.child.pid}\n`);
      if (!isWindows) {
        assert.equal(statSync(lock).mode & 0o777, 0o600);
      }
      for (const extra of [['--once'], []]) {
        const second = await runGateway(state, r.url, r.gateway, receiver, extra);
        assert.equal(second.code, 1, second.stdout + second.stderr);
        assert.equal(second.stdout, '');
        assert.equal(second.stderr, `gateway: another gateway (process ${first.child.pid}) is already running with this profile\n`);
      }
    } finally {
      first.child.kill('SIGTERM');
    }
    const { code } = /** @type {{ code: number | null }} */ (await first.exited);
    if (!isWindows) {
      // On Windows the signal ends the process at once.
      assert.equal(code, 0, first.state.stderr);
      assert.ok(!existsSync(lock), 'the lock is removed when the run ends');
    }

    // A process that has ended, such as a gateway that was killed, leaves a lock that the next run takes over.
    const ended = spawn(process.execPath, ['-e', '']);
    await new Promise((resolve) => ended.on('exit', resolve));
    writeFileSync(lock, `${ended.pid}\n`, { mode: 0o600 });
    await r.post([{ op: 'pin', id: 'p2', lat: 0, lon: 0 }]);
    const resumed = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(resumed.code, 0, resumed.stdout + resumed.stderr);
    assert.equal(resumed.stdout, 'Done with 1 operation, up to 2\n');
    assert.ok(!existsSync(lock));

    // A running process holds it, whatever it is.
    writeFileSync(lock, `${process.pid}\n`, { mode: 0o600 });
    const held = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(held.code, 1, held.stdout + held.stderr);
    assert.match(held.stderr, new RegExp(`^gateway: another gateway \\(process ${process.pid}\\)`));
    rmSync(lock);

    if (!isWindows) {
      // A link is not followed, and a lock that someone else could replace is not trusted.
      const elsewhere = join(work, 'elsewhere.lock');
      writeFileSync(elsewhere, `${ended.pid}\n`, { mode: 0o600 });
      symlinkSync(elsewhere, lock);
      const linked = await runGateway(state, r.url, r.gateway, receiver);
      assert.equal(linked.code, 1, linked.stdout + linked.stderr);
      assert.equal(linked.stderr, 'gateway: .gateway.lock must be a regular file, not a link, so it was not used. Remove it from the state folder if no gateway is running.\n');
      assert.equal(readFileSync(elsewhere, 'latin1'), `${ended.pid}\n`);
      rmSync(lock);
      writeFileSync(lock, `${ended.pid}\n`, { mode: 0o600 });
      chmodSync(lock, 0o622);
      const open = await runGateway(state, r.url, r.gateway, receiver);
      assert.equal(open.code, 1, open.stdout + open.stderr);
      assert.match(open.stderr, /^gateway: \.gateway\.lock must be a regular file owned by you with mode 0600, so it was not used\./);
      rmSync(lock);
    }
    assert.equal(receiver.requests.length, 2, 'only the two runs that held the lock wrote to the app');
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('sends a request whose answer was lost again with the same number, so the app applies it once', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    // The app applies the track's points, and the connection ends before its answer. The next two connections are
    // dropped too, so that the gateway is without the app for a few seconds.
    let dropped = false;
    receiver.dropBeforeAnswer = (envelope) => {
      if (dropped || envelope.request.t !== 'track_append') {
        return false;
      }
      dropped = true;
      receiver.silentConnections = 2;
      return true;
    };
    await r.post([
      { op: 'track', id: 't1', title: 'Flight 7', points: [{ lat: 37.5663, lon: 126.9779 }, { lat: 37.567, lon: 126.978 }] },
      { op: 'pin', id: 'p1', lat: 0, lon: 0 },
    ]);
    const result = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(result.code, 0, result.stdout + result.stderr);
    assert.equal(result.stdout, 'No answer from the Erdigo app; trying again.\nDone with 2 operations, up to 2\n');
    const appends = receiver.requests.filter((envelope) => envelope.request.t === 'track_append');
    assert.equal(appends.length, 2, 'sent twice');
    assert.equal(appends[0].seq, appends[1].seq, 'with the same number');
    assert.deepEqual(receiver.events.filter((event) => event.type === 'applied').map((event) => event.request), ['track_append', 'put'],
      'and applied once');
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('starts from the oldest operation of a relay that restarted', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const first = await relay();
  /** @type {Awaited<ReturnType<typeof relay>> | null} */
  let second = null;
  try {
    const state = await pairGateway(receiver);
    await first.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }, { op: 'pin', id: 'p2', lat: 0, lon: 0 }]);
    assert.equal((await runGateway(state, first.url, first.gateway, receiver)).code, 0);
    assert.equal(placeOf(state).after, 2);
    // The same address and tokens, a new process: a new epoch, and numbers from 1 again.
    await first.close();
    second = await relay({ port: first.port, tokens: { source: first.source, gateway: first.gateway } });
    assert.equal(second.url, first.url);
    await second.post([{ op: 'pin', id: 'p3', lat: 0, lon: 0 }]);
    const result = await runGateway(state, second.url, second.gateway, receiver);
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.stdout, 'The relay started a new numbering; starting from its oldest operation.\nDone with 1 operation, up to 1\n');
    assert.deepEqual(placeOf(state), { relay: second.url, epoch: second.queue.epoch, after: 1, missed_reported: false });
    assert.deepEqual(receiver.requests.map((envelope) => /** @type {any} */ (envelope.request).id), ['p1', 'p2', 'p3']);
  } finally {
    await first.close();
    await second?.close();
    await receiver.stop();
  }
});

test('starts again from the oldest operation of another relay', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const one = await relay();
  const two = await relay();
  try {
    const state = await pairGateway(receiver);
    await one.post([{ op: 'pin', id: 'a', lat: 0, lon: 0 }]);
    assert.equal((await runGateway(state, one.url, one.gateway, receiver)).code, 0);
    await two.post([{ op: 'pin', id: 'b', lat: 0, lon: 0 }]);
    const result = await runGateway(state, two.url, two.gateway, receiver);
    // It had no place with this relay, so there is nothing to say about a new numbering.
    assert.equal(result.stdout, 'Done with 1 operation, up to 1\n');
    assert.equal(placeOf(state).relay, two.url);
  } finally {
    await one.close();
    await two.close();
    await receiver.stop();
  }
});

test('says when operations expired on the relay before it fetched them', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  let clock = Date.now();
  const r = await relay({ now: () => clock, keepHours: 1 });
  try {
    const state = await pairGateway(receiver);
    await r.post([{ op: 'pin', id: 'old', lat: 0, lon: 0 }]);
    clock += 3_600_000;
    await r.post([{ op: 'pin', id: 'new', lat: 0, lon: 0 }]);
    const result = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(result.stdout, 'Some operations expired on the relay before they were fetched.\nDone with 1 operation, up to 2\n');
    assert.deepEqual(receiver.requests.map((envelope) => /** @type {any} */ (envelope.request).id), ['new']);

    // The relay goes on saying so until the gateway's place passes what expired, and the gateway says it once.
    await r.post([{ op: 'pin', id: 'lost-1', lat: 0, lon: 0 }, { op: 'pin', id: 'lost-2', lat: 0, lon: 0 }]);
    clock += 2 * 3_600_000;
    const noted = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(noted.stdout, 'Some operations expired on the relay before they were fetched.\n');
    assert.equal(placeOf(state).missed_reported, true);
    const quiet = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(quiet.code, 0, quiet.stderr);
    assert.equal(quiet.stdout, '');
    await r.post([{ op: 'pin', id: 'later', lat: 0, lon: 0 }]);
    const moved = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(moved.stdout, 'Done with 1 operation, up to 5\n');
    assert.deepEqual(placeOf(state), { relay: r.url, epoch: r.queue.epoch, after: 5, missed_reported: false });
    assert.deepEqual(receiver.requests.map((envelope) => /** @type {any} */ (envelope.request).id), ['new', 'later']);
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('says once that operations expired while it runs, and does not ask a relay that answers at once straight away', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const state = await pairGateway(receiver);
  const empty = (/** @type {boolean} */ reset) => () => ({ status: 200, body: JSON.stringify({ epoch: 'EEEEEEEEEEEEEEEE', reset, missed: true, ops: [] }) });
  // Like a relay that cannot hold a fetch open: every answer comes at once.
  const fake = await scriptedRelay([empty(true)], empty(false));
  try {
    const gateway = await startProgram(GATEWAY, ['run', '--relay', fake.url, '--host', '127.0.0.1', '--port', String(receiver.port), '--state-dir', state],
      /^Some operations expired/m, { ERDIGO_RELAY_TOKEN: token() });
    await new Promise((resolve) => setTimeout(resolve, 5000));
    gateway.child.kill();
    await gateway.exited;
    assert.equal(gateway.state.stdout, 'Some operations expired on the relay before they were fetched.\n');
    // About one fetch every two seconds.
    assert.ok(fake.queries.length >= 2 && fake.queries.length <= 5, `asked ${fake.queries.length} times`);
    assert.equal(fake.queries[1].get('wait'), '25');
  } finally {
    await fake.close();
    await receiver.stop();
  }
});

test('reads a place file without missed_reported, and refuses a damaged one', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    await r.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }, { op: 'pin', id: 'p2', lat: 0, lon: 0 }]);
    const place = join(state, 'relay-state.json');
    writeFileSync(place, JSON.stringify({ relay: r.url, epoch: r.queue.epoch, after: 1 }), { mode: 0o600 });
    const result = await runGateway(state, r.url, r.gateway, receiver);
    assert.equal(result.stdout, 'Done with 1 operation, up to 2\n');
    assert.deepEqual(receiver.requests.map((envelope) => /** @type {any} */ (envelope.request).id), ['p2']);
    for (const damaged of [
      { relay: r.url, epoch: r.queue.epoch, after: 2, missed_reported: 'yes' },
      { relay: r.url, epoch: r.queue.epoch, after: 2, other: false },
      { relay: r.url, epoch: r.queue.epoch, missed_reported: false },
    ]) {
      writeFileSync(place, JSON.stringify(damaged), { mode: 0o600 });
      const refused = await runGateway(state, r.url, r.gateway, receiver);
      assert.equal(refused.code, 1, JSON.stringify(damaged));
      assert.match(refused.stderr, /relay-state\.json is damaged/);
    }
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('runs until it is stopped, applying operations as they arrive, and stops on a signal', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    await r.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
    const gateway = await startProgram(GATEWAY, ['run', '--relay', r.url, '--host', '127.0.0.1', '--port', String(receiver.port), '--state-dir', state],
      /^Done with 1 operation, up to 1$/m, { ERDIGO_RELAY_TOKEN: r.gateway });
    try {
      // It is waiting on the relay now, and takes the next one as soon as it arrives.
      await new Promise((resolve) => setTimeout(resolve, 500));
      const sent = Date.now();
      await r.post([{ op: 'pin', id: 'p2', lat: 0, lon: 0 }]);
      for (let tries = 0; tries < 200 && !gateway.state.stdout.includes('up to 2'); tries += 1) {
        await new Promise((resolve) => setTimeout(resolve, 25));
      }
      assert.match(gateway.state.stdout, /^Done with 1 operation, up to 2$/m);
      assert.ok(Date.now() - sent < 5000);
    } finally {
      gateway.child.kill('SIGTERM');
    }
    const { code } = /** @type {{ code: number | null }} */ (await gateway.exited);
    if (!isWindows) {
      assert.equal(code, 0, gateway.state.stderr);
    }
    assert.deepEqual(placeOf(state).after, 2);
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('refuses a plain http relay beyond this computer, a missing or malformed token, and a missing pairing, with status 1', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    const expectOne = async (/** @type {Promise<{ code: number | null, stdout: string, stderr: string }>} */ running, /** @type {RegExp} */ says) => {
      const result = await running;
      assert.equal(result.code, 1, result.stdout + result.stderr);
      assert.match(result.stderr, says);
      assert.ok(!result.stderr.includes(r.gateway) && !result.stdout.includes(r.gateway), 'no token is printed');
    };
    for (const url of ['http://relay.example.com', 'http://192.168.1.10:8080', 'http://127.0.0.2:8080', 'ftp://127.0.0.1', 'relay.example.com']) {
      await expectOne(runGateway(state, url, r.gateway, receiver), /--relay must/);
    }
    await expectOne(runGateway(state, 'https://user:secret@relay.example.com', r.gateway, receiver), /cannot hold/);
    await expectOne(runGateway(state, `${r.url}/?x=1`, r.gateway, receiver), /cannot hold/);
    await expectOne(runGateway(state, r.url, undefined, receiver), /ERDIGO_RELAY_TOKEN/);
    await expectOne(runGateway(state, r.url, '', receiver), /ERDIGO_RELAY_TOKEN/);
    await expectOne(runGateway(state, r.url, 'short', receiver), /ERDIGO_RELAY_TOKEN/);
    await expectOne(runGateway(state, r.url, `${r.gateway}\nX-Injected: 1`, receiver), /ERDIGO_RELAY_TOKEN/);
    await expectOne(runGateway(stateDir(), r.url, r.gateway, receiver), /no pairing/);
    await expectOne(run(GATEWAY, ['run', '--relay', r.url, '--token', r.gateway, '--state-dir', state]), /not accepted as an argument/);
    await expectOne(run(GATEWAY, ['run', '--relay', r.url, '--state-dir', state, '--profile', 'other'], { env: { ERDIGO_RELAY_TOKEN: r.gateway } }), /cannot be used together/);
    await expectOne(run(GATEWAY, ['run', '--state-dir', state], { env: { ERDIGO_RELAY_TOKEN: r.gateway } }), /--relay is required/);
    await expectOne(run(GATEWAY, ['run', '--relay', r.url, '--host', '127.0.0.1', '--state-dir', state], { env: { ERDIGO_RELAY_TOKEN: r.gateway } }), /go together/);
    // A profile holds one pairing.
    const codeFile = join(work, 'code-again');
    writeFileSync(codeFile, receiver.qrText, { mode: 0o600 });
    await expectOne(run(GATEWAY, ['pair', '--name', 'Again', '--host', '127.0.0.1', '--port', String(receiver.port), '--code-file', codeFile, '--state-dir', state]),
      /already holds a pairing/);
    await expectOne(run(GATEWAY, ['pair', '--name', 'Code', '--code', 'ABCDE', '--state-dir', stateDir()]), /not accepted as an argument/);
    // Nothing was fetched by any of them.
    await r.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
    assert.equal(r.queue.count, 1);
    assert.equal(receiver.requests.length, 0);
  } finally {
    await r.close();
    await receiver.stop();
  }
});

test('stops with status 2 when the relay refuses the token', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const r = await relay();
  try {
    const state = await pairGateway(receiver);
    await r.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
    for (const wrong of [token(), r.source]) {
      const result = await runGateway(state, r.url, wrong, receiver);
      assert.equal(result.code, 2, result.stdout + result.stderr);
      assert.match(result.stderr, /refused the token/);
      assert.ok(!result.stderr.includes(wrong));
    }
    assert.equal(r.queue.count, 1);
    assert.equal(receiver.requests.length, 0);
  } finally {
    await r.close();
    await receiver.stop();
  }
});

/**
 * A relay of its own that answers each fetch with the next of `answers`, to see what the gateway does with answers a
 * real relay would not give.
 * @param {Array<(query: URLSearchParams) => { status: number, body: string | Buffer, type?: string }>} answers
 * @param {(query: URLSearchParams) => { status: number, body: string | Buffer, type?: string }} [fallback] once they are used up
 */
async function scriptedRelay(answers, fallback = () => ({ status: 200, body: '{"epoch":"EEEEEEEEEEEEEEEE","reset":false,"missed":false,"ops":[]}' })) {
  /** @type {URLSearchParams[]} */
  const queries = [];
  const server = createServer((request, response) => {
    const query = new URL(request.url ?? '/', 'http://relay').searchParams;
    queries.push(query);
    const next = answers.shift() ?? fallback;
    const { status, body, type = 'application/json' } = next(query);
    response.writeHead(status, { 'Content-Type': type });
    response.end(body);
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
  const url = `http://127.0.0.1:${/** @type {import('node:net').AddressInfo} */ (server.address()).port}`;
  return { url, queries, close: () => new Promise((resolve) => server.close(() => resolve(undefined))) };
}

const EPOCH = 'EEEEEEEEEEEEEEEE';
/** @param {object[]} entries */
const answerWith = (entries, { reset = true, epoch = EPOCH } = {}) => () => ({
  status: 200,
  body: JSON.stringify({ epoch, reset, missed: false, ops: entries }),
});
const entry = (/** @type {number} */ seq, /** @type {object} */ op) => ({ seq, received_ms: 1_700_000_000_000, op });

test('stops with status 2 when the relay breaks the API', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const state = await pairGateway(receiver);
  const pin1 = { op: 'pin', id: 'p1', lat: 0, lon: 0 };
  const broken = [
    ['not JSON', () => ({ status: 200, body: 'hello' })],
    ['not labelled JSON', () => ({ status: 200, body: '{}', type: 'text/html' })],
    ['a duplicate key', () => ({ status: 200, body: `{"epoch":"${EPOCH}","reset":true,"reset":true,"missed":false,"ops":[]}` })],
    ['a field too many', () => ({ status: 200, body: `{"epoch":"${EPOCH}","reset":true,"missed":false,"ops":[],"more":1}` })],
    ['a short epoch', answerWith([], { epoch: 'short' })],
    ['no reset without an epoch sent', answerWith([], { reset: false })],
    ['numbers that go down', answerWith([entry(2, pin1), entry(1, pin1)])],
    ['a number twice', answerWith([entry(1, pin1), entry(1, pin1)])],
    ['a number of 0', answerWith([entry(0, pin1)])],
    ['a fractional number', () => ({ status: 200, body: `{"epoch":"${EPOCH}","reset":true,"missed":false,"ops":[{"seq":1.5,"received_ms":0,"op":{}}]}` })],
    ['an op that is not an object', answerWith([entry(1, /** @type {any} */ ('pin'))])],
    ['an entry with a field too many', answerWith([{ ...entry(1, pin1), extra: 1 }])],
    ['too many operations', answerWith(Array.from({ length: 101 }, (_, index) => entry(index + 1, pin1)))],
    ['a 404', () => ({ status: 404, body: '{"error":"not_found"}' })],
    ['a 400', () => ({ status: 400, body: '{"error":"invalid_query"}' })],
    ['a redirect', () => ({ status: 302, body: '' })],
    ['more than 8 MiB', () => ({ status: 200, body: Buffer.alloc(8 * 1024 * 1024 + 1, 0x20) })],
  ];
  try {
    for (const [name, answer] of broken) {
      const fake = await scriptedRelay([/** @type {any} */ (answer)]);
      try {
        const result = await runGateway(state, fake.url, token(), receiver);
        assert.equal(result.code, 2, `${name}: ${result.stdout}${result.stderr}`);
        assert.match(result.stderr, /^gateway: the relay's answer does not follow the relay API/, String(name));
      } finally {
        await fake.close();
      }
    }
    // A relay that kept its epoch must answer above the place the gateway saved.
    const behind = await scriptedRelay([
      answerWith([entry(1, pin1), entry(2, pin1)]),
      answerWith([entry(2, pin1)], { reset: false }),
    ]);
    try {
      const result = await runGateway(state, behind.url, token(), receiver);
      assert.equal(result.code, 2, result.stdout);
      assert.match(result.stdout, /^Done with 2 operations, up to 2$/m);
      assert.equal(behind.queries[1].get('after'), '2');
    } finally {
      await behind.close();
    }
    assert.equal(receiver.requests.length, 2, 'only the two operations before the broken answer were applied');
  } finally {
    await receiver.stop();
  }
});

test('waits for a relay that cannot be reached or fails, and skips an operation it cannot use', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const state = await pairGateway(receiver);
  const fake = await scriptedRelay([
    () => ({ status: 503, body: '{"error":"busy"}' }),
    answerWith([
      entry(1, { op: 'pin', id: 'p1', lat: 0, lon: 0 }),
      entry(2, { op: 'circle', id: 'c1' }),
      entry(3, { op: 'pin', id: 'p2', lat: 91, lon: 0 }),
      entry(5, { op: 'pin', id: 'p3', lat: 0, lon: 0 }),
    ]),
  ]);
  try {
    const result = await runGateway(state, fake.url, token(), receiver);
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.stdout, [
      'The relay could not be reached; trying again in 1 second.',
      'Refused operation 2 (unknown): invalid_op',
      'Refused operation 3 (pin): invalid_op',
      'Done with 4 operations, up to 5 (2 refused)',
      '',
    ].join('\n'));
    assert.deepEqual(receiver.requests.map((envelope) => /** @type {any} */ (envelope.request).id), ['p1', 'p3']);
    // What it asked for: wait=0 with --once, and its place once it had one.
    assert.equal(fake.queries[0].get('epoch'), null);
    assert.equal(fake.queries[0].get('after'), '0');
    assert.equal(fake.queries[0].get('limit'), '100');
    assert.equal(fake.queries[0].get('wait'), '0');
    assert.equal(fake.queries[2].get('epoch'), EPOCH);
    assert.equal(fake.queries[2].get('after'), '5');
  } finally {
    await fake.close();
    await receiver.stop();
  }
});

test('fetches with wait=25 when it is not run with --once', async () => {
  const receiver = new TestReceiver();
  await receiver.start();
  const state = await pairGateway(receiver);
  const fake = await scriptedRelay([]);
  try {
    const gateway = spawn(process.execPath, [GATEWAY, 'run', '--relay', fake.url, '--host', '127.0.0.1', '--port', String(receiver.port), '--state-dir', state], {
      env: { ...process.env, ERDIGO_RELAY_TOKEN: token() },
      stdio: 'ignore',
    });
    const exited = new Promise((resolve) => gateway.on('close', resolve));
    for (let tries = 0; tries < 400 && fake.queries.length === 0; tries += 1) {
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    gateway.kill();
    await exited;
    assert.equal(fake.queries[0]?.get('wait'), '25');
  } finally {
    await fake.close();
    await receiver.stop();
  }
});
