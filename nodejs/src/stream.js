// An ordered byte stream over TCP with the integration-v1 framing: a two-byte big-endian length followed by one Noise
// message.

import { connect, isIP } from 'node:net';
import { MAX_FRAME_LENGTH } from './protocol.js';

/** Why a {@link ByteStream} operation failed. */
export const ByteStreamFailure = Object.freeze({
  closed: 'closed',
  timedOut: 'timedOut',
  frameTooLarge: 'frameTooLarge',
  connectionFailed: 'connectionFailed',
});

export class ByteStreamError extends Error {
  /** @param {string} failure one of {@link ByteStreamFailure} */
  constructor(failure) {
    super(`connection: ${failure}`);
    this.name = 'ByteStreamError';
    this.failure = failure;
  }
}

/**
 * @param {Uint8Array} body
 * @returns {Buffer}
 */
export function frame(body) {
  if (body.length > MAX_FRAME_LENGTH) {
    throw new ByteStreamError(ByteStreamFailure.frameTooLarge);
  }
  const output = Buffer.alloc(body.length + 2);
  output.writeUInt16BE(body.length, 0);
  output.set(body, 2);
  return output;
}

/**
 * An address as text, without an IPv6 zone and with an IPv4 address mapped into IPv6 written as IPv4.
 * @param {string} address
 */
export function addressText(address) {
  let text = address;
  const percent = text.indexOf('%');
  if (percent >= 0) {
    text = text.slice(0, percent);
  }
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/i.exec(text);
  return mapped ? mapped[1] : text;
}

/** Received data is held up to this much before the stream stops reading from the network. */
const HIGH_WATER = 2 * (MAX_FRAME_LENGTH + 2);

/** One task at a time may read, and one at a time may write. */
export class ByteStream {
  /**
   * Wraps a connected socket, or prepares a connection that {@link start} makes.
   * @param {{ socket?: import('node:net').Socket, host?: string, port?: number }} options
   */
  constructor({ socket, host, port }) {
    /** @type {import('node:net').Socket | null} */
    this.socket = socket ?? null;
    this.host = host ?? null;
    this.port = port ?? 0;
    /** @type {Buffer[]} */
    this.chunks = [];
    this.buffered = 0;
    this.ended = false;
    this.deadlineExpired = false;
    /** @type {NodeJS.Timeout | null} */
    this.deadlineTimer = null;
    /** @type {{ count: number, resolve: (data: Buffer) => void, reject: (error: Error) => void } | null} */
    this.pending = null;
    /** @type {{ host: string, port: number } | null} */
    this.peer = null;
    if (this.socket) {
      this.attach(this.socket);
    }
  }

  /** @param {import('node:net').Socket} socket */
  attach(socket) {
    socket.setNoDelay(true);
    // Kept now, because a socket forgets its peer once it has closed.
    this.peer = socket.remoteAddress && socket.remotePort
      ? { host: addressText(socket.remoteAddress), port: socket.remotePort }
      : null;
    socket.on('data', (/** @type {Buffer} */ chunk) => {
      this.chunks.push(chunk);
      this.buffered += chunk.length;
      if (this.buffered >= HIGH_WATER) {
        socket.pause();
      }
      this.fulfil();
    });
    const finish = () => {
      this.ended = true;
      this.fulfil();
    };
    socket.on('end', finish);
    socket.on('close', finish);
    socket.on('error', finish);
  }

  /** Bytes received but not yet read. After a failed read, this says whether the peer sent anything. */
  get bufferedByteCount() {
    return this.buffered;
  }

  /**
   * Waits until the connection is usable, or fails. A connection the network layer would keep retrying counts as
   * failed, so that callers can apply their own retry policy.
   * @param {number} timeoutMs
   * @param {AbortSignal} [signal]
   */
  async start(timeoutMs, signal) {
    if (this.socket !== null) {
      return;
    }
    signal?.throwIfAborted();
    if (!this.host || !Number.isInteger(this.port) || this.port < 1 || this.port > 65535) {
      throw new ByteStreamError(ByteStreamFailure.connectionFailed);
    }
    const socket = connect({ host: this.host, port: this.port, family: isIP(this.host) || undefined });
    this.socket = socket;
    await new Promise((resolve, reject) => {
      let settled = false;
      const done = (/** @type {Error | null} */ error) => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(timer);
        signal?.removeEventListener('abort', onAbort);
        socket.off('connect', onConnect);
        socket.off('error', onError);
        if (error) {
          socket.destroy();
          reject(error);
        } else {
          resolve(undefined);
        }
      };
      const onConnect = () => done(null);
      const onError = () => done(new ByteStreamError(ByteStreamFailure.connectionFailed));
      const onAbort = () => done(/** @type {Error} */ (signal?.reason));
      const timer = setTimeout(() => done(new ByteStreamError(ByteStreamFailure.timedOut)), Math.max(0, timeoutMs));
      socket.once('connect', onConnect);
      socket.once('error', onError);
      signal?.addEventListener('abort', onAbort, { once: true });
    });
    this.attach(socket);
  }

  /**
   * Closes the connection after `timeMs` unless {@link disarmDeadline} is called first. Pending reads and writes then
   * fail with {@link ByteStreamFailure.timedOut}.
   * @param {number} timeMs
   */
  armDeadline(timeMs) {
    this.disarmDeadline();
    const clamped = Math.min(Math.max(0, timeMs), 2 ** 31 - 1);
    this.deadlineTimer = setTimeout(() => {
      this.deadlineTimer = null;
      this.deadlineExpired = true;
      this.cancel();
    }, clamped);
  }

  disarmDeadline() {
    if (this.deadlineTimer !== null) {
      clearTimeout(this.deadlineTimer);
      this.deadlineTimer = null;
    }
  }

  /** Stops sending once everything already sent has gone out, so the peer can read it before the stream ends. */
  finishSending() {
    this.socket?.end();
  }

  /** Closes the connection. Pending reads and writes fail. */
  cancel() {
    this.socket?.destroy();
    this.ended = true;
    this.fulfil();
  }

  dispose() {
    this.disarmDeadline();
    this.cancel();
  }

  failure() {
    return new ByteStreamError(this.deadlineExpired ? ByteStreamFailure.timedOut : ByteStreamFailure.closed);
  }

  /**
   * @param {Uint8Array} data
   * @param {AbortSignal} [signal]
   */
  async send(data, signal) {
    signal?.throwIfAborted();
    const socket = this.socket;
    if (socket === null || socket.destroyed || this.ended) {
      throw this.failure();
    }
    await new Promise((resolve, reject) => {
      const onAbort = () => {
        this.cancel();
        reject(signal?.reason);
      };
      signal?.addEventListener('abort', onAbort, { once: true });
      socket.write(data, (error) => {
        signal?.removeEventListener('abort', onAbort);
        if (signal?.aborted) {
          return;
        }
        if (error) {
          this.cancel();
          reject(this.failure());
        } else {
          resolve(undefined);
        }
      });
    });
  }

  fulfil() {
    const pending = this.pending;
    if (pending === null) {
      return;
    }
    if (this.buffered >= pending.count) {
      this.pending = null;
      pending.resolve(this.take(pending.count));
    } else if (this.ended) {
      this.pending = null;
      pending.reject(this.failure());
    }
  }

  /** @param {number} count */
  take(count) {
    const all = this.chunks.length === 1 ? this.chunks[0] : Buffer.concat(this.chunks);
    const head = Buffer.from(all.subarray(0, count));
    const rest = all.subarray(count);
    this.chunks = rest.length > 0 ? [rest] : [];
    this.buffered = rest.length;
    if (this.buffered < HIGH_WATER && this.socket && !this.ended) {
      this.socket.resume();
    }
    return head;
  }

  /**
   * @param {number} count
   * @param {AbortSignal} [signal]
   * @returns {Promise<Buffer>}
   */
  readExactly(count, signal) {
    signal?.throwIfAborted();
    if (this.pending !== null) {
      return Promise.reject(new Error('one read at a time'));
    }
    if (this.buffered >= count) {
      return Promise.resolve(this.take(count));
    }
    if (this.ended) {
      return Promise.reject(this.failure());
    }
    return new Promise((resolve, reject) => {
      const onAbort = () => {
        this.pending = null;
        this.cancel();
        reject(signal?.reason);
      };
      signal?.addEventListener('abort', onAbort, { once: true });
      this.pending = {
        count,
        resolve: (data) => {
          signal?.removeEventListener('abort', onAbort);
          resolve(data);
        },
        reject: (error) => {
          signal?.removeEventListener('abort', onAbort);
          reject(error);
        },
      };
      this.socket?.resume();
    });
  }

  /**
   * Reads one frame and returns its body. Refuses a frame longer than `maxLength` before reading it.
   * @param {number} [maxLength]
   * @param {AbortSignal} [signal]
   */
  async readFrame(maxLength = MAX_FRAME_LENGTH, signal) {
    const header = await this.readExactly(2, signal);
    const length = header.readUInt16BE(0);
    if (length > maxLength) {
      throw new ByteStreamError(ByteStreamFailure.frameTooLarge);
    }
    return this.readExactly(length, signal);
  }

  /**
   * @param {Uint8Array} body
   * @param {AbortSignal} [signal]
   */
  sendFrame(body, signal) {
    return this.send(frame(body), signal);
  }

  /** The peer's address, for remembering where a receiver was found. */
  get remoteAddress() {
    return this.peer ?? null;
  }
}
