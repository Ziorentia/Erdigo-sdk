import ErdigoSDK
import Foundation

/// The operations of docs/cloud-relay.md. Values are checked with the SDK's own rules, so the relay
/// takes nothing the SDK would refuse to send.
enum Operations {
    struct Invalid: Error {
        let reason: String
    }

    private static let fields: [String: (required: [String], optional: [String])] = [
        "pin": (["id", "lat", "lon"], ["title"]),
        "marker": (["id", "lat", "lon"], ["title", "color"]),
        "note": (["id", "lat", "lon", "text"], []),
        "photo": (["id", "lat", "lon", "type", "data"], ["title"]),
        "track": (["id", "points"], ["title"]),
        "finish_track": (["id"], []),
        "move": (["id", "lat", "lon"], []),
        "delete": (["id"], []),
    ]

    /// The order fields are written in, so that every stored operation looks the same.
    private static let order = ["op", "id", "lat", "lon", "title", "color", "text", "type", "data", "points"]

    static let maxPoints = 100
    static let maxPhotoBytes = 1_048_576

    /// Checks one operation and returns it as the relay stores and hands it out.
    static func validate(_ value: JSONValue) throws -> Data {
        guard case .object(let object) = value else { throw Invalid(reason: "an operation must be an object") }
        guard case .string(let op)? = object["op"], let shape = fields[op] else {
            throw Invalid(reason: "op must be one of: \(fields.keys.sorted().joined(separator: ", "))")
        }
        for key in object.keys where key != "op" && !shape.required.contains(key) && !shape.optional.contains(key) {
            throw Invalid(reason: "\(op) has a field it does not take")
        }
        for key in shape.required where object[key] == nil {
            throw Invalid(reason: "\(op) needs \(key)")
        }

        var output: [String: JSONWriter.Value] = ["op": .string(op)]
        for (key, field) in object where key != "op" {
            output[key] = try check(key, field, op: op, object: object)
        }
        if op == "photo" { try checkPhoto(object) }
        return JSONWriter.encode(.object(order.compactMap { key in output[key].map { (key, $0) } }))
    }

    private static func check(_ key: String, _ value: JSONValue, op: String, object: [String: JSONValue]) throws -> JSONWriter.Value {
        switch key {
        case "id":
            guard case .string(let text) = value, (try? ItemID(text)) != nil else {
                throw Invalid(reason: "id must be 1 to 64 ASCII letters, digits, '-' or '_'")
            }
            return .string(text)
        case "lat", "lon":
            return try coordinatePart(key, value)
        case "title":
            guard case .string(let text) = value, (try? TextRules.validateTitle(text)) != nil else {
                throw Invalid(reason: "title must be 1 to 200 characters without control or text-direction characters")
            }
            return .string(text)
        case "text":
            guard case .string(let text) = value, (try? TextRules.validateNote(text)) != nil else {
                throw Invalid(reason: "text must be 1 to 4096 bytes without control or text-direction characters")
            }
            return .string(text)
        case "color":
            guard case .string(let text) = value, MarkerColor(rawValue: text) != nil else {
                throw Invalid(reason: "color must be one of: \(MarkerColor.allCases.map(\.rawValue).joined(separator: ", "))")
            }
            return .string(text)
        case "type":
            guard case .string(let text) = value, PhotoType(rawValue: text) != nil else {
                throw Invalid(reason: "type must be image/jpeg or image/png")
            }
            return .string(text)
        case "data":
            guard case .string(let text) = value else { throw Invalid(reason: "data must be a string") }
            return .string(text)
        case "points":
            return try points(value)
        default:
            throw Invalid(reason: "\(op) has a field it does not take")
        }
    }

    private static func coordinatePart(_ key: String, _ value: JSONValue) throws -> JSONWriter.Value {
        let limit = key == "lat" ? 90.0 : 180.0
        let name = key == "lat" ? "lat" : "lon"
        switch value {
        case .integer(let number) where Double(number).magnitude <= limit:
            return .integer(number)
        case .number(let number) where number.isFinite && number.magnitude <= limit:
            return .number(number)
        default:
            throw Invalid(reason: "\(name) must be a number from \(-Int(limit)) to \(Int(limit))")
        }
    }

    private static func points(_ value: JSONValue) throws -> JSONWriter.Value {
        guard case .array(let list) = value, (1...maxPoints).contains(list.count) else {
            throw Invalid(reason: "points must be a list of 1 to \(maxPoints) points")
        }
        return .array(try list.map { entry in
            guard case .object(let point) = entry else { throw Invalid(reason: "a point must be an object") }
            for key in point.keys where !["lat", "lon", "ts", "alt"].contains(key) {
                throw Invalid(reason: "a point takes only lat, lon, ts and alt")
            }
            guard let lat = point["lat"], let lon = point["lon"] else { throw Invalid(reason: "a point needs lat and lon") }
            var fields: [(String, JSONWriter.Value)] = [("lat", try coordinatePart("lat", lat)), ("lon", try coordinatePart("lon", lon))]
            if let ts = point["ts"] {
                guard case .integer(let number) = ts, (0...Int64((1 << 53) - 1)).contains(number) else {
                    throw Invalid(reason: "ts must be a whole number of milliseconds from 0 to 2^53-1")
                }
                fields.append(("ts", .integer(number)))
            }
            if let alt = point["alt"] {
                switch alt {
                case .integer(let number) where (-1000...100_000).contains(number):
                    fields.append(("alt", .integer(number)))
                case .number(let number) where number.isFinite && (-1000.0...100_000.0).contains(number):
                    fields.append(("alt", .number(number)))
                default:
                    throw Invalid(reason: "alt must be a number of metres from -1000 to 100000")
                }
            }
            return .object(fields)
        })
    }

    /// Standard base64 exactly as an encoder writes it, of a JPEG or PNG of the type given.
    private static func checkPhoto(_ object: [String: JSONValue]) throws {
        guard case .string(let type)? = object["type"], case .string(let text)? = object["data"] else { return }
        // Longer text cannot decode to a photo of the largest size, so it is not decoded at all.
        guard text.utf8.count <= (maxPhotoBytes + 2) / 3 * 4,
              let bytes = Data(base64Encoded: text), bytes.base64EncodedString() == text
        else {
            throw Invalid(reason: "data must be standard base64 with padding and no line breaks")
        }
        guard (1...maxPhotoBytes).contains(bytes.count) else {
            throw Invalid(reason: "data must decode to 1 to \(maxPhotoBytes) bytes")
        }
        guard PhotoType.detect(bytes)?.rawValue == type else {
            throw Invalid(reason: "data does not start like a file of its type")
        }
    }
}
