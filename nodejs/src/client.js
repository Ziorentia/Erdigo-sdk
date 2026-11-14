// Writes items to one paired receiver.

import { createHash, randomInt } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import { ReceiverTarget, browse, knownEndpoints } from './discovery.js';
import { DeviceIdentity, ErdigoError, ErdigoErrorKind, pairingFingerprint, receiverPairing } from './identity.js';
import { decodeReceiverMessage, encodeHello, encodeRequest } from './messages.js';
import {
  LIMITS_V1,
  MARKER_COLORS,
  TokenBucket,
  detectPhotoType,
  monotonicNow,
  validateCoordinate,
  validateItemId,
  validateNote,
  validateTitle,
  validateTrackPoint,
} from './model.js';
import { IK, NoiseHandshake } from './noise.js';
import { SDK_NAME } from './pairing.js';
import {
  ErrorCode,
  MAX_CHUNK_BYTES,
  MAX_HANDSHAKE_FRAME_LENGTH,
  MAX_POINTS_PER_MESSAGE,
  MAX_SEQUENCE,
  Mode,
  VERSION,
  WireError,
  isKnownErrorCode,
  isRetryableErrorCode,
  preamble,
} from './protocol.js';
import { ByteStream, frame } from './stream.js';

/** @typedef {import('./messages.js').Request} Request */
/** @typedef {import('./messages.js').ReceiverError} ReceiverError */
/** @typedef {import('./model.js').Coordinate} Coordinate */
/** @typedef {import('./model.js').Limits} Limits */
/** @typedef {import('./model.js').ItemKind} ItemKind */

/**
 * How long a request keeps trying while the receiver cannot be reached, for example while the computer is asleep or
 * the app is closed.
 *
 * The time allowed covers one outage: it counts from when the connection was lost, and starts again once a connection
 * is made. Time spent connected, such as waiting to stay within the receiver's limits, does not count. So this limits
 * how long a request waits for a receiver that cannot be reached, not how long it takes in all: if connections keep
 * being made and then lost before the answer arrives, the request keeps going, and requests made meanwhile wait their
 * turn. To limit the whole time, pass a `signal`, for example `AbortSignal.timeout(120_000)`.
 * @typedef {{ kind: 'none' } | { kind: 'forever' } | { kind: 'upTo', ms: number }} RetryPolicy
 */

export const RetryPolicy = Object.freeze({
  /** Fail at the first connection problem. @type {RetryPolicy} */
  none: Object.freeze({ kind: 'none' }),
  /** Keep trying until the request is stopped. @type {RetryPolicy} */
  forever: Object.freeze({ kind: 'forever' }),
  /**
   * Keep trying for this long, counted from when the connection was lost. The count starts again each time a
   * connection is made.
   * @param {number} ms
   * @returns {RetryPolicy}
   */
  upTo(ms) {
    if (typeof ms !== 'number' || !(ms >= 0)) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the time to keep trying must be zero or more milliseconds' });
    }
    return Object.freeze({ kind: 'upTo', ms });
  },
});

/** A problem with the connection, as opposed to an answer from the receiver. */
class ConnectionLost extends Error {
  constructor() {
    super('the connection was lost');
    this.name = 'ConnectionLost';
  }
}

/**
 * How many `rate_limited` answers one request will accept before giving up. An honest receiver needs only one or
 * two, so this only stops a receiver that never relents.
 */
const MAX_RATE_LIMITED_ANSWERS = 20;
const MINIMUM_RATE_LIMIT_WAIT = 0.05;

/**
 * Waits, and rejects with the signal's own reason if it is stopped.
 * @param {number} ms
 * @param {AbortSignal} [signal]
 */
async function sleep(ms, signal) {
  try {
    await delay(ms, undefined, { signal });
  } catch (error) {
    if (signal?.aborted) {
      throw signal.reason;
    }
    throw error;
  }
}

/**
 * 1, 2, 4 ... up to 60 seconds, with 20 percent jitter so several devices do not retry in step. In seconds.
 * @param {number} attempt
 */
export function backoff(attempt) {
  const base = Math.min(60, 2 ** Math.min(attempt, 6));
  const jitter = 0.8 + 0.4 * (randomInt(0, 1_000_001) / 1_000_000);
  return base * jitter;
}

/** An asynchronous lock that admits waiters strictly in the order they arrived, and lets a waiter stop waiting. */
export class FifoLock {
  constructor() {
    this.busy = false;
    /** @type {Array<{ resolve: () => void }>} */
    this.waiters = [];
  }

  /** @param {AbortSignal} [signal] */
  async acquire(signal) {
    signal?.throwIfAborted();
    if (!this.busy) {
      this.busy = true;
      return;
    }
    await new Promise((resolve, reject) => {
      const waiter = {
        resolve: () => {
          signal?.removeEventListener('abort', onAbort);
          resolve(undefined);
        },
      };
      const onAbort = () => {
        const index = this.waiters.indexOf(waiter);
        if (index >= 0) {
          this.waiters.splice(index, 1);
          reject(signal?.reason);
        }
      };
      this.waiters.push(waiter);
      signal?.addEventListener('abort', onAbort, { once: true });
    });
  }

  release() {
    const next = this.waiters.shift();
    if (next) {
      next.resolve();
    } else {
      this.busy = false;
    }
  }
}

/**
 * Turns a broken rule about an argument into the error the caller sees.
 * @template T
 * @param {() => T} body
 * @returns {T}
 */
function validated(body) {
  try {
    return body();
  } catch (error) {
    if (error instanceof WireError) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: error.detail, cause: error });
    }
    throw error;
  }
}

/** @param {string | null | undefined} title */
function validatedTitle(title) {
  if (title !== undefined && title !== null) {
    validated(() => validateTitle(title));
  }
  return title ?? null;
}

/**
 * Whether the receiver closes the connection after this answer. `paused` and `replaced` always do, even if a receiver
 * leaves out the flag.
 * @param {ReceiverError} error
 */
function endsConnection(error) {
  return error.fatal || error.code === ErrorCode.paused || error.code === ErrorCode.replaced;
}

/**
 * @typedef {object} Session
 * @property {ByteStream} stream
 * @property {import('./noise.js').NoiseTransport} transport
 * @property {import('./messages.js').Welcome} welcome
 * @property {number} lastSeq
 */

/**
 * @typedef {object} ClientOptions
 * @property {DeviceIdentity} identity this device's key
 * @property {import('./identity.js').ReceiverPairing} pairing the receiver to write to
 * @property {import('./store.js').DeviceStore} store where the connection counter and the receiver's last address
 *   are kept
 * @property {import('./discovery.js').ReceiverTarget} [target] where to look for the receiver. By default the last
 *   known address, then the local network.
 * @property {RetryPolicy} [retry] how long each outage may last before a request fails with
 *   {@link ErdigoErrorKind.noAnswer}. By default 30 seconds. This does not limit a request's total time.
 * @property {number} [connectTimeoutMs] how long one attempt to connect may take. By default 5 seconds.
 * @property {number} [ackTimeoutMs] how long to wait for the answer to one message. By default 15 seconds.
 */

/**
 * Writes items to one paired receiver.
 *
 * Requests run strictly one at a time, in the order they were made. A request resolves once the receiver has
 * acknowledged it. If the connection breaks, the request is sent again with the same sequence number, so the receiver
 * applies it at most once. Call {@link close} to close the connection.
 */
export class ErdigoClient {
  /** @param {ClientOptions} options */
  constructor({ identity, pairing, store, target = ReceiverTarget.discover, retry = RetryPolicy.upTo(30_000), connectTimeoutMs = 5000, ackTimeoutMs = 15_000 }) {
    if (!(identity instanceof DeviceIdentity)) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the identity must be a DeviceIdentity' });
    }
    if (!pairing || !store) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'a pairing and a store are needed' });
    }
    this.identity = identity;
    this.pairing = receiverPairing(pairing);
    this.store = store;
    this.target = target;
    this.retry = retry;
    this.connectTimeoutMs = connectTimeoutMs;
    this.ackTimeoutMs = ackTimeoutMs;
    this.gate = new FifoLock();
    /** @type {Session | null} */
    this.session = null;
    this.lastSessionAt = -Infinity;
    // These outlive a connection, because the receiver's own count does too.
    /** @type {string | null} */
    this.pacedLimits = null;
    /** @type {TokenBucket | null} */
    this.messageBucket = null;
    /** @type {TokenBucket | null} */
    this.pointBucket = null;
  }

  /**
   * The limits the receiver announced on the current connection.
   * @returns {Limits | null}
   */
  get limits() {
    return this.session?.welcome.limits ?? null;
  }

  /**
   * The item kinds the person allowed, as announced on the current connection.
   * @returns {ItemKind[] | null}
   */
  get allowedKinds() {
    return this.session?.welcome.kinds ?? null;
  }

  /**
   * Connects now rather than at the first request. Rejects with {@link ErdigoErrorKind.noAnswer} if the retry policy
   * runs out.
   * @param {{ signal?: AbortSignal }} [options]
   */
  async connect({ signal } = {}) {
    await this.serialised(() => this.retrying(async () => {
      await this.currentSession(signal);
    }, signal), signal);
  }

  /** Closes the connection. A later request connects again. Do not call this while a request is still running. */
  close() {
    const session = this.session;
    this.session = null;
    if (session !== null) {
      session.stream.dispose();
      session.transport.dispose();
    }
  }

  /** @param {{ signal?: AbortSignal }} [options] */
  async ping({ signal } = {}) {
    await this.perform({ t: 'ping' }, null, 0, signal);
  }

  /**
   * Creates the pin, or replaces it if this device already has one with this id.
   * @param {string} id
   * @param {Coordinate} at
   * @param {{ title?: string | null, signal?: AbortSignal }} [options]
   */
  async putPin(id, at, { title = null, signal } = {}) {
    const request = validated(() => ({ t: 'put', kind: 'pin', id: validateItemId(id), at: validateCoordinate(at), title: validatedTitle(title) }));
    await this.perform(/** @type {Request} */ (request), 'pin', 0, signal);
  }

  /**
   * @param {string} id
   * @param {Coordinate} at
   * @param {{ title?: string | null, color?: import('./model.js').MarkerColor | null, signal?: AbortSignal }} [options]
   */
  async putMarker(id, at, { title = null, color = null, signal } = {}) {
    const request = validated(() => {
      if (color !== null && !MARKER_COLORS.includes(color)) {
        throw new WireError(ErrorCode.invalidMessage, `a marker colour is one of: ${MARKER_COLORS.join(', ')}`);
      }
      return { t: 'put', kind: 'marker', id: validateItemId(id), at: validateCoordinate(at), title: validatedTitle(title), color };
    });
    await this.perform(/** @type {Request} */ (request), 'marker', 0, signal);
  }

  /**
   * A note is plain text. The receiver shows links as text and never opens them.
   * @param {string} id
   * @param {Coordinate} at
   * @param {string} text
   * @param {{ signal?: AbortSignal }} [options]
   */
  async putNote(id, at, text, { signal } = {}) {
    const request = validated(() => {
      validateNote(text);
      return { t: 'put', kind: 'note', id: validateItemId(id), at: validateCoordinate(at), text };
    });
    await this.perform(/** @type {Request} */ (request), 'note', 0, signal);
  }

  /**
   * Moves a pin, marker, note or photo. Tracks cannot be moved.
   * @param {string} id
   * @param {Coordinate} to
   * @param {{ signal?: AbortSignal }} [options]
   */
  async move(id, to, { signal } = {}) {
    const request = validated(() => ({ t: 'move', id: validateItemId(id), to: validateCoordinate(to) }));
    await this.perform(/** @type {Request} */ (request), null, 0, signal);
  }

  /**
   * Deletes one of this device's items. Deleting an id that does not exist succeeds.
   * @param {string} id
   * @param {{ signal?: AbortSignal }} [options]
   */
  async delete(id, { signal } = {}) {
    const request = validated(() => ({ t: 'delete', id: validateItemId(id) }));
    await this.perform(/** @type {Request} */ (request), null, 0, signal);
  }

  /**
   * Deletes every item this device created on this receiver, and nothing else.
   * @param {{ signal?: AbortSignal }} [options]
   */
  async deleteAll({ signal } = {}) {
    await this.perform({ t: 'delete_all' }, null, 0, signal);
  }

  /**
   * Appends points to a track, creating it if needed. Long lists are split into several messages, and no other
   * request runs in between.
   * @param {string} id
   * @param {import('./model.js').TrackPoint[]} points
   * @param {{ title?: string | null, signal?: AbortSignal }} [options]
   */
  async appendTrack(id, points, { title = null, signal } = {}) {
    const { itemId, all, firstTitle } = validated(() => {
      if (!Array.isArray(points) || points.length === 0) {
        throw new WireError(ErrorCode.invalidMessage, 'a track append needs at least one point');
      }
      return { itemId: validateItemId(id), all: points.map(validateTrackPoint), firstTitle: validatedTitle(title) };
    });
    await this.serialised(async () => {
      let pendingTitle = firstTitle;
      for (let start = 0; start < all.length; start += MAX_POINTS_PER_MESSAGE) {
        const batch = all.slice(start, start + MAX_POINTS_PER_MESSAGE);
        await this.performHoldingLock({ t: 'track_append', id: itemId, title: pendingTitle, points: batch }, 'track', batch.length, signal);
        pendingTitle = null;
      }
    }, signal);
  }

  /**
   * @param {string} id
   * @param {{ signal?: AbortSignal }} [options]
   */
  async finishTrack(id, { signal } = {}) {
    const request = validated(() => ({ t: 'track_finish', id: validateItemId(id) }));
    await this.perform(/** @type {Request} */ (request), 'track', 0, signal);
  }

  /**
   * Sends a JPEG or PNG photo. The format is detected from the data, and the receiver removes metadata such as EXIF
   * location when it stores the photo.
   * @param {string} id
   * @param {Coordinate} at
   * @param {Uint8Array} data
   * @param {{ title?: string | null, signal?: AbortSignal }} [options]
   */
  async putPhoto(id, at, data, { title = null, signal } = {}) {
    const { itemId, coordinate, checkedTitle } = validated(() => ({
      itemId: validateItemId(id),
      coordinate: validateCoordinate(at),
      checkedTitle: validatedTitle(title),
    }));
    if (!(data instanceof Uint8Array)) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'a photo must be JPEG or PNG data' });
    }
    const type = detectPhotoType(data);
    if (type === null) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'a photo must be JPEG or PNG data' });
    }
    if (data.length > LIMITS_V1.photoBytes) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: `a photo is at most ${LIMITS_V1.photoBytes} bytes` });
    }
    const bytes = Buffer.from(data);
    const chunkCount = Math.ceil(bytes.length / MAX_CHUNK_BYTES);
    /** @type {Request} */
    const begin = {
      t: 'photo_begin',
      id: itemId,
      at: coordinate,
      title: checkedTitle,
      type,
      size: bytes.length,
      sha256: createHash('sha256').update(bytes).digest('hex'),
      chunks: chunkCount,
    };
    await this.serialised(() => this.retrying(async () => {
      // The receiver drops a partial upload when the connection ends, so restart from the beginning.
      const current = await this.currentSession(signal);
      if (!current.welcome.kinds.includes('photo')) {
        throw new ErdigoError(ErdigoErrorKind.notPermitted, { itemKind: 'photo' });
      }
      await this.exchangeNext(begin, current, signal);
      for (let index = 0; index < chunkCount; index += 1) {
        const slice = bytes.subarray(index * MAX_CHUNK_BYTES, Math.min(bytes.length, (index + 1) * MAX_CHUNK_BYTES));
        await this.exchangeNext({ t: 'photo_chunk', id: itemId, index, data: slice }, current, signal);
      }
      await this.exchangeNext({ t: 'photo_end', id: itemId }, current, signal);
    }, signal), signal);
  }

  // Ordering

  /**
   * @param {() => Promise<void>} body
   * @param {AbortSignal} [signal]
   */
  async serialised(body, signal) {
    await this.gate.acquire(signal);
    try {
      await body();
    } finally {
      this.gate.release();
    }
  }

  // Requests

  /**
   * @param {Request} request
   * @param {ItemKind | null} kind
   * @param {number} points
   * @param {AbortSignal} [signal]
   */
  perform(request, kind, points, signal) {
    return this.serialised(() => this.performHoldingLock(request, kind, points, signal), signal);
  }

  /**
   * @param {Request} request
   * @param {ItemKind | null} kind
   * @param {number} points
   * @param {AbortSignal} [signal]
   */
  async performHoldingLock(request, kind, points, signal) {
    /** @type {number | null} */
    let assignedSeq = null;
    // A resend is the same request, so it does not wait for its allowance a second time.
    let pointsArePaid = false;
    await this.retrying(async () => {
      const current = await this.currentSession(signal);
      if (kind !== null && !current.welcome.kinds.includes(kind)) {
        throw new ErdigoError(ErdigoErrorKind.notPermitted, { itemKind: kind });
      }
      // A request keeps its number across reconnections, so the receiver can recognise a resend.
      if (assignedSeq !== null && current.lastSeq > assignedSeq) {
        throw new ErdigoError(ErdigoErrorKind.sequenceConflict, { lastSeq: current.lastSeq });
      }
      if (!pointsArePaid) {
        await this.pacePoints(points, signal);
        pointsArePaid = true;
      }
      const seq = assignedSeq ?? current.lastSeq + 1;
      assignedSeq = seq;
      await this.exchange(request, seq, current, signal);
    }, signal);
  }

  /**
   * Waits as long as asked, up to the longest wait a receiver may ask for.
   * @param {number} seconds
   * @param {AbortSignal} [signal]
   */
  static wait(seconds, signal) {
    return sleep(Math.max(0, Math.min(seconds, 3600)) * 1000, signal);
  }

  /**
   * @param {number} count
   * @param {AbortSignal} [signal]
   */
  async pacePoints(count, signal) {
    if (count <= 0) {
      return;
    }
    for (let wait = this.pointBucket?.take(monotonicNow(), count) ?? null; wait !== null; wait = this.pointBucket?.take(monotonicNow(), count) ?? null) {
      await ErdigoClient.wait(wait, signal);
    }
  }

  /** @param {AbortSignal} [signal] */
  async paceMessage(signal) {
    for (let wait = this.messageBucket?.take(monotonicNow()) ?? null; wait !== null; wait = this.messageBucket?.take(monotonicNow()) ?? null) {
      await ErdigoClient.wait(wait, signal);
    }
  }

  /**
   * @param {Request} request
   * @param {Session} current
   * @param {AbortSignal} [signal]
   */
  exchangeNext(request, current, signal) {
    return this.exchange(request, current.lastSeq + 1, current, signal);
  }

  /**
   * Sends one request and waits for its answer, honouring the receiver's rate limit.
   * @param {Request} request
   * @param {number} seq
   * @param {Session} current
   * @param {AbortSignal} [signal]
   */
  async exchange(request, seq, current, signal) {
    if (seq > MAX_SEQUENCE) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, {
        detail: 'the sequence number is exhausted; remove this device on the receiver and pair it again',
      });
    }
    const plaintext = encodeRequest(seq, request);
    let rateLimitedAnswers = 0;
    for (;;) {
      await this.paceMessage(signal);
      let answer;
      try {
        const sealed = current.transport.seal(plaintext);
        current.stream.armDeadline(this.ackTimeoutMs);
        await current.stream.sendFrame(sealed, signal);
        const data = await current.stream.readFrame(undefined, signal);
        current.stream.disarmDeadline();
        answer = decodeReceiverMessage(current.transport.open(data));
      } catch (error) {
        this.close();
        if (signal?.aborted) {
          throw signal.reason;
        }
        if (error instanceof WireError) {
          throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: error.detail, cause: error });
        }
        if (error instanceof ErdigoError) {
          throw error;
        }
        throw new ConnectionLost();
      }
      if (answer.t === 'ack') {
        if (answer.seq !== seq) {
          this.close();
          throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'the acknowledgement has the wrong sequence number' });
        }
        current.lastSeq = seq;
        return;
      }
      if (answer.t !== 'error') {
        this.close();
        throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'unexpected message in answer to a request' });
      }
      const { error } = answer;
      const ends = endsConnection(error);
      // An answer about one request must be about this one. An answer that ends the connection is about the
      // connection, so it ends it whatever number it carries.
      const answersThisRequest = error.seq === null || error.seq === seq;
      if (!ends && !answersThisRequest) {
        this.close();
        throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'the error answers a different sequence number' });
      }
      // Only an answer the receiver will keep the connection open after is worth waiting for.
      if (!ends && error.code === ErrorCode.rateLimited) {
        rateLimitedAnswers += 1;
        if (rateLimitedAnswers > MAX_RATE_LIMITED_ANSWERS) {
          this.close();
          throw new ErdigoError(ErdigoErrorKind.rejected, { receiverError: error });
        }
        const wait = (error.retryAfterMs ?? 1000) / 1000;
        await ErdigoClient.wait(Math.max(wait, MINIMUM_RATE_LIMIT_WAIT), signal);
        continue;
      }
      this.handle(error, seq, answersThisRequest, ends, current);
    }
  }

  /**
   * Turns an error answer into the error the caller sees. It always throws.
   * @param {ReceiverError} error
   * @param {number} seq
   * @param {boolean} answersThisRequest
   * @param {boolean} ends
   * @param {Session} current
   * @returns {never}
   */
  handle(error, seq, answersThisRequest, ends, current) {
    // A number from an answer to some other request says nothing about where this connection stands.
    if (answersThisRequest && error.lastSeq !== null) {
      current.lastSeq = Math.max(current.lastSeq, error.lastSeq);
    }
    if (ends) {
      this.close();
    }
    if (error.code === ErrorCode.paused) {
      throw new ConnectionLost();
    }
    if (error.code === ErrorCode.replaced) {
      throw new ErdigoError(ErdigoErrorKind.replaced, { receiverError: error });
    }
    if (error.code === ErrorCode.staleSeq || error.code === ErrorCode.seqConflict) {
      throw new ErdigoError(ErdigoErrorKind.sequenceConflict, { receiverError: error, lastSeq: current.lastSeq });
    }
    if (!ends && error.seq === seq && isKnownErrorCode(error.code) && !isRetryableErrorCode(error.code)) {
      // The receiver used up this number on the failed request.
      current.lastSeq = seq;
    }
    throw new ErdigoError(ErdigoErrorKind.rejected, { receiverError: error });
  }

  // Connection

  /**
   * @param {AbortSignal} [signal]
   * @returns {Promise<Session>}
   */
  async currentSession(signal) {
    if (this.session !== null) {
      return this.session;
    }
    // The remembered address comes first, because looking around the network takes a moment.
    const created = await this.firstAnswer(knownEndpoints(this.target, this.pairing), signal);
    if (created !== null) {
      return created;
    }
    if (this.target.kind === 'discover') {
      const found = await this.firstAnswer(await browse({ signal }), signal);
      if (found !== null) {
        return found;
      }
    }
    throw new ConnectionLost();
  }

  /**
   * Tries each endpoint in turn. A receiver that stays silent is simply not the right one.
   * @param {import('./discovery.js').ReceiverEndpoint[]} endpoints
   * @param {AbortSignal} [signal]
   * @returns {Promise<Session | null>}
   */
  async firstAnswer(endpoints, signal) {
    for (const endpoint of endpoints) {
      let created;
      try {
        created = await this.handshake(endpoint, signal);
      } catch (error) {
        if (error instanceof ConnectionLost) {
          continue;
        }
        throw error;
      }
      this.session = created;
      this.lastSessionAt = monotonicNow();
      this.preparePacing(created.welcome.limits);
      this.rememberAddress(created.stream);
      return created;
    }
    return null;
  }

  /** @param {Limits} limits */
  preparePacing(limits) {
    const key = JSON.stringify(limits);
    if (this.pacedLimits === key) {
      return;
    }
    this.pacedLimits = key;
    const now = monotonicNow();
    this.messageBucket = new TokenBucket(limits.messageBurst, limits.messagesPerSecond, now);
    this.pointBucket = new TokenBucket(limits.pointBurst, limits.pointsPerSecond, now);
  }

  /** @param {ByteStream} stream */
  rememberAddress(stream) {
    const address = stream.remoteAddress;
    if (address === null || (this.pairing.lastHost === address.host && this.pairing.lastPort === address.port)) {
      return;
    }
    this.pairing = { ...this.pairing, lastHost: address.host, lastPort: address.port };
    this.store.updateAddress(pairingFingerprint(this.pairing), address.host, address.port);
  }

  /**
   * @param {import('./discovery.js').ReceiverEndpoint} endpoint
   * @param {AbortSignal} [signal]
   * @returns {Promise<Session>}
   */
  async handshake(endpoint, signal) {
    const stream = new ByteStream({ host: endpoint.host, port: endpoint.port });
    /** @type {NoiseHandshake | null} */
    let handshake = null;
    /** @type {import('./noise.js').NoiseTransport | null} */
    let transport = null;
    try {
      await stream.start(this.connectTimeoutMs, signal);
      const prologue = preamble(Mode.connect);
      handshake = new NoiseHandshake({
        pattern: IK,
        initiator: true,
        prologue,
        staticKey: this.identity.privateKeyForHandshake(),
        remoteStaticKey: this.pairing.receiverKey,
      });
      // Written to disk first: the receiver ignores a counter it has already seen.
      const counter = this.store.nextConnectCounter(pairingFingerprint(this.pairing), Date.now());
      if (!Number.isSafeInteger(counter) || counter > MAX_SEQUENCE) {
        throw new ErdigoError(ErdigoErrorKind.storage, {
          detail: 'this device key has used up its connection counter. Create a new key and pair again.',
        });
      }
      const counterBytes = Buffer.alloc(8);
      counterBytes.writeBigUInt64BE(BigInt(counter));
      const first = handshake.writeMessage(counterBytes);
      stream.armDeadline(this.connectTimeoutMs);
      await stream.send(Buffer.concat([prologue, frame(first)]), signal);
      const second = await stream.readFrame(MAX_HANDSHAKE_FRAME_LENGTH, signal);
      if (handshake.readMessage(second).length !== 0) {
        throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'the handshake reply has an unexpected payload' });
      }
      transport = handshake.split();
      await stream.sendFrame(transport.seal(encodeHello({ versions: [VERSION], sdk: SDK_NAME })), signal);
      const data = await stream.readFrame(undefined, signal);
      stream.disarmDeadline();
      const message = decodeReceiverMessage(transport.open(data));
      if (message.t === 'welcome') {
        if (message.welcome.version !== VERSION) {
          throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'the receiver chose a version that was not offered' });
        }
        return { stream, transport, welcome: message.welcome, lastSeq: message.welcome.lastSeq };
      }
      if (message.t === 'error') {
        const { error } = message;
        if (error.code === ErrorCode.versionUnsupported) {
          throw new ErdigoError(ErdigoErrorKind.versionUnsupported, { receiverError: error, supported: error.supported ?? [] });
        }
        if (error.code === ErrorCode.paused) {
          throw new ConnectionLost();
        }
        throw new ErdigoError(ErdigoErrorKind.rejected, { receiverError: error });
      }
      throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'the receiver did not send welcome' });
    } catch (error) {
      stream.dispose();
      transport?.dispose();
      if (signal?.aborted) {
        throw signal.reason;
      }
      if (error instanceof ErdigoError || error instanceof ConnectionLost) {
        throw error;
      }
      if (error instanceof WireError) {
        throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: error.detail, cause: error });
      }
      throw new ConnectionLost();
    } finally {
      handshake?.dispose();
    }
  }

  /**
   * Runs `body` again after connection problems until the retry policy runs out. The time allowed counts from the
   * first failure, so a long paced wait does not use it up.
   * @param {() => Promise<void>} body
   * @param {AbortSignal} [signal]
   */
  async retrying(body, signal) {
    /** @type {number | null} */
    let outageStart = null;
    let attempt = 0;
    for (;;) {
      try {
        await body();
        return;
      } catch (error) {
        if (!(error instanceof ConnectionLost)) {
          throw error;
        }
        signal?.throwIfAborted();
        const now = monotonicNow();
        // Having connected since then, this is a new outage.
        if (outageStart !== null && this.lastSessionAt > outageStart) {
          outageStart = null;
          attempt = 0;
        }
        const start = outageStart ?? now;
        outageStart = start;
        let wait = backoff(attempt);
        attempt += 1;
        if (this.retry.kind === 'none') {
          throw new ErdigoError(ErdigoErrorKind.noAnswer);
        }
        if (this.retry.kind === 'upTo') {
          // Use all the time allowed, with a last try at the end of it.
          const remaining = this.retry.ms / 1000 - (now - start);
          if (remaining <= 0) {
            throw new ErdigoError(ErdigoErrorKind.noAnswer);
          }
          wait = Math.min(wait, remaining);
        }
        await sleep(wait * 1000, signal);
      }
    }
  }
}
