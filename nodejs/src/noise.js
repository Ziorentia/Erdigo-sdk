// The Noise Protocol Framework, limited to what integration-v1 uses: X25519, ChaCha20-Poly1305 and SHA-256, and the
// IK and XXpsk0 patterns. Everything comes from node:crypto.

import {
  createCipheriv,
  createDecipheriv,
  createHash,
  createHmac,
  createPrivateKey,
  createPublicKey,
  diffieHellman,
  randomBytes,
} from 'node:crypto';

export const DH_LENGTH = 32;
export const HASH_LENGTH = 32;
export const TAG_LENGTH = 16;
export const MAX_MESSAGE_LENGTH = 65535;
const NONCE_LIMIT = 2n ** 64n - 1n;

/** Failures raised by the Noise layer. They deliberately carry no key material. */
export const NoiseErrorCode = Object.freeze({
  invalidKey: 'invalidKey',
  weakSharedSecret: 'weakSharedSecret',
  truncatedMessage: 'truncatedMessage',
  decryptionFailed: 'decryptionFailed',
  nonceExhausted: 'nonceExhausted',
  messageTooLarge: 'messageTooLarge',
  wrongHandshakeState: 'wrongHandshakeState',
  missingKey: 'missingKey',
  /** split() was called a second time. A second pair of cipher states would repeat the first one's keys and nonces. */
  alreadySplit: 'alreadySplit',
});

export class NoiseError extends Error {
  /** @param {string} error one of {@link NoiseErrorCode} */
  constructor(error) {
    super(`Noise: ${error}`);
    this.name = 'NoiseError';
    this.error = error;
  }
}

// DER wrappers for raw X25519 keys (RFC 8410), so that node:crypto can use them.
const PKCS8_PREFIX = Buffer.from('302e020100300506032b656e04220420', 'hex');
const SPKI_PREFIX = Buffer.from('302a300506032b656e032100', 'hex');

/** @param {Uint8Array} raw */
function privateKeyObject(raw) {
  const der = Buffer.concat([PKCS8_PREFIX, raw]);
  try {
    return createPrivateKey({ key: der, format: 'der', type: 'pkcs8' });
  } finally {
    der.fill(0);
  }
}

/** @param {Uint8Array} raw */
function publicKeyObject(raw) {
  return createPublicKey({ key: Buffer.concat([SPKI_PREFIX, raw]), format: 'der', type: 'spki' });
}

/**
 * X25519 ignores the top bit of a public key, so one private key has more than one spelling. A receiver identifies
 * devices by these bytes, so it accepts only the canonical spelling: below 2^255-19, top bit clear.
 * @param {Uint8Array} key
 */
export function isCanonicalPublicKey(key) {
  if (key.length !== DH_LENGTH || (key[31] & 0x80) !== 0) {
    return false;
  }
  // Reject values from 2^255-19 up to 2^255-1, which are also non-canonical.
  let isAtLeastPrime = key[31] === 0x7f && key[0] >= 0xed;
  for (let index = 1; index <= 30 && isAtLeastPrime; index += 1) {
    isAtLeastPrime = key[index] === 0xff;
  }
  return !isAtLeastPrime;
}

/** A new X25519 private key from the system's secure random source. */
export function newPrivateKey() {
  return randomBytes(DH_LENGTH);
}

/**
 * The public key of an X25519 private key.
 * @param {Uint8Array} privateKey
 * @returns {Buffer}
 */
export function publicKeyOf(privateKey) {
  if (privateKey.length !== DH_LENGTH) {
    throw new NoiseError(NoiseErrorCode.invalidKey);
  }
  const spki = createPublicKey(privateKeyObject(privateKey)).export({ format: 'der', type: 'spki' });
  return Buffer.from(spki.subarray(spki.length - DH_LENGTH));
}

/**
 * X25519 with an explicit rejection of low-order public keys, which produce an all-zero secret.
 * @param {Uint8Array} privateKey
 * @param {Uint8Array} publicKey
 * @returns {Buffer}
 */
export function dh(privateKey, publicKey) {
  if (privateKey.length !== DH_LENGTH || publicKey.length !== DH_LENGTH) {
    throw new NoiseError(NoiseErrorCode.invalidKey);
  }
  let secret;
  try {
    secret = diffieHellman({ privateKey: privateKeyObject(privateKey), publicKey: publicKeyObject(publicKey) });
  } catch {
    // OpenSSL refuses some low-order points itself.
    throw new NoiseError(NoiseErrorCode.weakSharedSecret);
  }
  // Constant-time OR across every byte.
  let accumulated = 0;
  for (const value of secret) {
    accumulated |= value;
  }
  if (accumulated === 0) {
    throw new NoiseError(NoiseErrorCode.weakSharedSecret);
  }
  return secret;
}

/**
 * @param {Uint8Array} key
 * @param {Uint8Array} data
 */
function hmac(key, data) {
  return createHmac('sha256', key).update(data).digest();
}

/**
 * The Noise HKDF. Note that the input keying material is the HMAC message, keyed by the chaining key.
 * @param {Uint8Array} chainingKey
 * @param {Uint8Array} inputKeyMaterial
 * @param {number} outputs
 * @returns {Buffer[]}
 */
export function noiseHkdf(chainingKey, inputKeyMaterial, outputs) {
  const temporary = hmac(chainingKey, inputKeyMaterial);
  /** @type {Buffer[]} */
  const results = [];
  let previous = Buffer.alloc(0);
  for (let index = 1; index <= outputs; index += 1) {
    previous = hmac(temporary, Buffer.concat([previous, Buffer.from([index])]));
    results.push(previous);
  }
  temporary.fill(0);
  return results;
}

/** @typedef {'e' | 's' | 'ee' | 'es' | 'se' | 'ss' | 'psk'} Token */

/** A Noise handshake pattern. */
export class NoisePattern {
  /**
   * @param {string} name such as `IK`
   * @param {boolean} responderStaticIsPreMessage
   * @param {Token[][]} messages
   */
  constructor(name, responderStaticIsPreMessage, messages) {
    this.name = name;
    this.responderStaticIsPreMessage = responderStaticIsPreMessage;
    this.messages = messages;
    this.usesPsk = messages.some((message) => message.includes('psk'));
    this.protocolName = `Noise_${name}_25519_ChaChaPoly_SHA256`;
  }
}

/** Used for ordinary connections: only a device that already knows the receiver key can start one. */
export const IK = new NoisePattern('IK', true, [
  ['e', 'es', 's', 'ss'],
  ['e', 'ee', 'se'],
]);

/**
 * Used for pairing: the pairing code is mixed in before anything else, so a party without the code cannot produce a
 * first message the receiver will answer.
 */
export const XXpsk0 = new NoisePattern('XXpsk0', false, [
  ['psk', 'e'],
  ['e', 'ee', 's', 'es'],
  ['s', 'se'],
]);

/** One direction of an established Noise session. */
export class CipherState {
  constructor() {
    /** @type {Buffer | null} */
    this.key = null;
    this.counter = 0n;
  }

  get hasKey() {
    return this.key !== null;
  }

  /**
   * @param {Uint8Array} raw
   * @param {bigint} [start] not zero only in tests of what happens as the counter runs out
   */
  initialize(raw, start = 0n) {
    this.key?.fill(0);
    this.key = Buffer.from(raw.subarray(0, 32));
    this.counter = start;
  }

  nonce() {
    // Noise: 32 bits of zeros followed by the little-endian 64-bit counter.
    const nonce = Buffer.alloc(12);
    nonce.writeBigUInt64LE(this.counter, 4);
    return nonce;
  }

  /**
   * @param {Uint8Array} associatedData
   * @param {Uint8Array} plaintext
   * @returns {Buffer}
   */
  encrypt(associatedData, plaintext) {
    if (this.key === null) {
      return Buffer.from(plaintext);
    }
    // 2^64-1 is reserved by the Noise specification.
    if (this.counter >= NONCE_LIMIT) {
      throw new NoiseError(NoiseErrorCode.nonceExhausted);
    }
    const cipher = createCipheriv('chacha20-poly1305', this.key, this.nonce(), { authTagLength: TAG_LENGTH });
    cipher.setAAD(associatedData, { plaintextLength: plaintext.length });
    const output = Buffer.concat([cipher.update(plaintext), cipher.final(), cipher.getAuthTag()]);
    this.counter += 1n;
    return output;
  }

  /**
   * @param {Uint8Array} associatedData
   * @param {Uint8Array} ciphertext
   * @returns {Buffer}
   */
  decrypt(associatedData, ciphertext) {
    if (this.key === null) {
      return Buffer.from(ciphertext);
    }
    if (this.counter >= NONCE_LIMIT) {
      throw new NoiseError(NoiseErrorCode.nonceExhausted);
    }
    if (ciphertext.length < TAG_LENGTH) {
      throw new NoiseError(NoiseErrorCode.truncatedMessage);
    }
    const body = ciphertext.subarray(0, ciphertext.length - TAG_LENGTH);
    let output;
    try {
      const decipher = createDecipheriv('chacha20-poly1305', this.key, this.nonce(), { authTagLength: TAG_LENGTH });
      decipher.setAuthTag(ciphertext.subarray(ciphertext.length - TAG_LENGTH));
      decipher.setAAD(associatedData, { plaintextLength: body.length });
      output = Buffer.concat([decipher.update(body), decipher.final()]);
    } catch {
      throw new NoiseError(NoiseErrorCode.decryptionFailed);
    }
    this.counter += 1n;
    return output;
  }

  dispose() {
    this.key?.fill(0);
    this.key = null;
  }
}

class SymmetricState {
  /** @param {string} protocolName */
  constructor(protocolName) {
    this.cipher = new CipherState();
    const name = Buffer.from(protocolName, 'ascii');
    if (name.length <= HASH_LENGTH) {
      this.hash = Buffer.alloc(HASH_LENGTH);
      name.copy(this.hash);
    } else {
      this.hash = createHash('sha256').update(name).digest();
    }
    this.chainingKey = Buffer.from(this.hash);
  }

  get hasKey() {
    return this.cipher.hasKey;
  }

  /** @param {Uint8Array} data */
  mixHash(data) {
    this.hash = createHash('sha256').update(this.hash).update(data).digest();
  }

  /** @param {Uint8Array} inputKeyMaterial */
  mixKey(inputKeyMaterial) {
    const [chainingKey, key] = noiseHkdf(this.chainingKey, inputKeyMaterial, 2);
    this.chainingKey.fill(0);
    this.chainingKey = chainingKey;
    this.cipher.initialize(key);
    key.fill(0);
  }

  /** @param {Uint8Array} inputKeyMaterial */
  mixKeyAndHash(inputKeyMaterial) {
    const [chainingKey, hashInput, key] = noiseHkdf(this.chainingKey, inputKeyMaterial, 3);
    this.chainingKey.fill(0);
    this.chainingKey = chainingKey;
    this.mixHash(hashInput);
    this.cipher.initialize(key);
    hashInput.fill(0);
    key.fill(0);
  }

  /** @param {Uint8Array} plaintext */
  encryptAndHash(plaintext) {
    const ciphertext = this.cipher.encrypt(this.hash, plaintext);
    this.mixHash(ciphertext);
    return ciphertext;
  }

  /** @param {Uint8Array} ciphertext */
  decryptAndHash(ciphertext) {
    const plaintext = this.cipher.decrypt(this.hash, ciphertext);
    this.mixHash(ciphertext);
    return plaintext;
  }

  split() {
    const [first, second] = noiseHkdf(this.chainingKey, Buffer.alloc(0), 2);
    const one = new CipherState();
    const two = new CipherState();
    one.initialize(first);
    two.initialize(second);
    first.fill(0);
    second.fill(0);
    return [one, two];
  }

  dispose() {
    this.chainingKey.fill(0);
    this.cipher.dispose();
  }
}

/**
 * Encrypts and decrypts messages after a handshake has finished. A handshake yields one transport, because
 * {@link NoiseHandshake#split} succeeds only once. JavaScript runs one piece of code at a time, so no key and nonce
 * pair is used twice. The caller still has to send frames in the order they were sealed, because the peer can only
 * open them in that order.
 */
export class NoiseTransport {
  /**
   * @param {CipherState} sendCipher
   * @param {CipherState} receiveCipher
   * @param {Buffer} handshakeHash
   */
  constructor(sendCipher, receiveCipher, handshakeHash) {
    this.sendCipher = sendCipher;
    this.receiveCipher = receiveCipher;
    this.handshakeHash = handshakeHash;
  }

  /** @param {Uint8Array} plaintext */
  seal(plaintext) {
    if (plaintext.length + TAG_LENGTH > MAX_MESSAGE_LENGTH) {
      throw new NoiseError(NoiseErrorCode.messageTooLarge);
    }
    return this.sendCipher.encrypt(Buffer.alloc(0), plaintext);
  }

  /** @param {Uint8Array} ciphertext */
  open(ciphertext) {
    if (ciphertext.length > MAX_MESSAGE_LENGTH) {
      throw new NoiseError(NoiseErrorCode.messageTooLarge);
    }
    return this.receiveCipher.decrypt(Buffer.alloc(0), ciphertext);
  }

  /** Clears both keys. Every later operation fails, rather than falling back to no encryption. */
  dispose() {
    this.sendCipher.initialize(Buffer.alloc(32), NONCE_LIMIT);
    this.receiveCipher.initialize(Buffer.alloc(32), NONCE_LIMIT);
  }
}

/** The Noise handshake state machine. */
export class NoiseHandshake {
  /**
   * @param {object} options
   * @param {NoisePattern} options.pattern
   * @param {boolean} options.initiator
   * @param {Uint8Array} options.prologue
   * @param {Uint8Array | null} [options.staticKey] this side's X25519 private key. The handshake keeps a reference to
   *   it rather than a copy.
   * @param {Uint8Array | null} [options.remoteStaticKey]
   * @param {Uint8Array[]} [options.psks]
   * @param {Uint8Array | null} [options.fixedEphemeral] exists only so that published test vectors can be
   *   reproduced. Reusing an ephemeral key in real traffic breaks the protocol, so the SDK never passes one.
   */
  constructor({ pattern, initiator, prologue, staticKey = null, remoteStaticKey = null, psks = [], fixedEphemeral = null }) {
    if (psks.some((key) => key.length !== 32)) {
      throw new NoiseError(NoiseErrorCode.invalidKey);
    }
    if (pattern.usesPsk && psks.length === 0) {
      throw new NoiseError(NoiseErrorCode.missingKey);
    }
    if (remoteStaticKey !== null && remoteStaticKey.length !== DH_LENGTH) {
      throw new NoiseError(NoiseErrorCode.invalidKey);
    }
    if ((staticKey !== null && staticKey.length !== DH_LENGTH) || (fixedEphemeral !== null && fixedEphemeral.length !== DH_LENGTH)) {
      throw new NoiseError(NoiseErrorCode.invalidKey);
    }
    this.pattern = pattern;
    this.initiator = initiator;
    this.staticKey = staticKey;
    /** @type {Buffer | null} */
    this.remoteStaticKey = remoteStaticKey === null ? null : Buffer.from(remoteStaticKey);
    // Copies, so that the caller may clear its own.
    this.psks = psks.map((key) => Buffer.from(key));
    this.fixedEphemeral = fixedEphemeral;
    /** @type {Buffer | null} */
    this.ephemeralKey = null;
    /** @type {Buffer | null} */
    this.remoteEphemeralKey = null;
    this.messageIndex = 0;
    this.hasSplit = false;
    this.symmetric = new SymmetricState(pattern.protocolName);
    this.symmetric.mixHash(prologue);
    if (pattern.responderStaticIsPreMessage) {
      if (initiator) {
        if (this.remoteStaticKey === null) {
          throw new NoiseError(NoiseErrorCode.missingKey);
        }
        this.symmetric.mixHash(this.remoteStaticKey);
      } else {
        if (staticKey === null) {
          throw new NoiseError(NoiseErrorCode.missingKey);
        }
        this.symmetric.mixHash(publicKeyOf(staticKey));
      }
    }
  }

  get isComplete() {
    return this.messageIndex === this.pattern.messages.length;
  }

  get handshakeHash() {
    return this.symmetric.hash;
  }

  get isMyTurn() {
    return (this.messageIndex % 2 === 0) === this.initiator;
  }

  /** @template T @param {T | null} key @returns {T} */
  static require(key) {
    if (key === null) {
      throw new NoiseError(NoiseErrorCode.missingKey);
    }
    return key;
  }

  /** @param {Token} token */
  mixDh(token) {
    const need = NoiseHandshake.require;
    let secret;
    switch (token) {
      case 'ee':
        secret = dh(need(this.ephemeralKey), need(this.remoteEphemeralKey));
        break;
      case 'es':
        secret = this.initiator
          ? dh(need(this.ephemeralKey), need(this.remoteStaticKey))
          : dh(need(this.staticKey), need(this.remoteEphemeralKey));
        break;
      case 'se':
        secret = this.initiator
          ? dh(need(this.staticKey), need(this.remoteEphemeralKey))
          : dh(need(this.ephemeralKey), need(this.remoteStaticKey));
        break;
      case 'ss':
        secret = dh(need(this.staticKey), need(this.remoteStaticKey));
        break;
      default:
        throw new NoiseError(NoiseErrorCode.wrongHandshakeState);
    }
    try {
      this.symmetric.mixKey(secret);
    } finally {
      secret.fill(0);
    }
  }

  mixPsk() {
    const psk = this.psks.shift();
    if (psk === undefined) {
      throw new NoiseError(NoiseErrorCode.missingKey);
    }
    try {
      this.symmetric.mixKeyAndHash(psk);
    } finally {
      psk.fill(0);
    }
  }

  /**
   * @param {Uint8Array} [payload]
   * @returns {Buffer}
   */
  writeMessage(payload = Buffer.alloc(0)) {
    if (this.isComplete || !this.isMyTurn) {
      throw new NoiseError(NoiseErrorCode.wrongHandshakeState);
    }
    /** @type {Buffer[]} */
    const output = [];
    for (const token of this.pattern.messages[this.messageIndex]) {
      switch (token) {
        case 'e': {
          const key = this.fixedEphemeral !== null ? Buffer.from(this.fixedEphemeral) : newPrivateKey();
          this.ephemeralKey?.fill(0);
          this.ephemeralKey = key;
          const publicKey = publicKeyOf(key);
          output.push(publicKey);
          this.symmetric.mixHash(publicKey);
          if (this.pattern.usesPsk) {
            this.symmetric.mixKey(publicKey);
          }
          break;
        }
        case 's':
          output.push(this.symmetric.encryptAndHash(publicKeyOf(NoiseHandshake.require(this.staticKey))));
          break;
        case 'psk':
          this.mixPsk();
          break;
        default:
          this.mixDh(token);
      }
    }
    output.push(this.symmetric.encryptAndHash(payload));
    const message = Buffer.concat(output);
    if (message.length > MAX_MESSAGE_LENGTH) {
      throw new NoiseError(NoiseErrorCode.messageTooLarge);
    }
    this.messageIndex += 1;
    return message;
  }

  /**
   * @param {Uint8Array} message
   * @returns {Buffer} the payload
   */
  readMessage(message) {
    if (this.isComplete || this.isMyTurn) {
      throw new NoiseError(NoiseErrorCode.wrongHandshakeState);
    }
    if (message.length > MAX_MESSAGE_LENGTH) {
      throw new NoiseError(NoiseErrorCode.messageTooLarge);
    }
    let offset = 0;
    const take = (/** @type {number} */ count) => {
      if (message.length - offset < count) {
        throw new NoiseError(NoiseErrorCode.truncatedMessage);
      }
      const head = Buffer.from(message.subarray(offset, offset + count));
      offset += count;
      return head;
    };
    for (const token of this.pattern.messages[this.messageIndex]) {
      switch (token) {
        case 'e': {
          const publicKey = take(DH_LENGTH);
          this.remoteEphemeralKey = publicKey;
          this.symmetric.mixHash(publicKey);
          if (this.pattern.usesPsk) {
            this.symmetric.mixKey(publicKey);
          }
          break;
        }
        case 's': {
          const length = this.symmetric.hasKey ? DH_LENGTH + TAG_LENGTH : DH_LENGTH;
          this.remoteStaticKey = this.symmetric.decryptAndHash(take(length));
          break;
        }
        case 'psk':
          this.mixPsk();
          break;
        default:
          this.mixDh(token);
      }
    }
    const payload = this.symmetric.decryptAndHash(message.subarray(offset));
    this.messageIndex += 1;
    return payload;
  }

  /**
   * Call once {@link isComplete} is true. This succeeds only once, because a second transport would have the same
   * keys and start from the same nonces.
   * @returns {NoiseTransport}
   */
  split() {
    if (!this.isComplete) {
      throw new NoiseError(NoiseErrorCode.wrongHandshakeState);
    }
    if (this.hasSplit) {
      throw new NoiseError(NoiseErrorCode.alreadySplit);
    }
    this.hasSplit = true;
    const [first, second] = this.symmetric.split();
    const hash = Buffer.from(this.symmetric.hash);
    this.clearEphemeral();
    return new NoiseTransport(this.initiator ? first : second, this.initiator ? second : first, hash);
  }

  clearEphemeral() {
    this.ephemeralKey?.fill(0);
    this.ephemeralKey = null;
  }

  /** Clears the ephemeral key and the handshake's own key material. The static key belongs to the caller. */
  dispose() {
    this.clearEphemeral();
    for (const psk of this.psks) {
      psk.fill(0);
    }
    this.psks = [];
    this.symmetric.dispose();
  }
}
