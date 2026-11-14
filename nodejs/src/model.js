// The values a device writes, and the rules each must follow before it is sent.

import { hkdfSync, randomBytes } from 'node:crypto';
import { ErrorCode, MAX_DEVICE_NAME_SCALARS, WireError, isFingerprint } from './protocol.js';

/**
 * A place on the map, in degrees (WGS 84).
 * @typedef {{ lat: number, lon: number }} Coordinate
 */

/**
 * One point of a track. `ts` is milliseconds since 1970, and `alt` metres.
 * @typedef {{ lat: number, lon: number, ts?: number | null, alt?: number | null }} TrackPoint
 */

/**
 * Checks a coordinate: finite, latitude within -90...90 and longitude within -180...180.
 * @param {unknown} value
 * @returns {Coordinate}
 */
export function validateCoordinate(value) {
  const at = /** @type {{ lat?: unknown, lon?: unknown } | null} */ (value);
  const lat = at?.lat;
  const lon = at?.lon;
  if (typeof lat !== 'number' || typeof lon !== 'number' || !Number.isFinite(lat) || !Number.isFinite(lon)
    || lat < -90 || lat > 90 || lon < -180 || lon > 180) {
    throw new WireError(ErrorCode.invalidCoordinate, 'latitude must be within -90...90 and longitude within -180...180');
  }
  return { lat, lon };
}

/**
 * Checks an item id: 1 to 64 ASCII letters, digits, '-' or '_'.
 * @param {unknown} value
 * @returns {string}
 */
export function validateItemId(value) {
  if (typeof value !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(value)) {
    throw new WireError(ErrorCode.invalidId, "an item id is 1 to 64 ASCII letters, digits, '-' or '_'");
  }
  return value;
}

/** The kinds of item a device can write, in the order the protocol lists them. */
export const ITEM_KINDS = Object.freeze(['pin', 'marker', 'note', 'photo', 'track']);

/** @typedef {'pin' | 'marker' | 'note' | 'photo' | 'track'} ItemKind */

/**
 * @param {unknown} text
 * @returns {text is ItemKind}
 */
export function isItemKind(text) {
  return typeof text === 'string' && ITEM_KINDS.includes(text);
}

/**
 * The kinds given, without repeats, in the protocol's order.
 * @param {Iterable<ItemKind>} kinds
 * @returns {ItemKind[]}
 */
export function canonicalKinds(kinds) {
  const set = new Set(kinds);
  return /** @type {ItemKind[]} */ (ITEM_KINDS.filter((kind) => set.has(/** @type {ItemKind} */ (kind))));
}

/** The colours a marker can have. */
export const MARKER_COLORS = Object.freeze(['red', 'orange', 'yellow', 'green', 'blue', 'purple', 'gray']);

/** @typedef {'red' | 'orange' | 'yellow' | 'green' | 'blue' | 'purple' | 'gray'} MarkerColor */

/**
 * Checks a track point: a valid coordinate, a timestamp within 0...2^53-1 and an altitude within -1000...100000.
 * @param {unknown} value
 * @returns {TrackPoint}
 */
export function validateTrackPoint(value) {
  const point = /** @type {TrackPoint} */ (value);
  const coordinate = validateCoordinate(point);
  /** @type {TrackPoint} */
  const result = { lat: coordinate.lat, lon: coordinate.lon };
  if (point.ts !== undefined && point.ts !== null) {
    if (!Number.isSafeInteger(point.ts) || point.ts < 0) {
      throw new WireError(ErrorCode.invalidMessage, 'a track point timestamp is milliseconds since 1970, within 0...2^53-1');
    }
    result.ts = point.ts;
  }
  if (point.alt !== undefined && point.alt !== null) {
    if (typeof point.alt !== 'number' || !Number.isFinite(point.alt) || point.alt < -1000 || point.alt > 100_000) {
      throw new WireError(ErrorCode.invalidMessage, 'a track point altitude is within -1000...100000 metres');
    }
    result.alt = point.alt;
  }
  return result;
}

/** @typedef {'image/jpeg' | 'image/png'} PhotoType */

const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

/**
 * Detects the format from the first bytes.
 * @param {Uint8Array} data
 * @returns {PhotoType | null} null for anything but JPEG or PNG
 */
export function detectPhotoType(data) {
  if (data.length >= 3 && data[0] === 0xff && data[1] === 0xd8 && data[2] === 0xff) {
    return 'image/jpeg';
  }
  if (data.length >= 8 && Buffer.from(data.subarray(0, 8)).equals(PNG_SIGNATURE)) {
    return 'image/png';
  }
  return null;
}

/**
 * Limits a receiver enforces per device. The receiver announces them in `welcome`.
 * @typedef {object} Limits
 * @property {number} items
 * @property {number} photos
 * @property {number} photoBytes
 * @property {number} noteBytes
 * @property {number} titleScalars
 * @property {number} trackPoints
 * @property {number} openTracks
 * @property {number} tracks
 * @property {number} messagesPerSecond
 * @property {number} messageBurst
 * @property {number} pointsPerSecond
 * @property {number} pointBurst
 * @property {number} diskBytes
 */

/** The values of spec section 11. */
export const LIMITS_V1 = Object.freeze({
  items: 500,
  photos: 50,
  photoBytes: 1_048_576,
  noteBytes: 4096,
  titleScalars: 200,
  trackPoints: 3000,
  openTracks: 5,
  tracks: 200,
  messagesPerSecond: 5,
  messageBurst: 20,
  pointsPerSecond: 1,
  pointBurst: 100,
  diskBytes: 31_457_280,
});

/**
 * Control characters and invisible direction overrides, which can make text read differently from what it is.
 * @param {number} value a Unicode scalar value
 * @param {boolean} allowLineBreaks
 */
export function isDisallowedScalar(value, allowLineBreaks) {
  if (value === 0x0a || value === 0x09) {
    return !allowLineBreaks;
  }
  if (value <= 0x1f || (value >= 0x7f && value <= 0x9f)) {
    return true;
  }
  return value === 0x061c || value === 0x200e || value === 0x200f || (value >= 0x202a && value <= 0x202e)
    || (value >= 0x2066 && value <= 0x2069) || value === 0x2028 || value === 0x2029 || value === 0xfeff;
}

/**
 * The Unicode scalars of a string, or null if it holds a lone surrogate, which UTF-8 cannot carry.
 * @param {string} text
 * @returns {number[] | null}
 */
export function scalarsOf(text) {
  /** @type {number[]} */
  const scalars = [];
  for (const character of text) {
    const value = /** @type {number} */ (character.codePointAt(0));
    if (value >= 0xd800 && value <= 0xdfff) {
      return null;
    }
    scalars.push(value);
  }
  return scalars;
}

/**
 * @param {unknown} text
 * @param {Limits} [limits]
 */
export function validateTitle(text, limits = LIMITS_V1) {
  const scalars = typeof text === 'string' ? scalarsOf(text) : null;
  if (scalars === null) {
    throw new WireError(ErrorCode.invalidText, 'a title must be valid Unicode text');
  }
  if (scalars.length === 0) {
    throw new WireError(ErrorCode.invalidText, 'a title cannot be empty; leave it out instead');
  }
  if (scalars.length > limits.titleScalars) {
    throw new WireError(ErrorCode.tooLarge, `a title is at most ${limits.titleScalars} characters`);
  }
  if (scalars.some((scalar) => isDisallowedScalar(scalar, false))) {
    throw new WireError(ErrorCode.invalidText, 'a title cannot contain control or text-direction characters');
  }
}

/**
 * @param {unknown} text
 * @param {Limits} [limits]
 */
export function validateNote(text, limits = LIMITS_V1) {
  const scalars = typeof text === 'string' ? scalarsOf(text) : null;
  if (scalars === null) {
    throw new WireError(ErrorCode.invalidText, 'a note must be valid Unicode text');
  }
  if (scalars.length === 0) {
    throw new WireError(ErrorCode.invalidText, 'a note cannot be empty');
  }
  if (Buffer.byteLength(/** @type {string} */ (text), 'utf8') > limits.noteBytes) {
    throw new WireError(ErrorCode.tooLarge, `a note is at most ${limits.noteBytes} bytes of UTF-8`);
  }
  if (scalars.some((scalar) => isDisallowedScalar(scalar, true))) {
    throw new WireError(ErrorCode.invalidText, 'a note cannot contain control or text-direction characters');
  }
}

/**
 * White space as .NET's Char.IsWhiteSpace and Swift's Character.isWhitespace see it, so that every SDK trims the
 * same characters.
 * @param {number} value
 */
export function isWhiteSpaceScalar(value) {
  return (value >= 0x09 && value <= 0x0d) || value === 0x20 || value === 0x85 || value === 0xa0 || value === 0x1680
    || (value >= 0x2000 && value <= 0x200a) || value === 0x2028 || value === 0x2029 || value === 0x202f
    || value === 0x205f || value === 0x3000;
}

/**
 * Trims white space from both ends.
 * @param {string} text
 */
export function trimWhiteSpace(text) {
  const characters = Array.from(text);
  let start = 0;
  let end = characters.length;
  while (start < end && isWhiteSpaceScalar(/** @type {number} */ (characters[start].codePointAt(0)))) {
    start += 1;
  }
  while (end > start && isWhiteSpaceScalar(/** @type {number} */ (characters[end - 1].codePointAt(0)))) {
    end -= 1;
  }
  return characters.slice(start, end).join('');
}

/**
 * Receivers treat a device name as untrusted: unsafe characters are removed and it is cut to 40 characters. The SDK
 * does the same before sending one, so the person sees what the device meant.
 * @param {string} text
 */
export function sanitizeDeviceName(text) {
  let kept = '';
  for (const character of text) {
    const value = /** @type {number} */ (character.codePointAt(0));
    // A lone surrogate is dropped like any other character that cannot be shown safely.
    if (!(value >= 0xd800 && value <= 0xdfff) && !isDisallowedScalar(value, false)) {
      kept += character;
    }
  }
  return Array.from(trimWhiteSpace(kept)).slice(0, MAX_DEVICE_NAME_SCALARS).join('');
}

const CODE_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';
const PSK_SALT = Buffer.from('erdigo-integration-v1/pairing-psk', 'ascii');

/** The one-time code a person reads from the receiver: 100 random bits as 20 Crockford base32 characters. */
export class PairingCode {
  /**
   * Reads a code as a person may type it: hyphens and white space are ignored, letters are uppercased, O is read as
   * 0, and I and L as 1. Anything else, or any length but 20, is an error.
   * @param {string} text
   */
  constructor(text) {
    if (typeof text !== 'string') {
      throw new WireError(ErrorCode.invalidMessage, 'the pairing code has invalid characters');
    }
    let output = '';
    for (const character of text) {
      const value = /** @type {number} */ (character.codePointAt(0));
      if (value === 0x2d || isWhiteSpaceScalar(value)) {
        continue;
      }
      if (value > 0x7f) {
        throw new WireError(ErrorCode.invalidMessage, 'the pairing code has invalid characters');
      }
      let upper = character.toUpperCase();
      if (upper === 'O') {
        upper = '0';
      } else if (upper === 'I' || upper === 'L') {
        upper = '1';
      }
      if (!CODE_ALPHABET.includes(upper)) {
        throw new WireError(ErrorCode.invalidMessage, 'the pairing code has invalid characters');
      }
      output += upper;
    }
    if (output.length !== 20) {
      throw new WireError(ErrorCode.invalidMessage, 'the pairing code has 20 characters');
    }
    /** The 20 characters without separators. */
    this.canonical = output;
    Object.freeze(this);
  }

  /** Creates a new code from the system's secure random source. */
  static generate() {
    const random = randomBytes(20);
    // Each of the 20 bytes contributes exactly 5 unbiased bits.
    let text = '';
    for (const byte of random) {
      text += CODE_ALPHABET[byte & 0x1f];
    }
    random.fill(0);
    return new PairingCode(text);
  }

  /** `XXXXX-XXXXX-XXXXX-XXXXX`, for showing on the receiver's screen. */
  get display() {
    return [0, 1, 2, 3].map((group) => this.canonical.slice(group * 5, group * 5 + 5)).join('-');
  }

  /**
   * The Noise pre-shared key derived from the code. The caller owns the result and should clear it.
   * @returns {Buffer}
   */
  presharedKey() {
    const material = Buffer.from(this.canonical, 'ascii');
    try {
      return Buffer.from(hkdfSync('sha256', material, PSK_SALT, Buffer.alloc(0), 32));
    } finally {
      material.fill(0);
    }
  }

  /** @param {PairingCode} other */
  equals(other) {
    return other instanceof PairingCode && other.canonical === this.canonical;
  }

  /** Avoids writing the code to logs by accident. */
  toString() {
    return '<pairing code>';
  }

  toJSON() {
    return '<pairing code>';
  }

  [Symbol.for('nodejs.util.inspect.custom')]() {
    return '<pairing code>';
  }
}

const QR_PREFIX = 'erdigo-pair:1?';
/** Much longer than any valid text, which is 110 bytes without white space around it. */
const MAX_TICKET_TEXT_BYTES = 1024;

/**
 * What a person gives the SDK to pair: the code, and the receiver's key fingerprint when it comes from the QR code.
 * A typed code has no fingerprint, and the person then has to compare the key.
 */
export class PairingTicket {
  /**
   * @param {PairingCode} code
   * @param {string | null} [fingerprint]
   */
  constructor(code, fingerprint = null) {
    if (!(code instanceof PairingCode)) {
      throw new TypeError('a pairing ticket needs a PairingCode');
    }
    this.code = code;
    /** @type {string | null} */
    this.fingerprint = fingerprint;
    Object.freeze(this);
  }

  /**
   * Accepts either a bare code or the QR form `erdigo-pair:1?code=...&fp=...`. The QR form needs exactly one code and
   * exactly one valid fingerprint, because the key comparison is what makes it stronger than a typed code. Nothing
   * else may appear in it. Text over 1024 bytes is refused before it is looked at, because it may come from anywhere.
   * @param {string} text
   * @returns {PairingTicket}
   */
  static parse(text) {
    if (typeof text !== 'string') {
      throw new WireError(ErrorCode.invalidMessage, 'the pairing text is malformed');
    }
    if (Buffer.byteLength(text, 'utf8') > MAX_TICKET_TEXT_BYTES) {
      throw new WireError(ErrorCode.invalidMessage, 'the pairing text is too long');
    }
    const trimmed = trimWhiteSpace(text);
    if (!trimmed.startsWith(QR_PREFIX)) {
      return new PairingTicket(new PairingCode(trimmed));
    }
    /** @type {PairingCode | null} */
    let code = null;
    /** @type {string | null} */
    let fingerprint = null;
    // Empty pieces are kept, so that '&&', a trailing '&' and a field starting with '=' are refused below.
    for (const part of trimmed.slice(QR_PREFIX.length).split('&')) {
      const equals = part.indexOf('=');
      if (equals < 0) {
        throw new WireError(ErrorCode.invalidMessage, 'the pairing text is malformed');
      }
      const name = part.slice(0, equals);
      const value = part.slice(equals + 1);
      if (name === 'code' && code === null) {
        code = new PairingCode(value);
      } else if (name === 'fp' && fingerprint === null) {
        const lowered = value.toLowerCase();
        if (!isFingerprint(lowered)) {
          throw new WireError(ErrorCode.invalidMessage, 'the fingerprint in the pairing text is malformed');
        }
        fingerprint = lowered;
      } else {
        throw new WireError(ErrorCode.invalidMessage, 'the pairing text is malformed');
      }
    }
    if (code === null) {
      throw new WireError(ErrorCode.invalidMessage, 'the pairing text has no code');
    }
    if (fingerprint === null) {
      throw new WireError(ErrorCode.invalidMessage, 'the pairing text has no fingerprint');
    }
    return new PairingTicket(code, fingerprint);
  }

  /**
   * The text of the QR code, or null without a valid fingerprint, because QR text without one would look stronger
   * than it is.
   * @returns {string | null}
   */
  get uri() {
    return isFingerprint(this.fingerprint) ? `${QR_PREFIX}code=${this.code.display}&fp=${this.fingerprint}` : null;
  }

  toString() {
    return '<pairing ticket>';
  }

  toJSON() {
    return '<pairing ticket>';
  }

  [Symbol.for('nodejs.util.inspect.custom')]() {
    return '<pairing ticket>';
  }
}

/** A token bucket, which the client uses to stay below the rates a receiver announces. Times are in seconds. */
export class TokenBucket {
  /**
   * @param {number} capacity
   * @param {number} refillPerSecond
   * @param {number} now
   */
  constructor(capacity, refillPerSecond, now) {
    this.capacity = capacity;
    this.refillPerSecond = refillPerSecond;
    this.tokens = capacity;
    this.updatedAt = now;
  }

  /** @param {number} now */
  refill(now) {
    const elapsed = Math.max(0, now - this.updatedAt);
    this.tokens = Math.min(this.capacity, this.tokens + elapsed * this.refillPerSecond);
    this.updatedAt = now;
  }

  /**
   * Takes `count` tokens if possible and returns null. Otherwise takes nothing and returns how long to wait, in
   * seconds.
   * @param {number} now
   * @param {number} [count]
   * @returns {number | null}
   */
  take(now, count = 1) {
    this.refill(now);
    if (this.tokens >= count) {
      this.tokens -= count;
      return null;
    }
    if (this.refillPerSecond <= 0) {
      return Number.POSITIVE_INFINITY;
    }
    return (Math.min(count, this.capacity) - this.tokens) / this.refillPerSecond;
  }
}

/** A monotonic clock in seconds. */
export function monotonicNow() {
  return Number(process.hrtime.bigint()) / 1e9;
}
