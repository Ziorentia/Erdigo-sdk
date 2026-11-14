// A small DNS message reader and writer, for multicast DNS service discovery on the local network (RFC 6762 and
// RFC 6763). Everything it reads comes from the network, so every length and every name pointer is checked.

import { BlockList, isIP } from 'node:net';
import { networkInterfaces } from 'node:os';
import { SERVICE_TYPE } from './protocol.js';

export const MDNS_PORT = 5353;
export const MULTICAST_V4 = '224.0.0.251';
export const MULTICAST_V6 = 'ff02::fb';
export const SERVICE_NAME = `${SERVICE_TYPE}.local`;
const MAX_NAME_LENGTH = 255;
const MAX_MESSAGE_LENGTH = 9000;

/** DNS record types this code reads and writes. */
export const DnsType = Object.freeze({ a: 1, ptr: 12, txt: 16, aaaa: 28, srv: 33, any: 255 });

/**
 * @typedef {{ name: string, type: number, unicastResponse?: boolean }} DnsQuestion
 * @typedef {{ name: string, type: number, ttl: number, cacheFlush: boolean, target?: string, port?: number, address?: string, text?: string[] }} DnsRecord
 * @typedef {{ id: number, isResponse: boolean, authoritative: boolean, questions: DnsQuestion[], answers: DnsRecord[], additionals: DnsRecord[] }} DnsMessage
 */

/**
 * @param {string} left
 * @param {string} right
 */
export function namesEqual(left, right) {
  return trimDot(left).toLowerCase() === trimDot(right).toLowerCase();
}

/** @param {string} name */
export function trimDot(name) {
  return name.replace(/\.+$/, '');
}

/**
 * @param {DnsMessage} message
 * @returns {Buffer}
 */
export function encodeDns(message) {
  /** @type {number[]} */
  const output = [];
  const u16 = (/** @type {number} */ value) => output.push((value >> 8) & 0xff, value & 0xff);
  const u32 = (/** @type {number} */ value) => {
    u16(Math.floor(value / 0x10000) & 0xffff);
    u16(value & 0xffff);
  };
  const name = (/** @type {string} */ text) => {
    for (const label of trimDot(text).split('.')) {
      const bytes = Buffer.from(label, 'utf8');
      if (bytes.length === 0 || bytes.length > 63) {
        throw new RangeError('a DNS label is 1 to 63 bytes');
      }
      output.push(bytes.length, ...bytes);
    }
    output.push(0);
  };
  const record = (/** @type {DnsRecord} */ entry) => {
    name(entry.name);
    u16(entry.type);
    u16(0x0001 | (entry.cacheFlush ? 0x8000 : 0));
    u32(entry.ttl);
    const lengthAt = output.length;
    u16(0);
    const start = output.length;
    switch (entry.type) {
      case DnsType.ptr:
        name(/** @type {string} */ (entry.target));
        break;
      case DnsType.srv:
        u16(0);
        u16(0);
        u16(/** @type {number} */ (entry.port));
        name(/** @type {string} */ (entry.target));
        break;
      case DnsType.txt:
        for (const text of entry.text?.length ? entry.text : ['']) {
          const bytes = Buffer.from(text, 'utf8').subarray(0, 255);
          output.push(bytes.length, ...bytes);
        }
        break;
      case DnsType.a:
      case DnsType.aaaa:
        output.push(...addressBytes(/** @type {string} */ (entry.address)));
        break;
      default:
        throw new RangeError('unsupported record type');
    }
    const length = output.length - start;
    output[lengthAt] = (length >> 8) & 0xff;
    output[lengthAt + 1] = length & 0xff;
  };
  u16(message.id);
  u16((message.isResponse ? 0x8000 : 0) | (message.authoritative ? 0x0400 : 0));
  u16(message.questions.length);
  u16(message.answers.length);
  u16(0);
  u16(message.additionals.length);
  for (const question of message.questions) {
    name(question.name);
    u16(question.type);
    u16(0x0001 | (question.unicastResponse ? 0x8000 : 0));
  }
  message.answers.forEach(record);
  message.additionals.forEach(record);
  return Buffer.from(output);
}

/**
 * @param {string} address
 * @returns {number[]}
 */
function addressBytes(address) {
  const plain = address.split('%')[0];
  if (isIP(plain) === 4) {
    return plain.split('.').map(Number);
  }
  if (isIP(plain) !== 6) {
    throw new RangeError('not an address');
  }
  let [head, tail] = plain.includes('::') ? plain.split('::') : [plain, null];
  const expand = (/** @type {string} */ part) => (part === '' ? [] : part.split(':'));
  let groups = expand(head);
  let rest = tail === null ? [] : expand(tail);
  const last = (rest.length ? rest : groups).at(-1) ?? '';
  /** @type {string[]} */
  let embedded = [];
  if (last.includes('.')) {
    const [a, b, c, d] = last.split('.').map(Number);
    embedded = [((a << 8) | b).toString(16), ((c << 8) | d).toString(16)];
    if (rest.length) {
      rest = [...rest.slice(0, -1), ...embedded];
    } else {
      groups = [...groups.slice(0, -1), ...embedded];
    }
  }
  const missing = 8 - groups.length - rest.length;
  const all = tail === null ? groups : [...groups, ...Array(missing).fill('0'), ...rest];
  return all.flatMap((group) => {
    const value = parseInt(group, 16);
    return [(value >> 8) & 0xff, value & 0xff];
  });
}

/**
 * @param {Uint8Array} bytes
 * @returns {string}
 */
function addressFromBytes(bytes) {
  if (bytes.length === 4) {
    return Array.from(bytes).join('.');
  }
  const groups = [];
  for (let index = 0; index < 16; index += 2) {
    groups.push(((bytes[index] << 8) | bytes[index + 1]).toString(16));
  }
  // The shortest form, as other tools write it.
  let bestStart = -1;
  let bestLength = 0;
  for (let index = 0; index < 8;) {
    if (groups[index] !== '0') {
      index += 1;
      continue;
    }
    let end = index;
    while (end < 8 && groups[end] === '0') {
      end += 1;
    }
    if (end - index > bestLength && end - index >= 2) {
      bestStart = index;
      bestLength = end - index;
    }
    index = end;
  }
  if (bestStart < 0) {
    return groups.join(':');
  }
  return `${groups.slice(0, bestStart).join(':')}::${groups.slice(bestStart + bestLength).join(':')}`;
}

class FormatError extends Error {}

/**
 * @param {Uint8Array} bytes
 * @param {number} offset
 * @param {number} count
 */
function need(bytes, offset, count) {
  if (offset < 0 || count < 0 || offset + count > bytes.length) {
    throw new FormatError('truncated DNS message');
  }
}

/**
 * Reads a possibly compressed name. A pointer must point backwards, to before where this name started, so a loop of
 * pointers cannot exist.
 * @param {Uint8Array} bytes
 * @param {{ offset: number }} cursor
 * @param {number} [limit]
 */
function readName(bytes, cursor, limit = Number.MAX_SAFE_INTEGER) {
  /** @type {string[]} */
  const labels = [];
  let total = 0;
  let position = cursor.offset;
  let lowestStart = cursor.offset;
  let jumped = false;
  for (;;) {
    need(bytes, position, 1);
    if (!jumped && position >= limit) {
      throw new FormatError('name runs past its record');
    }
    const size = bytes[position];
    if ((size & 0xc0) === 0xc0) {
      need(bytes, position, 2);
      const target = ((size & 0x3f) << 8) | bytes[position + 1];
      if (!jumped) {
        cursor.offset = position + 2;
        jumped = true;
      }
      if (target >= lowestStart) {
        throw new FormatError('a name pointer must point backwards');
      }
      lowestStart = target;
      position = target;
      continue;
    }
    if ((size & 0xc0) !== 0) {
      throw new FormatError('unknown label type');
    }
    position += 1;
    if (size === 0) {
      if (!jumped) {
        cursor.offset = position;
      }
      break;
    }
    need(bytes, position, size);
    total += size + 1;
    if (total > MAX_NAME_LENGTH) {
      throw new FormatError('name too long');
    }
    labels.push(Buffer.from(bytes.subarray(position, position + size)).toString('utf8'));
    position += size;
  }
  if (!jumped && cursor.offset > limit) {
    throw new FormatError('name runs past its record');
  }
  return labels.join('.');
}

/**
 * @param {Uint8Array} bytes
 * @param {{ offset: number }} cursor
 * @returns {DnsRecord | null}
 */
function readRecord(bytes, cursor) {
  const name = readName(bytes, cursor);
  const offset = cursor.offset;
  need(bytes, offset, 10);
  const type = (bytes[offset] << 8) | bytes[offset + 1];
  const klass = (bytes[offset + 2] << 8) | bytes[offset + 3];
  const ttl = ((bytes[offset + 4] << 24) >>> 0) + ((bytes[offset + 5] << 16) | (bytes[offset + 6] << 8) | bytes[offset + 7]);
  const length = (bytes[offset + 8] << 8) | bytes[offset + 9];
  const start = offset + 10;
  need(bytes, start, length);
  const end = start + length;
  cursor.offset = end;
  if ((klass & 0x7fff) !== 0x0001) {
    return null;
  }
  const cacheFlush = (klass & 0x8000) !== 0;
  switch (type) {
    case DnsType.ptr:
      return { name, type, ttl, cacheFlush, target: readName(bytes, { offset: start }, end) };
    case DnsType.srv: {
      if (length < 7) {
        throw new FormatError('short SRV record');
      }
      const port = (bytes[start + 4] << 8) | bytes[start + 5];
      return { name, type, ttl, cacheFlush, port, target: readName(bytes, { offset: start + 6 }, end) };
    }
    case DnsType.txt: {
      const text = [];
      let position = start;
      while (position < end) {
        const size = bytes[position];
        position += 1;
        if (position + size > end) {
          throw new FormatError('bad TXT record');
        }
        text.push(Buffer.from(bytes.subarray(position, position + size)).toString('utf8'));
        position += size;
      }
      return { name, type, ttl, cacheFlush, text };
    }
    case DnsType.a:
      return length === 4 ? { name, type, ttl, cacheFlush, address: addressFromBytes(bytes.subarray(start, end)) } : null;
    case DnsType.aaaa:
      return length === 16 ? { name, type, ttl, cacheFlush, address: addressFromBytes(bytes.subarray(start, end)) } : null;
    default:
      return null;
  }
}

/**
 * Reads a message, or returns null for anything malformed. Records of other types are skipped.
 * @param {Uint8Array} data
 * @returns {DnsMessage | null}
 */
export function decodeDns(data) {
  if (data.length < 12 || data.length > MAX_MESSAGE_LENGTH) {
    return null;
  }
  try {
    const id = (data[0] << 8) | data[1];
    const flags = (data[2] << 8) | data[3];
    // Only a standard query or response with no error is of any use.
    if (((flags >> 11) & 0xf) !== 0 || (flags & 0xf) !== 0) {
      return null;
    }
    const counts = [0, 1, 2, 3].map((index) => (data[4 + index * 2] << 8) | data[5 + index * 2]);
    const cursor = { offset: 12 };
    /** @type {DnsQuestion[]} */
    const questions = [];
    for (let index = 0; index < counts[0]; index += 1) {
      const name = readName(data, cursor);
      need(data, cursor.offset, 4);
      const type = (data[cursor.offset] << 8) | data[cursor.offset + 1];
      const klass = (data[cursor.offset + 2] << 8) | data[cursor.offset + 3];
      cursor.offset += 4;
      questions.push({ name, type, unicastResponse: (klass & 0x8000) !== 0 });
    }
    /** @type {DnsRecord[][]} */
    const sections = [[], [], []];
    for (let section = 0; section < 3; section += 1) {
      for (let index = 0; index < counts[section + 1]; index += 1) {
        const record = readRecord(data, cursor);
        if (record !== null) {
          sections[section].push(record);
        }
      }
    }
    return { id, isResponse: (flags & 0x8000) !== 0, authoritative: (flags & 0x0400) !== 0, questions, answers: sections[0], additionals: sections[2] };
  } catch (error) {
    if (error instanceof FormatError) {
      return null;
    }
    throw error;
  }
}

/**
 * True for this computer, link-local addresses and addresses on a subnet one of this computer's interfaces is
 * directly on.
 * @param {string} address
 */
export function isLocalAddress(address) {
  let plain = address.split('%')[0];
  const mapped = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/i.exec(plain);
  if (mapped) {
    plain = mapped[1];
  }
  const family = isIP(plain);
  if (family === 0) {
    return false;
  }
  const local = new BlockList();
  local.addSubnet('127.0.0.0', 8, 'ipv4');
  local.addSubnet('169.254.0.0', 16, 'ipv4');
  local.addAddress('::1', 'ipv6');
  local.addSubnet('fe80::', 10, 'ipv6');
  for (const entries of Object.values(networkInterfaces())) {
    for (const entry of entries ?? []) {
      const prefix = Number(entry.cidr?.split('/')[1]);
      const kind = isIP(entry.address);
      if (kind !== 0 && Number.isInteger(prefix) && prefix > 0) {
        local.addSubnet(entry.address.split('%')[0], prefix, kind === 4 ? 'ipv4' : 'ipv6');
      }
    }
  }
  return local.check(plain, family === 4 ? 'ipv4' : 'ipv6');
}
