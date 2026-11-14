// Pairing and writing, end to end over real TCP, against the test receiver.

import assert from 'node:assert/strict';
import { createServer } from 'node:net';
import { test } from 'node:test';
import { ErdigoClient, RetryPolicy } from '../src/client.js';
import { ReceiverTarget } from '../src/discovery.js';
import { DeviceIdentity, ErdigoError, ErdigoErrorKind, pairingFingerprint } from '../src/identity.js';
import { LIMITS_V1, PairingCode, PairingTicket } from '../src/model.js';
import { pair } from '../src/pairing.js';
import { shortFingerprint } from '../src/protocol.js';
import { InMemoryDeviceStore } from '../src/store.js';
import { JPEG, noisyPng } from './support/images.js';
import { pairedDevice } from './support/paired.js';
import { TestReceiver } from './support/receiver.js';

const palace = { lat: 37.5796, lon: 126.977 };

/**
 * @param {string} kind
 * @param {() => Promise<unknown>} body
 */
async function expectKind(kind, body) {
  /** @type {ErdigoError | undefined} */
  let caught;
  await assert.rejects(body, (error) => {
    caught = /** @type {ErdigoError} */ (error);
    return error instanceof ErdigoError && error.kind === kind;
  });
  return /** @type {ErdigoError} */ (caught);
}

async function receiver(options = {}) {
  const started = new TestReceiver(options);
  const port = await started.start();
  return { receiver: started, target: ReceiverTarget.address('127.0.0.1', port) };
}

// Pairing

test('QR text pairs without asking, and the person may allow fewer kinds', async () => {
  const { receiver: mock, target } = await receiver({ kinds: ['pin'] });
  try {
    const identity = DeviceIdentity.generate();
    const pairing = await pair({ target, ticket: PairingTicket.parse(mock.qrText), identity, deviceName: 'Sensor', kinds: ['pin', 'marker'] });
    assert.deepEqual(pairing.kinds, ['pin']);
    assert.equal(pairingFingerprint(pairing), mock.fingerprint);
    assert.equal(pairing.lastHost, '127.0.0.1');
    assert.equal(pairing.label, `receiver-${mock.fingerprint.slice(0, 8)}`);
    const store = new InMemoryDeviceStore(identity);
    store.save(pairing);
    const client = new ErdigoClient({ identity, pairing, store, target });
    // A kind the person did not allow is refused here, without being sent.
    await expectKind(ErdigoErrorKind.notPermitted, () => client.putMarker('m', palace));
    assert.equal(mock.requests.length, 0);
    await client.putPin('p', palace);
    client.close();
  } finally {
    await mock.stop();
  }
});

test('a typed code without a question for the person is refused before anything is sent', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    const ticket = new PairingTicket(mock.code);
    await expectKind(ErdigoErrorKind.invalidArgument, () => pair({ target, ticket, identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'] }));
    assert.deepEqual(mock.events, []);
  } finally {
    await mock.stop();
  }
});

test('a typed code that the person does not confirm stops after message 2', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    /** @type {string[]} */
    const shown = [];
    await expectKind(ErdigoErrorKind.keyNotConfirmed, () => pair({
      target, ticket: new PairingTicket(mock.code), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
      confirmKey: (key) => {
        shown.push(key);
        return false;
      },
    }));
    assert.deepEqual(shown, [shortFingerprint(mock.fingerprint)]);
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.equal(mock.count('stopped after message 2'), 1);
    assert.equal(mock.count('pairing requested'), 0, 'the receiver heard neither the key nor the name');
  } finally {
    await mock.stop();
  }
});

test('only a yes pairs with a typed code', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    for (const answer of ['yes', 1, 'true', undefined]) {
      await expectKind(ErdigoErrorKind.keyNotConfirmed, () => pair({
        target, ticket: new PairingTicket(mock.code), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
        confirmKey: () => /** @type {any} */ (answer),
      }));
    }
    const pairing = await pair({
      target, ticket: new PairingTicket(mock.code), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
      confirmKey: async (key) => key === shortFingerprint(mock.fingerprint),
    });
    assert.equal(pairingFingerprint(pairing), mock.fingerprint);
  } finally {
    await mock.stop();
  }
});

// The receiver may still be closing the connection that showed the key, and so drop the next one unanswered.
test('after a confirmed key, a silent connection is tried again', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    const pairing = await pair({
      target, ticket: new PairingTicket(mock.code), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
      confirmKey: () => {
        mock.silentConnections = 2;
        return true;
      },
    });
    assert.equal(pairingFingerprint(pairing), mock.fingerprint);
    assert.equal(mock.count('silent'), 2);
    assert.equal(mock.count('pairing requested'), 1);
  } finally {
    await mock.stop();
  }
});

// Without a key a person confirmed a moment ago, silence is reported at once, as by the other SDKs.
test('QR text meeting silence is not tried again', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    mock.silentConnections = 1;
    await expectKind(ErdigoErrorKind.noAnswer, () => pair({
      target, ticket: PairingTicket.parse(mock.qrText), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
    }));
    assert.equal(mock.count('connection'), 1);
  } finally {
    await mock.stop();
  }
});

test('pairing can be stopped while it waits to try again', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    const controller = new AbortController();
    const reason = new Error('stopped by the test');
    await assert.rejects(
      pair({
        target, ticket: new PairingTicket(mock.code), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
        signal: controller.signal,
        confirmKey: () => {
          mock.silentConnections = 3;
          // Half a second passes before the second try.
          setTimeout(() => controller.abort(reason), 250);
          return true;
        },
      }),
      (error) => error === reason,
    );
    await new Promise((resolve) => setTimeout(resolve, 1500));
    assert.equal(mock.count('connection'), 2, 'the key was read once and pairing tried once');
  } finally {
    await mock.stop();
  }
});

test('a wrong fingerprint stops before message 3', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    await expectKind(ErdigoErrorKind.fingerprintMismatch, () => pair({
      target, ticket: new PairingTicket(mock.code, '0'.repeat(64)), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
    }));
    await new Promise((resolve) => setTimeout(resolve, 100));
    assert.equal(mock.count('pairing requested'), 0);
  } finally {
    await mock.stop();
  }
});

test('a wrong code gets no answer', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    await expectKind(ErdigoErrorKind.noAnswer, () => pair({
      target, ticket: new PairingTicket(PairingCode.generate(), mock.fingerprint), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'],
    }));
    assert.equal(mock.count('silent'), 1);
  } finally {
    await mock.stop();
  }
});

for (const [decision, kind] of [['deny', ErdigoErrorKind.pairingDenied], ['hold', ErdigoErrorKind.pairingTimedOut], ['code_reused', ErdigoErrorKind.codeReused]]) {
  test(`a receiver that answers ${decision} is reported`, async () => {
    const { receiver: mock, target } = await receiver({ decide: () => decision });
    try {
      await expectKind(kind, () => pair({
        target, ticket: PairingTicket.parse(mock.qrText), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'], approvalTimeoutMs: 300,
      }));
    } finally {
      await mock.stop();
    }
  });
}

test('pairing can be stopped while the person decides', async () => {
  const { receiver: mock, target } = await receiver({ decide: () => 'hold' });
  try {
    const controller = new AbortController();
    const reason = new Error('stopped by the test');
    setTimeout(() => controller.abort(reason), 200);
    await assert.rejects(
      pair({ target, ticket: PairingTicket.parse(mock.qrText), identity: DeviceIdentity.generate(), deviceName: 'Sensor', kinds: ['pin'], signal: controller.signal }),
      (error) => error === reason,
    );
  } finally {
    await mock.stop();
  }
});

test('pairing arguments are checked first', async () => {
  const ticket = PairingTicket.parse(`erdigo-pair:1?code=${PairingCode.generate().display}&fp=${'ab'.repeat(32)}`);
  const target = ReceiverTarget.address('127.0.0.1', 9);
  const identity = DeviceIdentity.generate();
  await expectKind(ErdigoErrorKind.invalidArgument, () => pair({ target, ticket, identity, deviceName: ' ‮ ', kinds: ['pin'] }));
  await expectKind(ErdigoErrorKind.invalidArgument, () => pair({ target, ticket, identity, deviceName: 'x', kinds: [] }));
  await expectKind(ErdigoErrorKind.invalidArgument, () => pair({ target, ticket, identity, deviceName: 'x', kinds: [/** @type {any} */ ('video')] }));
  await expectKind(ErdigoErrorKind.invalidArgument, () => pair({ target, ticket, identity, deviceName: 'x', kinds: ['pin'], label: '' }));
});

// Writing

test('every item kind is sent as the protocol says', async () => {
  const device = await pairedDevice();
  try {
    const { client, receiver: mock } = device;
    await client.ping();
    await client.putPin('gate', palace, { title: 'Palace gate' });
    await client.putMarker('tower', palace, { color: 'red' });
    await client.putNote('meet', palace, 'Meet here.\nBring water.');
    await client.putPhoto('view', palace, JPEG, { title: 'View' });
    await client.appendTrack('walk', [{ ...palace, ts: 1_700_000_000_000, alt: 38.5 }], { title: 'Walk' });
    await client.finishTrack('walk');
    await client.move('gate', { lat: 37.58, lon: 126.98 });
    await client.delete('meet');
    await client.deleteAll();
    assert.deepEqual(mock.requests.map((envelope) => envelope.seq), Array.from({ length: 12 }, (_, index) => index + 1));
    assert.deepEqual(mock.requests.map((envelope) => envelope.request.t), [
      'ping', 'put', 'put', 'put', 'photo_begin', 'photo_chunk', 'photo_end', 'track_append', 'track_finish', 'move', 'delete', 'delete_all',
    ]);
    const begin = mock.requests[4].request;
    assert.equal(begin.type, 'image/jpeg');
    assert.equal(begin.size, JPEG.length);
    assert.ok(Buffer.from(mock.requests[5].request.data).equals(JPEG));
    assert.equal(mock.count('connected'), 1);
  } finally {
    await device.done();
  }
});

test('a large photo goes in 48000-byte chunks', async () => {
  const device = await pairedDevice();
  try {
    const photo = noisyPng(200, 150);
    await device.client.putPhoto('big', palace, photo);
    const chunks = device.receiver.requests.filter((envelope) => envelope.request.t === 'photo_chunk').map((envelope) => envelope.request);
    assert.equal(chunks.length, Math.ceil(photo.length / 48_000));
    assert.deepEqual(chunks.map((chunk) => chunk.index), chunks.map((_, index) => index));
    assert.ok(chunks.slice(0, -1).every((chunk) => chunk.data.length === 48_000));
    assert.ok(Buffer.concat(chunks.map((chunk) => Buffer.from(chunk.data))).equals(photo));
  } finally {
    await device.done();
  }
});

test('a photo restarts from photo_begin after the connection is lost', async () => {
  const device = await pairedDevice();
  try {
    let dropped = false;
    device.receiver.dropBeforeAnswer = (envelope) => {
      if (!dropped && envelope.request.t === 'photo_chunk') {
        dropped = true;
        return true;
      }
      return false;
    };
    await device.client.putPhoto('view', palace, noisyPng(200, 150));
    const types = device.receiver.requests.map((envelope) => envelope.request.t);
    assert.equal(types.filter((type) => type === 'photo_begin').length, 2);
    assert.equal(device.receiver.count('connected'), 2);
  } finally {
    await device.done();
  }
});

test('a request whose answer was lost is resent with the same number and applied once', async () => {
  const device = await pairedDevice();
  try {
    let dropped = false;
    device.receiver.dropBeforeAnswer = () => {
      const drop = !dropped;
      dropped = true;
      return drop;
    };
    await device.client.putPin('gate', palace);
    assert.deepEqual(device.receiver.requests.map((envelope) => envelope.seq), [1, 1]);
    assert.equal(device.receiver.count('applied'), 1);
    assert.equal(device.receiver.count('connected'), 2);
  } finally {
    await device.done();
  }
});

test('a long track is split into batches of 100, with the title only in the first', async () => {
  const device = await pairedDevice({ receiver: { limits: { ...LIMITS_V1, pointsPerSecond: 100_000 } } });
  try {
    const points = Array.from({ length: 250 }, (_, index) => ({ lat: palace.lat, lon: palace.lon + index / 10_000 }));
    await device.client.appendTrack('walk', points, { title: 'Walk' });
    const appends = device.receiver.requests.map((envelope) => envelope.request);
    assert.deepEqual(appends.map((request) => request.points.length), [100, 100, 50]);
    assert.deepEqual(appends.map((request) => request.title), ['Walk', null, null]);
  } finally {
    await device.done();
  }
});

test('points are paced by the limits the receiver announces', async () => {
  const device = await pairedDevice({ receiver: { limits: { ...LIMITS_V1, pointsPerSecond: 50 } } });
  try {
    const points = Array.from({ length: 150 }, () => palace);
    const started = Date.now();
    await device.client.appendTrack('walk', points);
    // 100 at once, and 50 more at 50 a second.
    assert.ok(Date.now() - started >= 900, `took ${Date.now() - started} ms`);
  } finally {
    await device.done();
  }
});

test('requests run one at a time, in the order they were made', async () => {
  const device = await pairedDevice();
  try {
    await Promise.all(Array.from({ length: 10 }, (_, index) => device.client.putPin(`p${index}`, palace)));
    assert.deepEqual(device.receiver.requests.map((envelope) => envelope.request.id), Array.from({ length: 10 }, (_, index) => `p${index}`));
  } finally {
    await device.done();
  }
});

test('a request stopped while waiting its turn does not hold up the ones behind it', async () => {
  const device = await pairedDevice({ receiver: { limits: { ...LIMITS_V1, pointsPerSecond: 1 } } });
  try {
    // The first request waits a long time for its allowance of points.
    const slow = new AbortController();
    const first = device.client.appendTrack('walk', Array.from({ length: 100 }, () => palace)).then(() =>
      device.client.appendTrack('walk', [palace, palace], { signal: slow.signal }));
    const waiting = new AbortController();
    const stopped = device.client.putPin('stopped', palace, { signal: waiting.signal });
    waiting.abort(new Error('not needed'));
    await assert.rejects(stopped, /not needed/);
    slow.abort(new Error('too slow'));
    await assert.rejects(first, /too slow/);
    await device.client.putPin('after', palace);
    assert.ok(!device.receiver.requests.some((envelope) => envelope.request.id === 'stopped'));
  } finally {
    await device.done();
  }
});

test('invalid arguments are refused before anything is sent', async () => {
  const device = await pairedDevice();
  try {
    const { client } = device;
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.putPin('bad id', palace));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.putPin('a', { lat: 91, lon: 0 }));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.putPin('a', palace, { title: 'a‮b' }));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.putMarker('a', palace, { color: /** @type {any} */ ('pink') }));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.putNote('a', palace, ''));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.appendTrack('a', []));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.appendTrack('a', [{ ...palace, ts: -1 }]));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.putPhoto('a', palace, Buffer.from('GIF89a')));
    await expectKind(ErdigoErrorKind.invalidArgument, () => client.putPhoto('a', palace, Buffer.concat([JPEG, Buffer.alloc(1_048_576)])));
    assert.equal(device.receiver.count('connected'), 0);
  } finally {
    await device.done();
  }
});

test('the connect counter goes up with every connection, and the address is remembered', async () => {
  const device = await pairedDevice();
  try {
    const id = pairingFingerprint(device.pairing);
    await device.client.ping();
    device.client.close();
    await device.client.ping();
    assert.equal(device.receiver.count('connected'), 2);
    const next = device.store.nextConnectCounter(id, 0);
    assert.ok(next > Date.now() - 60_000);
    assert.equal(device.store.pairings()[0].lastPort, device.port);
  } finally {
    await device.done();
  }
});

test('with no receiver, a request fails at once without retrying, or after the time allowed', async () => {
  const server = createServer((socket) => socket.destroy());
  await new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(undefined)));
  const port = /** @type {import('node:net').AddressInfo} */ (server.address()).port;
  await new Promise((resolve) => server.close(() => resolve(undefined)));
  const identity = DeviceIdentity.generate();
  const target = ReceiverTarget.address('127.0.0.1', port);
  const pairing = { label: 'gone', receiverKey: identity.publicKey, kinds: ['pin'], lastHost: null, lastPort: null, pairedAt: new Date(), counterFloor: 0 };
  const store = new InMemoryDeviceStore(identity);
  let started = Date.now();
  await expectKind(ErdigoErrorKind.noAnswer, () => new ErdigoClient({ identity, pairing, store, target, retry: RetryPolicy.none }).ping());
  assert.ok(Date.now() - started < 2000);
  started = Date.now();
  await expectKind(ErdigoErrorKind.noAnswer, () => new ErdigoClient({ identity, pairing, store, target, retry: RetryPolicy.upTo(1500) }).ping());
  const took = Date.now() - started;
  assert.ok(took >= 1400 && took < 4000, `took ${took} ms`);
});

test('a receiver that does not know the device stays silent, which is no answer', async () => {
  const { receiver: mock, target } = await receiver();
  try {
    const identity = DeviceIdentity.generate();
    const pairing = { label: 'x', receiverKey: mock.publicKey, kinds: ['pin'], lastHost: null, lastPort: null, pairedAt: new Date(), counterFloor: 0 };
    const client = new ErdigoClient({ identity, pairing, store: new InMemoryDeviceStore(identity), target, retry: RetryPolicy.none });
    await expectKind(ErdigoErrorKind.noAnswer, () => client.ping());
    assert.equal(mock.count('silent'), 1);
  } finally {
    await mock.stop();
  }
});
