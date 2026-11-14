// Codes, tickets, text rules, strict JSON and the number format, as the .NET and Swift model tests check them.

import assert from 'node:assert/strict';
import { inspect } from 'node:util';
import { test } from 'node:test';
import { JsonParseError, formatNumber, parseStrictJson } from '../src/json.js';
import { decodeReceiverMessage, decodeRequest, encodeReceiverMessage, encodeRequest } from '../src/messages.js';
import {
  LIMITS_V1,
  PairingCode,
  PairingTicket,
  TokenBucket,
  detectPhotoType,
  sanitizeDeviceName,
  validateCoordinate,
  validateNote,
  validateTitle,
  validateTrackPoint,
} from '../src/model.js';
import { MAX_PLAINTEXT_LENGTH, Mode, WireError, fingerprint, parsePreamble, preamble, shortFingerprint } from '../src/protocol.js';

const wire = (/** @type {string} */ code) => (/** @type {unknown} */ error) => error instanceof WireError && error.code === code;

test('pairing codes are read as a person may type them', () => {
  const code = new PairingCode('abcde-fghjk-mnpqr-stvwx');
  assert.equal(code.canonical, 'ABCDEFGHJKMNPQRSTVWX');
  assert.equal(code.display, 'ABCDE-FGHJK-MNPQR-STVWX');
  assert.equal(new PairingCode(' o0il1 OOOOO IIIII LLLLL ').canonical, '00111000001111111111');
  assert.equal(new PairingCode('ABCDE\tFGHJK　MNPQR STVWX').canonical, 'ABCDEFGHJKMNPQRSTVWX');
  for (const text of ['ABCDE-FGHJK-MNPQR-STVW', 'ABCDE-FGHJK-MNPQR-STVWXY', 'ABCDE-FGHJK-MNPQR-STVWU', 'ÁBCDE-FGHJK-MNPQR-STVWX', '\ud800BCDE-FGHJK-MNPQR-STVWX', '']) {
    assert.throws(() => new PairingCode(text), wire('invalid_message'), JSON.stringify(text));
  }
});

test('a pairing code does not print itself', () => {
  const code = PairingCode.generate();
  assert.equal(String(code), '<pairing code>');
  assert.equal(JSON.stringify({ code }), '{"code":"<pairing code>"}');
  assert.ok(!inspect(code).includes(code.canonical));
  const ticket = new PairingTicket(code, 'ab'.repeat(32));
  assert.ok(!inspect(ticket).includes(code.canonical));
  assert.ok(!JSON.stringify({ ticket }).includes(code.canonical));
});

test('generated codes differ', () => {
  const codes = new Set(Array.from({ length: 50 }, () => PairingCode.generate().canonical));
  assert.equal(codes.size, 50);
});

test('pairing tickets are read from QR text', () => {
  const fp = 'ab'.repeat(32);
  const ticket = PairingTicket.parse(`erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&fp=${fp}`);
  assert.equal(ticket.fingerprint, fp);
  // The code is written back the way the receiver shows it: L is read as 1.
  assert.equal(ticket.uri, `erdigo-pair:1?code=TEST0-0N1YC-0DE00-00000&fp=${fp}`);
  const again = PairingTicket.parse(/** @type {string} */ (ticket.uri));
  assert.ok(again.code.equals(ticket.code));
  // Uppercase digits are read, and written back in lowercase.
  assert.equal(PairingTicket.parse(`erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&fp=${fp.toUpperCase()}`).fingerprint, fp);
  // A bare code stays the typed path, with no fingerprint and no QR text.
  const typed = PairingTicket.parse('  TEST0-0NLYC-0DE00-00000\n');
  assert.equal(typed.fingerprint, null);
  assert.equal(typed.uri, null);
  assert.equal(new PairingTicket(typed.code, 'not a fingerprint').uri, null);
});

const code = 'TEST0-0NLYC-0DE00-00000';
const fp = 'ab'.repeat(32);
const other = 'cd'.repeat(32);
for (const [name, text] of [
  ['nothing after the prefix', 'erdigo-pair:1?'],
  ['no fingerprint', `erdigo-pair:1?code=${code}`],
  ['no code', `erdigo-pair:1?fp=${fp}`],
  ['empty fingerprint', `erdigo-pair:1?code=${code}&fp=`],
  ['fingerprint without an equals sign', `erdigo-pair:1?code=${code}&fp`],
  ['empty code', `erdigo-pair:1?code=&fp=${fp}`],
  ['code without an equals sign', `erdigo-pair:1?code&fp=${fp}`],
  ['fingerprint one digit short', `erdigo-pair:1?code=${code}&fp=${fp.slice(0, -1)}`],
  ['fingerprint one digit long', `erdigo-pair:1?code=${code}&fp=${fp}a`],
  ['fingerprint of half the length', `erdigo-pair:1?code=${code}&fp=${fp.slice(0, 32)}`],
  ['fingerprint that is not hexadecimal', `erdigo-pair:1?code=${code}&fp=${'zz'.repeat(32)}`],
  ['fingerprint with an equals sign', `erdigo-pair:1?code=${code}&fp=${fp.slice(0, -1)}=`],
  ['fingerprint in other digits', `erdigo-pair:1?code=${code}&fp=${'１'.repeat(64)}`],
  ['fingerprint with a line break', `erdigo-pair:1?code=${code}&fp=${fp.slice(0, 32)}\n${fp.slice(0, 31)}`],
  ['repeated fingerprint', `erdigo-pair:1?code=${code}&fp=${fp}&fp=${fp}`],
  ['two fingerprints', `erdigo-pair:1?code=${code}&fp=${fp}&fp=${other}`],
  ['repeated code', `erdigo-pair:1?code=${code}&code=${code}&fp=${fp}`],
  ['repeated code after the fingerprint', `erdigo-pair:1?code=${code}&fp=${fp}&code=${code}`],
  ['an address', `erdigo-pair:1?code=${code}&fp=${fp}&host=192.0.2.1`],
  ['an unknown field in front', `erdigo-pair:1?x=1&code=${code}&fp=${fp}`],
  ['an unknown field with no value', `erdigo-pair:1?code=${code}&fp=${fp}&x`],
  ['a field with no name', `erdigo-pair:1?code=${code}&fp=${fp}&=1`],
  ['an equals sign before the code', `erdigo-pair:1?=code=${code}&fp=${fp}`],
  ['an equals sign before the fingerprint', `erdigo-pair:1?code=${code}&=fp=${fp}`],
  ['an equals sign before both', `erdigo-pair:1?=code=${code}&=fp=${fp}`],
  ['two equals signs before the fingerprint', `erdigo-pair:1?code=${code}&==fp=${fp}`],
  ['several equals signs before both', `erdigo-pair:1?===code=${code}&===fp=${fp}`],
  ['nothing but an equals sign', 'erdigo-pair:1?='],
  ['an empty field in the middle', `erdigo-pair:1?code=${code}&&fp=${fp}`],
  ['an empty field in front', `erdigo-pair:1?&code=${code}&fp=${fp}`],
  ['a trailing separator', `erdigo-pair:1?code=${code}&fp=${fp}&`],
  ['only separators', 'erdigo-pair:1?&&'],
  ['field names in capitals', `erdigo-pair:1?CODE=${code}&FP=${fp}`],
  ['another version', `erdigo-pair:2?code=${code}&fp=${fp}`],
  ['no question mark', `erdigo-pair:1code=${code}&fp=${fp}`],
  ['a prefix in capitals', `ERDIGO-PAIR:1?code=${code}&fp=${fp}`],
]) {
  test(`QR text needs exactly one code and one valid fingerprint: ${name}`, () => {
    assert.throws(() => PairingTicket.parse(text), wire('invalid_message'));
  });
}

test('pairing text over the limit is refused before it is read', () => {
  const qr = `erdigo-pair:1?code=${code}&fp=${fp}`;
  assert.equal(Buffer.byteLength(qr), 110);
  // White space is the one thing a reader tolerates, so it shows exactly where the limit is.
  for (const text of [qr, code]) {
    const atTheLimit = text + ' '.repeat(1024 - Buffer.byteLength(text));
    PairingTicket.parse(atTheLimit);
    assert.throws(() => PairingTicket.parse(`${atTheLimit} `), wire('invalid_message'));
  }
  // The limit counts bytes, not characters.
  const wide = '　'.repeat(Math.floor(1024 / 3) + 1);
  assert.ok(wide.length < 1024);
  assert.throws(() => PairingTicket.parse(code + wide), WireError);
  assert.throws(() => PairingTicket.parse(`erdigo-pair:1?code=${'-'.repeat(2_000_000)}${code}&fp=${fp}`), WireError);
  assert.throws(() => PairingTicket.parse(`erdigo-pair:1?${'&'.repeat(2_000_000)}`), WireError);
});

test('coordinates are validated', () => {
  assert.deepEqual(validateCoordinate({ lat: 90, lon: -180 }), { lat: 90, lon: -180 });
  for (const at of [{ lat: 90.0001, lon: 0 }, { lat: 0, lon: 180.5 }, { lat: Number.NaN, lon: 0 }, { lat: 0, lon: Infinity }, { lat: '1', lon: 0 }, null]) {
    assert.throws(() => validateCoordinate(at), wire('invalid_coordinate'));
  }
});

test('text rules', () => {
  validateTitle('Palace gate');
  validateTitle('경복궁 👋');
  validateTitle('x'.repeat(200));
  assert.throws(() => validateTitle(''), wire('invalid_text'));
  assert.throws(() => validateTitle('a\nb'), wire('invalid_text'));
  assert.throws(() => validateTitle('safe‮evil'), wire('invalid_text'));
  assert.throws(() => validateTitle('\ud800'), wire('invalid_text'));
  assert.throws(() => validateTitle('😀'.repeat(201)), wire('too_large'));
  validateNote('Line one\nLine two\ttabbed');
  assert.throws(() => validateNote('a\u0000b'), wire('invalid_text'));
  assert.throws(() => validateNote('﻿hello'), wire('invalid_text'));
  assert.throws(() => validateNote('가'.repeat(1366)), wire('too_large'));
  validateNote('가'.repeat(1365));
  assert.equal(sanitizeDeviceName('  Drone‮ A\u0007  '), 'Drone A');
  assert.equal(sanitizeDeviceName('x'.repeat(50)), 'x'.repeat(40));
  assert.equal(sanitizeDeviceName('\ud800⁦'), '');
});

test('track points are validated', () => {
  assert.deepEqual(validateTrackPoint({ lat: 1, lon: 2, ts: 0, alt: -1000 }), { lat: 1, lon: 2, ts: 0, alt: -1000 });
  assert.deepEqual(validateTrackPoint({ lat: 1, lon: 2 }), { lat: 1, lon: 2 });
  assert.throws(() => validateTrackPoint({ lat: 1, lon: 2, ts: -1 }), wire('invalid_message'));
  assert.throws(() => validateTrackPoint({ lat: 1, lon: 2, ts: 2 ** 53 }), wire('invalid_message'));
  assert.throws(() => validateTrackPoint({ lat: 1, lon: 2, ts: 1.5 }), wire('invalid_message'));
  assert.throws(() => validateTrackPoint({ lat: 1, lon: 2, alt: 100_000.1 }), wire('invalid_message'));
});

test('photo formats are detected from their first bytes', () => {
  assert.equal(detectPhotoType(Buffer.from([0xff, 0xd8, 0xff, 0xe0])), 'image/jpeg');
  assert.equal(detectPhotoType(Buffer.from('89504e470d0a1a0a', 'hex')), 'image/png');
  assert.equal(detectPhotoType(Buffer.from('GIF89a')), null);
  assert.equal(detectPhotoType(Buffer.from([0xff, 0xd8])), null);
});

test('requests round trip', () => {
  const requests = [
    { t: 'ping' },
    { t: 'put', kind: 'pin', id: 'a', at: { lat: 1, lon: 2 }, title: 'T' },
    { t: 'put', kind: 'marker', id: 'b', at: { lat: -1.5, lon: 2.25 }, title: null, color: 'gray' },
    { t: 'put', kind: 'note', id: 'c', at: { lat: 0, lon: 0 }, text: 'line\nline' },
    { t: 'move', id: 'a', to: { lat: 3, lon: 4 } },
    { t: 'delete', id: 'a' },
    { t: 'delete_all' },
    { t: 'photo_begin', id: 'p', at: { lat: 1, lon: 1 }, title: null, type: 'image/png', size: 96_001, sha256: 'ab'.repeat(32), chunks: 3 },
    { t: 'photo_chunk', id: 'p', index: 2, data: Buffer.from([1, 2, 3]) },
    { t: 'photo_end', id: 'p' },
    { t: 'track_append', id: 'w', title: 'Walk', points: [{ lat: 1, lon: 2, ts: 3, alt: 4.5 }, { lat: 5, lon: 6 }] },
    { t: 'track_finish', id: 'w' },
  ];
  for (const request of requests) {
    const decoded = decodeRequest(encodeRequest(7, /** @type {any} */ (request)));
    assert.equal(decoded.seq, 7);
    assert.equal(encodeRequest(7, decoded.request).toString(), encodeRequest(7, /** @type {any} */ (request)).toString());
  }
});

test('receiver messages round trip', () => {
  const messages = [
    { t: 'welcome', welcome: { version: 1, lastSeq: 9, kinds: ['pin'], limits: LIMITS_V1 } },
    { t: 'ack', seq: 1, dup: true },
    { t: 'error', error: { code: 'rate_limited', seq: 3, lastSeq: 2, retryAfterMs: 100, supported: [1], fatal: true } },
    { t: 'error', error: { code: 'not_found', seq: null, lastSeq: null, retryAfterMs: null, supported: null, fatal: false } },
    { t: 'pair_status', status: 'pending' },
    { t: 'pair_status', status: 'denied' },
    { t: 'pair_status', status: 'approved', kinds: ['pin', 'track'], counter: 12 },
  ];
  for (const message of messages) {
    assert.deepEqual(decodeReceiverMessage(encodeReceiverMessage(/** @type {any} */ (message))), message);
  }
});

for (const text of [
  '{"t":"ack","seq":1,"dup":false,"extra":1}',
  '{"t":"welcome","version":1,"last_seq":0,"kinds":[]}',
  '{"t":"error","code":"BAD CODE"}',
  '{"t":"pair_status","status":"approved"}',
  '{"t":"pair_status","status":"approved","kinds":["pin"]}',
  '{"t":"mystery"}',
  '{"t":"welcome","version":1,"last_seq":0,"kinds":[],"limits":{"items":500,"photos":50,"photo_bytes":1048576,"note_bytes":4096,"title_chars":200,"track_points":3000,"open_tracks":5,"tracks":200,"msgs_per_s":5,"msg_burst":20,"points_per_s":1,"point_burst":99,"disk_bytes":31457280}}',
  '{"t":"ack","seq":1.0,"dup":false}',
  '{"t":"ack","seq":0,"dup":false}',
]) {
  test(`the SDK rejects a malformed receiver message: ${text.slice(0, 50)}`, () => {
    assert.throws(() => decodeReceiverMessage(Buffer.from(text)), WireError);
  });
}

for (const text of ['', '{', '{}{}', '{"a":1,}', '[1,]', '01', '1.', '.5', '+1', '1e', 'tru', '"\\x"', '"\u0001"', '1e999', '[[[[[[[1]]]]]]]', '{"é":1,"é":2}', '"\\udc00"', '"\\ud800"', '"\\ud800\\u0041"', 'NaN', '﻿{}']) {
  test(`strict JSON refuses ${JSON.stringify(text)}`, () => {
    assert.throws(() => parseStrictJson(Buffer.from(text, 'utf8')), JsonParseError);
  });
}

test('strict JSON accepts what it should, and keeps integers apart from fractions', () => {
  assert.deepEqual(parseStrictJson(Buffer.from('"😀"')), { type: 'str', value: '😀' });
  assert.deepEqual(parseStrictJson(Buffer.from('"\\ud83d\\ude00"')), { type: 'str', value: '😀' });
  assert.deepEqual(parseStrictJson(Buffer.from('-1')), { type: 'int', value: -1n });
  assert.deepEqual(parseStrictJson(Buffer.from('1.0')), { type: 'num', value: 1 });
  assert.deepEqual(parseStrictJson(Buffer.from('1.5e2')), { type: 'num', value: 150 });
  // Too long for an integer, but still a number, so a field wanting an integer refuses it later.
  assert.deepEqual(parseStrictJson(Buffer.from('9223372036854775808')), { type: 'num', value: 9223372036854775808 });
  assert.deepEqual(parseStrictJson(Buffer.from('9223372036854775807')), { type: 'int', value: 9223372036854775807n });
  // Six levels are allowed, and seven are not.
  parseStrictJson(Buffer.from('[[[[[[1]]]]]]'));
  parseStrictJson(Buffer.from('{} \n'));
  assert.throws(() => parseStrictJson(Buffer.from('{"a":1,"a":1}')), (error) => error instanceof JsonParseError && error.reason === 'duplicate key');
});

test('the message size limit', () => {
  assert.throws(() => decodeRequest(Buffer.alloc(MAX_PLAINTEXT_LENGTH + 1, 0x20)), wire('too_large'));
});

test('the token bucket', () => {
  const bucket = new TokenBucket(2, 1, 100);
  assert.equal(bucket.take(100), null);
  assert.equal(bucket.take(100), null);
  assert.ok(Math.abs(/** @type {number} */ (bucket.take(100)) - 1) < 0.001);
  assert.equal(bucket.take(101), null);
  // Asking for more than the capacity never succeeds, and must not promise otherwise.
  assert.notEqual(new TokenBucket(2, 1, 0).take(0, 5), null);
});

// Numbers are written as the Swift SDK writes them. These pairs were printed by Swift's Double.description.
for (const [value, expected] of /** @type {Array<[number, string]>} */ ([
  [0, '0.0'], [-0, '-0.0'], [1, '1.0'], [37, '37.0'], [37.5796, '37.5796'], [126.977, '126.977'], [-33.8568, '-33.8568'],
  [0.0001, '0.0001'], [0.00001, '1e-05'], [0.000123, '0.000123'], [1e-7, '1e-07'], [1e15, '1000000000000000.0'],
  [1e16, '1e+16'], [9007199254740992, '9007199254740992.0'], [18014398509481984, '1.8014398509481984e+16'],
  [1.5e16, '1.5e+16'], [123456.789, '123456.789'], [100000, '100000.0'], [-1000, '-1000.0'], [38.5, '38.5'],
  [5e-324, '5e-324'], [1e-300, '1e-300'], [0.1, '0.1'], [0.30000000000000004, '0.30000000000000004'], [1e21, '1e+21'],
  [12345678901234567890, '1.2345678901234567e+19'],
])) {
  test(`numbers are written like Swift: ${expected}`, () => {
    assert.equal(formatNumber(value), expected);
    const parsed = parseStrictJson(Buffer.from(expected, 'ascii'));
    assert.ok(Object.is(parsed.type === 'num' ? parsed.value : Number(/** @type {any} */ (parsed).value), value));
  });
}

test('fingerprints and their short form', () => {
  const value = fingerprint(Buffer.alloc(32));
  assert.match(value, /^[0-9a-f]{64}$/);
  assert.equal(shortFingerprint(value), `${value.slice(0, 4)}-${value.slice(4, 8)}-${value.slice(8, 12)}-${value.slice(12, 16)}`);
  assert.equal(parsePreamble(preamble(Mode.connect)), Mode.connect);
  assert.equal(parsePreamble(Buffer.from('EIN1\u0003')), null);
  assert.equal(parsePreamble(Buffer.from('EIN2\u0001')), null);
});
