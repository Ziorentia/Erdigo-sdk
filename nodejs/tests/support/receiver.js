// A small receiver for the tests. It speaks integration-v1 over real TCP on the loopback interface, pairs devices
// with a code, and keeps sequence numbers as spec section 7 says, so that the client's handling of every answer can
// be checked, including answers the mocks of the other editions would not give by themselves. It is not a mock
// receiver for people to use: it checks no limits, stores no items, and is not hardened.

import { createServer } from 'node:net';
import { createHash } from 'node:crypto';
import { decodeHello, decodePairRequest, decodeRequest, encodeReceiverMessage } from '../../src/messages.js';
import { LIMITS_V1, PairingCode, PairingTicket } from '../../src/model.js';
import { IK, NoiseHandshake, XXpsk0, newPrivateKey, publicKeyOf } from '../../src/noise.js';
import { MAX_HANDSHAKE_FRAME_LENGTH, Mode, fingerprint, parsePreamble } from '../../src/protocol.js';
import { ByteStream } from '../../src/stream.js';

/**
 * @typedef {object} Device
 * @property {string} fingerprint
 * @property {string[]} kinds
 * @property {number} counter
 * @property {number} lastSeq
 * @property {string | null} lastHash
 * @property {Buffer | null} lastAnswer
 */

export class TestReceiver {
  constructor({ kinds = null, decide = null, limits = LIMITS_V1 } = {}) {
    this.privateKey = newPrivateKey();
    this.publicKey = publicKeyOf(this.privateKey);
    this.fingerprint = fingerprint(this.publicKey);
    /** Which kinds a pairing is allowed. Null allows what was asked for. */
    this.allowedKinds = kinds;
    /** Answers a pairing request: 'approve', 'deny', or 'hold' to never answer. */
    this.decide = decide ?? (() => 'approve');
    this.limits = limits;
    /** @type {Map<string, Device>} */
    this.devices = new Map();
    /** @type {Array<{ type: string, [key: string]: unknown }>} */
    this.events = [];
    /** @type {((envelope: { seq: number, request: any }) => any) | null} */
    this.answerOverride = null;
    /** Ends the connection after applying a request, before answering it, when this returns true. */
    /** @type {((envelope: { seq: number, request: any }) => boolean) | null} */
    this.dropBeforeAnswer = null;
    /** How many of the next connections are closed at once, without a word, before any is answered. */
    this.silentConnections = 0;
    /** Every request read, in order. */
    this.requests = [];
    this.code = PairingCode.generate();
    this.connections = new Set();
    this.server = createServer((socket) => {
      this.connections.add(socket);
      socket.on('close', () => this.connections.delete(socket));
      this.events.push({ type: 'connection' });
      if (this.silentConnections > 0) {
        this.silentConnections -= 1;
        this.events.push({ type: 'silent', reason: 'dropped' });
        socket.destroy();
        return;
      }
      this.serve(new ByteStream({ socket })).catch(() => {}).finally(() => socket.destroy());
    });
  }

  async start() {
    await new Promise((resolve) => this.server.listen(0, '127.0.0.1', () => resolve(undefined)));
    this.port = /** @type {import('node:net').AddressInfo} */ (this.server.address()).port;
    return this.port;
  }

  async stop() {
    for (const socket of this.connections) {
      socket.destroy();
    }
    await new Promise((resolve) => this.server.close(() => resolve(undefined)));
  }

  /** The QR text for the current code. */
  get qrText() {
    return /** @type {string} */ (new PairingTicket(this.code, this.fingerprint).uri);
  }

  /** @param {string} type */
  count(type) {
    return this.events.filter((event) => event.type === type).length;
  }

  /** @param {ByteStream} stream */
  async serve(stream) {
    const mode = parsePreamble(await stream.readExactly(5));
    const first = await stream.readFrame(MAX_HANDSHAKE_FRAME_LENGTH);
    if (mode === Mode.pair) {
      await this.servePairing(stream, first);
    } else if (mode === Mode.connect) {
      await this.serveConnection(stream, first);
    }
  }

  /**
   * @param {ByteStream} stream
   * @param {Buffer} first
   */
  async servePairing(stream, first) {
    const prologue = Buffer.from([0x45, 0x49, 0x4e, 0x31, Mode.pair]);
    const handshake = new NoiseHandshake({ pattern: XXpsk0, initiator: false, prologue, staticKey: this.privateKey, psks: [this.code.presharedKey()] });
    try {
      handshake.readMessage(first);
    } catch {
      this.events.push({ type: 'silent', reason: 'wrong code' });
      return;
    }
    await stream.sendFrame(handshake.writeMessage());
    let third;
    try {
      third = await stream.readFrame(MAX_HANDSHAKE_FRAME_LENGTH);
    } catch {
      this.events.push({ type: 'stopped after message 2' });
      return;
    }
    const payload = handshake.readMessage(third);
    const transport = handshake.split();
    const request = decodePairRequest(payload);
    const device = fingerprint(/** @type {Buffer} */ (handshake.remoteStaticKey));
    this.events.push({ type: 'pairing requested', device, name: request.name });
    await stream.sendFrame(transport.seal(encodeReceiverMessage({ t: 'pair_status', status: 'pending' })));
    const decision = await this.decide(request);
    if (decision === 'hold') {
      await new Promise(() => {});
    }
    if (decision === 'code_reused') {
      const error = { code: 'code_reused', seq: null, lastSeq: null, retryAfterMs: null, supported: null, fatal: true };
      await stream.sendFrame(transport.seal(encodeReceiverMessage({ t: 'error', error })));
      stream.finishSending();
      return;
    }
    if (decision === 'deny') {
      await stream.sendFrame(transport.seal(encodeReceiverMessage({ t: 'pair_status', status: 'denied' })));
      stream.finishSending();
      return;
    }
    const kinds = (this.allowedKinds ?? request.kinds).filter((kind) => request.kinds.includes(kind));
    const known = this.devices.get(device);
    this.devices.set(device, { fingerprint: device, kinds, counter: known?.counter ?? 0, lastSeq: known?.lastSeq ?? 0, lastHash: null, lastAnswer: null });
    this.events.push({ type: 'paired', device });
    await stream.sendFrame(transport.seal(encodeReceiverMessage({ t: 'pair_status', status: 'approved', kinds, counter: known?.counter ?? 0 })));
    stream.finishSending();
  }

  /**
   * @param {ByteStream} stream
   * @param {Buffer} first
   */
  async serveConnection(stream, first) {
    const prologue = Buffer.from([0x45, 0x49, 0x4e, 0x31, Mode.connect]);
    const handshake = new NoiseHandshake({ pattern: IK, initiator: false, prologue, staticKey: this.privateKey });
    let payload;
    try {
      payload = handshake.readMessage(first);
    } catch {
      this.events.push({ type: 'silent', reason: 'not a valid first message' });
      return;
    }
    const device = this.devices.get(fingerprint(/** @type {Buffer} */ (handshake.remoteStaticKey)));
    const counter = payload.length === 8 ? Number(payload.readBigUInt64BE(0)) : -1;
    if (!device || counter <= device.counter) {
      this.events.push({ type: 'silent', reason: device ? 'replayed counter' : 'unknown device' });
      return;
    }
    device.counter = counter;
    await stream.sendFrame(handshake.writeMessage());
    const transport = handshake.split();
    decodeHello(transport.open(await stream.readFrame()));
    this.events.push({ type: 'connected', device: device.fingerprint });
    const send = (/** @type {any} */ message) => stream.sendFrame(transport.seal(encodeReceiverMessage(message)));
    await send({ t: 'welcome', welcome: { version: 1, lastSeq: device.lastSeq, kinds: device.kinds, limits: this.limits } });
    try {
      for (;;) {
        const data = transport.open(await stream.readFrame());
        const envelope = decodeRequest(data);
        this.requests.push(envelope);
        const override = this.answerOverride?.(envelope) ?? null;
        if (override !== null) {
          await send(override);
          if (override.t === 'error' && override.error.fatal) {
            stream.finishSending();
            return;
          }
          continue;
        }
        const hash = createHash('sha256').update(data).digest('hex');
        if (envelope.seq === device.lastSeq && device.lastAnswer !== null) {
          if (hash === device.lastHash) {
            const stored = JSON.parse(device.lastAnswer.toString());
            await send(stored.t === 'ack' ? { t: 'ack', seq: stored.seq, dup: true } : { t: 'error', error: stored.error });
          } else {
            await send({ t: 'error', error: { code: 'seq_conflict', seq: envelope.seq, lastSeq: null, retryAfterMs: null, supported: null, fatal: false } });
          }
          continue;
        }
        if (envelope.seq <= device.lastSeq) {
          await send({ t: 'error', error: { code: 'stale_seq', seq: envelope.seq, lastSeq: device.lastSeq, retryAfterMs: null, supported: null, fatal: false } });
          continue;
        }
        const answer = { t: 'ack', seq: envelope.seq, dup: false };
        device.lastSeq = envelope.seq;
        device.lastHash = hash;
        device.lastAnswer = Buffer.from(JSON.stringify(answer));
        this.events.push({ type: 'applied', seq: envelope.seq, request: envelope.request.t });
        if (this.dropBeforeAnswer?.(envelope)) {
          return;
        }
        await send(answer);
      }
    } finally {
      this.events.push({ type: 'disconnected', device: device.fingerprint });
    }
  }
}

/**
 * @param {Partial<import('../../src/messages.js').ReceiverError> & { code: string }} fields
 */
export function errorAnswer(fields) {
  return { t: 'error', error: { seq: null, lastSeq: null, retryAfterMs: null, supported: null, fatal: false, ...fields } };
}
