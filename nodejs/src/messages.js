// Encodes and decodes every integration-v1 message. Decoding is an allow-list: unknown types and unknown fields are
// rejected, and nothing about identity is ever taken from a message.

import { JsonParseError, encodeJson, jsonInteger, jsonObject, parseStrictJson } from './json.js';
import {
  LIMITS_V1,
  isItemKind,
  validateCoordinate,
  validateItemId,
  validateNote,
  validateTitle,
  validateTrackPoint,
  MARKER_COLORS,
} from './model.js';
import {
  ErrorCode,
  MAX_CHUNK_BYTES,
  MAX_PLAINTEXT_LENGTH,
  MAX_POINTS_PER_MESSAGE,
  MAX_SEQUENCE,
  WireError,
  isFingerprint,
} from './protocol.js';

/** @typedef {import('./json.js').JsonValue} JsonValue */
/** @typedef {import('./model.js').Coordinate} Coordinate */
/** @typedef {import('./model.js').TrackPoint} TrackPoint */
/** @typedef {import('./model.js').ItemKind} ItemKind */
/** @typedef {import('./model.js').Limits} Limits */

/**
 * A request from a device to a receiver.
 * @typedef {{ t: 'ping' }
 *   | { t: 'put', kind: 'pin', id: string, at: Coordinate, title?: string | null }
 *   | { t: 'put', kind: 'marker', id: string, at: Coordinate, title?: string | null, color?: string | null }
 *   | { t: 'put', kind: 'note', id: string, at: Coordinate, text: string }
 *   | { t: 'move', id: string, to: Coordinate }
 *   | { t: 'delete', id: string }
 *   | { t: 'delete_all' }
 *   | { t: 'photo_begin', id: string, at: Coordinate, title?: string | null, type: string, size: number, sha256: string, chunks: number }
 *   | { t: 'photo_chunk', id: string, index: number, data: Uint8Array }
 *   | { t: 'photo_end', id: string }
 *   | { t: 'track_append', id: string, title?: string | null, points: TrackPoint[] }
 *   | { t: 'track_finish', id: string }} Request
 */

/**
 * What a receiver says when it refuses something.
 * @typedef {object} ReceiverError
 * @property {string} code kept as text: a device must treat a code it does not know as a permanent failure
 * @property {number | null} seq
 * @property {number | null} lastSeq
 * @property {number | null} retryAfterMs
 * @property {number[] | null} supported
 * @property {boolean} fatal
 */

/**
 * @typedef {{ version: number, lastSeq: number, kinds: ItemKind[], limits: Limits }} Welcome
 */

/**
 * A message from a receiver to a device.
 * @typedef {{ t: 'welcome', welcome: Welcome }
 *   | { t: 'ack', seq: number, dup: boolean }
 *   | { t: 'error', error: ReceiverError }
 *   | { t: 'pair_status', status: 'pending' }
 *   | { t: 'pair_status', status: 'denied' }
 *   | { t: 'pair_status', status: 'approved', kinds: ItemKind[], counter: number }} ReceiverMessage
 */

/**
 * @param {Partial<ReceiverError> & { code: string }} fields
 * @returns {ReceiverError}
 */
export function receiverError({ code, seq = null, lastSeq = null, retryAfterMs = null, supported = null, fatal = false }) {
  return { code, seq, lastSeq, retryAfterMs, supported, fatal };
}

/** Reads a JSON object against an explicit list of fields. */
class Fields {
  /** @param {JsonValue} value */
  constructor(value) {
    if (value.type !== 'obj') {
      throw new WireError(ErrorCode.invalidMessage, 'a message is a JSON object');
    }
    this.map = value.value;
  }

  /** @param {string} key */
  has(key) {
    return this.map.has(key);
  }

  /** @param {...string} allowed */
  only(...allowed) {
    for (const key of this.map.keys()) {
      if (!allowed.includes(key)) {
        throw new WireError(ErrorCode.invalidMessage, 'unknown field');
      }
    }
  }

  /** @param {string} key @returns {string} */
  string(key) {
    const value = this.map.get(key);
    if (value?.type !== 'str') {
      throw new WireError(ErrorCode.invalidMessage, `field '${key}' must be a string`);
    }
    return value.value;
  }

  /** @param {string} key */
  optionalString(key) {
    return this.has(key) ? this.string(key) : null;
  }

  /** @param {string} key @returns {bigint} */
  integer(key) {
    const value = this.map.get(key);
    if (value?.type !== 'int') {
      throw new WireError(ErrorCode.invalidMessage, `field '${key}' must be an integer`);
    }
    return value.value;
  }

  /** @param {string} key @returns {number} */
  number(key) {
    const value = this.map.get(key);
    if (value?.type === 'num') {
      return value.value;
    }
    if (value?.type === 'int') {
      return Number(value.value);
    }
    throw new WireError(ErrorCode.invalidMessage, `field '${key}' must be a number`);
  }

  /** @param {string} key @returns {boolean} */
  bool(key) {
    const value = this.map.get(key);
    if (value?.type !== 'bool') {
      throw new WireError(ErrorCode.invalidMessage, `field '${key}' must be true or false`);
    }
    return value.value;
  }

  /** @param {string} key @returns {JsonValue[]} */
  array(key) {
    const value = this.map.get(key);
    if (value?.type !== 'arr') {
      throw new WireError(ErrorCode.invalidMessage, `field '${key}' must be an array`);
    }
    return value.value;
  }

  seq(key = 'seq') {
    const value = this.integer(key);
    if (value < 1n || value > BigInt(MAX_SEQUENCE)) {
      throw new WireError(ErrorCode.invalidMessage, 'a sequence number is within 1...2^53-1');
    }
    return Number(value);
  }

  /** @param {string} key */
  nonNegative(key) {
    const value = this.integer(key);
    if (value < 0n || value > BigInt(MAX_SEQUENCE)) {
      throw new WireError(ErrorCode.invalidMessage, `field '${key}' is out of range`);
    }
    return Number(value);
  }

  id() {
    return validateItemId(this.string('id'));
  }

  coordinate() {
    const lat = this.number('lat');
    const lon = this.number('lon');
    return validateCoordinate({ lat, lon });
  }

  /** @param {Limits} limits */
  title(limits) {
    const title = this.optionalString('title');
    if (title === null) {
      return null;
    }
    validateTitle(title, limits);
    return title;
  }

  versions() {
    const values = this.array('versions');
    if (values.length < 1 || values.length > 16) {
      throw new WireError(ErrorCode.invalidMessage, "field 'versions' must have 1 to 16 entries");
    }
    return values.map(decodeVersion);
  }

  /** @returns {ItemKind[]} */
  kinds() {
    /** @type {ItemKind[]} */
    const kinds = [];
    for (const value of this.array('kinds')) {
      if (value.type !== 'str' || !isItemKind(value.value) || kinds.includes(value.value)) {
        throw new WireError(ErrorCode.invalidMessage, "field 'kinds' lists item kinds without repeats");
      }
      kinds.push(value.value);
    }
    return kinds;
  }

  sdkName() {
    const name = this.string('sdk');
    if (!/^[A-Za-z0-9._/-]{1,64}$/.test(name)) {
      throw new WireError(ErrorCode.invalidMessage, "field 'sdk' is 1 to 64 ASCII letters, digits or . _ / -");
    }
    return name;
  }
}

/** @param {JsonValue} value */
function decodeVersion(value) {
  if (value.type !== 'int' || value.value < 1n || value.value > 65535n) {
    throw new WireError(ErrorCode.invalidMessage, 'a version is an integer within 1...65535');
  }
  return Number(value.value);
}

/** @param {Uint8Array} data */
function parse(data) {
  if (data.length > MAX_PLAINTEXT_LENGTH) {
    throw new WireError(ErrorCode.tooLarge, 'a message is at most 65519 bytes');
  }
  let value;
  try {
    value = parseStrictJson(data);
  } catch (error) {
    if (error instanceof JsonParseError) {
      throw new WireError(ErrorCode.invalidMessage, 'the message is not valid strict JSON');
    }
    throw error;
  }
  return new Fields(value);
}

// Device to receiver

/**
 * @param {{ versions: number[], sdk: string }} hello
 * @returns {Buffer}
 */
export function encodeHello({ versions, sdk }) {
  return encodeJson(jsonObject([
    ['t', 'hello'],
    ['versions', versions.map((version) => jsonInteger(version))],
    ['sdk', sdk],
  ]));
}

/**
 * @param {Uint8Array} data
 * @returns {{ versions: number[], sdk: string }}
 */
export function decodeHello(data) {
  const fields = parse(data);
  if (fields.string('t') !== 'hello') {
    throw new WireError(ErrorCode.invalidMessage, 'the first message must be hello');
  }
  fields.only('t', 'versions', 'sdk');
  const versions = fields.versions();
  return { versions, sdk: fields.sdkName() };
}

/**
 * The payload of the third pairing message.
 * @param {{ versions: number[], sdk: string, name: string, kinds: ItemKind[] }} request
 * @returns {Buffer}
 */
export function encodePairRequest({ versions, sdk, name, kinds }) {
  return encodeJson(jsonObject([
    ['t', 'pair_request'],
    ['versions', versions.map((version) => jsonInteger(version))],
    ['sdk', sdk],
    ['name', name],
    ['kinds', [...kinds]],
  ]));
}

/**
 * @param {Uint8Array} data
 * @returns {{ versions: number[], sdk: string, name: string, kinds: ItemKind[] }}
 */
export function decodePairRequest(data) {
  const fields = parse(data);
  if (fields.string('t') !== 'pair_request') {
    throw new WireError(ErrorCode.invalidMessage, 'expected pair_request');
  }
  fields.only('t', 'versions', 'sdk', 'name', 'kinds');
  const name = fields.string('name');
  if (Buffer.byteLength(name, 'utf8') > 256) {
    throw new WireError(ErrorCode.tooLarge, 'a device name is too long');
  }
  const kinds = fields.kinds();
  if (kinds.length === 0) {
    throw new WireError(ErrorCode.invalidMessage, "field 'kinds' cannot be empty");
  }
  const versions = fields.versions();
  return { versions, sdk: fields.sdkName(), name, kinds };
}

/** @param {TrackPoint} point */
function encodePoint(point) {
  /** @type {Array<[string, any]>} */
  const fields = [['lat', point.lat], ['lon', point.lon]];
  if (point.ts !== undefined && point.ts !== null) {
    fields.push(['ts', jsonInteger(point.ts)]);
  }
  if (point.alt !== undefined && point.alt !== null) {
    fields.push(['alt', point.alt]);
  }
  return jsonObject(fields);
}

/**
 * Encodes a request with its sequence number. The field order is fixed, so a resent request is byte-for-byte the
 * same, and it is the order the Swift and .NET SDKs use.
 * @param {number} seq
 * @param {Request} request
 * @returns {Buffer}
 */
export function encodeRequest(seq, request) {
  /** @type {Array<[string, any]>} */
  const fields = [];
  const put = (/** @type {string} */ type) => {
    fields.push(['t', type], ['seq', jsonInteger(seq)]);
  };
  const putCoordinate = (/** @type {Coordinate} */ at) => {
    fields.push(['lat', at.lat], ['lon', at.lon]);
  };
  const putId = (/** @type {string} */ id) => {
    fields.push(['id', id]);
  };
  const putTitle = (/** @type {string | null | undefined} */ title) => {
    if (title !== undefined && title !== null) {
      fields.push(['title', title]);
    }
  };
  switch (request.t) {
    case 'ping':
      put('ping');
      break;
    case 'put':
      put('put');
      fields.push(['kind', request.kind]);
      putId(request.id);
      putCoordinate(request.at);
      if (request.kind === 'note') {
        fields.push(['text', request.text]);
      } else {
        putTitle(request.title);
        if (request.kind === 'marker' && request.color !== undefined && request.color !== null) {
          fields.push(['color', request.color]);
        }
      }
      break;
    case 'move':
      put('move');
      putId(request.id);
      putCoordinate(request.to);
      break;
    case 'delete':
      put('delete');
      putId(request.id);
      break;
    case 'delete_all':
      put('delete_all');
      break;
    case 'photo_begin':
      put('photo_begin');
      putId(request.id);
      putCoordinate(request.at);
      putTitle(request.title);
      fields.push(
        ['type', request.type],
        ['size', jsonInteger(request.size)],
        ['sha256', request.sha256],
        ['chunks', jsonInteger(request.chunks)],
      );
      break;
    case 'photo_chunk':
      put('photo_chunk');
      putId(request.id);
      fields.push(['index', jsonInteger(request.index)], ['data', Buffer.from(request.data).toString('base64')]);
      break;
    case 'photo_end':
      put('photo_end');
      putId(request.id);
      break;
    case 'track_append':
      put('track_append');
      putId(request.id);
      putTitle(request.title);
      fields.push(['points', request.points.map(encodePoint)]);
      break;
    case 'track_finish':
      put('track_finish');
      putId(request.id);
      break;
    default:
      throw new TypeError('unknown request');
  }
  return encodeJson(jsonObject(fields));
}

/**
 * Base64 with padding and nothing else: no white space, no line breaks and no stray bits in the last character, so
 * that one chunk has exactly one spelling.
 * @param {string} text
 * @returns {Buffer | null}
 */
function decodeCanonicalBase64(text) {
  if (!/^[A-Za-z0-9+/=]*$/.test(text) || text.length % 4 !== 0) {
    return null;
  }
  const data = Buffer.from(text, 'base64');
  return data.toString('base64') === text ? data : null;
}

/**
 * Decodes a request. Throws {@link WireError} for any message that is not exactly what the specification allows.
 * A receiver that announced lower limits passes them here, so that it enforces what it announced.
 * @param {Uint8Array} data
 * @param {Limits} [limits]
 * @returns {{ seq: number, request: Request }}
 */
export function decodeRequest(data, limits = LIMITS_V1) {
  const fields = parse(data);
  const type = fields.string('t');
  const seq = fields.seq();
  /** @type {Request} */
  let request;
  switch (type) {
    case 'ping':
      fields.only('t', 'seq');
      request = { t: 'ping' };
      break;
    case 'put': {
      const kind = fields.string('kind');
      switch (kind) {
        case 'pin': {
          fields.only('t', 'seq', 'kind', 'id', 'lat', 'lon', 'title');
          const id = fields.id();
          const at = fields.coordinate();
          request = { t: 'put', kind: 'pin', id, at, title: fields.title(limits) };
          break;
        }
        case 'marker': {
          fields.only('t', 'seq', 'kind', 'id', 'lat', 'lon', 'title', 'color');
          const text = fields.optionalString('color');
          if (text !== null && !MARKER_COLORS.includes(text)) {
            throw new WireError(ErrorCode.invalidMessage, 'unknown marker color');
          }
          const id = fields.id();
          const at = fields.coordinate();
          request = { t: 'put', kind: 'marker', id, at, title: fields.title(limits), color: text };
          break;
        }
        case 'note': {
          fields.only('t', 'seq', 'kind', 'id', 'lat', 'lon', 'text');
          const text = fields.string('text');
          validateNote(text, limits);
          const id = fields.id();
          request = { t: 'put', kind: 'note', id, at: fields.coordinate(), text };
          break;
        }
        default:
          throw new WireError(ErrorCode.invalidMessage, "field 'kind' must be pin, marker or note");
      }
      break;
    }
    case 'move': {
      fields.only('t', 'seq', 'id', 'lat', 'lon');
      const id = fields.id();
      request = { t: 'move', id, to: fields.coordinate() };
      break;
    }
    case 'delete':
      fields.only('t', 'seq', 'id');
      request = { t: 'delete', id: fields.id() };
      break;
    case 'delete_all':
      fields.only('t', 'seq');
      request = { t: 'delete_all' };
      break;
    case 'photo_begin': {
      fields.only('t', 'seq', 'id', 'lat', 'lon', 'title', 'type', 'size', 'sha256', 'chunks');
      const photoType = fields.string('type');
      if (photoType !== 'image/jpeg' && photoType !== 'image/png') {
        throw new WireError(ErrorCode.photoInvalid, 'a photo is image/jpeg or image/png');
      }
      const size = fields.integer('size');
      const chunks = fields.integer('chunks');
      const digest = fields.string('sha256');
      if (size < 1n || chunks < 1n) {
        throw new WireError(ErrorCode.invalidMessage, "fields 'size' and 'chunks' must be positive");
      }
      if (size > BigInt(limits.photoBytes)) {
        throw new WireError(ErrorCode.tooLarge, `a photo is at most ${limits.photoBytes} bytes`);
      }
      const chunkSize = BigInt(MAX_CHUNK_BYTES);
      if (chunks !== (size + chunkSize - 1n) / chunkSize) {
        throw new WireError(ErrorCode.invalidMessage, "field 'chunks' does not match field 'size'");
      }
      if (!isFingerprint(digest)) {
        throw new WireError(ErrorCode.invalidMessage, "field 'sha256' is 64 lowercase hexadecimal digits");
      }
      const id = fields.id();
      const at = fields.coordinate();
      request = {
        t: 'photo_begin', id, at, title: fields.title(limits), type: photoType, size: Number(size), sha256: digest, chunks: Number(chunks),
      };
      break;
    }
    case 'photo_chunk': {
      fields.only('t', 'seq', 'id', 'index', 'data');
      const index = fields.integer('index');
      if (index < 0n || index > 65534n) {
        throw new WireError(ErrorCode.invalidMessage, "field 'index' is out of range");
      }
      const chunk = decodeCanonicalBase64(fields.string('data'));
      if (chunk === null) {
        throw new WireError(ErrorCode.invalidMessage, "field 'data' must be canonical base64");
      }
      if (chunk.length === 0) {
        throw new WireError(ErrorCode.invalidMessage, 'a photo chunk cannot be empty');
      }
      if (chunk.length > MAX_CHUNK_BYTES) {
        throw new WireError(ErrorCode.tooLarge, `a photo chunk is at most ${MAX_CHUNK_BYTES} bytes`);
      }
      request = { t: 'photo_chunk', id: fields.id(), index: Number(index), data: chunk };
      break;
    }
    case 'photo_end':
      fields.only('t', 'seq', 'id');
      request = { t: 'photo_end', id: fields.id() };
      break;
    case 'track_append': {
      fields.only('t', 'seq', 'id', 'title', 'points');
      const values = fields.array('points');
      if (values.length < 1 || values.length > MAX_POINTS_PER_MESSAGE) {
        throw new WireError(ErrorCode.invalidMessage, `field 'points' has 1 to ${MAX_POINTS_PER_MESSAGE} entries`);
      }
      /** @type {TrackPoint[]} */
      const points = [];
      for (const value of values) {
        const point = new Fields(value);
        point.only('lat', 'lon', 'ts', 'alt');
        const alt = point.has('alt') ? point.number('alt') : null;
        const coordinate = point.coordinate();
        let ts = null;
        if (point.has('ts')) {
          const raw = point.integer('ts');
          if (raw < 0n || raw > BigInt(MAX_SEQUENCE)) {
            throw new WireError(ErrorCode.invalidMessage, 'a track point timestamp is milliseconds since 1970, within 0...2^53-1');
          }
          ts = Number(raw);
        }
        points.push(validateTrackPoint({ ...coordinate, ts, alt }));
      }
      const id = fields.id();
      request = { t: 'track_append', id, title: fields.title(limits), points };
      break;
    }
    case 'track_finish':
      fields.only('t', 'seq', 'id');
      request = { t: 'track_finish', id: fields.id() };
      break;
    default:
      throw new WireError(ErrorCode.invalidMessage, 'unknown message type');
  }
  return { seq, request };
}

// Receiver to device

/**
 * @param {ReceiverMessage} message
 * @returns {Buffer}
 */
export function encodeReceiverMessage(message) {
  switch (message.t) {
    case 'welcome': {
      const { welcome } = message;
      const { limits } = welcome;
      return encodeJson(jsonObject([
        ['t', 'welcome'],
        ['version', jsonInteger(welcome.version)],
        ['last_seq', jsonInteger(welcome.lastSeq)],
        ['kinds', [...welcome.kinds]],
        ['limits', jsonObject([
          ['items', jsonInteger(limits.items)],
          ['photos', jsonInteger(limits.photos)],
          ['photo_bytes', jsonInteger(limits.photoBytes)],
          ['note_bytes', jsonInteger(limits.noteBytes)],
          ['title_chars', jsonInteger(limits.titleScalars)],
          ['track_points', jsonInteger(limits.trackPoints)],
          ['open_tracks', jsonInteger(limits.openTracks)],
          ['tracks', jsonInteger(limits.tracks)],
          ['msgs_per_s', jsonInteger(limits.messagesPerSecond)],
          ['msg_burst', jsonInteger(limits.messageBurst)],
          ['points_per_s', jsonInteger(limits.pointsPerSecond)],
          ['point_burst', jsonInteger(limits.pointBurst)],
          ['disk_bytes', jsonInteger(limits.diskBytes)],
        ])],
      ]));
    }
    case 'ack':
      return encodeJson(jsonObject([['t', 'ack'], ['seq', jsonInteger(message.seq)], ['dup', message.dup]]));
    case 'error': {
      const { error } = message;
      /** @type {Array<[string, any]>} */
      const fields = [['t', 'error'], ['code', error.code]];
      if (error.seq !== null) {
        fields.push(['seq', jsonInteger(error.seq)]);
      }
      if (error.lastSeq !== null) {
        fields.push(['last_seq', jsonInteger(error.lastSeq)]);
      }
      if (error.retryAfterMs !== null) {
        fields.push(['retry_after_ms', jsonInteger(error.retryAfterMs)]);
      }
      if (error.supported !== null) {
        fields.push(['supported', error.supported.map((version) => jsonInteger(version))]);
      }
      if (error.fatal) {
        fields.push(['fatal', true]);
      }
      return encodeJson(jsonObject(fields));
    }
    case 'pair_status':
      if (message.status === 'approved') {
        return encodeJson(jsonObject([
          ['t', 'pair_status'],
          ['status', 'approved'],
          ['kinds', [...message.kinds]],
          ['counter', jsonInteger(message.counter)],
        ]));
      }
      return encodeJson(jsonObject([['t', 'pair_status'], ['status', message.status]]));
    default:
      throw new TypeError('unknown message');
  }
}

/**
 * @param {JsonValue} value
 * @returns {Limits}
 */
function decodeLimits(value) {
  const fields = new Fields(value);
  fields.only(
    'items', 'photos', 'photo_bytes', 'note_bytes', 'title_chars', 'track_points', 'open_tracks', 'tracks',
    'msgs_per_s', 'msg_burst', 'points_per_s', 'point_burst', 'disk_bytes',
  );
  const positive = (/** @type {string} */ key) => {
    const number = fields.integer(key);
    if (number < 1n || number > 2n ** 40n) {
      throw new WireError(ErrorCode.invalidMessage, 'a limit is out of range');
    }
    return Number(number);
  };
  const positiveInt = (/** @type {string} */ key) => Math.min(positive(key), 2 ** 31 - 1);
  // A device may batch this many points, so a smaller burst could never be satisfied.
  if (positive('point_burst') < MAX_POINTS_PER_MESSAGE) {
    throw new WireError(ErrorCode.invalidMessage, "field 'point_burst' is below the maximum batch size");
  }
  return {
    items: positiveInt('items'),
    photos: positiveInt('photos'),
    photoBytes: positiveInt('photo_bytes'),
    noteBytes: positiveInt('note_bytes'),
    titleScalars: positiveInt('title_chars'),
    trackPoints: positiveInt('track_points'),
    openTracks: positiveInt('open_tracks'),
    tracks: positiveInt('tracks'),
    messagesPerSecond: positiveInt('msgs_per_s'),
    messageBurst: positiveInt('msg_burst'),
    pointsPerSecond: positiveInt('points_per_s'),
    pointBurst: positiveInt('point_burst'),
    diskBytes: positive('disk_bytes'),
  };
}

/**
 * @param {Uint8Array} data
 * @returns {ReceiverMessage}
 */
export function decodeReceiverMessage(data) {
  const fields = parse(data);
  switch (fields.string('t')) {
    case 'welcome': {
      fields.only('t', 'version', 'last_seq', 'kinds', 'limits');
      const version = fields.integer('version');
      if (version < 1n || version > 65535n) {
        throw new WireError(ErrorCode.invalidMessage, "field 'version' is out of range");
      }
      const limitsValue = fields.map.get('limits');
      if (limitsValue === undefined) {
        throw new WireError(ErrorCode.invalidMessage, "field 'limits' is missing");
      }
      const lastSeq = fields.nonNegative('last_seq');
      const kinds = fields.kinds();
      return { t: 'welcome', welcome: { version: Number(version), lastSeq, kinds, limits: decodeLimits(limitsValue) } };
    }
    case 'ack': {
      fields.only('t', 'seq', 'dup');
      const seq = fields.seq();
      return { t: 'ack', seq, dup: fields.bool('dup') };
    }
    case 'error': {
      fields.only('t', 'code', 'seq', 'last_seq', 'retry_after_ms', 'supported', 'fatal');
      const code = fields.string('code');
      if (!/^[a-z_]{1,64}$/.test(code)) {
        throw new WireError(ErrorCode.invalidMessage, "field 'code' is lowercase letters and underscores");
      }
      const versions = fields.has('supported') ? fields.array('supported') : null;
      // Empty is allowed: a receiver may have dropped every version this device could speak.
      if (versions !== null && versions.length > 16) {
        throw new WireError(ErrorCode.invalidMessage, "field 'supported' has at most 16 entries");
      }
      const supported = versions === null ? null : versions.map(decodeVersion);
      const seq = fields.has('seq') ? fields.seq() : null;
      const lastSeq = fields.has('last_seq') ? fields.nonNegative('last_seq') : null;
      const retryAfterMs = fields.has('retry_after_ms') ? fields.nonNegative('retry_after_ms') : null;
      const fatal = fields.has('fatal') && fields.bool('fatal');
      return { t: 'error', error: { code, seq, lastSeq, retryAfterMs, supported, fatal } };
    }
    case 'pair_status':
      switch (fields.string('status')) {
        case 'pending':
          fields.only('t', 'status');
          return { t: 'pair_status', status: 'pending' };
        case 'denied':
          fields.only('t', 'status');
          return { t: 'pair_status', status: 'denied' };
        case 'approved': {
          fields.only('t', 'status', 'kinds', 'counter');
          const kinds = fields.kinds();
          return { t: 'pair_status', status: 'approved', kinds, counter: fields.nonNegative('counter') };
        }
        default:
          throw new WireError(ErrorCode.invalidMessage, 'unknown pairing status');
      }
    default:
      throw new WireError(ErrorCode.invalidMessage, 'unknown message type');
  }
}
