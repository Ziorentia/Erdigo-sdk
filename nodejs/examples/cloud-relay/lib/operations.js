// The rules of the relay API (docs/cloud-relay.md) that the relay and the gateway share. Operations are checked with
// the SDK's own rules: the relay checks each one it is sent, and the gateway each one it fetches, because a relay may
// be a server of your own.

import { parseStrictJson, JsonParseError } from '../../../src/json.js';
import {
  MARKER_COLORS,
  detectPhotoType,
  validateCoordinate,
  validateItemId,
  validateNote,
  validateTitle,
  validateTrackPoint,
} from '../../../src/model.js';
import { WireError } from '../../../src/protocol.js';

const MAX_PHOTO_BYTES = 1_048_576;
const MAX_TRACK_POINTS = 100;
export const MAX_SEQUENCE = 2 ** 53 - 1;
/** Deep enough for any valid body or answer; anything deeper is not JSON this API can hold. */
const MAX_DEPTH = 16;

const TOKEN = /^[A-Za-z0-9._~-]{32,256}$/;
const EPOCH = /^[A-Za-z0-9_-]{16,64}$/;

/** A token is 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'. @param {unknown} text */
export function isToken(text) {
  return typeof text === 'string' && TOKEN.test(text);
}

/** An epoch is 16 to 64 characters from A-Z, a-z, 0-9, '-' and '_'. @param {unknown} text */
export function isEpoch(text) {
  return typeof text === 'string' && EPOCH.test(text);
}

const SHAPES = Object.freeze({
  pin: { required: ['id', 'lat', 'lon'], optional: ['title'], says: 'pin takes id, lat, lon and an optional title' },
  marker: { required: ['id', 'lat', 'lon'], optional: ['title', 'color'], says: 'marker takes id, lat, lon and an optional title and color' },
  note: { required: ['id', 'lat', 'lon', 'text'], optional: [], says: 'note takes id, lat, lon and text' },
  photo: { required: ['id', 'lat', 'lon', 'type', 'data'], optional: ['title'], says: 'photo takes id, lat, lon, type, data and an optional title' },
  track: { required: ['id', 'points'], optional: ['title'], says: 'track takes id, points and an optional title' },
  finish_track: { required: ['id'], optional: [], says: 'finish_track takes only id' },
  move: { required: ['id', 'lat', 'lon'], optional: [], says: 'move takes id, lat and lon' },
  delete: { required: ['id'], optional: [], says: 'delete takes only id' },
});

/** An operation that breaks a rule. `reason` is a short English explanation that never repeats what was sent. */
export class OpError extends Error {
  /** @param {string} reason */
  constructor(reason) {
    super(reason);
    this.name = 'OpError';
    this.reason = reason;
  }
}

/** @param {unknown} name */
export function isOpName(name) {
  return typeof name === 'string' && Object.hasOwn(SHAPES, name);
}

/**
 * Parses strict JSON: no duplicate keys, no byte order mark, valid UTF-8, nothing after the value.
 * @param {Uint8Array} bytes
 * @returns {import('../../../src/json.js').JsonValue | null} null if the bytes are not strict JSON
 */
export function parseJson(bytes) {
  try {
    return parseStrictJson(bytes, MAX_DEPTH);
  } catch (error) {
    if (error instanceof JsonParseError) {
      return null;
    }
    throw error;
  }
}

/**
 * Runs one of the SDK's checks and turns its refusal into an {@link OpError}.
 * @template T
 * @param {() => T} check
 * @returns {T}
 */
function sdk(check) {
  try {
    return check();
  } catch (error) {
    if (error instanceof WireError) {
      throw new OpError(error.detail);
    }
    throw error;
  }
}

/**
 * A JSON number as a JavaScript number. Integers come from the parser as bigints.
 * @param {import('../../../src/json.js').JsonValue | undefined} value
 * @param {string} what
 */
function number(value, what) {
  if (value?.type === 'num') {
    return value.value;
  }
  if (value?.type === 'int') {
    return Number(value.value);
  }
  throw new OpError(`${what} must be a number`);
}

/**
 * @param {import('../../../src/json.js').JsonValue | undefined} value
 * @param {string} what
 */
function string(value, what) {
  if (value?.type !== 'str') {
    throw new OpError(`${what} must be a string`);
  }
  return value.value;
}

/**
 * Standard base64 with padding, exactly as an encoder writes it, with nothing else in it.
 * @param {string} text
 * @returns {Buffer | null}
 */
function decodeCanonicalBase64(text) {
  if (text.length === 0 || text.length % 4 !== 0 || text.length > Math.ceil(MAX_PHOTO_BYTES / 3) * 4 + 4
    || !/^[A-Za-z0-9+/]*={0,2}$/.test(text)) {
    return null;
  }
  const data = Buffer.from(text, 'base64');
  return data.toString('base64') === text ? data : null;
}

/**
 * @param {import('../../../src/json.js').JsonValue} value
 */
function trackPoint(value) {
  if (value.type !== 'obj') {
    throw new OpError('each point is an object with lat, lon and optionally ts and alt');
  }
  /** @type {{ lat?: number, lon?: number, ts?: number, alt?: number }} */
  const point = {};
  for (const [key, field] of value.value) {
    if (key === 'lat' || key === 'lon') {
      point[key] = number(field, 'lat and lon');
    } else if (key === 'ts') {
      // An integer field written as a fraction is refused, as the protocol does (spec section 6.1).
      if (field.type !== 'int' || field.value < 0n || field.value > BigInt(MAX_SEQUENCE)) {
        throw new OpError('ts is whole milliseconds since 1970, from 0 to 2^53-1');
      }
      point.ts = Number(field.value);
    } else if (key === 'alt') {
      point.alt = number(field, 'alt');
    } else {
      throw new OpError('a point takes only lat, lon, ts and alt');
    }
  }
  if (point.lat === undefined || point.lon === undefined) {
    throw new OpError('each point needs lat and lon');
  }
  sdk(() => validateTrackPoint(point));
  return point;
}

/**
 * Checks one operation and returns it as a plain object, with its fields in the order they came. Throws
 * {@link OpError} if it breaks a rule. A photo's decoded bytes are returned as `photo`.
 * @param {import('../../../src/json.js').JsonValue} value
 * @returns {{ op: Record<string, any>, name: string, photo: Buffer | null }}
 */
export function readOperation(value) {
  if (value.type !== 'obj') {
    throw new OpError('an operation is a JSON object');
  }
  const fields = value.value;
  const nameValue = fields.get('op');
  if (nameValue?.type !== 'str' || !isOpName(nameValue.value)) {
    throw new OpError('op is one of pin, marker, note, photo, track, finish_track, move and delete');
  }
  const name = nameValue.value;
  const shape = SHAPES[/** @type {keyof typeof SHAPES} */ (name)];
  for (const key of fields.keys()) {
    if (key !== 'op' && !shape.required.includes(key) && !shape.optional.includes(key)) {
      throw new OpError(shape.says);
    }
  }
  for (const key of shape.required) {
    if (!fields.has(key)) {
      throw new OpError(shape.says);
    }
  }
  /** @type {Record<string, any>} */
  const op = {};
  /** @type {Buffer | null} */
  let photo = null;
  for (const [key, field] of fields) {
    switch (key) {
      case 'op':
        op.op = name;
        break;
      case 'id':
        op.id = sdk(() => validateItemId(string(field, 'id')));
        break;
      case 'lat':
      case 'lon':
        op[key] = number(field, 'lat and lon');
        break;
      case 'title': {
        const title = string(field, 'title');
        sdk(() => validateTitle(title));
        op.title = title;
        break;
      }
      case 'color': {
        const color = string(field, 'color');
        if (!MARKER_COLORS.includes(color)) {
          throw new OpError(`color is one of ${MARKER_COLORS.join(', ')}`);
        }
        op.color = color;
        break;
      }
      case 'text': {
        const text = string(field, 'text');
        sdk(() => validateNote(text));
        op.text = text;
        break;
      }
      case 'type': {
        const type = string(field, 'type');
        if (type !== 'image/jpeg' && type !== 'image/png') {
          throw new OpError('type is image/jpeg or image/png');
        }
        op.type = type;
        break;
      }
      case 'data': {
        const text = string(field, 'data');
        photo = decodeCanonicalBase64(text);
        if (photo === null) {
          throw new OpError('data is standard base64 with padding and no line breaks');
        }
        if (photo.length > MAX_PHOTO_BYTES) {
          throw new OpError(`a photo is 1 to ${MAX_PHOTO_BYTES} bytes`);
        }
        op.data = text;
        break;
      }
      case 'points': {
        if (field.type !== 'arr' || field.value.length < 1 || field.value.length > MAX_TRACK_POINTS) {
          throw new OpError(`points is a list of 1 to ${MAX_TRACK_POINTS} points`);
        }
        op.points = field.value.map(trackPoint);
        break;
      }
      default:
        throw new OpError(shape.says);
    }
  }
  if (op.lat !== undefined) {
    sdk(() => validateCoordinate({ lat: op.lat, lon: op.lon }));
  }
  if (photo !== null && detectPhotoType(photo) !== op.type) {
    throw new OpError('data does not start with the bytes of its type');
  }
  return { op, name, photo };
}
