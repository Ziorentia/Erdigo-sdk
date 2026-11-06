@_exported import ErdigoCore
import CryptoKit
import Foundation

/// The device's one secret: an X25519 key used only for this protocol.
public struct DeviceIdentity: Sendable {
    let privateKey: Curve25519.KeyAgreement.PrivateKey

    public var publicKey: Data { privateKey.publicKey.rawRepresentation }
    public var fingerprint: String { IntegrationV1.fingerprint(of: publicKey) }

    public static func generate() -> DeviceIdentity {
        DeviceIdentity(privateKey: Curve25519.KeyAgreement.PrivateKey())
    }

    init(privateKey: Curve25519.KeyAgreement.PrivateKey) {
        self.privateKey = privateKey
    }

    public init(rawPrivateKey: Data) throws {
        guard rawPrivateKey.count == 32 else { throw ErdigoError.storage("a device key is 32 bytes") }
        do {
            privateKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: rawPrivateKey)
        } catch {
            throw ErdigoError.storage("the device key is not valid")
        }
    }

    /// Only for writing the key to protected storage.
    public func rawPrivateKeyForStorage() -> Data {
        privateKey.rawRepresentation
    }
}

/// What a device remembers about one receiver. An SDK can be paired with several receivers.
public struct ReceiverPairing: Codable, Equatable, Sendable {
    /// A local name for this receiver. It is never sent anywhere.
    public var label: String
    public var receiverKey: Data
    public var kinds: [ItemKind]
    public var lastHost: String?
    public var lastPort: UInt16?
    public var pairedAt: Date
    /// The highest connect counter the receiver had accepted when it approved this pairing.
    /// A later counter must be greater, even if this device has lost its own count.
    public var counterFloor: UInt64

    public var fingerprint: String { IntegrationV1.fingerprint(of: receiverKey) }

    public init(
        label: String,
        receiverKey: Data,
        kinds: [ItemKind],
        lastHost: String? = nil,
        lastPort: UInt16? = nil,
        pairedAt: Date = Date(),
        counterFloor: UInt64 = 0
    ) {
        self.counterFloor = counterFloor
        self.label = label
        self.receiverKey = receiverKey
        self.kinds = kinds
        self.lastHost = lastHost
        self.lastPort = lastPort
        self.pairedAt = pairedAt
    }
}

/// Failures reported by the SDK.
public enum ErdigoError: Error, Equatable, Sendable {
    /// No receiver answered. A receiver stays silent when it is off, asleep, on another network,
    /// when the pairing code is wrong or expired, and when this device is not allowed. These cases
    /// cannot be told apart, by design.
    case noAnswer
    /// The receiver's key does not match the fingerprint supplied for pairing.
    case fingerprintMismatch
    /// The person at the receiver declined the request.
    case pairingDenied
    /// The person at the receiver did not answer in time.
    case pairingTimedOut
    /// The code was used twice. The receiver warned its user and removed the pairing made with it.
    case codeReused
    /// The receiver rejected a request.
    case rejected(ReceiverError)
    /// The item kind is not among those the person allowed for this device.
    case notPermitted(ItemKind)
    /// The receiver does not support any protocol version this SDK speaks.
    case versionUnsupported(supported: [Int])
    /// Another connection using this device key took over.
    case replaced
    /// Another writer using this device key advanced the sequence number.
    case sequenceConflict(lastSeq: UInt64)
    /// The receiver broke the protocol.
    case protocolViolation(String)
    case invalidArgument(String)
    case storage(String)
}

extension ErdigoError: LocalizedError {
    public var errorDescription: String? {
        switch self {
        case .noAnswer:
            return "No receiver answered. It may be off, asleep or on another network, the pairing code may be wrong or expired, or this device may have been removed. The first time, macOS may also be asking for Local Network permission."
        case .fingerprintMismatch:
            return "The receiver's key does not match the expected fingerprint. Pairing was stopped."
        case .pairingDenied:
            return "The request was declined on the receiver."
        case .pairingTimedOut:
            return "Nobody confirmed the request on the receiver in time."
        case .codeReused:
            return "This pairing code was already used. The receiver removed the pairing made with it. Ask for a new code."
        case .rejected(let error):
            return "The receiver rejected the request: \(error.code)."
        case .notPermitted(let kind):
            return "This device is not allowed to send '\(kind.rawValue)' items."
        case .versionUnsupported(let supported):
            return "The receiver supports protocol versions \(supported), which this SDK does not speak. Update the SDK."
        case .replaced:
            return "Another connection using this device key took over."
        case .sequenceConflict:
            return "Another program is writing with this device key at the same time."
        case .protocolViolation(let detail):
            return "The receiver broke the protocol: \(detail)."
        case .invalidArgument(let detail):
            return detail
        case .storage(let detail):
            return detail
        }
    }
}
