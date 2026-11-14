// The official Noise vectors and every integration-v1 vector, byte for byte.

import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import {
  decodeHello,
  decodeReceiverMessage,
  decodeRequest,
  encodeHello,
  encodePairRequest,
  encodeReceiverMessage,
  encodeRequest,
} from '../src/messages.js';
import { LIMITS_V1, PairingCode, PairingTicket } from '../src/model.js';
import {
  IK,
  NoiseError,
  NoiseErrorCode,
  NoiseHandshake,
  NoisePattern,
  XXpsk0,
  dh,
  isCanonicalPublicKey,
  newPrivateKey,
  publicKeyOf,
} from '../src/noise.js';
import {
  CONNECT_FIRST_MESSAGE_LENGTH,
  Mode,
  PAIR_FIRST_MESSAGE_LENGTH,
  WireError,
  fingerprint,
  preamble,
} from '../src/protocol.js';
import { frame } from '../src/stream.js';
import { bytes, loadJson } from './support/vectors.js';

const patterns = {
  Noise_IK_25519_ChaChaPoly_SHA256: IK,
  Noise_XXpsk3_25519_ChaChaPoly_SHA256: new NoisePattern('XXpsk3', false, [
    ['e'],
    ['e', 'ee', 's', 'es'],
    ['s', 'se', 'psk'],
  ]),
  Noise_NNpsk0_25519_ChaChaPoly_SHA256: new NoisePattern('NNpsk0', false, [
    ['psk', 'e'],
    ['e', 'ee'],
  ]),
};

const hex = (/** @type {Uint8Array} */ data) => Buffer.from(data).toString('hex');

/** Runs one official vector through both roles and compares every byte. */
function run(vector) {
  const pattern = patterns[vector.protocol_name];
  assert.ok(pattern, vector.protocol_name);
  const initiator = new NoiseHandshake({
    pattern,
    initiator: true,
    prologue: bytes(vector.init_prologue),
    staticKey: bytes(vector.init_static),
    remoteStaticKey: bytes(vector.init_remote_static),
    psks: (vector.init_psks ?? []).map(bytes),
    fixedEphemeral: bytes(vector.init_ephemeral),
  });
  const responder = new NoiseHandshake({
    pattern,
    initiator: false,
    prologue: bytes(vector.resp_prologue),
    staticKey: bytes(vector.resp_static),
    remoteStaticKey: bytes(vector.resp_remote_static),
    psks: (vector.resp_psks ?? []).map(bytes),
    fixedEphemeral: bytes(vector.resp_ephemeral),
  });
  const messages = vector.messages;
  let index = 0;
  while (!initiator.isComplete) {
    const payload = bytes(messages[index].payload);
    const expected = bytes(messages[index].ciphertext);
    const [writer, reader] = index % 2 === 0 ? [initiator, responder] : [responder, initiator];
    assert.equal(hex(writer.writeMessage(payload)), hex(expected));
    assert.equal(hex(reader.readMessage(expected)), hex(payload));
    index += 1;
  }
  assert.ok(responder.isComplete);
  const initiatorTransport = initiator.split();
  const responderTransport = responder.split();
  if (vector.handshake_hash) {
    assert.equal(hex(initiatorTransport.handshakeHash), vector.handshake_hash);
    assert.equal(hex(responderTransport.handshakeHash), vector.handshake_hash);
  }
  // Continue alternating, as the official vectors do.
  for (; index < messages.length; index += 1) {
    const payload = bytes(messages[index].payload);
    const expected = bytes(messages[index].ciphertext);
    const [sender, receiver] = index % 2 === 0 ? [initiatorTransport, responderTransport] : [responderTransport, initiatorTransport];
    assert.equal(hex(sender.seal(payload)), hex(expected));
    assert.equal(hex(receiver.open(expected)), hex(payload));
  }
}

test('the official Noise vectors pass', () => {
  const file = loadJson('spec/vectors/noise/cacophony-subset.json');
  assert.equal(file.vectors.length, 3);
  for (const vector of file.vectors) {
    run(vector);
  }
});

function establishedPair() {
  const deviceKey = newPrivateKey();
  const receiverKey = newPrivateKey();
  const prologue = Buffer.from('test');
  const device = new NoiseHandshake({ pattern: IK, initiator: true, prologue, staticKey: deviceKey, remoteStaticKey: publicKeyOf(receiverKey) });
  const receiver = new NoiseHandshake({ pattern: IK, initiator: false, prologue, staticKey: receiverKey });
  receiver.readMessage(device.writeMessage());
  device.readMessage(receiver.writeMessage());
  return [device.split(), receiver.split()];
}

const noiseError = (/** @type {string} */ code) => (/** @type {unknown} */ error) => error instanceof NoiseError && error.error === code;

test('a tampered transport message is rejected', () => {
  const [device, receiver] = establishedPair();
  const sealed = device.seal(Buffer.from('hello'));
  sealed[0] ^= 0x01;
  assert.throws(() => receiver.open(sealed), noiseError(NoiseErrorCode.decryptionFailed));
});

test('a replayed transport message is rejected', () => {
  const [device, receiver] = establishedPair();
  const sealed = device.seal(Buffer.from('hello'));
  assert.equal(receiver.open(sealed).toString(), 'hello');
  assert.throws(() => receiver.open(sealed), NoiseError);
});

test('a handshake used out of order is refused', () => {
  const handshake = new NoiseHandshake({ pattern: IK, initiator: false, prologue: Buffer.alloc(0), staticKey: newPrivateKey() });
  assert.throws(() => handshake.writeMessage(), noiseError(NoiseErrorCode.wrongHandshakeState));
  assert.throws(() => handshake.split(), noiseError(NoiseErrorCode.wrongHandshakeState));
});

test('a handshake splits only once', () => {
  const deviceKey = newPrivateKey();
  const receiverKey = newPrivateKey();
  const device = new NoiseHandshake({ pattern: IK, initiator: true, prologue: Buffer.alloc(0), staticKey: deviceKey, remoteStaticKey: publicKeyOf(receiverKey) });
  const receiver = new NoiseHandshake({ pattern: IK, initiator: false, prologue: Buffer.alloc(0), staticKey: receiverKey });
  receiver.readMessage(device.writeMessage());
  device.readMessage(receiver.writeMessage());
  device.split();
  assert.throws(() => device.split(), noiseError(NoiseErrorCode.alreadySplit));
});

test('a disposed transport refuses to seal rather than send in the clear', () => {
  const [device] = establishedPair();
  device.dispose();
  assert.throws(() => device.seal(Buffer.from('hello')), noiseError(NoiseErrorCode.nonceExhausted));
});

test('a mismatched prologue fails the handshake', () => {
  const receiverKey = newPrivateKey();
  const device = new NoiseHandshake({
    pattern: IK, initiator: true, prologue: preamble(Mode.connect), staticKey: newPrivateKey(), remoteStaticKey: publicKeyOf(receiverKey),
  });
  const receiver = new NoiseHandshake({ pattern: IK, initiator: false, prologue: preamble(Mode.pair), staticKey: receiverKey });
  assert.throws(() => receiver.readMessage(device.writeMessage()), NoiseError);
});

test('a pairing first message fails without the code', () => {
  const prologue = preamble(Mode.pair);
  const device = new NoiseHandshake({
    pattern: XXpsk0, initiator: true, prologue, staticKey: newPrivateKey(), psks: [PairingCode.generate().presharedKey()],
  });
  const receiver = new NoiseHandshake({
    pattern: XXpsk0, initiator: false, prologue, staticKey: newPrivateKey(), psks: [PairingCode.generate().presharedKey()],
  });
  assert.throws(() => receiver.readMessage(device.writeMessage()), noiseError(NoiseErrorCode.decryptionFailed));
});

// Public keys of small order produce an all-zero secret. Without this check an attacker could force a key that
// everyone knows.
for (const point of [
  '0000000000000000000000000000000000000000000000000000000000000000',
  '0100000000000000000000000000000000000000000000000000000000000000',
  'e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800',
  '5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157',
  'ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f',
  'edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f',
  'eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f',
]) {
  test(`a low-order public key is rejected: ${point.slice(0, 8)}`, () => {
    assert.throws(() => dh(newPrivateKey(), bytes(point)), noiseError(NoiseErrorCode.weakSharedSecret));
  });
}

test('the canonical key rule', () => {
  const key = publicKeyOf(newPrivateKey());
  assert.ok(isCanonicalPublicKey(key));
  key[31] |= 0x80;
  assert.ok(!isCanonicalPublicKey(key));
  assert.ok(!isCanonicalPublicKey(bytes('edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f')));
  assert.ok(isCanonicalPublicKey(bytes('ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f')));
  assert.ok(!isCanonicalPublicKey(Buffer.alloc(31)));
});

/** Replays a full integration-v1 exchange from the vectors, playing both sides with the listed keys. */
function replay(name, pattern, psk) {
  const vector = loadJson(`spec/vectors/integration-v1/${name}`);
  const prologue = bytes(vector.preamble_and_prologue);
  const psks = psk ? [bytes(vector.psk)] : [];
  if (psk) {
    const code = new PairingCode(vector.pairing_code_as_typed);
    assert.equal(code.canonical, vector.pairing_code_canonical);
    assert.equal(hex(code.presharedKey()), hex(psks[0]));
    const ticket = PairingTicket.parse(vector.qr_text);
    assert.ok(ticket.code.equals(code));
    assert.equal(ticket.fingerprint, fingerprint(bytes(vector.receiver_static_public)));
  }
  assert.equal(hex(publicKeyOf(bytes(vector.device_static_private))), vector.device_static_public);
  assert.equal(hex(publicKeyOf(bytes(vector.receiver_static_private))), vector.receiver_static_public);
  const device = new NoiseHandshake({
    pattern,
    initiator: true,
    prologue,
    staticKey: bytes(vector.device_static_private),
    remoteStaticKey: psk ? null : bytes(vector.receiver_static_public),
    psks,
    fixedEphemeral: bytes(vector.device_ephemeral_private),
  });
  const receiver = new NoiseHandshake({
    pattern,
    initiator: false,
    prologue,
    staticKey: bytes(vector.receiver_static_private),
    psks,
    fixedEphemeral: bytes(vector.receiver_ephemeral_private),
  });
  let deviceTransport = null;
  let receiverTransport = null;
  for (const entry of vector.frames) {
    const wire = bytes(entry.wire);
    if (entry.stage === 'preamble') {
      assert.equal(hex(wire), hex(prologue));
      continue;
    }
    const payload = bytes(entry.payload);
    const noise = bytes(entry.noise_message);
    assert.equal(hex(frame(noise)), hex(wire));
    const fromDevice = entry.direction === 'device_to_receiver';
    if (!device.isComplete || !receiver.isComplete) {
      const [writer, reader] = fromDevice ? [device, receiver] : [receiver, device];
      assert.equal(hex(writer.writeMessage(payload)), hex(noise), entry.stage);
      assert.equal(hex(reader.readMessage(noise)), hex(payload), entry.stage);
      if (device.isComplete && receiver.isComplete) {
        deviceTransport = device.split();
        receiverTransport = receiver.split();
        assert.equal(hex(deviceTransport.handshakeHash), vector.handshake_hash);
      }
    } else if (fromDevice) {
      assert.equal(hex(deviceTransport.seal(payload)), hex(noise), entry.stage);
      assert.equal(hex(receiverTransport.open(noise)), hex(payload), entry.stage);
      if (entry.stage === 'hello') {
        decodeHello(payload);
      } else {
        decodeRequest(payload);
      }
    } else {
      assert.equal(hex(receiverTransport.seal(payload)), hex(noise), entry.stage);
      assert.equal(hex(deviceTransport.open(noise)), hex(payload), entry.stage);
      decodeReceiverMessage(payload);
    }
  }
  assert.ok(device.isComplete);
}

test('the connection vector is reproduced byte for byte', () => replay('handshake-connect.json', IK, false));

test('the pairing vector is reproduced byte for byte', () => replay('handshake-pair.json', XXpsk0, true));

test('the vector keys are derived from public labels', () => {
  const vector = loadJson('spec/vectors/integration-v1/handshake-connect.json');
  const expected = createHash('sha256').update('erdigo-integration-v1 test-only key: device static', 'utf8').digest('hex');
  assert.equal(vector.device_static_private, expected);
  assert.equal(fingerprint(bytes(vector.device_static_public)), vector.device_fingerprint);
  assert.equal(fingerprint(bytes(vector.receiver_static_public)), vector.receiver_fingerprint);
});

test('the first connect message carries the counter and has the required length', () => {
  const vector = loadJson('spec/vectors/integration-v1/handshake-connect.json');
  const first = vector.frames[1];
  assert.equal(bytes(first.noise_message).length, CONNECT_FIRST_MESSAGE_LENGTH);
  assert.equal(bytes(first.payload).readBigUInt64BE(0), BigInt(vector.connect_counter));
  const pair = loadJson('spec/vectors/integration-v1/handshake-pair.json');
  assert.equal(bytes(pair.frames[1].noise_message).length, PAIR_FIRST_MESSAGE_LENGTH);
});

test('messages are encoded byte for byte like the vectors', () => {
  const payload = (/** @type {string} */ file, /** @type {string} */ stage) =>
    loadJson(`spec/vectors/integration-v1/${file}`).frames.find((entry) => entry.stage === stage).payload_text;
  const text = (/** @type {Buffer} */ data) => data.toString('utf8');
  assert.equal(text(encodeHello({ versions: [1], sdk: 'erdigo-test/1.0' })), payload('handshake-connect.json', 'hello'));
  assert.equal(
    text(encodeReceiverMessage({ t: 'welcome', welcome: { version: 1, lastSeq: 41, kinds: ['pin', 'track'], limits: LIMITS_V1 } })),
    payload('handshake-connect.json', 'welcome'),
  );
  assert.equal(
    text(encodeRequest(42, { t: 'put', kind: 'pin', id: 'gate', at: { lat: 37.5796, lon: 126.977 }, title: 'Palace gate' })),
    payload('handshake-connect.json', 'request'),
  );
  assert.equal(text(encodeReceiverMessage({ t: 'ack', seq: 42, dup: false })), payload('handshake-connect.json', 'ack'));
  assert.equal(
    text(encodePairRequest({ versions: [1], sdk: 'erdigo-test/1.0', name: 'Test device', kinds: ['pin', 'track'] })),
    payload('handshake-pair.json', 'handshake_3'),
  );
  assert.equal(text(encodeReceiverMessage({ t: 'pair_status', status: 'pending' })), payload('handshake-pair.json', 'pair_status_pending'));
  assert.equal(
    text(encodeReceiverMessage({ t: 'pair_status', status: 'approved', kinds: ['pin'], counter: 0 })),
    payload('handshake-pair.json', 'pair_status_approved'),
  );
});

test('the message vectors are accepted and refused with the right codes', () => {
  const file = loadJson('spec/vectors/integration-v1/messages.json');
  for (const entry of file.valid) {
    const envelope = decodeRequest(Buffer.from(entry.json, 'utf8'));
    assert.ok(envelope.seq >= 1, entry.name);
    // What decodes also encodes back to bytes that decode to the same request.
    const again = decodeRequest(encodeRequest(envelope.seq, envelope.request));
    assert.deepEqual(again, envelope, entry.name);
  }
  assert.ok(file.invalid.length > 0);
  const refusals = [
    ...file.invalid.map((entry) => [entry.name, entry.code, Buffer.from(entry.json, 'utf8')]),
    ...file.invalid_bytes.map((entry) => [entry.name, entry.code, bytes(entry.hex)]),
  ];
  for (const [name, code, data] of refusals) {
    assert.throws(
      () => decodeRequest(data),
      (error) => error instanceof WireError && error.code === code,
      `${name}: expected ${code}`,
    );
  }
});
