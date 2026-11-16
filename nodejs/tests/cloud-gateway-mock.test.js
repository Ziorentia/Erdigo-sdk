// The cloud relay example end to end against the mock receiver of another edition: the relay and the gateway run as
// programs, as the README shows, and what the mock holds is read from its console. The mock is chosen with
// ERDIGO_MOCK, as for tests/interop.test.js:
//
//     ERDIGO_MOCK='["../dotnet/src/erdigo-send/bin/Release/net8.0/erdigo-send"]' node --test tests/cloud-gateway-mock.test.js
//
// Without it these tests are skipped.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, realpathSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { GATEWAY, RELAY, call, run, startProgram, token } from './support/cloud-relay.js';
import { loadJson } from './support/vectors.js';

const command = process.env.ERDIGO_MOCK ? JSON.parse(process.env.ERDIGO_MOCK) : null;
const skip = command === null ? 'set ERDIGO_MOCK to run a mock receiver of another edition' : false;
const isWindows = process.platform === 'win32';
const cases = loadJson('docs/cloud-relay-cases.json');
const PNG_1X1 = cases.valid_ops.find((/** @type {any} */ sample) => sample.name === 'photo, a 1x1 PNG').op.data;

// The long name of the temporary folder: on Windows a code file must be inside the user's profile folder.
const work = mkdtempSync(join(realpathSync.native(tmpdir()), 'erdigo-gateway-mock-'));
after(() => rmSync(work, { recursive: true, force: true }));
let folders = 0;

/** A mock receiver of another edition, driven through its console. */
class Mock {
  constructor() {
    this.codeFile = join(work, 'code');
    this.output = '';
    const [program, ...rest] = command;
    this.process = spawn(program, [...rest, 'serve', '--mock', '--code-file', this.codeFile], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.exited = new Promise((resolve) => this.process.on('close', resolve));
    for (const stream of [this.process.stdout, this.process.stderr]) {
      stream.on('data', (chunk) => {
        this.output += chunk.toString();
      });
    }
  }

  /**
   * @param {string | RegExp} wanted
   * @param {number} [from]
   */
  async waitFor(wanted, from = 0) {
    const end = Date.now() + 60_000;
    for (;;) {
      const text = this.output.slice(from);
      if (typeof wanted === 'string' ? text.includes(wanted) : wanted.test(text)) {
        return text;
      }
      if (Date.now() > end || this.process.exitCode !== null) {
        throw new Error(`the mock did not say ${wanted}:\n${this.output}`);
      }
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
  }

  async start() {
    await this.waitFor("Type 'help'");
    this.port = Number(/port (\d+)/.exec(this.output)?.[1]);
  }

  /** @param {string} line */
  type(line) {
    this.process.stdin.write(`${line}\n`);
  }

  /** Opens a new pairing window, whose QR text the mock writes to the code file. */
  async freshCode() {
    const count = (this.output.match(/Pairing text written/g) ?? []).length;
    this.type('code');
    await this.waitFor(new RegExp(`(?:Pairing text written[^]*){${count + 1}}`));
  }

  /** @param {string} name */
  async deviceNumber(name) {
    const from = this.output.length;
    this.type('devices');
    const text = await this.waitFor(new RegExp(`(\\d+)\\. '${name}'`), from);
    return Number(new RegExp(`(\\d+)\\. '${name}'`).exec(text)?.[1]);
  }

  /** The items a device holds, one line each, sorted. @param {string} name */
  async items(name) {
    const device = await this.deviceNumber(name);
    const from = this.output.length;
    this.type(`items ${device}`);
    this.type('help');
    const text = await this.waitFor('code | devices', from);
    return text.split('\n').filter((line) => / at .* title=/.test(line)).map((line) => line.trim()).sort();
  }

  /** How many requests the mock has applied. */
  applied() {
    return (this.output.match(/^\[mock\] #\d+ /gm) ?? []).length;
  }

  async stop() {
    this.process.kill();
    await this.exited;
  }
}

/** The relay, run as the README shows. */
async function startRelay() {
  const source = token();
  const gateway = token();
  const started = await startProgram(RELAY, ['--listen', '127.0.0.1:0'], /^Relay listening on (http:\/\/127\.0\.0\.1:\d+)$/m, {
    ERDIGO_RELAY_SOURCE_TOKENS: source,
    ERDIGO_RELAY_GATEWAY_TOKEN: gateway,
  });
  const url = started.match[1];
  return {
    url,
    gateway,
    /** @param {object[]} ops */
    async post(ops) {
      const answer = await call(`${url}/v1/ops`, { method: 'POST', token: source, body: { ops } });
      assert.equal(answer.status, 200, answer.text);
      return answer.json;
    },
    /** What is still waiting, without forgetting anything. */
    async waiting() {
      const answer = await call(`${url}/v1/ops`, { token: gateway });
      assert.equal(answer.status, 200, answer.text);
      return answer.json.ops;
    },
    async stop() {
      started.child.kill(isWindows ? undefined : 'SIGTERM');
      await started.exited;
    },
  };
}

/** @type {Mock} */
let mock;

before(async () => {
  if (skip) {
    return;
  }
  mock = new Mock();
  await mock.start();
});

after(async () => {
  await mock?.stop();
});

/**
 * Pairs a new gateway with the mock through the code file, as the README shows.
 * @param {string} name
 * @param {string[]} [extra]
 */
async function pairGateway(name, extra = []) {
  folders += 1;
  const state = join(work, `state-${folders}`);
  if (folders > 1) {
    await mock.freshCode();
  }
  const result = await run(GATEWAY, ['pair', '--name', name, '--host', '127.0.0.1', '--port', String(mock.port), '--code-file', mock.codeFile,
    '--state-dir', state, ...extra]);
  assert.equal(result.code, 0, result.stdout + result.stderr);
  return state;
}

/**
 * @param {string} state
 * @param {string} url
 * @param {string} gatewayToken
 */
function runOnce(state, url, gatewayToken) {
  return run(GATEWAY, ['run', '--relay', url, '--host', '127.0.0.1', '--port', String(mock.port), '--state-dir', state, '--once'], {
    env: { ERDIGO_RELAY_TOKEN: gatewayToken },
  });
}

test('writes every kind of operation to the mock, and applies nothing twice', { skip }, async () => {
  const relay = await startRelay();
  try {
    const state = await pairGateway('Cloud gateway');
    await relay.post([
      { op: 'pin', id: 'p1', lat: 37.5663, lon: 126.9779, title: 'Seoul City Hall' },
      { op: 'marker', id: 'm1', lat: 1.5, lon: 2.5, title: 'Gate', color: 'red' },
      { op: 'note', id: 'n1', lat: 0, lon: 0, text: 'line one\nline\ttwo' },
      { op: 'photo', id: 'ph1', lat: 0, lon: 0, type: 'image/png', data: PNG_1X1, title: 'View' },
      { op: 'track', id: 't1', title: 'Flight 7', points: [{ lat: 37.5663, lon: 126.9779, ts: 1_700_000_000_000, alt: 120 }, { lat: 37.567, lon: 126.978 }] },
      { op: 'finish_track', id: 't1' },
      { op: 'move', id: 'p1', lat: 37.5, lon: 127 },
      { op: 'pin', id: 'gone', lat: 0, lon: 0 },
      { op: 'delete', id: 'gone' },
    ]);
    const first = await runOnce(state, relay.url, relay.gateway);
    assert.equal(first.code, 0, first.stdout + first.stderr);
    assert.equal(first.stdout, 'Done with 9 operations, up to 9\n');
    const held = [
      'marker m1 at 1.5,2.5 title=Gate points=0',
      'note n1 at 0.0,0.0 title=- points=0',
      'photo ph1 at 0.0,0.0 title=View points=0',
      'pin p1 at 37.5,127.0 title=Seoul City Hall points=0',
      'track t1 at -,- title=Flight 7 points=2',
    ];
    assert.deepEqual(await mock.items('Cloud gateway'), held);
    assert.deepEqual(await relay.waiting(), [], 'the relay forgot what the gateway was done with');

    const applied = mock.applied();
    const second = await runOnce(state, relay.url, relay.gateway);
    assert.equal(second.code, 0, second.stderr);
    assert.equal(second.stdout, '');
    assert.equal(mock.applied(), applied, 'nothing was sent again');
    assert.deepEqual(await mock.items('Cloud gateway'), held);
  } finally {
    await relay.stop();
  }
});

test('a gateway paired for some kinds refuses the others with not_permitted, and goes on', { skip }, async () => {
  const relay = await startRelay();
  try {
    const state = await pairGateway('Pins only', ['--kinds', 'pin']);
    await relay.post([
      { op: 'marker', id: 'm1', lat: 0, lon: 0, color: 'blue' },
      { op: 'pin', id: 'p1', lat: 37.5663, lon: 126.9779 },
      { op: 'photo', id: 'ph1', lat: 0, lon: 0, type: 'image/png', data: PNG_1X1 },
    ]);
    const result = await runOnce(state, relay.url, relay.gateway);
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.stdout, [
      'Refused operation 1 (marker): not_permitted',
      'Refused operation 3 (photo): not_permitted',
      'Done with 3 operations, up to 3 (2 refused)',
      '',
    ].join('\n'));
    assert.deepEqual(await mock.items('Pins only'), ['pin p1 at 37.5663,126.9779 title=- points=0']);
  } finally {
    await relay.stop();
  }
});

test('starts from the oldest operation of a relay that restarted', { skip }, async () => {
  let relay = await startRelay();
  const state = await pairGateway('Restarts');
  try {
    await relay.post([{ op: 'pin', id: 'before', lat: 0, lon: 0 }]);
    assert.equal((await runOnce(state, relay.url, relay.gateway)).code, 0);
    const place = JSON.parse(readFileSync(join(state, 'relay-state.json'), 'utf8'));
    await relay.stop();
    // The same address and tokens, so that only the restart is new.
    const { port } = new URL(relay.url);
    const source = token();
    const started = await startProgram(RELAY, ['--listen', `127.0.0.1:${port}`], /^Relay listening on (http:\/\/127\.0\.0\.1:\d+)$/m, {
      ERDIGO_RELAY_SOURCE_TOKENS: source,
      ERDIGO_RELAY_GATEWAY_TOKEN: relay.gateway,
    });
    relay = {
      ...relay,
      stop: async () => {
        started.child.kill(isWindows ? undefined : 'SIGTERM');
        await started.exited;
      },
    };
    const posted = await call(`${relay.url}/v1/ops`, { method: 'POST', token: source, body: { ops: [{ op: 'pin', id: 'after', lat: 1, lon: 1 }] } });
    assert.deepEqual(posted.json, { accepted: 1, last_seq: 1 });
    const result = await runOnce(state, relay.url, relay.gateway);
    assert.equal(result.code, 0, result.stderr);
    assert.equal(result.stdout, 'The relay started a new numbering; starting from its oldest operation.\nDone with 1 operation, up to 1\n');
    const now = JSON.parse(readFileSync(join(state, 'relay-state.json'), 'utf8'));
    assert.notEqual(now.epoch, place.epoch);
    assert.deepEqual(await mock.items('Restarts'), ['pin after at 1.0,1.0 title=- points=0', 'pin before at 0.0,0.0 title=- points=0']);
  } finally {
    await relay.stop();
  }
});

test('wrong tokens and plain http beyond this computer are refused before anything is fetched', { skip }, async () => {
  const relay = await startRelay();
  try {
    const state = await pairGateway('Refusals');
    await relay.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
    const wrong = await runOnce(state, relay.url, token());
    assert.equal(wrong.code, 2, wrong.stderr);
    const missing = await run(GATEWAY, ['run', '--relay', relay.url, '--state-dir', state, '--once']);
    assert.equal(missing.code, 1, missing.stderr);
    const plain = await runOnce(state, 'http://relay.example.com', relay.gateway);
    assert.equal(plain.code, 1, plain.stderr);
    assert.equal((await relay.waiting()).length, 1);
    assert.deepEqual(await mock.items('Refusals'), []);
  } finally {
    await relay.stop();
  }
});

test('waits while the app is paused, and goes on when it resumes', { skip }, async () => {
  const relay = await startRelay();
  try {
    const state = await pairGateway('Paused');
    let from = mock.output.length;
    mock.type('pause');
    await mock.waitFor('Paused.', from);
    await relay.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }]);
    const gateway = await startProgram(GATEWAY, ['run', '--relay', relay.url, '--host', '127.0.0.1', '--port', String(mock.port), '--state-dir', state, '--once'],
      /^No answer from the Erdigo app; trying again\.$/m, { ERDIGO_RELAY_TOKEN: relay.gateway });
    from = mock.output.length;
    mock.type('resume');
    await mock.waitFor('Resumed.', from);
    const { code } = /** @type {{ code: number | null }} */ (await gateway.exited);
    assert.equal(code, 0, gateway.state.stdout + gateway.state.stderr);
    assert.match(gateway.state.stdout, /^Done with 1 operation, up to 1$/m);
    assert.deepEqual(await mock.items('Paused'), ['pin p1 at 0.0,0.0 title=- points=0']);
  } finally {
    await relay.stop();
  }
});

// Last, because it stops the mock.
test('with the mock stopped, --once exits with status 3 and the operations stay on the relay', { skip }, async () => {
  const relay = await startRelay();
  try {
    const state = await pairGateway('Stopped');
    await mock.stop();
    await relay.post([{ op: 'pin', id: 'p1', lat: 0, lon: 0 }, { op: 'note', id: 'n1', lat: 0, lon: 0, text: 'hello' }]);
    const result = await runOnce(state, relay.url, relay.gateway);
    assert.equal(result.code, 3, result.stdout + result.stderr);
    assert.equal(result.stdout, 'No answer from the Erdigo app; trying again.\nThe Erdigo app could not be reached. The operations stay on the relay.\n');
    assert.deepEqual((await relay.waiting()).map((/** @type {any} */ entry) => entry.seq), [1, 2]);
  } finally {
    await relay.stop();
  }
});
