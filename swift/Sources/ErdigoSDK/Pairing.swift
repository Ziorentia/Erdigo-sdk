@_spi(ErdigoInternal) import ErdigoCore
import Foundation
import Network

/// Pairs this device with a receiver, using the one-time code a person reads from the receiver's screen.
public enum Pairing {
    public static let sdkName = "erdigo-swift/0.1.0"

    /// - Parameters:
    ///   - target: where to find the receiver.
    ///   - ticket: the code, and the receiver's key fingerprint when it comes from the QR code.
    ///   - identity: this device's key.
    ///   - deviceName: shown to the person who confirms the request. Treated as untrusted by the receiver.
    ///   - kinds: what this device wants to send. The person may allow fewer.
    ///   - label: a local name for the receiver. Defaults to the start of its fingerprint.
    ///   - approvalTimeout: how long to wait for the person to confirm.
    ///   - confirmKey: shows a person the receiver's key, and returns whether it matches the key on the
    ///     receiver's screen. It gets the key in the form the receiver shows it. Needed for a typed code,
    ///     because a code proves only that the other side knows it, and not called for any other ticket.
    ///
    /// A ticket with a fingerprint is compared here, and pairing stops on a mismatch. For a typed code,
    /// `confirmKey` runs before the receiver hears anything about this device, so the receiver still
    /// shows its key. If it returns false, this throws `ErdigoError.keyNotConfirmed`, the code stays
    /// valid, and the receiver has seen neither this device's key nor its name. If it returns true,
    /// the key the person confirmed is compared as if it came from a QR code.
    public static func pair(
        target: ReceiverTarget = .discover,
        ticket: PairingTicket,
        identity: DeviceIdentity,
        deviceName: String,
        kinds: Set<ItemKind>,
        label: String? = nil,
        handshakeTimeout: TimeInterval = 5,
        approvalTimeout: TimeInterval = 120,
        confirmKey: (@Sendable (_ receiverKey: String) async -> Bool)? = nil
    ) async throws -> ReceiverPairing {
        let name = TextRules.sanitizeDeviceName(deviceName)
        guard !name.isEmpty else { throw ErdigoError.invalidArgument("the device name cannot be empty") }
        guard !kinds.isEmpty else { throw ErdigoError.invalidArgument("choose at least one item kind") }
        if let label { try validateLabel(label) }

        var ticket = ticket
        var confirmedByAPerson = false
        if ticket.fingerprint == nil {
            guard let confirmKey else {
                throw ErdigoError.invalidArgument("a typed code needs confirmKey, because a person has to compare the receiver's key")
            }
            let found = try await receiverFingerprint(target: target, code: ticket.code, handshakeTimeout: handshakeTimeout)
            guard await confirmKey(IntegrationV1.shortFingerprint(found)) else { throw ErdigoError.keyNotConfirmed }
            ticket = PairingTicket(code: ticket.code, fingerprint: found)
            confirmedByAPerson = true
        }

        let request = PairRequest(
            versions: [IntegrationV1.version],
            sdk: sdkName,
            name: name,
            kinds: ItemKind.canonicalList(kinds)
        )

        var lastError: ErdigoError = .noAnswer
        search: for endpoint in try await Discovery.candidates(target: target) {
            // After a person has confirmed the key, the receiver has just answered with this code, so silence now is
            // most likely the receiver still closing the connection that showed the key: a receiver limits how many
            // unproven connections one address may have at once. Trying again is safe, because nothing about this
            // device is sent until the receiver has proven the code and its key matches the one confirmed.
            let tries = confirmedByAPerson ? confirmedAttempts : 1
            for round in 0..<tries {
                if round > 0 {
                    // Throws if the task is cancelled while it waits.
                    try await Task.sleep(nanoseconds: confirmedRetryWait * UInt64(round))
                }
                do {
                    return try await attempt(
                        endpoint: endpoint,
                        ticket: ticket,
                        identity: identity,
                        request: request,
                        label: label,
                        handshakeTimeout: handshakeTimeout,
                        approvalTimeout: approvalTimeout
                    )
                } catch ErdigoError.noAnswer {
                    // A cancelled task lands here too, because its connection was closed.
                    try Task.checkCancellation()
                } catch let error as ErdigoError {
                    // A definite answer from a receiver holding this code ends the search.
                    lastError = error
                    break search
                }
            }
        }
        throw lastError
    }

    /// How many times the pairing connection is tried after a person confirmed the key.
    private static let confirmedAttempts = 3

    /// Half a second before the second try, and a second before the third.
    private static let confirmedRetryWait: UInt64 = 500_000_000

    /// Finds the key of the receiver that holds this code, and stops there, so that a person can
    /// compare it. This runs the exchange up to message 2, as spec 4.1 describes.
    private static func receiverFingerprint(
        target: ReceiverTarget,
        code: PairingCode,
        handshakeTimeout: TimeInterval
    ) async throws -> String {
        // The handshake needs a key, but uses it only in message 3, which is never sent here.
        let unused = DeviceIdentity.generate()
        var lastError: ErdigoError = .noAnswer
        for endpoint in try await Discovery.candidates(target: target) {
            let stream = ByteStream(endpoint: endpoint)
            defer { stream.cancel() }
            do {
                let (_, receiverKey) = try await readReceiverKey(
                    from: stream, code: code, identity: unused, handshakeTimeout: handshakeTimeout
                )
                return IntegrationV1.fingerprint(of: receiverKey)
            } catch ErdigoError.noAnswer {
                try Task.checkCancellation()
                continue
            } catch let error as ErdigoError {
                lastError = error
                break
            }
        }
        throw lastError
    }

    static func validateLabel(_ label: String) throws {
        do {
            try TextRules.validateTitle(label)
        } catch {
            throw ErdigoError.invalidArgument("the receiver label cannot be empty or contain control characters")
        }
    }

    /// Runs the handshake up to the receiver's answer. This device's key and name have not been sent yet.
    private static func readReceiverKey(
        from stream: ByteStream,
        code: PairingCode,
        identity: DeviceIdentity,
        handshakeTimeout: TimeInterval
    ) async throws -> (handshake: NoiseHandshake, receiverKey: Data) {
        let prologue = IntegrationV1.preamble(.pair)
        let handshake: NoiseHandshake
        let first: Data
        do {
            try await stream.start(timeout: handshakeTimeout)
            handshake = try NoiseHandshake(
                pattern: .xxPSK0,
                isInitiator: true,
                prologue: prologue,
                staticKey: identity.privateKey,
                psk: code.presharedKey()
            )
            first = try handshake.writeMessage()
            stream.armDeadline(handshakeTimeout)
            try await stream.send(prologue + ByteStream.frame(first))
        } catch {
            throw ErdigoError.noAnswer
        }

        let second: Data
        do {
            second = try await stream.readFrame(maxLength: IntegrationV1.maxHandshakeFrameLength)
        } catch {
            throw ErdigoError.noAnswer
        }
        // Reading this message proves the receiver knows the code too.
        do {
            _ = try handshake.readMessage(second)
        } catch {
            throw ErdigoError.noAnswer
        }

        guard let receiverKey = handshake.remoteStaticKey, Noise.isCanonicalPublicKey(receiverKey) else {
            throw ErdigoError.protocolViolation("the receiver sent an unusable key")
        }
        return (handshake, receiverKey)
    }

    private static func attempt(
        endpoint: NWEndpoint,
        ticket: PairingTicket,
        identity: DeviceIdentity,
        request: PairRequest,
        label: String?,
        handshakeTimeout: TimeInterval,
        approvalTimeout: TimeInterval
    ) async throws -> ReceiverPairing {
        let stream = ByteStream(endpoint: endpoint)
        defer { stream.cancel() }

        let (handshake, receiverKey) = try await readReceiverKey(
            from: stream, code: ticket.code, identity: identity, handshakeTimeout: handshakeTimeout
        )
        let fingerprint = IntegrationV1.fingerprint(of: receiverKey)
        if let expected = ticket.fingerprint, expected != fingerprint {
            throw ErdigoError.fingerprintMismatch
        }

        let transport: NoiseTransport
        do {
            let third = try handshake.writeMessage(payload: Wire.encode(request))
            try await stream.sendFrame(third)
            transport = try handshake.split()
        } catch {
            throw ErdigoError.noAnswer
        }

        // The person now decides, which can take a while.
        stream.armDeadline(approvalTimeout)
        while true {
            let message: ReceiverMessage
            do {
                let frame = try await stream.readFrame()
                message = try Wire.decodeReceiverMessage(try transport.open(frame))
            } catch ByteStream.Failure.timedOut {
                throw ErdigoError.pairingTimedOut
            } catch let error as WireError {
                throw ErdigoError.protocolViolation(error.detail)
            } catch {
                throw ErdigoError.noAnswer
            }

            switch message {
            case .pairStatus(.pending):
                continue
            case .pairStatus(.denied):
                throw ErdigoError.pairingDenied
            case .pairStatus(.approved(let kinds, let counter)):
                let address = stream.remoteAddress
                return ReceiverPairing(
                    label: label ?? "receiver-\(fingerprint.prefix(8))",
                    receiverKey: receiverKey,
                    kinds: kinds,
                    lastHost: address?.host,
                    lastPort: address?.port,
                    counterFloor: counter
                )
            case .error(let error):
                switch error.code {
                case ErrorCode.codeReused.rawValue:
                    throw ErdigoError.codeReused
                case ErrorCode.versionUnsupported.rawValue:
                    throw ErdigoError.versionUnsupported(supported: error.supported ?? [])
                default:
                    throw ErdigoError.rejected(error)
                }
            default:
                throw ErdigoError.protocolViolation("unexpected message during pairing")
            }
        }
    }
}
