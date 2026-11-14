// Pairs this device with a receiver, using the one-time code a person reads from the receiver's screen.

import { setTimeout as delay } from 'node:timers/promises';
import { candidates } from './discovery.js';
import { DeviceIdentity, ErdigoError, ErdigoErrorKind, receiverPairing } from './identity.js';
import { decodeReceiverMessage, encodePairRequest } from './messages.js';
import { ITEM_KINDS, PairingTicket, canonicalKinds, sanitizeDeviceName, validateTitle } from './model.js';
import { NoiseHandshake, XXpsk0, isCanonicalPublicKey } from './noise.js';
import { ErrorCode, MAX_HANDSHAKE_FRAME_LENGTH, Mode, VERSION, WireError, fingerprint, preamble, shortFingerprint } from './protocol.js';
import { ByteStream, ByteStreamError, ByteStreamFailure, frame } from './stream.js';

/** Sent to receivers for diagnostics only. */
export const SDK_NAME = 'erdigo-nodejs/0.1.0';

/**
 * Shows a person the receiver's key, and returns whether it matches the key on the receiver's screen.
 * @callback ConfirmReceiverKey
 * @param {string} receiverKey the key in the form the receiver shows it: the first 16 digits of its fingerprint
 * @param {{ signal?: AbortSignal }} context
 * @returns {boolean | Promise<boolean>}
 */

/**
 * @typedef {object} PairOptions
 * @property {import('./discovery.js').ReceiverTarget} target where to find the receiver
 * @property {PairingTicket} ticket the code, and the receiver's key fingerprint when it comes from the QR code
 * @property {DeviceIdentity} identity this device's key
 * @property {string} deviceName shown to the person who confirms the request. Treated as untrusted by the receiver.
 * @property {Iterable<import('./model.js').ItemKind>} kinds what this device wants to send. The person may allow fewer.
 * @property {string} [label] a local name for the receiver. Defaults to the start of its fingerprint.
 * @property {number} [handshakeTimeoutMs] how long a handshake may take. Five seconds by default.
 * @property {number} [approvalTimeoutMs] how long to wait for the person to confirm. 120 seconds by default.
 * @property {ConfirmReceiverKey} [confirmKey] needed for a typed code, because a code proves only that the other side
 *   knows it, and not called for any other ticket
 * @property {AbortSignal} [signal] stops pairing. A stopped pairing rejects with the signal's reason.
 */

/**
 * Pairs with a receiver and returns what to store about it.
 *
 * A ticket with a fingerprint is compared here, and pairing stops on a mismatch. For a typed code, `confirmKey`
 * runs before the receiver hears anything about this device, so the receiver still shows its key. If it returns
 * false, this rejects with {@link ErdigoErrorKind.keyNotConfirmed}, the code stays valid, and the receiver has seen
 * neither this device's key nor its name. If it returns true, the key the person confirmed is compared as if it came
 * from a QR code, and the pairing connection is tried up to three times if it meets silence.
 * @param {PairOptions} options
 * @returns {Promise<import('./identity.js').ReceiverPairing>}
 */
export async function pair({
  target,
  ticket,
  identity,
  deviceName,
  kinds,
  label,
  handshakeTimeoutMs = 5000,
  approvalTimeoutMs = 120_000,
  confirmKey,
  signal,
}) {
  if (!(ticket instanceof PairingTicket)) {
    throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the ticket must come from PairingTicket.parse' });
  }
  if (!(identity instanceof DeviceIdentity)) {
    throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the identity must be a DeviceIdentity' });
  }
  if (typeof deviceName !== 'string') {
    throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the device name must be text' });
  }
  const name = sanitizeDeviceName(deviceName);
  if (name.length === 0) {
    throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the device name cannot be empty' });
  }
  const wanted = [...(kinds ?? [])];
  if (wanted.length === 0) {
    throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'choose at least one item kind' });
  }
  if (!wanted.every((kind) => ITEM_KINDS.includes(kind))) {
    throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the item kinds are pin, marker, note, photo and track' });
  }
  if (label !== undefined && label !== null) {
    validateLabel(label);
  }
  signal?.throwIfAborted();
  let current = ticket;
  let confirmedByAPerson = false;
  if (current.fingerprint === null) {
    if (typeof confirmKey !== 'function') {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, {
        detail: "a typed code needs confirmKey, because a person has to compare the receiver's key",
      });
    }
    const found = await receiverFingerprint(target, current, handshakeTimeoutMs, signal);
    signal?.throwIfAborted();
    if ((await confirmKey(shortFingerprint(found), { signal })) !== true) {
      throw new ErdigoError(ErdigoErrorKind.keyNotConfirmed);
    }
    signal?.throwIfAborted();
    current = new PairingTicket(current.code, found);
    confirmedByAPerson = true;
  }
  const request = encodePairRequest({ versions: [VERSION], sdk: SDK_NAME, name, kinds: canonicalKinds(wanted) });
  // After a person has confirmed the key, the receiver has just answered with this code, so silence now is most likely
  // the receiver still closing the connection that showed the key: a receiver limits how many unproven connections one
  // address may have at once. Trying again is safe, because nothing about this device is sent until the receiver has
  // proven the code and its key matches the one confirmed.
  const tries = confirmedByAPerson ? CONFIRMED_ATTEMPTS : 1;
  let lastError = new ErdigoError(ErdigoErrorKind.noAnswer);
  search: for (const endpoint of await candidates(target, signal)) {
    for (let round = 0; round < tries; round += 1) {
      if (round > 0) {
        await sleep(CONFIRMED_RETRY_WAIT_MS * round, signal);
      }
      try {
        return await attempt(endpoint, current, identity, request, label ?? null, handshakeTimeoutMs, approvalTimeoutMs, signal);
      } catch (error) {
        // A stopped pairing lands here too, because its connection was closed.
        signal?.throwIfAborted();
        if (error instanceof ErdigoError && error.kind === ErdigoErrorKind.noAnswer) {
          continue;
        }
        // A definite answer from a receiver holding this code ends the search.
        lastError = /** @type {ErdigoError} */ (error);
        break search;
      }
    }
  }
  signal?.throwIfAborted();
  throw lastError;
}

/** How many times the pairing connection is tried after a person confirmed the key. */
const CONFIRMED_ATTEMPTS = 3;

/** The wait before the second try. The third waits twice as long. */
const CONFIRMED_RETRY_WAIT_MS = 500;

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

/** @param {string} label */
export function validateLabel(label) {
  try {
    validateTitle(label);
  } catch (error) {
    if (error instanceof WireError) {
      throw new ErdigoError(ErdigoErrorKind.invalidArgument, { detail: 'the receiver label cannot be empty or contain control characters' });
    }
    throw error;
  }
}

/**
 * Finds the key of the receiver that holds this code, and stops there, so that a person can compare it. This runs
 * the exchange up to message 2, as spec section 4.1 describes.
 * @param {import('./discovery.js').ReceiverTarget} target
 * @param {PairingTicket} ticket
 * @param {number} handshakeTimeoutMs
 * @param {AbortSignal} [signal]
 */
async function receiverFingerprint(target, ticket, handshakeTimeoutMs, signal) {
  // The handshake needs a key, but uses it only in message 3, which is never sent here.
  const unused = DeviceIdentity.generate();
  let lastError = new ErdigoError(ErdigoErrorKind.noAnswer);
  try {
    for (const endpoint of await candidates(target, signal)) {
      const stream = new ByteStream({ host: endpoint.host, port: endpoint.port });
      try {
        const { handshake, receiverKey } = await readReceiverKey(stream, ticket, unused, handshakeTimeoutMs, signal);
        handshake.dispose();
        return fingerprint(receiverKey);
      } catch (error) {
        signal?.throwIfAborted();
        if (error instanceof ErdigoError && error.kind === ErdigoErrorKind.noAnswer) {
          continue;
        }
        lastError = /** @type {ErdigoError} */ (error);
        break;
      } finally {
        stream.dispose();
      }
    }
  } finally {
    unused.dispose();
  }
  signal?.throwIfAborted();
  throw lastError;
}

/**
 * Runs the handshake up to the receiver's answer. This device's key and name have not been sent yet.
 * @param {ByteStream} stream
 * @param {PairingTicket} ticket
 * @param {DeviceIdentity} identity
 * @param {number} handshakeTimeoutMs
 * @param {AbortSignal} [signal]
 */
async function readReceiverKey(stream, ticket, identity, handshakeTimeoutMs, signal) {
  const prologue = preamble(Mode.pair);
  const psk = ticket.code.presharedKey();
  /** @type {NoiseHandshake} */
  let handshake;
  try {
    await stream.start(handshakeTimeoutMs, signal);
    handshake = new NoiseHandshake({ pattern: XXpsk0, initiator: true, prologue, staticKey: identity.privateKeyForHandshake(), psks: [psk] });
    const first = handshake.writeMessage();
    stream.armDeadline(handshakeTimeoutMs);
    await stream.send(Buffer.concat([prologue, frame(first)]), signal);
  } catch (error) {
    if (error instanceof ErdigoError) {
      throw error;
    }
    throw new ErdigoError(ErdigoErrorKind.noAnswer, { cause: error });
  } finally {
    psk.fill(0);
  }
  try {
    const second = await stream.readFrame(MAX_HANDSHAKE_FRAME_LENGTH, signal);
    // Reading this message proves the receiver knows the code too.
    handshake.readMessage(second);
  } catch (error) {
    handshake.dispose();
    throw new ErdigoError(ErdigoErrorKind.noAnswer, { cause: error });
  }
  const receiverKey = handshake.remoteStaticKey;
  if (receiverKey === null || !isCanonicalPublicKey(receiverKey)) {
    handshake.dispose();
    throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'the receiver sent an unusable key' });
  }
  return { handshake, receiverKey };
}

/**
 * @param {import('./discovery.js').ReceiverEndpoint} endpoint
 * @param {PairingTicket} ticket
 * @param {DeviceIdentity} identity
 * @param {Buffer} request
 * @param {string | null} label
 * @param {number} handshakeTimeoutMs
 * @param {number} approvalTimeoutMs
 * @param {AbortSignal} [signal]
 */
async function attempt(endpoint, ticket, identity, request, label, handshakeTimeoutMs, approvalTimeoutMs, signal) {
  const stream = new ByteStream({ host: endpoint.host, port: endpoint.port });
  try {
    const { handshake, receiverKey } = await readReceiverKey(stream, ticket, identity, handshakeTimeoutMs, signal);
    let transport;
    try {
      const found = fingerprint(receiverKey);
      if (ticket.fingerprint !== null && ticket.fingerprint !== found) {
        throw new ErdigoError(ErdigoErrorKind.fingerprintMismatch);
      }
      try {
        const third = handshake.writeMessage(request);
        await stream.sendFrame(third, signal);
        transport = handshake.split();
      } catch (error) {
        throw new ErdigoError(ErdigoErrorKind.noAnswer, { cause: error });
      }
      // The person now decides, which can take a while.
      stream.armDeadline(approvalTimeoutMs);
      for (;;) {
        let message;
        try {
          const data = await stream.readFrame(undefined, signal);
          message = decodeReceiverMessage(transport.open(data));
        } catch (error) {
          if (error instanceof ByteStreamError && error.failure === ByteStreamFailure.timedOut) {
            throw new ErdigoError(ErdigoErrorKind.pairingTimedOut, { cause: error });
          }
          if (error instanceof WireError) {
            throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: error.detail, cause: error });
          }
          throw new ErdigoError(ErdigoErrorKind.noAnswer, { cause: error });
        }
        if (message.t === 'pair_status') {
          if (message.status === 'pending') {
            continue;
          }
          if (message.status === 'denied') {
            throw new ErdigoError(ErdigoErrorKind.pairingDenied);
          }
          const address = stream.remoteAddress;
          return receiverPairing({
            label: label ?? `receiver-${found.slice(0, 8)}`,
            receiverKey,
            kinds: message.kinds,
            lastHost: address?.host ?? null,
            lastPort: address?.port ?? null,
            counterFloor: message.counter,
          });
        }
        if (message.t === 'error') {
          const { error } = message;
          if (error.code === ErrorCode.codeReused) {
            throw new ErdigoError(ErdigoErrorKind.codeReused, { receiverError: error });
          }
          if (error.code === ErrorCode.versionUnsupported) {
            throw new ErdigoError(ErdigoErrorKind.versionUnsupported, { receiverError: error, supported: error.supported ?? [] });
          }
          throw new ErdigoError(ErdigoErrorKind.rejected, { receiverError: error });
        }
        throw new ErdigoError(ErdigoErrorKind.protocolViolation, { detail: 'unexpected message during pairing' });
      }
    } finally {
      handshake.dispose();
      transport?.dispose();
    }
  } finally {
    stream.dispose();
  }
}
