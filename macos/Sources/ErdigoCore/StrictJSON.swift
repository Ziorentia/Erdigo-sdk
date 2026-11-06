import Foundation

/// A decoded JSON value. Integers and fractional numbers are kept apart so that a
/// field documented as an integer cannot silently accept `1.5`.
public enum JSONValue: Equatable, Sendable {
    case null
    case bool(Bool)
    case integer(Int64)
    case number(Double)
    case string(String)
    case array([JSONValue])
    case object([String: JSONValue])
}

/// An RFC 8259 parser that refuses input ordinary parsers tolerate: duplicate keys,
/// invalid UTF-8, a byte order mark, unpaired surrogates, non-finite numbers,
/// anything but white space after the value, and more than six levels of nesting.
///
/// A hand-written parser is used because `JSONSerialization` keeps the last value of a
/// duplicate key, so two implementations could read the same bytes differently.
public enum StrictJSON {
    public enum ParseError: Error, Equatable, Sendable {
        case invalidUTF8
        case unexpectedByte(offset: Int)
        case unexpectedEnd
        case duplicateKey
        case tooDeep
        case numberOutOfRange
        case invalidEscape
        case trailingData
    }

    public static let defaultMaxDepth = 6

    public static func parse(_ data: Data, maxDepth: Int = defaultMaxDepth) throws -> JSONValue {
        let bytes = [UInt8](data)
        guard isValidUTF8(bytes) else { throw ParseError.invalidUTF8 }
        var parser = Parser(bytes: bytes, maxDepth: maxDepth)
        let value = try parser.parseDocument()
        return value
    }

    /// Rejects overlong forms, encoded surrogates and values above U+10FFFF.
    public static func isValidUTF8(_ bytes: [UInt8]) -> Bool {
        var index = 0
        while index < bytes.count {
            let first = bytes[index]
            let length: Int
            var minimumSecond: UInt8 = 0x80
            var maximumSecond: UInt8 = 0xBF
            switch first {
            case 0x00...0x7F:
                index += 1
                continue
            case 0xC2...0xDF:
                length = 2
            case 0xE0:
                length = 3
                minimumSecond = 0xA0
            case 0xE1...0xEC, 0xEE...0xEF:
                length = 3
            case 0xED:
                length = 3
                maximumSecond = 0x9F
            case 0xF0:
                length = 4
                minimumSecond = 0x90
            case 0xF1...0xF3:
                length = 4
            case 0xF4:
                length = 4
                maximumSecond = 0x8F
            default:
                return false
            }
            guard index + length <= bytes.count else { return false }
            guard bytes[index + 1] >= minimumSecond, bytes[index + 1] <= maximumSecond else { return false }
            if length > 2 {
                for offset in 2..<length where bytes[index + offset] & 0xC0 != 0x80 {
                    return false
                }
            }
            index += length
        }
        return true
    }

    private struct Parser {
        let bytes: [UInt8]
        let maxDepth: Int
        var index = 0

        init(bytes: [UInt8], maxDepth: Int) {
            self.bytes = bytes
            self.maxDepth = maxDepth
        }

        mutating func parseDocument() throws -> JSONValue {
            skipWhitespace()
            let value = try parseValue(depth: 1)
            skipWhitespace()
            guard index == bytes.count else { throw ParseError.trailingData }
            return value
        }

        mutating func skipWhitespace() {
            while index < bytes.count {
                switch bytes[index] {
                case 0x20, 0x09, 0x0A, 0x0D:
                    index += 1
                default:
                    return
                }
            }
        }

        func peek() throws -> UInt8 {
            guard index < bytes.count else { throw ParseError.unexpectedEnd }
            return bytes[index]
        }

        mutating func expect(_ literal: StaticString) throws {
            let expected = literal.withUTF8Buffer { Array($0) }
            guard index + expected.count <= bytes.count else { throw ParseError.unexpectedEnd }
            guard Array(bytes[index..<(index + expected.count)]) == expected else {
                throw ParseError.unexpectedByte(offset: index)
            }
            index += expected.count
        }

        mutating func parseValue(depth: Int) throws -> JSONValue {
            switch try peek() {
            case UInt8(ascii: "{"):
                guard depth <= maxDepth else { throw ParseError.tooDeep }
                return try parseObject(depth: depth)
            case UInt8(ascii: "["):
                guard depth <= maxDepth else { throw ParseError.tooDeep }
                return try parseArray(depth: depth)
            case UInt8(ascii: "\""):
                return .string(try parseString())
            case UInt8(ascii: "t"):
                try expect("true")
                return .bool(true)
            case UInt8(ascii: "f"):
                try expect("false")
                return .bool(false)
            case UInt8(ascii: "n"):
                try expect("null")
                return .null
            case UInt8(ascii: "-"), UInt8(ascii: "0")...UInt8(ascii: "9"):
                return try parseNumber()
            default:
                throw ParseError.unexpectedByte(offset: index)
            }
        }

        mutating func parseObject(depth: Int) throws -> JSONValue {
            index += 1
            var result: [String: JSONValue] = [:]
            var rawKeys = Set<[UInt8]>()
            skipWhitespace()
            if try peek() == UInt8(ascii: "}") {
                index += 1
                return .object(result)
            }
            while true {
                skipWhitespace()
                guard try peek() == UInt8(ascii: "\"") else { throw ParseError.unexpectedByte(offset: index) }
                let key = try parseString()
                // Swift String equality uses canonical equivalence, so also compare the scalars.
                guard rawKeys.insert(Array(key.utf8)).inserted, result[key] == nil else {
                    throw ParseError.duplicateKey
                }
                skipWhitespace()
                guard try peek() == UInt8(ascii: ":") else { throw ParseError.unexpectedByte(offset: index) }
                index += 1
                skipWhitespace()
                result[key] = try parseValue(depth: depth + 1)
                skipWhitespace()
                let next = try peek()
                index += 1
                if next == UInt8(ascii: "}") { return .object(result) }
                guard next == UInt8(ascii: ",") else { throw ParseError.unexpectedByte(offset: index - 1) }
            }
        }

        mutating func parseArray(depth: Int) throws -> JSONValue {
            index += 1
            var result: [JSONValue] = []
            skipWhitespace()
            if try peek() == UInt8(ascii: "]") {
                index += 1
                return .array(result)
            }
            while true {
                skipWhitespace()
                result.append(try parseValue(depth: depth + 1))
                skipWhitespace()
                let next = try peek()
                index += 1
                if next == UInt8(ascii: "]") { return .array(result) }
                guard next == UInt8(ascii: ",") else { throw ParseError.unexpectedByte(offset: index - 1) }
            }
        }

        mutating func parseHex4() throws -> UInt32 {
            guard index + 4 <= bytes.count else { throw ParseError.unexpectedEnd }
            var value: UInt32 = 0
            for _ in 0..<4 {
                let byte = bytes[index]
                index += 1
                let digit: UInt8
                switch byte {
                case UInt8(ascii: "0")...UInt8(ascii: "9"):
                    digit = byte - UInt8(ascii: "0")
                case UInt8(ascii: "a")...UInt8(ascii: "f"):
                    digit = byte - UInt8(ascii: "a") + 10
                case UInt8(ascii: "A")...UInt8(ascii: "F"):
                    digit = byte - UInt8(ascii: "A") + 10
                default:
                    throw ParseError.invalidEscape
                }
                value = value << 4 | UInt32(digit)
            }
            return value
        }

        mutating func parseString() throws -> String {
            index += 1
            var output: [UInt8] = []
            while true {
                let byte = try peek()
                switch byte {
                case UInt8(ascii: "\""):
                    index += 1
                    return String(decoding: output, as: UTF8.self)
                case UInt8(ascii: "\\"):
                    index += 1
                    let escape = try peek()
                    index += 1
                    switch escape {
                    case UInt8(ascii: "\""): output.append(0x22)
                    case UInt8(ascii: "\\"): output.append(0x5C)
                    case UInt8(ascii: "/"): output.append(0x2F)
                    case UInt8(ascii: "b"): output.append(0x08)
                    case UInt8(ascii: "f"): output.append(0x0C)
                    case UInt8(ascii: "n"): output.append(0x0A)
                    case UInt8(ascii: "r"): output.append(0x0D)
                    case UInt8(ascii: "t"): output.append(0x09)
                    case UInt8(ascii: "u"):
                        var scalar = try parseHex4()
                        if (0xDC00...0xDFFF).contains(scalar) { throw ParseError.invalidEscape }
                        if (0xD800...0xDBFF).contains(scalar) {
                            guard index + 2 <= bytes.count,
                                  bytes[index] == UInt8(ascii: "\\"),
                                  bytes[index + 1] == UInt8(ascii: "u")
                            else { throw ParseError.invalidEscape }
                            index += 2
                            let low = try parseHex4()
                            guard (0xDC00...0xDFFF).contains(low) else { throw ParseError.invalidEscape }
                            scalar = 0x10000 + ((scalar - 0xD800) << 10) + (low - 0xDC00)
                        }
                        guard let value = Unicode.Scalar(scalar) else { throw ParseError.invalidEscape }
                        output.append(contentsOf: Array(String(Character(value)).utf8))
                    default:
                        throw ParseError.invalidEscape
                    }
                case 0x00...0x1F:
                    throw ParseError.unexpectedByte(offset: index)
                default:
                    output.append(byte)
                    index += 1
                }
            }
        }

        mutating func parseNumber() throws -> JSONValue {
            let start = index
            var isInteger = true
            if bytes[index] == UInt8(ascii: "-") { index += 1 }

            let first = try peek()
            guard (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(first) else {
                throw ParseError.unexpectedByte(offset: index)
            }
            index += 1
            if first == UInt8(ascii: "0"),
               index < bytes.count,
               (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(bytes[index]) {
                throw ParseError.unexpectedByte(offset: index)
            }
            consumeDigits()

            if index < bytes.count, bytes[index] == UInt8(ascii: ".") {
                isInteger = false
                index += 1
                try requireDigit()
                consumeDigits()
            }
            if index < bytes.count, bytes[index] == UInt8(ascii: "e") || bytes[index] == UInt8(ascii: "E") {
                isInteger = false
                index += 1
                if index < bytes.count, bytes[index] == UInt8(ascii: "+") || bytes[index] == UInt8(ascii: "-") {
                    index += 1
                }
                try requireDigit()
                consumeDigits()
            }

            let text = String(decoding: bytes[start..<index], as: UTF8.self)
            // An integer too long for 64 bits is still a number. A field that needs an integer refuses it later.
            if isInteger, let value = Int64(text) { return .integer(value) }
            guard let value = Double(text), value.isFinite else { throw ParseError.numberOutOfRange }
            return .number(value)
        }

        mutating func consumeDigits() {
            while index < bytes.count, (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(bytes[index]) {
                index += 1
            }
        }

        func requireDigit() throws {
            let byte = try peek()
            guard (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte) else {
                throw ParseError.unexpectedByte(offset: index)
            }
        }
    }
}

/// Writes JSON with a stable field order, so a retried message is byte-for-byte identical.
public enum JSONWriter {
    public indirect enum Value {
        case bool(Bool)
        case integer(Int64)
        case number(Double)
        case string(String)
        case array([Value])
        case object([(String, Value)])
    }

    public static func encode(_ value: Value) -> Data {
        var output = ""
        write(value, into: &output)
        return Data(output.utf8)
    }

    private static func write(_ value: Value, into output: inout String) {
        switch value {
        case .bool(let flag):
            output += flag ? "true" : "false"
        case .integer(let number):
            output += String(number)
        case .number(let number):
            // Swift prints the shortest text that round-trips, which is always valid JSON for finite values.
            precondition(number.isFinite, "non-finite numbers cannot be written as JSON")
            output += number.description
        case .string(let text):
            writeString(text, into: &output)
        case .array(let values):
            output += "["
            for (index, element) in values.enumerated() {
                if index > 0 { output += "," }
                write(element, into: &output)
            }
            output += "]"
        case .object(let fields):
            output += "{"
            for (index, field) in fields.enumerated() {
                if index > 0 { output += "," }
                writeString(field.0, into: &output)
                output += ":"
                write(field.1, into: &output)
            }
            output += "}"
        }
    }

    private static func writeString(_ text: String, into output: inout String) {
        output += "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": output += "\\\""
            case "\\": output += "\\\\"
            case "\n": output += "\\n"
            case "\t": output += "\\t"
            case "\r": output += "\\r"
            case _ where scalar.value < 0x20:
                output += String(format: "\\u%04x", scalar.value)
            default:
                output.unicodeScalars.append(scalar)
            }
        }
        output += "\""
    }
}
