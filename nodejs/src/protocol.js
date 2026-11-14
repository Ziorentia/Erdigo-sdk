// Constants and small helpers of the integration-v1 protocol. See spec/integration-v1.md.

import { createHash } from 'node:crypto';

export const VERSION = 1;
export const SERVICE_TYPE = '_erdigo-int._tcp';
export const MAX_FRAME_LENGTH = 65535;
export const MAX_PLAINTEXT_LENGTH = 65519;
export const MAX_HANDSHAKE_FRAME_LENGTH = 1024;
export const MAX_CHUNK_BYTES = 48_000;
export const MAX_POINTS_PER_MESSAGE = 100;
export const MAX_SEQUENCE = 2 ** 53 - 1;
export const MAX_DEVICE_NAME_SCALARS = 40;
export const CONNECT_FIRST_MESSAGE_LENGTH = 32 + 48 + 8 + 16;
export const PAIR_FIRST_MESSAGE_LENGTH = 32 + 16;

/** The fifth byte of the preamble. */
export const Mode = Object.freeze({ connect: 0x01, pair: 0x02 });

/**
 * Five bytes sent in the clear before the handshake. They are also the Noise prologue, so tampering with them makes
 * the handshake fail.
 * @param {number} mode {@link Mode.connect} or {@link Mode.pair}
 * @returns {Buffer}
 */
export function preamble(mode) {
  return Buffer.from([0x45, 0x49, 0x4e, 0x31, mode]);
}

/**
 * @param {Uint8Array} data
 * @returns {number | null} the mode, or null for anything that is not a preamble
 */
export function parsePreamble(data) {
  if (data.length !== 5 || data[0] !== 0x45 || data[1] !== 0x49 || data[2] !== 0x4e || data[3] !== 0x31) {
    return null;
  }
  return data[4] === Mode.connect || data[4] === Mode.pair ? data[4] : null;
}

const FINGERPRINT_LABEL = Buffer.from('erdigo-integration-v1/fingerprint', 'ascii');

/**
 * The receiver's or a device's key fingerprint, as 64 lowercase hexadecimal digits.
 * @param {Uint8Array} publicKey
 * @returns {string}
 */
export function fingerprint(publicKey) {
  return createHash('sha256').update(FINGERPRINT_LABEL).update(publicKey).digest('hex');
}

/**
 * The first 16 hexadecimal digits in groups of four, for showing to people.
 * @param {string} text
 * @returns {string}
 */
export function shortFingerprint(text) {
  const head = text.length > 16 ? text.slice(0, 16) : text;
  const groups = [];
  for (let index = 0; index < head.length; index += 4) {
    groups.push(head.slice(index, index + 4));
  }
  return groups.join('-');
}

/** Lowercase hexadecimal. */
export function hexEncode(/** @type {Uint8Array} */ data) {
  return Buffer.from(data.buffer, data.byteOffset, data.byteLength).toString('hex');
}

/**
 * Accepts lowercase or uppercase hexadecimal digits only, in pairs.
 * @param {string} text
 * @returns {Buffer | null} null for anything else
 */
export function hexDecode(text) {
  if (typeof text !== 'string' || text.length % 2 !== 0 || !/^[0-9a-fA-F]*$/.test(text)) {
    return null;
  }
  return Buffer.from(text, 'hex');
}

/**
 * True for exactly 64 lowercase hexadecimal digits, the one spelling a fingerprint has (spec section 3).
 * @param {unknown} text
 */
export function isFingerprint(text) {
  return typeof text === 'string' && /^[0-9a-f]{64}$/.test(text);
}

/** The error codes of spec section 10, as they are spelled on the wire. */
export const ErrorCode = Object.freeze({
  versionUnsupported: 'version_unsupported',
  invalidMessage: 'invalid_message',
  invalidId: 'invalid_id',
  invalidCoordinate: 'invalid_coordinate',
  invalidText: 'invalid_text',
  notPermitted: 'not_permitted',
  notFound: 'not_found',
  kindConflict: 'kind_conflict',
  invalidState: 'invalid_state',
  staleSeq: 'stale_seq',
  seqConflict: 'seq_conflict',
  limitItems: 'limit_items',
  limitPhotos: 'limit_photos',
  limitTracks: 'limit_tracks',
  limitDisk: 'limit_disk',
  tooLarge: 'too_large',
  photoInvalid: 'photo_invalid',
  rateLimited: 'rate_limited',
  tooManyErrors: 'too_many_errors',
  paused: 'paused',
  replaced: 'replaced',
  codeReused: 'code_reused',
  internalError: 'internal_error',
});

/** @type {Set<string>} */
const KNOWN_CODES = new Set(Object.values(ErrorCode));

/** @param {string} code */
export function isKnownErrorCode(code) {
  return KNOWN_CODES.has(code);
}

/**
 * Retryable errors do not use up the sequence number.
 * @param {string} code
 */
export function isRetryableErrorCode(code) {
  return code === ErrorCode.rateLimited || code === ErrorCode.paused || code === ErrorCode.internalError;
}

/** A wire-level rejection, carrying the error code a receiver would report. */
export class WireError extends Error {
  /**
   * @param {string} code one of {@link ErrorCode}
   * @param {string} detail
   */
  constructor(code, detail) {
    super(detail);
    this.name = 'WireError';
    /** @type {string} */
    this.code = code;
    /** @type {string} */
    this.detail = detail;
  }
}
