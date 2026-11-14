// The device's key, what it remembers about each receiver, and the errors the SDK reports.

import { DH_LENGTH, newPrivateKey, publicKeyOf } from './noise.js';
import { fingerprint, shortFingerprint } from './protocol.js';

/** What went wrong, for {@link ErdigoError}. */
export const ErdigoErrorKind = Object.freeze({
  /**
   * No receiver answered. A receiver stays silent when it is off, asleep, on another network, when the pairing code
   * is wrong or expired, and when this device is not allowed. These cases cannot be told apart, by design.
   */
  noAnswer: 'noAnswer',
  /** The receiver's key does not match the fingerprint supplied for pairing. */
  fingerprintMismatch: 'fingerprintMismatch',
  /** The person did not confirm the receiver's key, so the receiver was not asked to pair. */
  keyNotConfirmed: 'keyNotConfirmed',
  /** The person at the receiver declined the request. */
  pairingDenied: 'pairingDenied',
  /** The person at the receiver did not answer in time. */
  pairingTimedOut: 'pairingTimedOut',
  /** The code was used twice. The receiver warned its user and removed the pairing made with it. */
  codeReused: 'codeReused',
  /** The receiver rejected a request. See {@link ErdigoError#receiverError}. */
  rejected: 'rejected',
  /** The item kind is not among those the person allowed for this device. See {@link ErdigoError#itemKind}. */
  notPermitted: 'notPermitted',
  /** The receiver does not support any protocol version this SDK speaks. See {@link ErdigoError#supported}. */
  versionUnsupported: 'versionUnsupported',
  /** Another connection using this device key took over. */
  replaced: 'replaced',
  /** Another writer using this device key advanced the sequence number. See {@link ErdigoError#lastSeq}. */
  sequenceConflict: 'sequenceConflict',
  /** The receiver broke the protocol. */
  protocolViolation: 'protocolViolation',
  invalidArgument: 'invalidArgument',
  /** Stored state could not be used. See {@link ErdigoError#storageProblem}. */
  storage: 'storage',
});

/** What was wrong with stored state. */
export const StorageProblem = Object.freeze({
  none: 'none',
  /** The device key file exists but its contents are not a device key. A new key was not created. */
  identityDamaged: 'identityDamaged',
  /** The device key file exists but could not be opened or read. A new key was not created. */
  identityUnreadable: 'identityUnreadable',
  /** The pairing file exists but its contents could not be understood. */
  stateDamaged: 'stateDamaged',
  /** The pairing file exists but could not be opened or read. */
  stateUnreadable: 'stateUnreadable',
  /** A folder or file could be read or replaced by someone else, so it was not used. */
  insecure: 'insecure',
  /** Anything else, such as a full disk. */
  other: 'other',
});

/**
 * @param {string} kind
 * @param {string | null} detail
 * @param {import('./messages.js').ReceiverError | null} error
 * @param {string | null} itemKind
 * @param {number[] | null} supported
 */
function describe(kind, detail, error, itemKind, supported) {
  switch (kind) {
    case ErdigoErrorKind.noAnswer:
      return 'No receiver answered. It may be off, asleep or on another network, the pairing code may be wrong or expired, or this device may have been removed.';
    case ErdigoErrorKind.fingerprintMismatch:
      return "The receiver's key does not match the expected fingerprint. Pairing was stopped.";
    case ErdigoErrorKind.keyNotConfirmed:
      return "The receiver's key was not confirmed. No pairing was saved, no items were sent, and the receiver was not asked to pair.";
    case ErdigoErrorKind.pairingDenied:
      return 'The request was declined on the receiver.';
    case ErdigoErrorKind.pairingTimedOut:
      return 'Nobody confirmed the request on the receiver in time.';
    case ErdigoErrorKind.codeReused:
      return 'This pairing code was already used. The receiver removed the pairing made with it. Ask for a new code.';
    case ErdigoErrorKind.rejected:
      return `The receiver rejected the request: ${error?.code ?? 'unknown'}.`;
    case ErdigoErrorKind.notPermitted:
      return `This device is not allowed to send '${itemKind ?? 'unknown'}' items.`;
    case ErdigoErrorKind.versionUnsupported:
      return `The receiver supports protocol versions [${(supported ?? []).join(', ')}], which this SDK does not speak. Update the SDK.`;
    case ErdigoErrorKind.replaced:
      return 'Another connection using this device key took over.';
    case ErdigoErrorKind.sequenceConflict:
      return 'Another program is writing with this device key at the same time.';
    case ErdigoErrorKind.protocolViolation:
      return `The receiver broke the protocol: ${detail}.`;
    default:
      return detail ?? kind;
  }
}

/** Failures reported by the SDK. */
export class ErdigoError extends Error {
  /**
   * @param {string} kind one of {@link ErdigoErrorKind}
   * @param {object} [options]
   * @param {string | null} [options.detail]
   * @param {import('./messages.js').ReceiverError | null} [options.receiverError]
   * @param {string | null} [options.itemKind]
   * @param {number[] | null} [options.supported]
   * @param {number | null} [options.lastSeq]
   * @param {string} [options.storageProblem] one of {@link StorageProblem}
   * @param {unknown} [options.cause]
   */
  constructor(kind, { detail = null, receiverError = null, itemKind = null, supported = null, lastSeq = null, storageProblem = StorageProblem.none, cause } = {}) {
    super(describe(kind, detail, receiverError, itemKind, supported), cause === undefined ? undefined : { cause });
    this.name = 'ErdigoError';
    /** @type {string} */
    this.kind = kind;
    /** @type {string | null} */
    this.detail = detail;
    /** @type {import('./messages.js').ReceiverError | null} */
    this.receiverError = receiverError;
    /** @type {string | null} */
    this.itemKind = itemKind;
    /** @type {number[] | null} */
    this.supported = supported;
    /** @type {number | null} */
    this.lastSeq = lastSeq;
    /** @type {string} */
    this.storageProblem = kind === ErdigoErrorKind.storage && storageProblem === StorageProblem.none ? StorageProblem.other : storageProblem;
  }
}

const privateKeys = new WeakMap();

/**
 * The device's one secret: an X25519 key used only for this protocol. The key is kept out of the object's own
 * properties, so that printing or serialising an identity never shows it. JavaScript gives no guarantee that a key
 * is wiped from memory; {@link dispose} clears the one copy this object holds.
 */
export class DeviceIdentity {
  /** @param {Buffer} privateKey */
  constructor(privateKey) {
    if (!(privateKey instanceof Uint8Array) || privateKey.length !== DH_LENGTH) {
      throw new ErdigoError(ErdigoErrorKind.storage, { detail: 'a device key is 32 bytes', storageProblem: StorageProblem.identityDamaged });
    }
    const key = Buffer.from(privateKey);
    privateKeys.set(this, key);
    /** The public key, which identifies this device to a receiver. */
    this.publicKey = publicKeyOf(key);
    this.fingerprint = fingerprint(this.publicKey);
    Object.freeze(this);
  }

  static generate() {
    const key = newPrivateKey();
    try {
      return new DeviceIdentity(key);
    } finally {
      key.fill(0);
    }
  }

  /**
   * Restores a key read from protected storage. The caller should clear `raw` afterwards.
   * @param {Uint8Array} raw
   */
  static fromPrivateKey(raw) {
    return new DeviceIdentity(Buffer.from(raw));
  }

  /**
   * The private key, for the handshake. Not a copy.
   * @internal
   * @returns {Buffer}
   */
  privateKeyForHandshake() {
    const key = privateKeys.get(this);
    if (!key) {
      throw new Error('this identity was disposed');
    }
    return key;
  }

  /** Only for writing the key to protected storage. The caller owns the copy and must clear it. */
  exportPrivateKeyForStorage() {
    return Buffer.from(this.privateKeyForHandshake());
  }

  /** Clears the private key. The identity cannot be used afterwards. */
  dispose() {
    privateKeys.get(this)?.fill(0);
    privateKeys.delete(this);
  }

  toString() {
    return `<device identity ${shortFingerprint(this.fingerprint)}>`;
  }

  [Symbol.for('nodejs.util.inspect.custom')]() {
    return this.toString();
  }
}

/**
 * What a device remembers about one receiver. An SDK can be paired with several receivers.
 * @typedef {object} ReceiverPairing
 * @property {string} label a local name for this receiver. It is never sent anywhere.
 * @property {Buffer} receiverKey
 * @property {import('./model.js').ItemKind[]} kinds
 * @property {string | null} lastHost
 * @property {number | null} lastPort
 * @property {Date} pairedAt
 * @property {number} counterFloor the highest connect counter the receiver had accepted when it approved this
 *   pairing. A later counter must be greater, even if this device has lost its own count.
 */

/**
 * @param {Omit<ReceiverPairing, 'lastHost' | 'lastPort' | 'pairedAt' | 'counterFloor'> & Partial<ReceiverPairing>} fields
 * @returns {ReceiverPairing}
 */
export function receiverPairing({ label, receiverKey, kinds, lastHost = null, lastPort = null, pairedAt = new Date(), counterFloor = 0 }) {
  return {
    label,
    receiverKey: Buffer.from(receiverKey),
    kinds: [...kinds],
    lastHost,
    lastPort,
    pairedAt,
    counterFloor,
  };
}

/**
 * The fingerprint of the receiver a pairing is for.
 * @param {ReceiverPairing} pairing
 */
export function pairingFingerprint(pairing) {
  return fingerprint(pairing.receiverKey);
}
