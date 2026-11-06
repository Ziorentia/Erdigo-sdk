import CryptoKit
import Foundation

/// Failures raised by the Noise layer. They deliberately carry no key material.
public enum NoiseError: Error, Equatable, Sendable {
    case invalidKey
    case weakSharedSecret
    case truncatedMessage
    case decryptionFailed
    case nonceExhausted
    case messageTooLarge
    case wrongHandshakeState
    case missingKey
}

/// A Noise handshake pattern, limited to the DH, cipher and hash functions used by
/// integration-v1: `25519`, `ChaChaPoly` and `SHA256`.
public struct NoisePattern: Sendable {
    @_spi(TestVectors) public enum Token: Sendable { case e, s, ee, es, se, ss, psk }

    let name: String
    let responderStaticIsPreMessage: Bool
    let messages: [[Token]]

    /// Lets the test suite run official vectors for patterns this protocol does not use.
    @_spi(TestVectors) public init(name: String, responderStaticIsPreMessage: Bool, messages: [[Token]]) {
        self.name = name
        self.responderStaticIsPreMessage = responderStaticIsPreMessage
        self.messages = messages
    }

    var usesPSK: Bool { messages.contains { $0.contains(.psk) } }
    var protocolName: String { "Noise_\(name)_25519_ChaChaPoly_SHA256" }

    /// Used for ordinary connections: only a device that already knows the receiver key can start one.
    public static let ik = NoisePattern(
        name: "IK",
        responderStaticIsPreMessage: true,
        messages: [[.e, .es, .s, .ss], [.e, .ee, .se]]
    )

    /// Used for pairing: the pairing code is mixed in before anything else, so a party
    /// without the code cannot produce a first message the receiver will answer.
    public static let xxPSK0 = NoisePattern(
        name: "XXpsk0",
        responderStaticIsPreMessage: false,
        messages: [[.psk, .e], [.e, .ee, .s, .es], [.s, .se]]
    )
}

/// One direction of an established Noise session.
struct NoiseCipherState {
    private var key: SymmetricKey?
    private var counter: UInt64 = 0

    var hasKey: Bool { key != nil }

    mutating func initialize(key raw: Data) {
        key = SymmetricKey(data: raw)
        counter = 0
    }

    private func nonce() throws -> ChaChaPoly.Nonce {
        // Noise: 32 bits of zeros followed by the little-endian 64-bit counter.
        var bytes = Data(repeating: 0, count: 4)
        withUnsafeBytes(of: counter.littleEndian) { bytes.append(contentsOf: $0) }
        return try ChaChaPoly.Nonce(data: bytes)
    }

    mutating func encrypt(associatedData: Data, plaintext: Data) throws -> Data {
        guard let key else { return plaintext }
        // 2^64-1 is reserved by the Noise specification.
        guard counter < UInt64.max else { throw NoiseError.nonceExhausted }
        let box = try ChaChaPoly.seal(plaintext, using: key, nonce: nonce(), authenticating: associatedData)
        counter += 1
        return box.ciphertext + box.tag
    }

    mutating func decrypt(associatedData: Data, ciphertext: Data) throws -> Data {
        guard let key else { return ciphertext }
        guard counter < UInt64.max else { throw NoiseError.nonceExhausted }
        guard ciphertext.count >= Noise.tagLength else { throw NoiseError.truncatedMessage }
        let rebased = Data(ciphertext)
        do {
            let box = try ChaChaPoly.SealedBox(
                nonce: nonce(),
                ciphertext: rebased.prefix(rebased.count - Noise.tagLength),
                tag: rebased.suffix(Noise.tagLength)
            )
            let plaintext = try ChaChaPoly.open(box, using: key, authenticating: associatedData)
            counter += 1
            return plaintext
        } catch {
            throw NoiseError.decryptionFailed
        }
    }
}

public enum Noise {
    public static let dhLength = 32
    public static let hashLength = 32
    public static let tagLength = 16
    public static let maxMessageLength = 65535

    /// X25519 ignores the top bit of a public key, so one private key has more than one spelling.
    /// A receiver identifies devices by these bytes, so it accepts only the canonical spelling.
    public static func isCanonicalPublicKey(_ key: Data) -> Bool {
        let bytes = [UInt8](key)
        guard bytes.count == dhLength, bytes[31] & 0x80 == 0 else { return false }
        // Reject values from 2^255-19 up to 2^255-1, which are also non-canonical.
        let isAtLeastPrime = bytes[31] == 0x7F && bytes[0] >= 0xED && bytes[1...30].allSatisfy { $0 == 0xFF }
        return !isAtLeastPrime
    }

    static func hmac(_ key: Data, _ data: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: data, using: SymmetricKey(data: key)))
    }

    /// The Noise HKDF. Note that the input keying material is the HMAC *message*, keyed by the chaining key.
    static func hkdf(chainingKey: Data, inputKeyMaterial: Data, outputs: Int) -> [Data] {
        let temporary = hmac(chainingKey, inputKeyMaterial)
        var results: [Data] = []
        var previous = Data()
        for index in 1...outputs {
            var message = previous
            message.append(UInt8(index))
            previous = hmac(temporary, message)
            results.append(previous)
        }
        return results
    }

    /// X25519 with an explicit rejection of low-order public keys, which produce an all-zero secret.
    @_spi(TestVectors) public static func dh(_ privateKey: Curve25519.KeyAgreement.PrivateKey, _ publicKey: Data) throws -> Data {
        guard publicKey.count == dhLength else { throw NoiseError.invalidKey }
        let secret: Data
        do {
            let peer = try Curve25519.KeyAgreement.PublicKey(rawRepresentation: publicKey)
            secret = try privateKey.sharedSecretFromKeyAgreement(with: peer).withUnsafeBytes { Data($0) }
        } catch {
            throw NoiseError.weakSharedSecret
        }
        // Constant-time OR across every byte.
        guard secret.count == dhLength, secret.reduce(UInt8(0), { $0 | $1 }) != 0 else {
            throw NoiseError.weakSharedSecret
        }
        return secret
    }
}

struct NoiseSymmetricState {
    private(set) var chainingKey: Data
    private(set) var hash: Data
    private var cipher = NoiseCipherState()

    var hasKey: Bool { cipher.hasKey }

    init(protocolName: String) {
        let name = Data(protocolName.utf8)
        if name.count <= Noise.hashLength {
            hash = name + Data(repeating: 0, count: Noise.hashLength - name.count)
        } else {
            hash = Data(SHA256.hash(data: name))
        }
        chainingKey = hash
    }

    mutating func mixHash(_ data: Data) {
        hash = Data(SHA256.hash(data: hash + data))
    }

    mutating func mixKey(_ inputKeyMaterial: Data) {
        let output = Noise.hkdf(chainingKey: chainingKey, inputKeyMaterial: inputKeyMaterial, outputs: 2)
        chainingKey = output[0]
        cipher.initialize(key: output[1])
    }

    mutating func mixKeyAndHash(_ inputKeyMaterial: Data) {
        let output = Noise.hkdf(chainingKey: chainingKey, inputKeyMaterial: inputKeyMaterial, outputs: 3)
        chainingKey = output[0]
        mixHash(output[1])
        cipher.initialize(key: output[2])
    }

    mutating func encryptAndHash(_ plaintext: Data) throws -> Data {
        let ciphertext = try cipher.encrypt(associatedData: hash, plaintext: plaintext)
        mixHash(ciphertext)
        return ciphertext
    }

    mutating func decryptAndHash(_ ciphertext: Data) throws -> Data {
        let plaintext = try cipher.decrypt(associatedData: hash, ciphertext: ciphertext)
        mixHash(ciphertext)
        return plaintext
    }

    func split() -> (NoiseCipherState, NoiseCipherState) {
        let output = Noise.hkdf(chainingKey: chainingKey, inputKeyMaterial: Data(), outputs: 2)
        var first = NoiseCipherState()
        var second = NoiseCipherState()
        first.initialize(key: output[0])
        second.initialize(key: output[1])
        return (first, second)
    }
}

/// Encrypts and decrypts messages after a handshake has finished.
public struct NoiseTransport: Sendable {
    private var sendCipher: NoiseCipherState
    private var receiveCipher: NoiseCipherState
    public let handshakeHash: Data

    init(sendCipher: NoiseCipherState, receiveCipher: NoiseCipherState, handshakeHash: Data) {
        self.sendCipher = sendCipher
        self.receiveCipher = receiveCipher
        self.handshakeHash = handshakeHash
    }

    public mutating func seal(_ plaintext: Data) throws -> Data {
        guard plaintext.count + Noise.tagLength <= Noise.maxMessageLength else { throw NoiseError.messageTooLarge }
        return try sendCipher.encrypt(associatedData: Data(), plaintext: plaintext)
    }

    public mutating func open(_ ciphertext: Data) throws -> Data {
        guard ciphertext.count <= Noise.maxMessageLength else { throw NoiseError.messageTooLarge }
        return try receiveCipher.decrypt(associatedData: Data(), ciphertext: ciphertext)
    }
}

extension NoiseCipherState: @unchecked Sendable {}

/// The Noise handshake state machine.
public final class NoiseHandshake {
    private let pattern: NoisePattern
    private let isInitiator: Bool
    private var symmetric: NoiseSymmetricState
    private let staticKey: Curve25519.KeyAgreement.PrivateKey?
    private var ephemeralKey: Curve25519.KeyAgreement.PrivateKey?
    private let fixedEphemeral: Curve25519.KeyAgreement.PrivateKey?
    public private(set) var remoteStaticKey: Data?
    private var remoteEphemeralKey: Data?
    private var psks: [Data]
    private var messageIndex = 0

    public var isComplete: Bool { messageIndex == pattern.messages.count }
    public var handshakeHash: Data { symmetric.hash }

    public convenience init(
        pattern: NoisePattern,
        isInitiator: Bool,
        prologue: Data,
        staticKey: Curve25519.KeyAgreement.PrivateKey?,
        remoteStaticKey: Data? = nil,
        psk: Data? = nil
    ) throws {
        try self.init(
            pattern: pattern,
            isInitiator: isInitiator,
            prologue: prologue,
            staticKey: staticKey,
            remoteStaticKey: remoteStaticKey,
            psks: psk.map { [$0] } ?? [],
            fixedEphemeral: nil
        )
    }

    /// `fixedEphemeral` exists only so that published test vectors can be reproduced.
    /// Reusing an ephemeral key in real traffic breaks the protocol.
    @_spi(TestVectors) public init(
        pattern: NoisePattern,
        isInitiator: Bool,
        prologue: Data,
        staticKey: Curve25519.KeyAgreement.PrivateKey?,
        remoteStaticKey: Data?,
        psks: [Data],
        fixedEphemeral: Curve25519.KeyAgreement.PrivateKey?
    ) throws {
        guard psks.allSatisfy({ $0.count == 32 }) else { throw NoiseError.invalidKey }
        if pattern.usesPSK && psks.isEmpty { throw NoiseError.missingKey }
        if let remoteStaticKey, remoteStaticKey.count != Noise.dhLength { throw NoiseError.invalidKey }

        self.pattern = pattern
        self.isInitiator = isInitiator
        self.staticKey = staticKey
        self.remoteStaticKey = remoteStaticKey
        self.psks = psks
        self.fixedEphemeral = fixedEphemeral
        symmetric = NoiseSymmetricState(protocolName: pattern.protocolName)
        symmetric.mixHash(prologue)

        if pattern.responderStaticIsPreMessage {
            if isInitiator {
                guard let remoteStaticKey else { throw NoiseError.missingKey }
                symmetric.mixHash(remoteStaticKey)
            } else {
                guard let staticKey else { throw NoiseError.missingKey }
                symmetric.mixHash(staticKey.publicKey.rawRepresentation)
            }
        }
    }

    private var isMyTurn: Bool { (messageIndex % 2 == 0) == isInitiator }

    private func nextPSK() throws -> Data {
        guard !psks.isEmpty else { throw NoiseError.missingKey }
        return psks.removeFirst()
    }

    private func requireStatic() throws -> Curve25519.KeyAgreement.PrivateKey {
        guard let staticKey else { throw NoiseError.missingKey }
        return staticKey
    }

    private func requireEphemeral() throws -> Curve25519.KeyAgreement.PrivateKey {
        guard let ephemeralKey else { throw NoiseError.missingKey }
        return ephemeralKey
    }

    private func requireRemote(_ key: Data?) throws -> Data {
        guard let key else { throw NoiseError.missingKey }
        return key
    }

    private func mixDH(_ token: NoisePattern.Token) throws {
        switch token {
        case .ee:
            try symmetric.mixKey(Noise.dh(requireEphemeral(), requireRemote(remoteEphemeralKey)))
        case .es:
            if isInitiator {
                try symmetric.mixKey(Noise.dh(requireEphemeral(), requireRemote(remoteStaticKey)))
            } else {
                try symmetric.mixKey(Noise.dh(requireStatic(), requireRemote(remoteEphemeralKey)))
            }
        case .se:
            if isInitiator {
                try symmetric.mixKey(Noise.dh(requireStatic(), requireRemote(remoteEphemeralKey)))
            } else {
                try symmetric.mixKey(Noise.dh(requireEphemeral(), requireRemote(remoteStaticKey)))
            }
        case .ss:
            try symmetric.mixKey(Noise.dh(requireStatic(), requireRemote(remoteStaticKey)))
        default:
            throw NoiseError.wrongHandshakeState
        }
    }

    public func writeMessage(payload: Data = Data()) throws -> Data {
        guard !isComplete, isMyTurn else { throw NoiseError.wrongHandshakeState }
        var output = Data()
        for token in pattern.messages[messageIndex] {
            switch token {
            case .e:
                let key = fixedEphemeral ?? Curve25519.KeyAgreement.PrivateKey()
                ephemeralKey = key
                let publicKey = key.publicKey.rawRepresentation
                output.append(publicKey)
                symmetric.mixHash(publicKey)
                if pattern.usesPSK { symmetric.mixKey(publicKey) }
            case .s:
                try output.append(symmetric.encryptAndHash(requireStatic().publicKey.rawRepresentation))
            case .psk:
                try symmetric.mixKeyAndHash(nextPSK())
            case .ee, .es, .se, .ss:
                try mixDH(token)
            }
        }
        try output.append(symmetric.encryptAndHash(payload))
        guard output.count <= Noise.maxMessageLength else { throw NoiseError.messageTooLarge }
        messageIndex += 1
        return output
    }

    public func readMessage(_ message: Data) throws -> Data {
        guard !isComplete, !isMyTurn else { throw NoiseError.wrongHandshakeState }
        guard message.count <= Noise.maxMessageLength else { throw NoiseError.messageTooLarge }
        var rest = Data(message)

        func take(_ count: Int) throws -> Data {
            guard rest.count >= count else { throw NoiseError.truncatedMessage }
            let head = Data(rest.prefix(count))
            rest = Data(rest.dropFirst(count))
            return head
        }

        for token in pattern.messages[messageIndex] {
            switch token {
            case .e:
                let publicKey = try take(Noise.dhLength)
                remoteEphemeralKey = publicKey
                symmetric.mixHash(publicKey)
                if pattern.usesPSK { symmetric.mixKey(publicKey) }
            case .s:
                let length = symmetric.hasKey ? Noise.dhLength + Noise.tagLength : Noise.dhLength
                remoteStaticKey = try symmetric.decryptAndHash(take(length))
            case .psk:
                try symmetric.mixKeyAndHash(nextPSK())
            case .ee, .es, .se, .ss:
                try mixDH(token)
            }
        }
        let payload = try symmetric.decryptAndHash(rest)
        messageIndex += 1
        return payload
    }

    /// Call once `isComplete` is true.
    public func split() throws -> NoiseTransport {
        guard isComplete else { throw NoiseError.wrongHandshakeState }
        let (first, second) = symmetric.split()
        return NoiseTransport(
            sendCipher: isInitiator ? first : second,
            receiveCipher: isInitiator ? second : first,
            handshakeHash: symmetric.hash
        )
    }
}
