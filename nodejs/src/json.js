// Strict JSON: a parser that refuses what ordinary parsers tolerate, and a writer with a stable field order.
//
// A hand-written parser is used, as in the Swift and .NET SDKs, so that every SDK reads the same bytes the same way.
// JSON.parse keeps the last of two duplicate keys, accepts lone surrogate escapes and does not tell 1 from 1.0, so
// it cannot be used for anything that comes from a receiver.

/** Why {@link parseStrictJson} refused its input. */
export class JsonParseError extends Error {
  /**
   * @param {string} reason
   * @param {number} [offset]
   */
  constructor(reason, offset = -1) {
    super(`strict JSON: ${reason}`);
    this.name = 'JsonParseError';
    this.reason = reason;
    this.offset = offset;
  }
}

export const DEFAULT_MAX_DEPTH = 6;

/**
 * A decoded JSON value. Integers and fractional numbers are kept apart, so that a field documented as an integer
 * cannot silently accept `1.5`. An integer keeps its exact value as a bigint.
 * @typedef {{ type: 'null' }
 *   | { type: 'bool', value: boolean }
 *   | { type: 'int', value: bigint }
 *   | { type: 'num', value: number }
 *   | { type: 'str', value: string }
 *   | { type: 'arr', value: JsonValue[] }
 *   | { type: 'obj', value: Map<string, JsonValue> }} JsonValue
 */

const INT64_MIN = -(2n ** 63n);
const INT64_MAX = 2n ** 63n - 1n;
const strictDecoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

/**
 * Rejects overlong forms, encoded surrogates and values above U+10FFFF.
 * @param {Uint8Array} bytes
 */
export function isValidUtf8(bytes) {
  let index = 0;
  while (index < bytes.length) {
    const first = bytes[index];
    let length;
    let minimumSecond = 0x80;
    let maximumSecond = 0xbf;
    if (first <= 0x7f) {
      index += 1;
      continue;
    } else if (first >= 0xc2 && first <= 0xdf) {
      length = 2;
    } else if (first === 0xe0) {
      length = 3;
      minimumSecond = 0xa0;
    } else if ((first >= 0xe1 && first <= 0xec) || first === 0xee || first === 0xef) {
      length = 3;
    } else if (first === 0xed) {
      length = 3;
      maximumSecond = 0x9f;
    } else if (first === 0xf0) {
      length = 4;
      minimumSecond = 0x90;
    } else if (first >= 0xf1 && first <= 0xf3) {
      length = 4;
    } else if (first === 0xf4) {
      length = 4;
      maximumSecond = 0x8f;
    } else {
      return false;
    }
    if (index + length > bytes.length) {
      return false;
    }
    if (bytes[index + 1] < minimumSecond || bytes[index + 1] > maximumSecond) {
      return false;
    }
    for (let offset = 2; offset < length; offset += 1) {
      if ((bytes[index + offset] & 0xc0) !== 0x80) {
        return false;
      }
    }
    index += length;
  }
  return true;
}

/**
 * An RFC 8259 parser that refuses duplicate keys, invalid UTF-8, a byte order mark, unpaired surrogates, non-finite
 * numbers, anything but white space after the value, and more than six levels of nesting. Keys that are the same
 * text in different Unicode spellings count as duplicates, because Swift compares strings that way, and two SDKs must
 * not disagree about whether a message has a duplicate key.
 * @param {Uint8Array} data
 * @param {number} [maxDepth]
 * @returns {JsonValue}
 */
export function parseStrictJson(data, maxDepth = DEFAULT_MAX_DEPTH) {
  if (!isValidUtf8(data)) {
    throw new JsonParseError('invalid UTF-8');
  }
  const bytes = data;
  let index = 0;

  const fail = (/** @type {string} */ reason, at = index) => {
    throw new JsonParseError(reason, at);
  };
  const skipWhitespace = () => {
    while (index < bytes.length && (bytes[index] === 0x20 || bytes[index] === 0x09 || bytes[index] === 0x0a || bytes[index] === 0x0d)) {
      index += 1;
    }
  };
  const peek = () => {
    if (index >= bytes.length) {
      fail('unexpected end');
    }
    return bytes[index];
  };
  const expect = (/** @type {string} */ literal) => {
    if (index + literal.length > bytes.length) {
      fail('unexpected end');
    }
    for (let offset = 0; offset < literal.length; offset += 1) {
      if (bytes[index + offset] !== literal.charCodeAt(offset)) {
        fail('unexpected byte');
      }
    }
    index += literal.length;
  };
  const isDigit = (/** @type {number} */ byte) => byte >= 0x30 && byte <= 0x39;
  const consumeDigits = () => {
    while (index < bytes.length && isDigit(bytes[index])) {
      index += 1;
    }
  };
  const requireDigit = () => {
    if (!isDigit(peek())) {
      fail('unexpected byte');
    }
  };
  const parseHex4 = () => {
    if (index + 4 > bytes.length) {
      fail('unexpected end');
    }
    let value = 0;
    for (let count = 0; count < 4; count += 1) {
      const digit = bytes[index];
      index += 1;
      let number;
      if (digit >= 0x30 && digit <= 0x39) {
        number = digit - 0x30;
      } else if (digit >= 0x61 && digit <= 0x66) {
        number = digit - 0x61 + 10;
      } else if (digit >= 0x41 && digit <= 0x46) {
        number = digit - 0x41 + 10;
      } else {
        fail('invalid escape', index - 1);
      }
      value = (value << 4) | /** @type {number} */ (number);
    }
    return value;
  };
  const parseString = () => {
    index += 1;
    /** @type {number[]} */
    const output = [];
    for (;;) {
      const current = peek();
      if (current === 0x22) {
        index += 1;
        // Every byte here was checked as UTF-8, and every escape is a scalar value, so this loses nothing.
        return strictDecoder.decode(Uint8Array.from(output));
      }
      if (current === 0x5c) {
        index += 1;
        const escape = peek();
        index += 1;
        switch (escape) {
          case 0x22: output.push(0x22); break;
          case 0x5c: output.push(0x5c); break;
          case 0x2f: output.push(0x2f); break;
          case 0x62: output.push(0x08); break;
          case 0x66: output.push(0x0c); break;
          case 0x6e: output.push(0x0a); break;
          case 0x72: output.push(0x0d); break;
          case 0x74: output.push(0x09); break;
          case 0x75: {
            let scalar = parseHex4();
            if (scalar >= 0xdc00 && scalar <= 0xdfff) {
              fail('invalid escape');
            }
            if (scalar >= 0xd800 && scalar <= 0xdbff) {
              if (index + 2 > bytes.length || bytes[index] !== 0x5c || bytes[index + 1] !== 0x75) {
                fail('invalid escape');
              }
              index += 2;
              const low = parseHex4();
              if (low < 0xdc00 || low > 0xdfff) {
                fail('invalid escape');
              }
              scalar = 0x10000 + ((scalar - 0xd800) << 10) + (low - 0xdc00);
            }
            for (const byte of Buffer.from(String.fromCodePoint(scalar), 'utf8')) {
              output.push(byte);
            }
            break;
          }
          default:
            fail('invalid escape', index - 1);
        }
        continue;
      }
      if (current <= 0x1f) {
        fail('unexpected byte');
      }
      output.push(current);
      index += 1;
    }
  };
  /** @returns {JsonValue} */
  const parseNumber = () => {
    const start = index;
    let isInteger = true;
    if (bytes[index] === 0x2d) {
      index += 1;
    }
    const first = peek();
    if (!isDigit(first)) {
      fail('unexpected byte');
    }
    index += 1;
    if (first === 0x30 && index < bytes.length && isDigit(bytes[index])) {
      fail('unexpected byte');
    }
    consumeDigits();
    if (index < bytes.length && bytes[index] === 0x2e) {
      isInteger = false;
      index += 1;
      requireDigit();
      consumeDigits();
    }
    if (index < bytes.length && (bytes[index] === 0x65 || bytes[index] === 0x45)) {
      isInteger = false;
      index += 1;
      if (index < bytes.length && (bytes[index] === 0x2b || bytes[index] === 0x2d)) {
        index += 1;
      }
      requireDigit();
      consumeDigits();
    }
    const text = Buffer.from(bytes.subarray(start, index)).toString('latin1');
    // An integer too long for 64 bits is still a number. A field that needs an integer refuses it later.
    if (isInteger) {
      const integer = BigInt(text);
      if (integer >= INT64_MIN && integer <= INT64_MAX) {
        return { type: 'int', value: integer };
      }
    }
    const value = Number(text);
    if (!Number.isFinite(value)) {
      fail('number out of range', start);
    }
    return { type: 'num', value };
  };
  /** @returns {JsonValue} */
  const parseObject = (/** @type {number} */ depth) => {
    index += 1;
    /** @type {Map<string, JsonValue>} */
    const result = new Map();
    const canonicalKeys = new Set();
    skipWhitespace();
    if (peek() === 0x7d) {
      index += 1;
      return { type: 'obj', value: result };
    }
    for (;;) {
      skipWhitespace();
      if (peek() !== 0x22) {
        fail('unexpected byte');
      }
      const key = parseString();
      // The same key in another Unicode spelling is a duplicate too, as it is for Swift.
      const canonical = key.normalize('NFC');
      if (result.has(key) || canonicalKeys.has(canonical)) {
        fail('duplicate key');
      }
      canonicalKeys.add(canonical);
      skipWhitespace();
      if (peek() !== 0x3a) {
        fail('unexpected byte');
      }
      index += 1;
      skipWhitespace();
      result.set(key, parseValue(depth + 1));
      skipWhitespace();
      const next = peek();
      index += 1;
      if (next === 0x7d) {
        return { type: 'obj', value: result };
      }
      if (next !== 0x2c) {
        fail('unexpected byte', index - 1);
      }
    }
  };
  /** @returns {JsonValue} */
  const parseArray = (/** @type {number} */ depth) => {
    index += 1;
    /** @type {JsonValue[]} */
    const result = [];
    skipWhitespace();
    if (peek() === 0x5d) {
      index += 1;
      return { type: 'arr', value: result };
    }
    for (;;) {
      skipWhitespace();
      result.push(parseValue(depth + 1));
      skipWhitespace();
      const next = peek();
      index += 1;
      if (next === 0x5d) {
        return { type: 'arr', value: result };
      }
      if (next !== 0x2c) {
        fail('unexpected byte', index - 1);
      }
    }
  };
  /** @returns {JsonValue} */
  const parseValue = (/** @type {number} */ depth) => {
    const byte = peek();
    switch (byte) {
      case 0x7b:
        if (depth > maxDepth) {
          fail('too deep');
        }
        return parseObject(depth);
      case 0x5b:
        if (depth > maxDepth) {
          fail('too deep');
        }
        return parseArray(depth);
      case 0x22:
        return { type: 'str', value: parseString() };
      case 0x74:
        expect('true');
        return { type: 'bool', value: true };
      case 0x66:
        expect('false');
        return { type: 'bool', value: false };
      case 0x6e:
        expect('null');
        return { type: 'null' };
      default:
        if (byte === 0x2d || isDigit(byte)) {
          return parseNumber();
        }
        return fail('unexpected byte');
    }
  };

  skipWhitespace();
  const value = parseValue(1);
  skipWhitespace();
  if (index !== bytes.length) {
    fail('trailing data');
  }
  return value;
}

// Writing. Objects keep their fields in the order given, so a resent message is byte-for-byte identical. A
// JavaScript number is written as an integer only when wrapped with jsonInteger; every other number is written the
// way the Swift SDK writes a Double.

/** Marks a number to be written as an integer. */
export class JsonInteger {
  /** @param {number | bigint} value */
  constructor(value) {
    const big = BigInt(value);
    this.value = big;
  }
}

/**
 * @param {number | bigint} value
 * @returns {JsonInteger}
 */
export function jsonInteger(value) {
  return new JsonInteger(value);
}

/** An object with its fields in order. */
export class JsonObject {
  /** @param {Array<[string, any]>} fields */
  constructor(fields) {
    this.fields = fields;
  }
}

/**
 * @param {Array<[string, any]>} fields
 * @returns {JsonObject}
 */
export function jsonObject(fields) {
  return new JsonObject(fields);
}

/**
 * Writes a value as compact JSON in UTF-8. Refuses a string with a lone surrogate instead of quietly changing it.
 * @param {any} value
 * @returns {Buffer}
 */
export function encodeJson(value) {
  /** @type {string[]} */
  const output = [];
  write(value, output);
  return Buffer.from(output.join(''), 'utf8');
}

/**
 * @param {any} value
 * @param {string[]} output
 */
function write(value, output) {
  if (value === true || value === false) {
    output.push(value ? 'true' : 'false');
  } else if (value instanceof JsonInteger) {
    output.push(value.value.toString());
  } else if (typeof value === 'number') {
    output.push(formatNumber(value));
  } else if (typeof value === 'string') {
    writeString(value, output);
  } else if (Array.isArray(value)) {
    output.push('[');
    value.forEach((item, index) => {
      if (index > 0) {
        output.push(',');
      }
      write(item, output);
    });
    output.push(']');
  } else if (value instanceof JsonObject) {
    output.push('{');
    value.fields.forEach(([key, item], index) => {
      if (index > 0) {
        output.push(',');
      }
      writeString(key, output);
      output.push(':');
      write(item, output);
    });
    output.push('}');
  } else {
    throw new TypeError('unknown JSON value');
  }
}

/**
 * @param {string} text
 * @param {string[]} output
 */
function writeString(text, output) {
  if (!isWellFormed(text)) {
    throw new TypeError('a string with a lone surrogate cannot be written as UTF-8');
  }
  let result = '"';
  for (const character of text) {
    switch (character) {
      case '"': result += '\\"'; break;
      case '\\': result += '\\\\'; break;
      case '\n': result += '\\n'; break;
      case '\t': result += '\\t'; break;
      case '\r': result += '\\r'; break;
      default: {
        const code = /** @type {number} */ (character.codePointAt(0));
        result += code < 0x20 ? `\\u${code.toString(16).padStart(4, '0')}` : character;
      }
    }
  }
  output.push(`${result}"`);
}

/**
 * True if the string holds no lone surrogate, so that it can be written as UTF-8 without change.
 * @param {string} text
 */
export function isWellFormed(text) {
  for (let index = 0; index < text.length; index += 1) {
    const unit = text.charCodeAt(index);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = text.charCodeAt(index + 1);
      if (!(next >= 0xdc00 && next <= 0xdfff)) {
        return false;
      }
      index += 1;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) {
      return false;
    }
  }
  return true;
}

/**
 * The shortest text that reads back as the same number, spelled the way the Swift SDK spells it, so that every SDK
 * writes the same bytes for the same request: `37.0`, `0.0001`, `1e-05`, `1e+16`.
 * @param {number} number
 * @returns {string}
 */
export function formatNumber(number) {
  if (!Number.isFinite(number)) {
    throw new RangeError('non-finite numbers cannot be written as JSON');
  }
  if (number === 0) {
    return Object.is(number, -0) ? '-0.0' : '0.0';
  }
  // toExponential() with no argument gives as many digits as it takes to read back the same number.
  const exponential = Math.abs(number).toExponential();
  const [mantissa, exponentText] = exponential.split('e');
  const scientific = Number(exponentText);
  const digits = mantissa.replace('.', '').replace(/0+$/, '') || '0';
  let output = number < 0 ? '-' : '';
  if (scientific < -4 || Math.abs(number) > 9007199254740992) {
    output += digits[0];
    if (digits.length > 1) {
      output += `.${digits.slice(1)}`;
    }
    output += `e${scientific < 0 ? '-' : '+'}${String(Math.abs(scientific)).padStart(2, '0')}`;
  } else if (scientific >= 0) {
    const integerDigits = scientific + 1;
    if (digits.length <= integerDigits) {
      output += `${digits}${'0'.repeat(integerDigits - digits.length)}.0`;
    } else {
      output += `${digits.slice(0, integerDigits)}.${digits.slice(integerDigits)}`;
    }
  } else {
    output += `0.${'0'.repeat(-scientific - 1)}${digits}`;
  }
  return output;
}
