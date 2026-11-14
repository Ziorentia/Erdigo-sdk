// Pairs with, and writes every item kind to, the mock receiver of another edition, over real TCP. The mock is chosen
// with ERDIGO_MOCK, a JSON array holding the command that runs that edition's erdigo-send, such as
//
//     ERDIGO_MOCK='["../windows/src/erdigo-send/bin/Release/net8.0/erdigo-send"]' node --test tests/interop.test.js
//     ERDIGO_MOCK='["../.build/debug/erdigo-send"]' node --test tests/interop.test.js
//
// Without it these tests are skipped.

import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { after, before, test } from 'node:test';
import { ErdigoClient, RetryPolicy } from '../src/client.js';
import { ReceiverTarget, browse } from '../src/discovery.js';
import { DeviceIdentity, ErdigoError, ErdigoErrorKind } from '../src/identity.js';
import { ITEM_KINDS, PairingCode, PairingTicket } from '../src/model.js';
import { pair } from '../src/pairing.js';
import { InMemoryDeviceStore } from '../src/store.js';
import { JPEG, noisyPng, png } from './support/images.js';

const command = process.env.ERDIGO_MOCK ? JSON.parse(process.env.ERDIGO_MOCK) : null;
const skip = command === null ? 'set ERDIGO_MOCK to run a mock receiver of another edition' : false;

/** A mock receiver of another edition, driven through its console as a person would. */
class Mock {
  /** @param {string[]} [extra] more options for `serve --mock` */
  constructor(extra = []) {
    this.folder = mkdtempSync(join(tmpdir(), 'erdigo-interop-'));
    this.codeFile = join(this.folder, 'code');
    this.output = '';
    const [program, ...rest] = command;
    this.process = spawn(program, [...rest, 'serve', '--mock', ...extra, '--code-file', this.codeFile], { stdio: ['pipe', 'pipe', 'pipe'] });
    this.process.stdout.on('data', (chunk) => {
      this.output += chunk.toString();
    });
    this.process.stderr.on('data', (chunk) => {
      this.output += chunk.toString();
    });
  }

  /**
   * Waits for text to appear in the mock's output, after `from` characters.
   * @param {string | RegExp} wanted
   * @param {number} [from]
   */
  async waitFor(wanted, from = 0, seconds = 60) {
    const end = Date.now() + seconds * 1000;
    for (;;) {
      const text = this.output.slice(from);
      if (typeof wanted === 'string' ? text.includes(wanted) : wanted.test(text)) {
        return text;
      }
      if (Date.now() > end || this.process.exitCode !== null) {
        throw new Error(`the mock did not say ${wanted}:\n${this.output}`);
      }
      await new Promise((resolve) => setTimeout(resolve, 50));
    }
  }

  async start() {
    await this.waitFor("Type 'help'");
    this.port = Number(/port (\d+)/.exec(this.output)?.[1]);
    this.shortKey = /Receiver key: ([0-9a-f-]+)/.exec(this.output)?.[1];
    return this.port;
  }

  /** @param {string} line */
  type(line) {
    this.process.stdin.write(`${line}\n`);
  }

  /** The QR text of a new pairing window. */
  async freshCode() {
    const before = (this.output.match(/Pairing text written/g) ?? []).length;
    this.type('code');
    for (let tries = 0; tries < 200; tries += 1) {
      if ((this.output.match(/Pairing text written/g) ?? []).length > before) {
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
    return this.qrText();
  }

  qrText() {
    return readFileSync(this.codeFile, 'utf8').trim();
  }

  /** @param {number} device */
  async items(device) {
    const from = this.output.length;
    this.type(`items ${device}`);
    this.type('help');
    const text = await this.waitFor('code | devices', from);
    return text.split('\n').filter((line) => / at .* title=/.test(line));
  }

  pairingRequests() {
    return (this.output.match(/pairing request/g) ?? []).length;
  }

  stop() {
    this.process.kill();
    rmSync(this.folder, { recursive: true, force: true });
  }
}

/** @type {Mock} */
let mock;
/** @type {import('../src/discovery.js').ReceiverTarget} */
let target;

before(async () => {
  if (skip) {
    return;
  }
  mock = new Mock();
  target = ReceiverTarget.address('127.0.0.1', await mock.start());
});

after(() => mock?.stop());

/**
 * @param {string} kind
 * @param {() => Promise<unknown>} body
 */
async function expectKind(kind, body) {
  await assert.rejects(body, (error) => error instanceof ErdigoError && error.kind === kind);
}

const palace = { lat: 37.5796, lon: 126.977 };
const cityHall = { lat: 37.5663, lon: 126.9779 };

test('pairs with QR text and writes every item kind', { skip }, async () => {
  const identity = DeviceIdentity.generate();
  const store = new InMemoryDeviceStore(identity);
  const pairing = await pair({ target, ticket: PairingTicket.parse(mock.qrText()), identity, deviceName: 'Node.js interop', kinds: ITEM_KINDS });
  assert.deepEqual(pairing.kinds, [...ITEM_KINDS]);
  store.save(pairing);
  const client = new ErdigoClient({ identity, pairing, store, target });
  try {
    await client.ping();
    await client.putPin('gate', palace, { title: 'Palace gate' });
    await client.putMarker('tower', { lat: 48.8584, lon: 2.2945 }, { title: 'Tower', color: 'blue' });
    await client.putNote('meet', cityHall, 'Meet at the gate.\nBring water. 물 챙기기');
    await client.putPhoto('photo-jpeg', palace, JPEG, { title: 'JPEG' });
    const large = noisyPng(200, 150);
    assert.ok(large.length > 48_000 * 1.5, 'the PNG takes several chunks');
    await client.putPhoto('photo-png', cityHall, large, { title: 'PNG' });
    await client.putPhoto('photo-small', cityHall, png(16, 16));
    const points = Array.from({ length: 100 }, (_, step) => ({
      lat: palace.lat + ((cityHall.lat - palace.lat) * step) / 99,
      lon: palace.lon + ((cityHall.lon - palace.lon) * step) / 99,
      ts: 1_700_000_000_000 + step * 1000,
      alt: 38.5,
    }));
    await client.appendTrack('walk', points, { title: 'Walk to City Hall' });
    await client.finishTrack('walk');
    await client.move('gate', { lat: 37.58, lon: 126.98 });
    await client.putPin('temporary', palace);
    await client.delete('temporary');
    await client.delete('never-existed');
    await expectKind(ErdigoErrorKind.rejected, () => client.move('missing', palace));

    const items = await mock.items(1);
    const find = (/** @type {string} */ prefix) => items.find((line) => line.startsWith(prefix));
    assert.match(find('pin gate') ?? '', /at 37\.58,126\.98 title=Palace gate/);
    assert.match(find('marker tower') ?? '', /title=Tower/);
    assert.match(find('note meet') ?? '', /at 37\.5663,126\.9779/);
    assert.ok(find('photo photo-jpeg'), items.join('\n'));
    assert.ok(find('photo photo-png'), items.join('\n'));
    assert.ok(find('photo photo-small'), items.join('\n'));
    assert.match(find('track walk') ?? '', /title=Walk to City Hall points=100/);
    assert.equal(find('pin temporary'), undefined);

    await client.deleteAll();
    assert.deepEqual(await mock.items(1), []);
  } finally {
    client.close();
  }
});

test('a typed code asks before the receiver hears a request, and pairs once confirmed', { skip }, async () => {
  const code = PairingTicket.parse(await mock.freshCode()).code;
  const ticket = new PairingTicket(code);
  const identity = DeviceIdentity.generate();
  const before = mock.pairingRequests();
  /** @type {string[]} */
  const shown = [];
  await expectKind(ErdigoErrorKind.keyNotConfirmed, () => pair({
    target, ticket, identity, deviceName: 'Typed', kinds: ['pin'], confirmKey: (key) => {
      shown.push(key);
      return false;
    },
  }));
  assert.deepEqual(shown, [mock.shortKey]);
  assert.equal(mock.pairingRequests(), before, 'the receiver heard no request');
  // The code is still good, and a person who compares the key and says yes pairs. A person takes a moment to
  // answer, and the receiver delays an address whose handshake stopped after message 2 for up to a second (spec
  // sections 4.3 and 5.2), so this answers no faster.
  const person = () => new Promise((resolve) => setTimeout(resolve, 1500));
  await person();
  const pairing = await pair({
    target, ticket, identity, deviceName: 'Typed', kinds: ['pin'], confirmKey: async (key) => {
      await person();
      return key === mock.shortKey;
    },
  });
  assert.deepEqual(pairing.kinds, ['pin']);
  assert.equal(mock.pairingRequests(), before + 1);
});

test('a wrong fingerprint stops pairing before the receiver hears a request', { skip }, async () => {
  const code = PairingTicket.parse(await mock.freshCode()).code;
  const before = mock.pairingRequests();
  await expectKind(ErdigoErrorKind.fingerprintMismatch, () => pair({
    target, ticket: new PairingTicket(code, '0'.repeat(64)), identity: DeviceIdentity.generate(), deviceName: 'Wrong', kinds: ['pin'],
  }));
  assert.equal(mock.pairingRequests(), before);
});

test('a wrong code gets no answer', { skip }, async () => {
  await mock.freshCode();
  const wrong = new PairingTicket(PairingCode.generate(), '0'.repeat(64));
  await expectKind(ErdigoErrorKind.noAnswer, () => pair({
    target, ticket: wrong, identity: DeviceIdentity.generate(), deviceName: 'Stranger', kinds: ['pin'], handshakeTimeoutMs: 3000,
  }));
});

test('a paused receiver is waited for, and a removed device gets no answer', { skip }, async () => {
  const identity = DeviceIdentity.generate();
  const store = new InMemoryDeviceStore(identity);
  const pairing = await pair({ target, ticket: PairingTicket.parse(await mock.freshCode()), identity, deviceName: 'Paused', kinds: ['pin'] });
  store.save(pairing);
  const client = new ErdigoClient({ identity, pairing, store, target, retry: RetryPolicy.upTo(30_000) });
  try {
    await client.putPin('before', palace);
    const from = mock.output.length;
    mock.type('pause');
    await mock.waitFor('Paused.', from);
    const writing = client.putPin('during', palace);
    await new Promise((resolve) => setTimeout(resolve, 1500));
    mock.type('resume');
    await writing;

    const devices = mock.output.length;
    mock.type('devices');
    const list = await mock.waitFor(/'Paused'/, devices);
    const number = /(\d+)\. 'Paused'/.exec(list)?.[1];
    assert.ok(number, list);
    mock.type(`remove ${number}`);
    await new Promise((resolve) => setTimeout(resolve, 500));
    const removed = new ErdigoClient({ identity, pairing, store, target, retry: RetryPolicy.none });
    await expectKind(ErdigoErrorKind.noAnswer, () => removed.ping());
    removed.close();
  } finally {
    client.close();
  }
});

// Finds a mock that advertises on the local network, over real multicast, and writes to it. A machine without a
// multicast-capable interface, or a network that drops multicast, cannot run this, so it runs only when
// ERDIGO_MULTICAST_TESTS=1 is set. The mock listens on every interface, which a desktop firewall may ask about.
const multicast = skip || (process.env.ERDIGO_MULTICAST_TESTS === '1' ? false : 'set ERDIGO_MULTICAST_TESTS=1 to use real multicast');

test('a receiver advertised on the local network is found, paired with and written to', { skip: multicast }, async () => {
  const lan = new Mock(['--lan', '--bonjour', '--auto-approve']);
  try {
    const port = await lan.start();
    let endpoints = [];
    for (let attempt = 0; attempt < 5 && !endpoints.some((endpoint) => endpoint.port === port); attempt += 1) {
      endpoints = await browse();
    }
    assert.ok(endpoints.some((endpoint) => endpoint.port === port), JSON.stringify(endpoints));
    const identity = DeviceIdentity.generate();
    const store = new InMemoryDeviceStore(identity);
    const pairing = await pair({
      target: ReceiverTarget.discover, ticket: PairingTicket.parse(lan.qrText()), identity, deviceName: 'Found', kinds: ['pin'],
    });
    assert.equal(pairing.lastPort, port);
    store.save({ ...pairing, lastHost: null, lastPort: null });
    const client = new ErdigoClient({ identity, pairing: { ...pairing, lastHost: null, lastPort: null }, store, target: ReceiverTarget.discover });
    try {
      await client.putPin('found', palace);
    } finally {
      client.close();
    }
    assert.ok((await lan.items(1)).some((line) => line.startsWith('pin found')));
  } finally {
    lan.stop();
  }
});
