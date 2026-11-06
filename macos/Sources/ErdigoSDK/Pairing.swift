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
    public static func pair(
        target: ReceiverTarget = .discover,
        ticket: PairingTicket,
        identity: DeviceIdentity,
        deviceName: String,
        kinds: Set<ItemKind>,
        label: String? = nil,
        handshakeTimeout: TimeInterval = 5,
        approvalTimeout: TimeInterval = 120
    ) async throws -> ReceiverPairing {
        let name = TextRules.sanitizeDeviceName(deviceName)
        guard !name.isEmpty else { throw ErdigoError.invalidArgument("the device name cannot be empty") }
        guard !kinds.isEmpty else { throw ErdigoError.invalidArgument("choose at least one item kind") }
        if let label { try validateLabel(label) }

        let request = PairRequest(
            versions: [IntegrationV1.version],
            sdk: sdkName,
            name: name,
            kinds: ItemKind.canonicalList(kinds)
        )

        var lastError: ErdigoError = .noAnswer
        for endpoint in try await Discovery.candidates(target: target) {
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
                continue
            } catch let error as ErdigoError {
                // A definite answer from a receiver holding this code ends the search.
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
                psk: ticket.code.presharedKey()
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
        let fingerprint = IntegrationV1.fingerprint(of: receiverKey)
        if let expected = ticket.fingerprint, expected != fingerprint {
            throw ErdigoError.fingerprintMismatch
        }

        var transport: NoiseTransport
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
