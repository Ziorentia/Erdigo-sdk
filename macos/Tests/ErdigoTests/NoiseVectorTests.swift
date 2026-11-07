import CryptoKit
@_spi(TestVectors) @_spi(ErdigoInternal) import ErdigoCore
import Foundation
import XCTest

/// Loads vector files from the shared spec folder.
enum VectorFiles {
    static var repositoryRoot: URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    static func load(_ relativePath: String) throws -> [String: Any] {
        let data = try Data(contentsOf: repositoryRoot.appendingPathComponent(relativePath))
        return try XCTUnwrap(try JSONSerialization.jsonObject(with: data) as? [String: Any])
    }
}

private func bytes(_ text: String?) throws -> Data {
    try XCTUnwrap(Data(hexString: try XCTUnwrap(text)))
}

private func key(_ text: String?) throws -> Curve25519.KeyAgreement.PrivateKey? {
    guard let text else { return nil }
    return try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: try bytes(text))
}

final class NoiseVectorTests: XCTestCase {
    private let patterns: [String: NoisePattern] = [
        "Noise_IK_25519_ChaChaPoly_SHA256": .ik,
        "Noise_XXpsk3_25519_ChaChaPoly_SHA256": NoisePattern(
            name: "XXpsk3",
            responderStaticIsPreMessage: false,
            messages: [[.e], [.e, .ee, .s, .es], [.s, .se, .psk]]
        ),
        "Noise_NNpsk0_25519_ChaChaPoly_SHA256": NoisePattern(
            name: "NNpsk0",
            responderStaticIsPreMessage: false,
            messages: [[.psk, .e], [.e, .ee]]
        ),
    ]

    /// Runs one official vector through both roles and compares every byte.
    private func run(_ vector: [String: Any]) throws {
        let name = try XCTUnwrap(vector["protocol_name"] as? String)
        let pattern = try XCTUnwrap(patterns[name], name)
        let initiatorPSKs = try (vector["init_psks"] as? [String] ?? []).map { try bytes($0) }
        let responderPSKs = try (vector["resp_psks"] as? [String] ?? []).map { try bytes($0) }

        let initiator = try NoiseHandshake(
            pattern: pattern,
            isInitiator: true,
            prologue: try bytes(vector["init_prologue"] as? String),
            staticKey: try key(vector["init_static"] as? String),
            remoteStaticKey: try (vector["init_remote_static"] as? String).map { try bytes($0) },
            psks: initiatorPSKs,
            fixedEphemeral: try key(vector["init_ephemeral"] as? String)
        )
        let responder = try NoiseHandshake(
            pattern: pattern,
            isInitiator: false,
            prologue: try bytes(vector["resp_prologue"] as? String),
            staticKey: try key(vector["resp_static"] as? String),
            remoteStaticKey: try (vector["resp_remote_static"] as? String).map { try bytes($0) },
            psks: responderPSKs,
            fixedEphemeral: try key(vector["resp_ephemeral"] as? String)
        )

        let messages = try XCTUnwrap(vector["messages"] as? [[String: String]])
        var index = 0
        while !initiator.isComplete {
            let payload = try bytes(messages[index]["payload"])
            let expected = try bytes(messages[index]["ciphertext"])
            let (writer, reader) = index % 2 == 0 ? (initiator, responder) : (responder, initiator)
            XCTAssertEqual(try writer.writeMessage(payload: payload).hexString, expected.hexString, "\(name) message \(index)")
            XCTAssertEqual(try reader.readMessage(expected).hexString, payload.hexString, "\(name) message \(index)")
            index += 1
        }
        XCTAssertTrue(responder.isComplete)

        let initiatorTransport = try initiator.split()
        let responderTransport = try responder.split()
        if let hash = vector["handshake_hash"] as? String {
            XCTAssertEqual(initiatorTransport.handshakeHash.hexString, hash, name)
            XCTAssertEqual(responderTransport.handshakeHash.hexString, hash, name)
        }

        // Continue alternating, as the official vectors do.
        while index < messages.count {
            let payload = try bytes(messages[index]["payload"])
            let expected = try bytes(messages[index]["ciphertext"])
            if index % 2 == 0 {
                XCTAssertEqual(try initiatorTransport.seal(payload).hexString, expected.hexString, "\(name) message \(index)")
                XCTAssertEqual(try responderTransport.open(expected).hexString, payload.hexString, "\(name) message \(index)")
            } else {
                XCTAssertEqual(try responderTransport.seal(payload).hexString, expected.hexString, "\(name) message \(index)")
                XCTAssertEqual(try initiatorTransport.open(expected).hexString, payload.hexString, "\(name) message \(index)")
            }
            index += 1
        }
    }

    func testOfficialVectors() throws {
        let file = try VectorFiles.load("spec/vectors/noise/cacophony-subset.json")
        let vectors = try XCTUnwrap(file["vectors"] as? [[String: Any]])
        XCTAssertEqual(vectors.count, 3)
        for vector in vectors {
            try run(vector)
        }
    }
}

final class NoiseBehaviourTests: XCTestCase {
    private func establishedPair() throws -> (NoiseTransport, NoiseTransport) {
        let deviceKey = Curve25519.KeyAgreement.PrivateKey()
        let receiverKey = Curve25519.KeyAgreement.PrivateKey()
        let prologue = Data("test".utf8)
        let device = try NoiseHandshake(
            pattern: .ik, isInitiator: true, prologue: prologue, staticKey: deviceKey,
            remoteStaticKey: receiverKey.publicKey.rawRepresentation
        )
        let receiver = try NoiseHandshake(pattern: .ik, isInitiator: false, prologue: prologue, staticKey: receiverKey)
        _ = try receiver.readMessage(try device.writeMessage())
        _ = try device.readMessage(try receiver.writeMessage())
        return (try device.split(), try receiver.split())
    }

    func testTamperedTransportMessageIsRejected() throws {
        let (device, receiver) = try establishedPair()
        var sealed = try device.seal(Data("hello".utf8))
        sealed[sealed.startIndex] ^= 0x01
        XCTAssertThrowsError(try receiver.open(sealed)) { XCTAssertEqual($0 as? NoiseError, .decryptionFailed) }
    }

    func testReplayedTransportMessageIsRejected() throws {
        let (device, receiver) = try establishedPair()
        let sealed = try device.seal(Data("hello".utf8))
        XCTAssertEqual(try receiver.open(sealed), Data("hello".utf8))
        XCTAssertThrowsError(try receiver.open(sealed))
    }

    func testOutOfOrderUseIsRejected() throws {
        let key = Curve25519.KeyAgreement.PrivateKey()
        let handshake = try NoiseHandshake(pattern: .ik, isInitiator: false, prologue: Data(), staticKey: key)
        XCTAssertThrowsError(try handshake.writeMessage()) { XCTAssertEqual($0 as? NoiseError, .wrongHandshakeState) }
        XCTAssertThrowsError(try handshake.split()) { XCTAssertEqual($0 as? NoiseError, .wrongHandshakeState) }
    }

    func testPrologueMismatchFailsTheHandshake() throws {
        let receiverKey = Curve25519.KeyAgreement.PrivateKey()
        let device = try NoiseHandshake(
            pattern: .ik, isInitiator: true, prologue: IntegrationV1.preamble(.connect),
            staticKey: Curve25519.KeyAgreement.PrivateKey(), remoteStaticKey: receiverKey.publicKey.rawRepresentation
        )
        let receiver = try NoiseHandshake(pattern: .ik, isInitiator: false, prologue: IntegrationV1.preamble(.pair), staticKey: receiverKey)
        XCTAssertThrowsError(try receiver.readMessage(try device.writeMessage()))
    }

    func testPairingFirstMessageFailsWithoutTheCode() throws {
        let prologue = IntegrationV1.preamble(.pair)
        let right = try PairingCode.generate()
        let wrong = try PairingCode.generate()
        let device = try NoiseHandshake(
            pattern: .xxPSK0, isInitiator: true, prologue: prologue,
            staticKey: Curve25519.KeyAgreement.PrivateKey(), psk: wrong.presharedKey()
        )
        let receiver = try NoiseHandshake(
            pattern: .xxPSK0, isInitiator: false, prologue: prologue,
            staticKey: Curve25519.KeyAgreement.PrivateKey(), psk: right.presharedKey()
        )
        XCTAssertThrowsError(try receiver.readMessage(try device.writeMessage())) {
            XCTAssertEqual($0 as? NoiseError, .decryptionFailed)
        }
    }

    /// Public keys of small order produce an all-zero secret. Without this check an
    /// attacker could force a key that everyone knows.
    func testLowOrderPublicKeysAreRejected() throws {
        let lowOrderPoints = [
            "0000000000000000000000000000000000000000000000000000000000000000",
            "0100000000000000000000000000000000000000000000000000000000000000",
            "e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800",
            "5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157",
            "ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
            "edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
            "eeffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f",
        ]
        let key = Curve25519.KeyAgreement.PrivateKey()
        for point in lowOrderPoints {
            let raw = try XCTUnwrap(Data(hexString: point))
            XCTAssertThrowsError(try Noise.dh(key, raw), point) {
                XCTAssertEqual($0 as? NoiseError, .weakSharedSecret, point)
            }
        }
    }
}

final class IntegrationVectorTests: XCTestCase {
    private func replay(_ name: String, pattern: NoisePattern, psk: Bool) throws {
        let vector = try VectorFiles.load("spec/vectors/integration-v1/\(name)")
        let prologue = try bytes(vector["preamble_and_prologue"] as? String)
        let frames = try XCTUnwrap(vector["frames"] as? [[String: Any]])
        let psks: [Data] = psk ? [try bytes(vector["psk"] as? String)] : []

        if psk {
            let typed = try XCTUnwrap(vector["pairing_code_as_typed"] as? String)
            let code = try PairingCode(parsing: typed)
            XCTAssertEqual(code.canonical, vector["pairing_code_canonical"] as? String)
            XCTAssertEqual(code.presharedKey(), psks[0])
            let ticket = try PairingTicket(parsing: try XCTUnwrap(vector["qr_text"] as? String))
            XCTAssertEqual(ticket.code, code)
        }

        let device = try NoiseHandshake(
            pattern: pattern,
            isInitiator: true,
            prologue: prologue,
            staticKey: try key(vector["device_static_private"] as? String),
            remoteStaticKey: psk ? nil : try bytes(vector["receiver_static_public"] as? String),
            psks: psks,
            fixedEphemeral: try key(vector["device_ephemeral_private"] as? String)
        )
        let receiver = try NoiseHandshake(
            pattern: pattern,
            isInitiator: false,
            prologue: prologue,
            staticKey: try key(vector["receiver_static_private"] as? String),
            remoteStaticKey: nil,
            psks: psks,
            fixedEphemeral: try key(vector["receiver_ephemeral_private"] as? String)
        )

        var deviceTransport: NoiseTransport?
        var receiverTransport: NoiseTransport?

        for frame in frames {
            let stage = try XCTUnwrap(frame["stage"] as? String)
            let wire = try bytes(frame["wire"] as? String)
            if stage == "preamble" {
                XCTAssertEqual(wire, prologue)
                continue
            }
            let payload = try bytes(frame["payload"] as? String)
            let noise = try bytes(frame["noise_message"] as? String)
            XCTAssertEqual(try ByteStream.frame(noise), wire, stage)
            let fromDevice = frame["direction"] as? String == "device_to_receiver"

            if !device.isComplete || !receiver.isComplete {
                let (writer, reader) = fromDevice ? (device, receiver) : (receiver, device)
                XCTAssertEqual(try writer.writeMessage(payload: payload), noise, stage)
                XCTAssertEqual(try reader.readMessage(noise), payload, stage)
                if device.isComplete, receiver.isComplete {
                    deviceTransport = try device.split()
                    receiverTransport = try receiver.split()
                    XCTAssertEqual(deviceTransport?.handshakeHash.hexString, vector["handshake_hash"] as? String)
                }
            } else if fromDevice {
                XCTAssertEqual(try deviceTransport?.seal(payload), noise, stage)
                XCTAssertEqual(try receiverTransport?.open(noise), payload, stage)
                if stage == "hello" { _ = try Wire.decodeHello(payload) } else { _ = try Wire.decodeRequest(payload) }
            } else {
                XCTAssertEqual(try receiverTransport?.seal(payload), noise, stage)
                XCTAssertEqual(try deviceTransport?.open(noise), payload, stage)
                _ = try Wire.decodeReceiverMessage(payload)
            }
        }
        XCTAssertTrue(device.isComplete)
    }

    func testConnectVector() throws {
        try replay("handshake-connect.json", pattern: .ik, psk: false)
    }

    func testPairVector() throws {
        try replay("handshake-pair.json", pattern: .xxPSK0, psk: true)
    }

    func testVectorKeysAreDerivedFromPublicLabels() throws {
        let vector = try VectorFiles.load("spec/vectors/integration-v1/handshake-connect.json")
        let label = "erdigo-integration-v1 test-only key: device static"
        let expected = Data(SHA256.hash(data: Data(label.utf8))).hexString
        XCTAssertEqual(vector["device_static_private"] as? String, expected)
    }

    func testMessageVectors() throws {
        let file = try VectorFiles.load("spec/vectors/integration-v1/messages.json")
        for entry in try XCTUnwrap(file["valid"] as? [[String: String]]) {
            XCTAssertNoThrow(try Wire.decodeRequest(Data(try XCTUnwrap(entry["json"]).utf8)), entry["name"] ?? "")
        }
        let invalid = try XCTUnwrap(file["invalid"] as? [[String: String]])
        XCTAssertFalse(invalid.isEmpty)
        for entry in invalid {
            XCTAssertThrowsError(try Wire.decodeRequest(Data(try XCTUnwrap(entry["json"]).utf8)), entry["name"] ?? "") {
                XCTAssertEqual(($0 as? WireError)?.code.rawValue, entry["code"], entry["name"] ?? "")
            }
        }
        for entry in try XCTUnwrap(file["invalid_bytes"] as? [[String: String]]) {
            XCTAssertThrowsError(try Wire.decodeRequest(try bytes(entry["hex"])), entry["name"] ?? "") {
                XCTAssertEqual(($0 as? WireError)?.code.rawValue, entry["code"], entry["name"] ?? "")
            }
        }
    }
}
