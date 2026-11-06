import Foundation

/// A request from a device to a receiver. Decoding is an allow-list: unknown types and
/// unknown fields are rejected, and nothing about identity is ever taken from the message.
public enum Request: Equatable, Sendable {
    case ping
    case putPin(id: ItemID, at: Coordinate, title: String?)
    case putMarker(id: ItemID, at: Coordinate, title: String?, color: MarkerColor?)
    case putNote(id: ItemID, at: Coordinate, text: String)
    case move(id: ItemID, to: Coordinate)
    case delete(id: ItemID)
    case deleteAll
    case photoBegin(PhotoHeader)
    case photoChunk(id: ItemID, index: Int, data: Data)
    case photoEnd(id: ItemID)
    case trackAppend(id: ItemID, title: String?, points: [TrackPoint])
    case trackFinish(id: ItemID)
}

public struct PhotoHeader: Equatable, Sendable {
    public let id: ItemID
    public let at: Coordinate
    public let title: String?
    public let type: PhotoType
    public let size: Int
    public let sha256: String
    public let chunks: Int

    public init(id: ItemID, at: Coordinate, title: String?, type: PhotoType, size: Int, sha256: String, chunks: Int) {
        self.id = id
        self.at = at
        self.title = title
        self.type = type
        self.size = size
        self.sha256 = sha256
        self.chunks = chunks
    }
}

public struct RequestEnvelope: Equatable, Sendable {
    public let seq: UInt64
    public let request: Request

    public init(seq: UInt64, request: Request) {
        self.seq = seq
        self.request = request
    }
}

/// The first message on a connection, sent by the device.
public struct Hello: Equatable, Sendable {
    public let versions: [Int]
    public let sdk: String

    public init(versions: [Int], sdk: String) {
        self.versions = versions
        self.sdk = sdk
    }
}

/// The payload of the third pairing message.
public struct PairRequest: Equatable, Sendable {
    public let versions: [Int]
    public let sdk: String
    public let name: String
    public let kinds: [ItemKind]

    public init(versions: [Int], sdk: String, name: String, kinds: [ItemKind]) {
        self.versions = versions
        self.sdk = sdk
        self.name = name
        self.kinds = kinds
    }
}

public struct Welcome: Equatable, Sendable {
    public let version: Int
    public let lastSeq: UInt64
    public let kinds: [ItemKind]
    public let limits: Limits

    public init(version: Int, lastSeq: UInt64, kinds: [ItemKind], limits: Limits) {
        self.version = version
        self.lastSeq = lastSeq
        self.kinds = kinds
        self.limits = limits
    }
}

public struct ReceiverError: Equatable, Sendable {
    /// Kept as text: a device must treat a code it does not know as a permanent failure.
    public let code: String
    public let seq: UInt64?
    public let lastSeq: UInt64?
    public let retryAfterMS: UInt64?
    public let supported: [Int]?
    public let fatal: Bool

    public init(code: String, seq: UInt64? = nil, lastSeq: UInt64? = nil, retryAfterMS: UInt64? = nil, supported: [Int]? = nil, fatal: Bool = false) {
        self.code = code
        self.seq = seq
        self.lastSeq = lastSeq
        self.retryAfterMS = retryAfterMS
        self.supported = supported
        self.fatal = fatal
    }
}

public enum PairStatus: Equatable, Sendable {
    case pending
    /// `counter` is the highest connect counter the receiver has accepted from this device.
    case approved(kinds: [ItemKind], counter: UInt64)
    case denied
}

public enum ReceiverMessage: Equatable, Sendable {
    case welcome(Welcome)
    case ack(seq: UInt64, duplicate: Bool)
    case error(ReceiverError)
    case pairStatus(PairStatus)
}

/// Reads a JSON object against an explicit list of fields.
struct Fields {
    let map: [String: JSONValue]

    init(_ value: JSONValue) throws {
        guard case .object(let map) = value else { throw WireError(.invalidMessage, "a message is a JSON object") }
        self.map = map
    }

    func only(_ allowed: Set<String>) throws {
        for key in map.keys where !allowed.contains(key) {
            throw WireError(.invalidMessage, "unknown field")
        }
    }

    func string(_ key: String) throws -> String {
        guard case .string(let text)? = map[key] else { throw WireError(.invalidMessage, "field '\(key)' must be a string") }
        return text
    }

    func optionalString(_ key: String) throws -> String? {
        map[key] == nil ? nil : try string(key)
    }

    func integer(_ key: String) throws -> Int64 {
        guard case .integer(let number)? = map[key] else { throw WireError(.invalidMessage, "field '\(key)' must be an integer") }
        return number
    }

    func optionalInteger(_ key: String) throws -> Int64? {
        map[key] == nil ? nil : try integer(key)
    }

    func number(_ key: String) throws -> Double {
        switch map[key] {
        case .number(let value)?: return value
        case .integer(let value)?: return Double(value)
        default: throw WireError(.invalidMessage, "field '\(key)' must be a number")
        }
    }

    func bool(_ key: String) throws -> Bool {
        guard case .bool(let flag)? = map[key] else { throw WireError(.invalidMessage, "field '\(key)' must be true or false") }
        return flag
    }

    func array(_ key: String) throws -> [JSONValue] {
        guard case .array(let values)? = map[key] else { throw WireError(.invalidMessage, "field '\(key)' must be an array") }
        return values
    }

    func seq(_ key: String = "seq") throws -> UInt64 {
        let value = try integer(key)
        guard value >= 1, UInt64(value) <= IntegrationV1.maxSequence else {
            throw WireError(.invalidMessage, "a sequence number is within 1...2^53-1")
        }
        return UInt64(value)
    }

    func nonNegative(_ key: String) throws -> UInt64 {
        let value = try integer(key)
        guard value >= 0, UInt64(value) <= IntegrationV1.maxSequence else {
            throw WireError(.invalidMessage, "field '\(key)' is out of range")
        }
        return UInt64(value)
    }

    func id() throws -> ItemID { try ItemID(try string("id")) }

    func coordinate() throws -> Coordinate {
        try Coordinate(latitude: try number("lat"), longitude: try number("lon"))
    }

    func title(limits: Limits) throws -> String? {
        guard let title = try optionalString("title") else { return nil }
        try TextRules.validateTitle(title, limits: limits)
        return title
    }

    func versions() throws -> [Int] {
        let values = try array("versions")
        guard (1...16).contains(values.count) else { throw WireError(.invalidMessage, "field 'versions' must have 1 to 16 entries") }
        return try values.map { value in
            guard case .integer(let number) = value, (1...65535).contains(number) else {
                throw WireError(.invalidMessage, "a version is an integer within 1...65535")
            }
            return Int(number)
        }
    }

    func kinds() throws -> [ItemKind] {
        let values = try array("kinds")
        var kinds: [ItemKind] = []
        for value in values {
            guard case .string(let text) = value, let kind = ItemKind(rawValue: text), !kinds.contains(kind) else {
                throw WireError(.invalidMessage, "field 'kinds' lists item kinds without repeats")
            }
            kinds.append(kind)
        }
        return kinds
    }

    func sdkName() throws -> String {
        let name = try string("sdk")
        let scalars = Array(name.unicodeScalars)
        let allowed = scalars.allSatisfy { scalar in
            switch scalar {
            case "a"..."z", "A"..."Z", "0"..."9", ".", "_", "/", "-": return true
            default: return false
            }
        }
        guard (1...64).contains(scalars.count), allowed else {
            throw WireError(.invalidMessage, "field 'sdk' is 1 to 64 ASCII letters, digits or . _ / -")
        }
        return name
    }
}

public enum Wire {
    // MARK: Device to receiver

    public static func encode(_ hello: Hello) -> Data {
        JSONWriter.encode(.object([
            ("t", .string("hello")),
            ("versions", .array(hello.versions.map { .integer(Int64($0)) })),
            ("sdk", .string(hello.sdk)),
        ]))
    }

    public static func decodeHello(_ data: Data) throws -> Hello {
        let fields = try parse(data)
        guard try fields.string("t") == "hello" else { throw WireError(.invalidMessage, "the first message must be hello") }
        try fields.only(["t", "versions", "sdk"])
        return Hello(versions: try fields.versions(), sdk: try fields.sdkName())
    }

    public static func encode(_ request: PairRequest) -> Data {
        JSONWriter.encode(.object([
            ("t", .string("pair_request")),
            ("versions", .array(request.versions.map { .integer(Int64($0)) })),
            ("sdk", .string(request.sdk)),
            ("name", .string(request.name)),
            ("kinds", .array(request.kinds.map { .string($0.rawValue) })),
        ]))
    }

    public static func decodePairRequest(_ data: Data) throws -> PairRequest {
        let fields = try parse(data)
        guard try fields.string("t") == "pair_request" else { throw WireError(.invalidMessage, "expected pair_request") }
        try fields.only(["t", "versions", "sdk", "name", "kinds"])
        let name = try fields.string("name")
        guard name.utf8.count <= 256 else { throw WireError(.tooLarge, "a device name is too long") }
        let kinds = try fields.kinds()
        guard !kinds.isEmpty else { throw WireError(.invalidMessage, "field 'kinds' cannot be empty") }
        return PairRequest(versions: try fields.versions(), sdk: try fields.sdkName(), name: name, kinds: kinds)
    }

    public static func encode(_ envelope: RequestEnvelope) -> Data {
        var fields: [(String, JSONWriter.Value)] = []
        func put(type: String) {
            fields.append(("t", .string(type)))
            fields.append(("seq", .integer(Int64(envelope.seq))))
        }
        func put(coordinate: Coordinate) {
            fields.append(("lat", .number(coordinate.latitude)))
            fields.append(("lon", .number(coordinate.longitude)))
        }
        switch envelope.request {
        case .ping:
            put(type: "ping")
        case .putPin(let id, let at, let title):
            put(type: "put")
            fields.append(("kind", .string("pin")))
            fields.append(("id", .string(id.rawValue)))
            put(coordinate: at)
            if let title { fields.append(("title", .string(title))) }
        case .putMarker(let id, let at, let title, let color):
            put(type: "put")
            fields.append(("kind", .string("marker")))
            fields.append(("id", .string(id.rawValue)))
            put(coordinate: at)
            if let title { fields.append(("title", .string(title))) }
            if let color { fields.append(("color", .string(color.rawValue))) }
        case .putNote(let id, let at, let text):
            put(type: "put")
            fields.append(("kind", .string("note")))
            fields.append(("id", .string(id.rawValue)))
            put(coordinate: at)
            fields.append(("text", .string(text)))
        case .move(let id, let to):
            put(type: "move")
            fields.append(("id", .string(id.rawValue)))
            put(coordinate: to)
        case .delete(let id):
            put(type: "delete")
            fields.append(("id", .string(id.rawValue)))
        case .deleteAll:
            put(type: "delete_all")
        case .photoBegin(let header):
            put(type: "photo_begin")
            fields.append(("id", .string(header.id.rawValue)))
            put(coordinate: header.at)
            if let title = header.title { fields.append(("title", .string(title))) }
            fields.append(("type", .string(header.type.rawValue)))
            fields.append(("size", .integer(Int64(header.size))))
            fields.append(("sha256", .string(header.sha256)))
            fields.append(("chunks", .integer(Int64(header.chunks))))
        case .photoChunk(let id, let index, let data):
            put(type: "photo_chunk")
            fields.append(("id", .string(id.rawValue)))
            fields.append(("index", .integer(Int64(index))))
            fields.append(("data", .string(data.base64EncodedString())))
        case .photoEnd(let id):
            put(type: "photo_end")
            fields.append(("id", .string(id.rawValue)))
        case .trackAppend(let id, let title, let points):
            put(type: "track_append")
            fields.append(("id", .string(id.rawValue)))
            if let title { fields.append(("title", .string(title))) }
            fields.append(("points", .array(points.map(encode))))
        case .trackFinish(let id):
            put(type: "track_finish")
            fields.append(("id", .string(id.rawValue)))
        }
        return JSONWriter.encode(.object(fields))
    }

    private static func encode(_ point: TrackPoint) -> JSONWriter.Value {
        var fields: [(String, JSONWriter.Value)] = [
            ("lat", .number(point.coordinate.latitude)),
            ("lon", .number(point.coordinate.longitude)),
        ]
        if let timestamp = point.timestampMS { fields.append(("ts", .integer(timestamp))) }
        if let altitude = point.altitudeM { fields.append(("alt", .number(altitude))) }
        return .object(fields)
    }

    /// Decodes a request. Throws `WireError` for any message that is not exactly what the specification allows.
    /// A receiver that announced lower limits passes them here, so that it enforces what it announced.
    public static func decodeRequest(_ data: Data, limits: Limits = .v1) throws -> RequestEnvelope {
        let fields = try parse(data)
        let type = try fields.string("t")
        let seq = try fields.seq()
        let request: Request

        switch type {
        case "ping":
            try fields.only(["t", "seq"])
            request = .ping
        case "put":
            let kind = try fields.string("kind")
            switch kind {
            case "pin":
                try fields.only(["t", "seq", "kind", "id", "lat", "lon", "title"])
                request = .putPin(id: try fields.id(), at: try fields.coordinate(), title: try fields.title(limits: limits))
            case "marker":
                try fields.only(["t", "seq", "kind", "id", "lat", "lon", "title", "color"])
                var color: MarkerColor?
                if let text = try fields.optionalString("color") {
                    guard let parsed = MarkerColor(rawValue: text) else { throw WireError(.invalidMessage, "unknown marker color") }
                    color = parsed
                }
                request = .putMarker(id: try fields.id(), at: try fields.coordinate(), title: try fields.title(limits: limits), color: color)
            case "note":
                try fields.only(["t", "seq", "kind", "id", "lat", "lon", "text"])
                let text = try fields.string("text")
                try TextRules.validateNote(text, limits: limits)
                request = .putNote(id: try fields.id(), at: try fields.coordinate(), text: text)
            default:
                throw WireError(.invalidMessage, "field 'kind' must be pin, marker or note")
            }
        case "move":
            try fields.only(["t", "seq", "id", "lat", "lon"])
            request = .move(id: try fields.id(), to: try fields.coordinate())
        case "delete":
            try fields.only(["t", "seq", "id"])
            request = .delete(id: try fields.id())
        case "delete_all":
            try fields.only(["t", "seq"])
            request = .deleteAll
        case "photo_begin":
            try fields.only(["t", "seq", "id", "lat", "lon", "title", "type", "size", "sha256", "chunks"])
            guard let type = PhotoType(rawValue: try fields.string("type")) else {
                throw WireError(.photoInvalid, "a photo is image/jpeg or image/png")
            }
            let size = try fields.integer("size")
            let chunks = try fields.integer("chunks")
            let digest = try fields.string("sha256")
            guard size >= 1, chunks >= 1 else {
                throw WireError(.invalidMessage, "fields 'size' and 'chunks' must be positive")
            }
            guard size <= Int64(limits.photoBytes) else {
                throw WireError(.tooLarge, "a photo is at most \(limits.photoBytes) bytes")
            }
            let chunkSize = Int64(IntegrationV1.maxChunkBytes)
            guard chunks == (size + chunkSize - 1) / chunkSize else {
                throw WireError(.invalidMessage, "field 'chunks' does not match field 'size'")
            }
            guard digest.utf8.count == 64,
                  digest.utf8.allSatisfy({ (0x30...0x39).contains($0) || (0x61...0x66).contains($0) })
            else { throw WireError(.invalidMessage, "field 'sha256' is 64 lowercase hexadecimal digits") }
            request = .photoBegin(PhotoHeader(
                id: try fields.id(),
                at: try fields.coordinate(),
                title: try fields.title(limits: limits),
                type: type,
                size: Int(size),
                sha256: digest,
                chunks: Int(chunks)
            ))
        case "photo_chunk":
            try fields.only(["t", "seq", "id", "index", "data"])
            let index = try fields.integer("index")
            guard (0...65534).contains(index) else { throw WireError(.invalidMessage, "field 'index' is out of range") }
            let text = try fields.string("data")
            guard let data = Data(base64Encoded: text), data.base64EncodedString() == text else {
                throw WireError(.invalidMessage, "field 'data' must be canonical base64")
            }
            guard !data.isEmpty else { throw WireError(.invalidMessage, "a photo chunk cannot be empty") }
            guard data.count <= IntegrationV1.maxChunkBytes else {
                throw WireError(.tooLarge, "a photo chunk is at most \(IntegrationV1.maxChunkBytes) bytes")
            }
            request = .photoChunk(id: try fields.id(), index: Int(index), data: data)
        case "photo_end":
            try fields.only(["t", "seq", "id"])
            request = .photoEnd(id: try fields.id())
        case "track_append":
            try fields.only(["t", "seq", "id", "title", "points"])
            let values = try fields.array("points")
            guard (1...IntegrationV1.maxPointsPerMessage).contains(values.count) else {
                throw WireError(.invalidMessage, "field 'points' has 1 to \(IntegrationV1.maxPointsPerMessage) entries")
            }
            let points = try values.map { value -> TrackPoint in
                let point = try Fields(value)
                try point.only(["lat", "lon", "ts", "alt"])
                let altitude = point.map["alt"] == nil ? nil : try point.number("alt")
                return try TrackPoint(coordinate: try point.coordinate(), timestampMS: try point.optionalInteger("ts"), altitudeM: altitude)
            }
            request = .trackAppend(id: try fields.id(), title: try fields.title(limits: limits), points: points)
        case "track_finish":
            try fields.only(["t", "seq", "id"])
            request = .trackFinish(id: try fields.id())
        default:
            throw WireError(.invalidMessage, "unknown message type")
        }
        return RequestEnvelope(seq: seq, request: request)
    }

    // MARK: Receiver to device

    public static func encode(_ message: ReceiverMessage) -> Data {
        switch message {
        case .welcome(let welcome):
            let limits = welcome.limits
            return JSONWriter.encode(.object([
                ("t", .string("welcome")),
                ("version", .integer(Int64(welcome.version))),
                ("last_seq", .integer(Int64(welcome.lastSeq))),
                ("kinds", .array(welcome.kinds.map { .string($0.rawValue) })),
                ("limits", .object([
                    ("items", .integer(Int64(limits.items))),
                    ("photos", .integer(Int64(limits.photos))),
                    ("photo_bytes", .integer(Int64(limits.photoBytes))),
                    ("note_bytes", .integer(Int64(limits.noteBytes))),
                    ("title_chars", .integer(Int64(limits.titleScalars))),
                    ("track_points", .integer(Int64(limits.trackPoints))),
                    ("open_tracks", .integer(Int64(limits.openTracks))),
                    ("tracks", .integer(Int64(limits.tracks))),
                    ("msgs_per_s", .integer(Int64(limits.messagesPerSecond))),
                    ("msg_burst", .integer(Int64(limits.messageBurst))),
                    ("points_per_s", .integer(Int64(limits.pointsPerSecond))),
                    ("point_burst", .integer(Int64(limits.pointBurst))),
                    ("disk_bytes", .integer(Int64(limits.diskBytes))),
                ])),
            ]))
        case .ack(let seq, let duplicate):
            return JSONWriter.encode(.object([
                ("t", .string("ack")),
                ("seq", .integer(Int64(seq))),
                ("dup", .bool(duplicate)),
            ]))
        case .error(let error):
            var fields: [(String, JSONWriter.Value)] = [("t", .string("error")), ("code", .string(error.code))]
            if let seq = error.seq { fields.append(("seq", .integer(Int64(seq)))) }
            if let lastSeq = error.lastSeq { fields.append(("last_seq", .integer(Int64(lastSeq)))) }
            if let retry = error.retryAfterMS { fields.append(("retry_after_ms", .integer(Int64(retry)))) }
            if let supported = error.supported {
                fields.append(("supported", .array(supported.map { .integer(Int64($0)) })))
            }
            if error.fatal { fields.append(("fatal", .bool(true))) }
            return JSONWriter.encode(.object(fields))
        case .pairStatus(let status):
            switch status {
            case .pending:
                return JSONWriter.encode(.object([("t", .string("pair_status")), ("status", .string("pending"))]))
            case .denied:
                return JSONWriter.encode(.object([("t", .string("pair_status")), ("status", .string("denied"))]))
            case .approved(let kinds, let counter):
                return JSONWriter.encode(.object([
                    ("t", .string("pair_status")),
                    ("status", .string("approved")),
                    ("kinds", .array(kinds.map { .string($0.rawValue) })),
                    ("counter", .integer(Int64(counter))),
                ]))
            }
        }
    }

    public static func decodeReceiverMessage(_ data: Data) throws -> ReceiverMessage {
        let fields = try parse(data)
        switch try fields.string("t") {
        case "welcome":
            try fields.only(["t", "version", "last_seq", "kinds", "limits"])
            let version = try fields.integer("version")
            guard (1...65535).contains(version) else { throw WireError(.invalidMessage, "field 'version' is out of range") }
            guard let limitsValue = fields.map["limits"] else { throw WireError(.invalidMessage, "field 'limits' is missing") }
            return .welcome(Welcome(
                version: Int(version),
                lastSeq: try fields.nonNegative("last_seq"),
                kinds: try fields.kinds(),
                limits: try decodeLimits(limitsValue)
            ))
        case "ack":
            try fields.only(["t", "seq", "dup"])
            return .ack(seq: try fields.seq(), duplicate: try fields.bool("dup"))
        case "error":
            try fields.only(["t", "code", "seq", "last_seq", "retry_after_ms", "supported", "fatal"])
            let code = try fields.string("code")
            let codeIsValid = code.utf8.allSatisfy { (0x61...0x7A).contains($0) || $0 == 0x5F }
            guard (1...64).contains(code.utf8.count), codeIsValid else {
                throw WireError(.invalidMessage, "field 'code' is lowercase letters and underscores")
            }
            let versions = fields.map["supported"] == nil ? nil : try fields.array("supported")
            // Empty is allowed: a receiver may have dropped every version this device could speak.
            guard (versions?.count ?? 0) <= 16 else { throw WireError(.invalidMessage, "field 'supported' has at most 16 entries") }
            let supported = try versions?.map { value -> Int in
                guard case .integer(let number) = value, (1...65535).contains(number) else {
                    throw WireError(.invalidMessage, "a version is an integer within 1...65535")
                }
                return Int(number)
            }
            return .error(ReceiverError(
                code: code,
                seq: fields.map["seq"] == nil ? nil : try fields.seq(),
                lastSeq: fields.map["last_seq"] == nil ? nil : try fields.nonNegative("last_seq"),
                retryAfterMS: fields.map["retry_after_ms"] == nil ? nil : try fields.nonNegative("retry_after_ms"),
                supported: supported,
                fatal: fields.map["fatal"] == nil ? false : try fields.bool("fatal")
            ))
        case "pair_status":
            switch try fields.string("status") {
            case "pending":
                try fields.only(["t", "status"])
                return .pairStatus(.pending)
            case "denied":
                try fields.only(["t", "status"])
                return .pairStatus(.denied)
            case "approved":
                try fields.only(["t", "status", "kinds", "counter"])
                return .pairStatus(.approved(kinds: try fields.kinds(), counter: try fields.nonNegative("counter")))
            default:
                throw WireError(.invalidMessage, "unknown pairing status")
            }
        default:
            throw WireError(.invalidMessage, "unknown message type")
        }
    }

    private static func decodeLimits(_ value: JSONValue) throws -> Limits {
        let fields = try Fields(value)
        try fields.only([
            "items", "photos", "photo_bytes", "note_bytes", "title_chars", "track_points", "open_tracks", "tracks",
            "msgs_per_s", "msg_burst", "points_per_s", "point_burst", "disk_bytes",
        ])
        func positive(_ key: String) throws -> Int {
            let number = try fields.integer(key)
            guard number >= 1, number <= 1 << 40 else { throw WireError(.invalidMessage, "a limit is out of range") }
            return Int(number)
        }
        // A device may batch this many points, so a smaller burst could never be satisfied.
        guard try positive("point_burst") >= IntegrationV1.maxPointsPerMessage else {
            throw WireError(.invalidMessage, "field 'point_burst' is below the maximum batch size")
        }
        return Limits(
            items: try positive("items"),
            photos: try positive("photos"),
            photoBytes: try positive("photo_bytes"),
            noteBytes: try positive("note_bytes"),
            titleScalars: try positive("title_chars"),
            trackPoints: try positive("track_points"),
            openTracks: try positive("open_tracks"),
            tracks: try positive("tracks"),
            messagesPerSecond: try positive("msgs_per_s"),
            messageBurst: try positive("msg_burst"),
            pointsPerSecond: try positive("points_per_s"),
            pointBurst: try positive("point_burst"),
            diskBytes: try positive("disk_bytes")
        )
    }

    private static func parse(_ data: Data) throws -> Fields {
        guard data.count <= IntegrationV1.maxPlaintextLength else { throw WireError(.tooLarge, "a message is at most 65519 bytes") }
        do {
            return try Fields(try StrictJSON.parse(data))
        } catch let error as WireError {
            throw error
        } catch {
            throw WireError(.invalidMessage, "the message is not valid strict JSON")
        }
    }
}
