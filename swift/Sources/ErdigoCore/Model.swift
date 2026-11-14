import CryptoKit
import Foundation
import Security

/// Constants of the integration-v1 protocol. See spec/integration-v1.md.
public enum IntegrationV1 {
    public static let version = 1
    public static let serviceType = "_erdigo-int._tcp"
    public static let maxFrameLength = 65535
    public static let maxPlaintextLength = 65519
    public static let maxHandshakeFrameLength = 1024
    public static let maxChunkBytes = 48_000
    public static let maxPointsPerMessage = 100
    public static let maxSequence: UInt64 = (1 << 53) - 1
    public static let maxDeviceNameScalars = 40
    public static let connectFirstMessageLength = 32 + 48 + 8 + 16
    public static let pairFirstMessageLength = 32 + 16

    public enum Mode: UInt8, Sendable {
        case connect = 0x01
        case pair = 0x02
    }

    /// Five bytes sent in the clear before the handshake. They are also the Noise
    /// prologue, so tampering with them makes the handshake fail.
    public static func preamble(_ mode: Mode) -> Data {
        Data("EIN1".utf8) + Data([mode.rawValue])
    }

    public static func parsePreamble(_ data: Data) -> Mode? {
        guard data.count == 5, Data(data.prefix(4)) == Data("EIN1".utf8) else { return nil }
        return Mode(rawValue: data[data.startIndex + 4])
    }

    /// The receiver's or a device's key fingerprint, as lowercase hexadecimal.
    public static func fingerprint(of publicKey: Data) -> String {
        var input = Data("erdigo-integration-v1/fingerprint".utf8)
        input.append(publicKey)
        return Data(SHA256.hash(data: input)).hexString
    }

    /// First 16 hexadecimal digits in groups of four, for showing to people.
    public static func shortFingerprint(_ fingerprint: String) -> String {
        let head = Array(fingerprint.prefix(16))
        return stride(from: 0, to: head.count, by: 4)
            .map { String(head[$0..<min($0 + 4, head.count)]) }
            .joined(separator: "-")
    }
}

public extension Data {
    var hexString: String { map { String(format: "%02x", $0) }.joined() }

    /// Accepts lowercase or uppercase hexadecimal only.
    init?(hexString text: String) {
        let digits = Array(text.utf8)
        guard digits.count % 2 == 0 else { return nil }
        var output = Data(capacity: digits.count / 2)
        var index = 0
        while index < digits.count {
            guard let high = Data.hexValue(digits[index]), let low = Data.hexValue(digits[index + 1]) else { return nil }
            output.append(high << 4 | low)
            index += 2
        }
        self = output
    }

    private static func hexValue(_ byte: UInt8) -> UInt8? {
        switch byte {
        case UInt8(ascii: "0")...UInt8(ascii: "9"): return byte - UInt8(ascii: "0")
        case UInt8(ascii: "a")...UInt8(ascii: "f"): return byte - UInt8(ascii: "a") + 10
        case UInt8(ascii: "A")...UInt8(ascii: "F"): return byte - UInt8(ascii: "A") + 10
        default: return nil
        }
    }
}

/// A wire-level rejection, carrying the error code the receiver reports.
public struct WireError: Error, Equatable, Sendable {
    public let code: ErrorCode
    public let detail: String

    public init(_ code: ErrorCode, _ detail: String) {
        self.code = code
        self.detail = detail
    }
}

public enum ErrorCode: String, Sendable, CaseIterable {
    case versionUnsupported = "version_unsupported"
    case invalidMessage = "invalid_message"
    case invalidID = "invalid_id"
    case invalidCoordinate = "invalid_coordinate"
    case invalidText = "invalid_text"
    case notPermitted = "not_permitted"
    case notFound = "not_found"
    case kindConflict = "kind_conflict"
    case invalidState = "invalid_state"
    case staleSeq = "stale_seq"
    case seqConflict = "seq_conflict"
    case limitItems = "limit_items"
    case limitPhotos = "limit_photos"
    case limitTracks = "limit_tracks"
    case limitDisk = "limit_disk"
    case tooLarge = "too_large"
    case photoInvalid = "photo_invalid"
    case rateLimited = "rate_limited"
    case tooManyErrors = "too_many_errors"
    case paused = "paused"
    case replaced = "replaced"
    case codeReused = "code_reused"
    case internalError = "internal_error"

    /// Retryable errors do not consume the sequence number.
    public var isRetryable: Bool {
        switch self {
        case .rateLimited, .paused, .internalError: return true
        default: return false
        }
    }
}

/// A validated coordinate: finite and in range.
public struct Coordinate: Equatable, Sendable {
    public let latitude: Double
    public let longitude: Double

    public init(latitude: Double, longitude: Double) throws {
        guard latitude.isFinite, longitude.isFinite, (-90.0...90.0).contains(latitude), (-180.0...180.0).contains(longitude) else {
            throw WireError(.invalidCoordinate, "latitude must be within -90...90 and longitude within -180...180")
        }
        self.latitude = latitude
        self.longitude = longitude
    }
}

/// An item identifier: 1 to 64 ASCII letters, digits, `-` or `_`.
public struct ItemID: Hashable, Sendable, CustomStringConvertible {
    public let rawValue: String

    public init(_ rawValue: String) throws {
        let scalars = Array(rawValue.unicodeScalars)
        guard (1...64).contains(scalars.count), scalars.allSatisfy(ItemID.isAllowed) else {
            throw WireError(.invalidID, "an item id is 1 to 64 ASCII letters, digits, '-' or '_'")
        }
        self.rawValue = rawValue
    }

    private static func isAllowed(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar {
        case "a"..."z", "A"..."Z", "0"..."9", "-", "_": return true
        default: return false
        }
    }

    public var description: String { rawValue }
}

public enum ItemKind: String, CaseIterable, Sendable, Codable, Hashable {
    case pin, marker, note, photo, track

    /// `pin`, `marker` and `note` are carried by `put`. `photo` and `track` have their own messages.
    public var usesPut: Bool { self == .pin || self == .marker || self == .note }

    public static func canonicalList<S: Sequence>(_ kinds: S) -> [ItemKind] where S.Element == ItemKind {
        let set = Set(kinds)
        return ItemKind.allCases.filter(set.contains)
    }
}

public enum MarkerColor: String, CaseIterable, Sendable {
    case red, orange, yellow, green, blue, purple, gray
}

public struct TrackPoint: Equatable, Sendable {
    public let coordinate: Coordinate
    /// Milliseconds since the Unix epoch.
    public let timestampMS: Int64?
    public let altitudeM: Double?

    public init(coordinate: Coordinate, timestampMS: Int64? = nil, altitudeM: Double? = nil) throws {
        if let timestampMS, timestampMS < 0 || timestampMS > (1 << 53) - 1 {
            throw WireError(.invalidMessage, "a track point timestamp is milliseconds since 1970, within 0...2^53-1")
        }
        if let altitudeM, !altitudeM.isFinite || !(-1000.0...100_000.0).contains(altitudeM) {
            throw WireError(.invalidMessage, "a track point altitude is within -1000...100000 metres")
        }
        self.coordinate = coordinate
        self.timestampMS = timestampMS
        self.altitudeM = altitudeM
    }
}

public enum PhotoType: String, Sendable {
    case jpeg = "image/jpeg"
    case png = "image/png"

    /// Detects the format from the first bytes. Anything but JPEG or PNG returns nil.
    public static func detect(_ data: Data) -> PhotoType? {
        let bytes = [UInt8](data.prefix(8))
        if bytes.count >= 3, bytes[0] == 0xFF, bytes[1] == 0xD8, bytes[2] == 0xFF { return .jpeg }
        if bytes == [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A] { return .png }
        return nil
    }
}

/// Limits a receiver enforces per device. The receiver announces them in `welcome`.
public struct Limits: Equatable, Sendable {
    public var items: Int
    public var photos: Int
    public var photoBytes: Int
    public var noteBytes: Int
    public var titleScalars: Int
    public var trackPoints: Int
    public var openTracks: Int
    public var tracks: Int
    public var messagesPerSecond: Int
    public var messageBurst: Int
    public var pointsPerSecond: Int
    public var pointBurst: Int
    public var diskBytes: Int

    public static let v1 = Limits(
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
        diskBytes: 31_457_280
    )

    public init(
        items: Int, photos: Int, photoBytes: Int, noteBytes: Int, titleScalars: Int, trackPoints: Int,
        openTracks: Int, tracks: Int, messagesPerSecond: Int, messageBurst: Int, pointsPerSecond: Int, pointBurst: Int, diskBytes: Int
    ) {
        self.items = items
        self.photos = photos
        self.photoBytes = photoBytes
        self.noteBytes = noteBytes
        self.titleScalars = titleScalars
        self.trackPoints = trackPoints
        self.openTracks = openTracks
        self.tracks = tracks
        self.messagesPerSecond = messagesPerSecond
        self.messageBurst = messageBurst
        self.pointsPerSecond = pointsPerSecond
        self.pointBurst = pointBurst
        self.diskBytes = diskBytes
    }
}

/// Rules for text that a person will eventually read on the receiver's screen.
public enum TextRules {
    /// Control characters and invisible direction overrides, which can make text read differently from what it is.
    public static func isDisallowed(_ scalar: Unicode.Scalar, allowLineBreaks: Bool) -> Bool {
        switch scalar.value {
        case 0x0A, 0x09:
            return !allowLineBreaks
        case 0x00...0x1F, 0x7F...0x9F:
            return true
        case 0x061C, 0x200E, 0x200F, 0x202A...0x202E, 0x2066...0x2069, 0x2028, 0x2029, 0xFEFF:
            return true
        default:
            return false
        }
    }

    public static func validateTitle(_ text: String, limits: Limits = .v1) throws {
        let scalars = text.unicodeScalars
        guard !scalars.isEmpty else { throw WireError(.invalidText, "a title cannot be empty; leave it out instead") }
        guard scalars.count <= limits.titleScalars else {
            throw WireError(.tooLarge, "a title is at most \(limits.titleScalars) characters")
        }
        guard !scalars.contains(where: { isDisallowed($0, allowLineBreaks: false) }) else {
            throw WireError(.invalidText, "a title cannot contain control or text-direction characters")
        }
    }

    public static func validateNote(_ text: String, limits: Limits = .v1) throws {
        guard !text.isEmpty else { throw WireError(.invalidText, "a note cannot be empty") }
        guard text.utf8.count <= limits.noteBytes else {
            throw WireError(.tooLarge, "a note is at most \(limits.noteBytes) bytes of UTF-8")
        }
        guard !text.unicodeScalars.contains(where: { isDisallowed($0, allowLineBreaks: true) }) else {
            throw WireError(.invalidText, "a note cannot contain control or text-direction characters")
        }
    }

    /// Receivers treat a device name as untrusted: unsafe characters are removed and it is cut to 40 characters.
    public static func sanitizeDeviceName(_ text: String) -> String {
        var view = String.UnicodeScalarView()
        for scalar in text.unicodeScalars where !isDisallowed(scalar, allowLineBreaks: false) {
            view.append(scalar)
        }
        let cleaned = String(view).trimmingCharacters(in: .whitespacesAndNewlines)
        var limited = String.UnicodeScalarView()
        limited.append(contentsOf: cleaned.unicodeScalars.prefix(IntegrationV1.maxDeviceNameScalars))
        return String(limited)
    }
}

/// The one-time code a person reads from the receiver: 100 random bits as 20 Crockford base32 characters.
public struct PairingCode: Equatable, Sendable, CustomStringConvertible {
    static let alphabet = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ".utf8)

    /// The 20 characters without separators.
    public let canonical: String

    public init(parsing text: String) throws {
        var output: [UInt8] = []
        for scalar in text.unicodeScalars {
            if scalar == "-" || scalar.properties.isWhitespace { continue }
            guard scalar.isASCII else { throw WireError(.invalidMessage, "the pairing code has invalid characters") }
            var byte = UInt8(scalar.value)
            if (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(byte) { byte -= 0x20 }
            switch byte {
            case UInt8(ascii: "O"): byte = UInt8(ascii: "0")
            case UInt8(ascii: "I"), UInt8(ascii: "L"): byte = UInt8(ascii: "1")
            default: break
            }
            guard PairingCode.alphabet.contains(byte) else {
                throw WireError(.invalidMessage, "the pairing code has invalid characters")
            }
            output.append(byte)
        }
        guard output.count == 20 else { throw WireError(.invalidMessage, "the pairing code has 20 characters") }
        canonical = String(decoding: output, as: UTF8.self)
    }

    /// Creates a new code from the system's secure random source.
    public static func generate() throws -> PairingCode {
        var random = [UInt8](repeating: 0, count: 20)
        guard SecRandomCopyBytes(kSecRandomDefault, random.count, &random) == errSecSuccess else {
            throw WireError(.internalError, "the system random source failed")
        }
        // Each of the 20 bytes contributes exactly 5 unbiased bits.
        let text = random.map { alphabet[Int($0 & 0x1F)] }
        return try PairingCode(parsing: String(decoding: text, as: UTF8.self))
    }

    /// `XXXXX-XXXXX-XXXXX-XXXXX`, for showing on the receiver's screen.
    public var display: String {
        let characters = Array(canonical)
        return stride(from: 0, to: characters.count, by: 5)
            .map { String(characters[$0..<($0 + 5)]) }
            .joined(separator: "-")
    }

    /// The Noise pre-shared key derived from the code.
    public func presharedKey() -> Data {
        let key = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: Data(canonical.utf8)),
            salt: Data("erdigo-integration-v1/pairing-psk".utf8),
            info: Data(),
            outputByteCount: 32
        )
        return key.withUnsafeBytes { Data($0) }
    }

    /// Avoids writing the code to logs by accident.
    public var description: String { "<pairing code>" }
}

/// What a person gives the SDK to pair: the code, and the receiver's key fingerprint when it comes
/// from the QR code. A typed code has no fingerprint, and the person then has to compare the key.
public struct PairingTicket: Equatable, Sendable {
    public let code: PairingCode
    public let fingerprint: String?

    public init(code: PairingCode, fingerprint: String? = nil) {
        self.code = code
        self.fingerprint = fingerprint
    }

    /// Accepts either a bare code or the QR form `erdigo-pair:1?code=...&fp=...`.
    ///
    /// The QR form needs exactly one code and exactly one valid fingerprint, because the key
    /// comparison is what makes it stronger than a typed code. Nothing else may appear in it.
    /// Text over 1024 bytes is refused before it is looked at, because it may come from anywhere.
    public init(parsing text: String) throws {
        guard text.utf8.count <= Self.maxTextLength else { throw WireError(.invalidMessage, "the pairing text is too long") }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let prefix = "erdigo-pair:1?"
        guard trimmed.hasPrefix(prefix) else {
            self.init(code: try PairingCode(parsing: trimmed))
            return
        }
        var code: PairingCode?
        var fingerprint: String?
        // Empty pieces are kept, so that `&&`, a trailing `&` and a field starting with `=` are refused below.
        for part in trimmed.dropFirst(prefix.count).split(separator: "&", omittingEmptySubsequences: false) {
            let pieces = part.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
            guard pieces.count == 2 else { throw WireError(.invalidMessage, "the pairing text is malformed") }
            switch pieces[0] {
            case "code" where code == nil:
                code = try PairingCode(parsing: pieces[1])
            case "fp" where fingerprint == nil:
                let lowered = pieces[1].lowercased()
                guard Self.isValidFingerprint(lowered) else {
                    throw WireError(.invalidMessage, "the fingerprint in the pairing text is malformed")
                }
                fingerprint = lowered
            default:
                throw WireError(.invalidMessage, "the pairing text is malformed")
            }
        }
        guard let code else { throw WireError(.invalidMessage, "the pairing text has no code") }
        guard let fingerprint else { throw WireError(.invalidMessage, "the pairing text has no fingerprint") }
        self.init(code: code, fingerprint: fingerprint)
    }

    /// The text of the QR code, or nil without a valid fingerprint, because QR text without one
    /// would look stronger than it is.
    public var uri: String? {
        guard let fingerprint, Self.isValidFingerprint(fingerprint) else { return nil }
        return "erdigo-pair:1?code=\(code.display)&fp=\(fingerprint)"
    }

    /// Much longer than any valid text, which is 110 bytes without white space around it.
    private static let maxTextLength = 1024

    /// 64 lowercase hexadecimal digits, the one spelling a fingerprint has (spec section 3).
    private static func isValidFingerprint(_ text: String) -> Bool {
        let digits = Array(text.utf8)
        return digits.count == 2 * Noise.hashLength && digits.allSatisfy { (0x30...0x39).contains($0) || (0x61...0x66).contains($0) }
    }
}

/// A token bucket, used by receivers to enforce rates and by the SDK to stay below them.
public struct TokenBucket: Sendable {
    public let capacity: Double
    public let refillPerSecond: Double
    private var tokens: Double
    private var updatedAt: TimeInterval

    public init(capacity: Int, refillPerSecond: Int, now: TimeInterval) {
        self.capacity = Double(capacity)
        self.refillPerSecond = Double(refillPerSecond)
        tokens = Double(capacity)
        updatedAt = now
    }

    private mutating func refill(now: TimeInterval) {
        let elapsed = max(0, now - updatedAt)
        tokens = min(capacity, tokens + elapsed * refillPerSecond)
        updatedAt = now
    }

    /// Takes `count` tokens if possible. Otherwise takes nothing and returns how long to wait, in seconds.
    public mutating func take(_ count: Int = 1, now: TimeInterval) -> TimeInterval? {
        refill(now: now)
        let wanted = Double(count)
        if tokens >= wanted {
            tokens -= wanted
            return nil
        }
        guard refillPerSecond > 0 else { return .infinity }
        return (min(wanted, capacity) - tokens) / refillPerSecond
    }
}
