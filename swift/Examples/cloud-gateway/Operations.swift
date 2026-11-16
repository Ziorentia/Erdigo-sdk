import ErdigoSDK
import Foundation

/// A relay operation, read into the SDK's own types. Reading it applies the same rules as the relay,
/// because a server of your own may take the relay's place.
enum Operation {
    case pin(id: String, at: Coordinate, title: String?)
    case marker(id: String, at: Coordinate, title: String?, color: MarkerColor?)
    case note(id: String, at: Coordinate, text: String)
    case photo(id: String, at: Coordinate, data: Data, title: String?)
    case track(id: String, points: [TrackPoint], title: String?)
    case finishTrack(id: String)
    case move(id: String, to: Coordinate)
    case delete(id: String)

    /// Why an operation was not applied, as an error code to print.
    struct Refused: Error {
        let code: String
    }

    private static let fields: [String: Set<String>] = [
        "pin": ["id", "lat", "lon", "title"],
        "marker": ["id", "lat", "lon", "title", "color"],
        "note": ["id", "lat", "lon", "text"],
        "photo": ["id", "lat", "lon", "type", "data", "title"],
        "track": ["id", "points", "title"],
        "finish_track": ["id"],
        "move": ["id", "lat", "lon"],
        "delete": ["id"],
    ]

    /// The name to print. Only a known name, because anything from the relay could be someone's text.
    static func name(of value: JSONValue) -> String {
        if case .object(let object) = value, case .string(let op)? = object["op"], fields[op] != nil { return op }
        return "unknown"
    }

    init(_ value: JSONValue) throws {
        guard case .object(let object) = value, case .string(let op)? = object["op"], let allowed = Self.fields[op],
              object.keys.allSatisfy({ $0 == "op" || allowed.contains($0) })
        else { throw Refused(code: "invalid_op") }
        let read = Reader(object: object)
        switch op {
        case "pin":
            self = .pin(id: try read.id(), at: try read.coordinate(), title: try read.title())
        case "marker":
            self = .marker(id: try read.id(), at: try read.coordinate(), title: try read.title(), color: try read.color())
        case "note":
            self = .note(id: try read.id(), at: try read.coordinate(), text: try read.text())
        case "photo":
            self = .photo(id: try read.id(), at: try read.coordinate(), data: try read.photo(), title: try read.title())
        case "track":
            self = .track(id: try read.id(), points: try read.points(), title: try read.title())
        case "finish_track":
            self = .finishTrack(id: try read.id())
        case "move":
            self = .move(id: try read.id(), to: try read.coordinate())
        default:
            self = .delete(id: try read.id())
        }
    }

    func apply(with client: ErdigoClient) async throws {
        switch self {
        case .pin(let id, let at, let title):
            try await client.putPin(id: id, at: at, title: title)
        case .marker(let id, let at, let title, let color):
            try await client.putMarker(id: id, at: at, title: title, color: color)
        case .note(let id, let at, let text):
            try await client.putNote(id: id, at: at, text: text)
        case .photo(let id, let at, let data, let title):
            // The SDK sends it in pieces and starts again from the first if the connection breaks.
            try await client.putPhoto(id: id, at: at, data: data, title: title)
        case .track(let id, let points, let title):
            try await client.appendTrack(id: id, points: points, title: title)
        case .finishTrack(let id):
            try await client.finishTrack(id: id)
        case .move(let id, let to):
            try await client.move(id: id, to: to)
        case .delete(let id):
            try await client.delete(id: id)
        }
    }
}

/// Reads fields with the SDK's validators, turning what they refuse into the protocol's error code.
private struct Reader {
    let object: [String: JSONValue]

    private func checked<T>(_ body: () throws -> T) throws -> T {
        do {
            return try body()
        } catch let error as WireError {
            throw Operation.Refused(code: error.code.rawValue)
        }
    }

    private func string(_ key: String) throws -> String {
        guard case .string(let text)? = object[key] else { throw Operation.Refused(code: "invalid_op") }
        return text
    }

    private func number(_ value: JSONValue?) throws -> Double {
        switch value {
        case .integer(let number)?: return Double(number)
        case .number(let number)?: return number
        default: throw Operation.Refused(code: "invalid_op")
        }
    }

    func id() throws -> String {
        let text = try string("id")
        _ = try checked { try ItemID(text) }
        return text
    }

    func coordinate() throws -> Coordinate {
        let latitude = try number(object["lat"])
        let longitude = try number(object["lon"])
        return try checked { try Coordinate(latitude: latitude, longitude: longitude) }
    }

    func title() throws -> String? {
        guard object["title"] != nil else { return nil }
        let text = try string("title")
        try checked { try TextRules.validateTitle(text) }
        return text
    }

    func text() throws -> String {
        let text = try string("text")
        try checked { try TextRules.validateNote(text) }
        return text
    }

    func color() throws -> MarkerColor? {
        guard object["color"] != nil else { return nil }
        guard let color = MarkerColor(rawValue: try string("color")) else { throw Operation.Refused(code: "invalid_op") }
        return color
    }

    func photo() throws -> Data {
        guard let type = PhotoType(rawValue: try string("type")) else { throw Operation.Refused(code: "invalid_op") }
        let text = try string("data")
        guard text.utf8.count <= (Limits.v1.photoBytes + 2) / 3 * 4 else { throw Operation.Refused(code: "too_large") }
        // Only base64 exactly as an encoder writes it, so that every program reads the same bytes.
        guard let data = Data(base64Encoded: text), data.base64EncodedString() == text, !data.isEmpty else {
            throw Operation.Refused(code: "invalid_op")
        }
        guard data.count <= Limits.v1.photoBytes else { throw Operation.Refused(code: "too_large") }
        guard PhotoType.detect(data) == type else { throw Operation.Refused(code: "invalid_op") }
        return data
    }

    func points() throws -> [TrackPoint] {
        guard case .array(let list)? = object["points"], (1...IntegrationV1.maxPointsPerMessage).contains(list.count) else {
            throw Operation.Refused(code: "invalid_op")
        }
        return try list.map { entry in
            guard case .object(let point) = entry, point.keys.allSatisfy(["lat", "lon", "ts", "alt"].contains) else {
                throw Operation.Refused(code: "invalid_op")
            }
            let at = try Reader(object: point).coordinate()
            var timestamp: Int64?
            if let value = point["ts"] {
                guard case .integer(let number) = value else { throw Operation.Refused(code: "invalid_op") }
                timestamp = number
            }
            let altitude = try point["alt"].map { try number($0) }
            return try checked { try TrackPoint(coordinate: at, timestampMS: timestamp, altitudeM: altitude) }
        }
    }
}
