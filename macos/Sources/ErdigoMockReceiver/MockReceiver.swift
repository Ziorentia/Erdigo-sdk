import CryptoKit
@_spi(ErdigoInternal) import ErdigoCore
import Foundation
import Network

/// A stand-in for the receiver side of integration-v1, for developing and testing without the Erdigo app.
///
/// It enforces the rules in the specification, with two differences from a real receiver: it keeps
/// everything in memory and draws no map, and unless `setApprovalHandler` is used it allows every
/// pairing request without asking anyone. By default it listens on the loopback interface only.
public actor MockReceiver {
    public struct Options: Sendable {
        /// 0 asks the system for a free port.
        public var port: UInt16 = 0
        /// Accept connections from the local network instead of this computer only.
        public var acceptsLocalNetwork = false
        /// Advertise over Bonjour with a random name that changes every hour.
        public var advertises = false
        public var limits = Limits.v1
        public var acceptedVersions = [IntegrationV1.version]
        public var handshakeTimeout: TimeInterval = 10
        public var idleTimeout: TimeInterval = 300
        /// How long a peer may take to accept an answer before it is disconnected.
        public var sendTimeout: TimeInterval = 30
        public var pairingWindow: TimeInterval = 600
        public var approvalTimeout: TimeInterval = 120
        /// How long to hold a connection before closing it without a reply.
        public var silentDelay: ClosedRange<Double> = 0.2...1.0
        /// How long an address stays under a penalty after its first failed attempt. Doubles up to 60 seconds.
        public var addressPenaltyBase: TimeInterval = 0.5
        /// How long an address under a penalty has to finish a handshake.
        public var penalisedHandshakeTimeout: TimeInterval = 1
        public var maxPreAuthConnections = 8
        public var maxPreAuthConnectionsPerAddress = 2
        public var diskBytesTotal = 209_715_200
        /// Rejected messages tolerated in one minute before the device is disconnected.
        public var maxRejectionsPerMinute = 20
        public var receiverKey: Data?

        public init() {}
    }

    public struct ApprovalRequest: Sendable {
        public let deviceName: String
        public let deviceFingerprint: String
        public let requestedKinds: [ItemKind]
        public let address: String?
        /// True if the person removed this device earlier.
        public let wasRemovedBefore: Bool
    }

    public enum ApprovalDecision: Sendable {
        case allow(kinds: [ItemKind], name: String?)
        case deny
    }

    public enum Event: Equatable, Sendable {
        case listening(port: UInt16)
        case pairingWindowOpened
        case pairingRequested(device: String)
        case paired(device: String, kinds: [ItemKind])
        case pairingDenied(device: String)
        case codeReusedAlarm(firstDevice: String)
        case silentlyDropped(reason: String)
        case connected(device: String)
        case disconnected(device: String)
        case applied(device: String, seq: UInt64, summary: String)
        case rejected(device: String, seq: UInt64?, code: String)
        case revoked(device: String)
    }

    public struct Item: Equatable, Sendable {
        public let id: String
        public let kind: ItemKind
        public var latitude: Double?
        public var longitude: Double?
        public var title: String?
        public var text: String?
        public var color: String?
        public var photo: Data?
        public var points: [TrackPoint]
        public var isFinished: Bool
    }

    private struct Upload {
        let header: PhotoHeader
        var received = Data()
        var nextIndex = 0
    }

    private enum Outcome: Equatable {
        case ok
        case failed(ReceiverError)
    }

    /// An authenticated connection. The transport lives here so the receiver can also
    /// send a closing message from outside the connection's own task.
    private final class LiveConnection {
        let stream: ByteStream
        let transport: NoiseTransport

        init(stream: ByteStream, transport: NoiseTransport) {
            self.stream = stream
            self.transport = transport
        }
    }

    /// What a device was last told about waiting, so the receiver can tell whether it listened.
    private struct RetryAdvice {
        var retryAt: TimeInterval = 0
        var usedSlack = false

        /// A device may arrive a little early, because neither its clock nor the network is exact.
        /// It uses that up until it next arrives on time. Anything earlier, or more often, did not wait.
        mutating func arrivalCameTooSoon(now: TimeInterval, wait: TimeInterval) -> Bool {
            let tooSoon: Bool
            if now >= retryAt {
                tooSoon = false
                usedSlack = false
            } else if now >= retryAt - 0.05, !usedSlack {
                tooSoon = false
                usedSlack = true
            } else {
                tooSoon = true
            }
            retryAt = now + wait
            return tooSoon
        }
    }

    private final class Device {
        let key: Data
        let fingerprint: String
        var name: String
        var kinds: [ItemKind]
        var lastCounter: UInt64 = 0
        var lastSeq: UInt64 = 0
        var lastDigest: Data?
        var lastOutcome: Outcome = .ok
        var items: [String: Item] = [:]
        var upload: Upload?
        var diskBytes = 0
        var messageBucket: TokenBucket
        var pointBucket: TokenBucket
        var messageRetry = RetryAdvice()
        var pointRetry = RetryAdvice()
        var rejections: [TimeInterval] = []
        var ignoredUntil: TimeInterval = 0
        var nextPenalty: TimeInterval = 60
        var connection: LiveConnection?

        init(key: Data, name: String, kinds: [ItemKind], limits: Limits, now: TimeInterval) {
            self.key = key
            fingerprint = IntegrationV1.fingerprint(of: key)
            self.name = name
            self.kinds = kinds
            messageBucket = TokenBucket(capacity: limits.messageBurst, refillPerSecond: limits.messagesPerSecond, now: now)
            pointBucket = TokenBucket(capacity: limits.pointBurst, refillPerSecond: limits.pointsPerSecond, now: now)
        }
    }

    /// Whichever comes first decides: the person, the timer, or a pause or alarm.
    private final class ApprovalRace: @unchecked Sendable {
        private let lock = NSLock()
        private var continuation: CheckedContinuation<ApprovalDecision, Never>?
        private var decided: ApprovalDecision?

        func finish(_ decision: ApprovalDecision) {
            lock.lock()
            defer { lock.unlock() }
            guard decided == nil else { return }
            decided = decision
            continuation?.resume(returning: decision)
            continuation = nil
        }

        func wait() async -> ApprovalDecision {
            await withCheckedContinuation { (continuation: CheckedContinuation<ApprovalDecision, Never>) in
                lock.lock()
                defer { lock.unlock() }
                if let decided {
                    continuation.resume(returning: decided)
                } else {
                    self.continuation = continuation
                }
            }
        }
    }

    private enum WindowUse {
        case unused
        case pending(key: Data, status: Task<PairStatus, Never>, race: ApprovalRace)
        case approved(key: Data, kinds: [ItemKind])
        case denied(key: Data)
        case burned
    }

    private struct PairingWindow {
        let id = UUID()
        let code: PairingCode
        let expiresAt: TimeInterval
        var use: WindowUse = .unused
        var seenEphemeralKeys = Set<Data>()
        var seenOrder: [Data] = []
    }

    /// A connection's claim on the limited slots for connections that have not yet proven themselves.
    private final class Admission {
        let address: String
        let isPenalised: Bool
        var isHeld = true

        init(address: String, isPenalised: Bool) {
            self.address = address
            self.isPenalised = isPenalised
        }
    }

    private let options: Options
    private let staticKey: Curve25519.KeyAgreement.PrivateKey
    private var listener: NWListener?
    private var rotationTask: Task<Void, Never>?
    private var boundPort: UInt16 = 0
    private var devices: [Data: Device] = [:]
    private var removedKeys = Set<Data>()
    /// The counters of removed devices, so that old recordings do not become usable again.
    private var removedCounters: [Data: UInt64] = [:]
    /// Items of removed devices, kept unless the person asked to delete them.
    private var retainedItems: [Data: [String: Item]] = [:]
    private var window: PairingWindow?
    private var isPaused = false
    private var preAuthTotal = 0
    private var preAuthByAddress: [String: Int] = [:]
    private var addressPenalty: [String: (until: TimeInterval, seconds: TimeInterval)] = [:]
    private var openStreams: [UUID: ByteStream] = [:]
    private var photoDecodeTail: Task<Void, Never>?
    public private(set) var events: [Event] = []
    private var eventHandler: (@Sendable (Event) -> Void)?
    private var approvalHandler: (@Sendable (ApprovalRequest) async -> ApprovalDecision)?

    public init(options: Options = Options()) throws {
        self.options = options
        if let raw = options.receiverKey {
            staticKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: raw)
        } else {
            staticKey = Curve25519.KeyAgreement.PrivateKey()
        }
    }

    // MARK: Control

    public var publicKey: Data { staticKey.publicKey.rawRepresentation }
    public var fingerprint: String { IntegrationV1.fingerprint(of: publicKey) }
    public var port: UInt16 { boundPort }

    public func setEventHandler(_ handler: (@Sendable (Event) -> Void)?) {
        eventHandler = handler
    }

    /// Stands in for the confirmation dialog. By default every request is allowed as asked.
    /// If the handler takes longer than `approvalTimeout`, the answer is no and its task is cancelled.
    public func setApprovalHandler(_ handler: (@Sendable (ApprovalRequest) async -> ApprovalDecision)?) {
        approvalHandler = handler
    }

    /// Starts listening and returns the port.
    @discardableResult
    public func start() async throws -> UInt16 {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: nil, tcp: tcp)
        if options.acceptsLocalNetwork {
            parameters.acceptLocalOnly = true
        } else {
            parameters.requiredInterfaceType = .loopback
        }
        let listener: NWListener
        if options.port == 0 {
            listener = try NWListener(using: parameters)
        } else {
            guard let port = NWEndpoint.Port(rawValue: options.port) else { throw ByteStream.Failure.connectionFailed }
            listener = try NWListener(using: parameters, on: port)
        }
        if options.advertises {
            listener.service = Self.randomService()
        }
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else {
                connection.cancel()
                return
            }
            let stream = ByteStream(connection: connection)
            Task { await self.serve(stream) }
        }

        let port: UInt16 = try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<UInt16, Error>) in
            let once = OnceContinuation(continuation)
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    once.finish(.success(listener.port?.rawValue ?? 0))
                case .failed(let error):
                    once.finish(.failure(error))
                case .cancelled:
                    once.finish(.failure(ByteStream.Failure.closed))
                default:
                    break
                }
            }
            listener.start(queue: DispatchQueue(label: "erdigo.mock.listener"))
        }
        self.listener = listener
        boundPort = port
        record(.listening(port: port))

        if options.advertises {
            rotationTask = Task { [weak self] in
                while !Task.isCancelled {
                    try? await Task.sleep(nanoseconds: 3_600_000_000_000)
                    if Task.isCancelled { return }
                    await self?.rotateServiceName()
                }
            }
        }
        return port
    }

    private func rotateServiceName() {
        guard !isPaused else { return }
        listener?.service = Self.randomService()
    }

    private nonisolated static func randomService() -> NWListener.Service {
        let name = (0..<8).map { _ in String(format: "%02x", UInt8.random(in: 0...255)) }.joined()
        return NWListener.Service(name: name, type: IntegrationV1.serviceType, txtRecord: NWTXTRecord(["v": "1"]))
    }

    public func stop() {
        rotationTask?.cancel()
        rotationTask = nil
        listener?.cancel()
        listener = nil
        if case .pending(_, _, let race)? = window?.use { race.finish(.deny) }
        for stream in openStreams.values { stream.cancel() }
        openStreams.removeAll()
    }

    /// Opens a pairing window and returns its one-time ticket. Any earlier window is closed.
    public func openPairingWindow() throws -> PairingTicket {
        if case .pending(_, _, let race)? = window?.use { race.finish(.deny) }
        let code = try PairingCode.generate()
        window = PairingWindow(code: code, expiresAt: Self.now() + options.pairingWindow)
        record(.pairingWindowOpened)
        return PairingTicket(code: code, fingerprint: fingerprint)
    }

    /// Opens a window with a code chosen by a test, to play a second receiver holding the same code.
    @_spi(TestVectors) public func openPairingWindow(code: PairingCode) -> PairingTicket {
        window = PairingWindow(code: code, expiresAt: Self.now() + options.pairingWindow)
        record(.pairingWindowOpened)
        return PairingTicket(code: code, fingerprint: fingerprint)
    }

    public func closePairingWindow() {
        if case .pending(_, _, let race)? = window?.use { race.finish(.deny) }
        window = nil
    }

    /// Stops all connections at once and answers nothing until `setPaused(false)`.
    public func setPaused(_ paused: Bool) {
        isPaused = paused
        if options.advertises {
            listener?.service = paused ? nil : Self.randomService()
        }
        guard paused else { return }
        if case .pending(_, _, let race)? = window?.use { race.finish(.deny) }

        var authenticated = Set<UUID>()
        for device in devices.values {
            if let connection = device.connection {
                device.connection = nil
                authenticated.insert(connection.stream.identifier)
                send(.error(ReceiverError(code: ErrorCode.paused.rawValue, fatal: true)), thenCloseOn: connection)
            }
        }
        // Everything still in a handshake simply ends.
        for (id, stream) in openStreams where !authenticated.contains(id) {
            stream.cancel()
        }
    }

    /// Sends a last message and closes, even if the peer has stopped reading.
    private func send(_ message: ReceiverMessage, thenCloseOn connection: LiveConnection) {
        guard let sealed = try? connection.transport.seal(Wire.encode(message)) else {
            connection.stream.cancel()
            return
        }
        let stream = connection.stream
        stream.armDeadline(2)
        Task {
            try? await stream.sendFrame(sealed)
            stream.cancel()
        }
    }

    /// Removes a device from the list. With `deleteItems`, also deletes its items, including
    /// those kept from an earlier removal.
    public func revoke(deviceFingerprint: String, deleteItems: Bool = false) {
        if let device = devices.values.first(where: { $0.fingerprint == deviceFingerprint }) {
            remove(device, deleteItems: deleteItems)
        } else if deleteItems, let key = retainedItems.keys.first(where: { IntegrationV1.fingerprint(of: $0) == deviceFingerprint }) {
            retainedItems[key] = nil
        }
    }

    private func remove(_ device: Device, deleteItems: Bool) {
        // No message: a removed device learns only that the connection ended.
        device.connection?.stream.cancel()
        device.connection = nil
        devices[device.key] = nil
        removedKeys.insert(device.key)
        removedCounters[device.key] = device.lastCounter
        retainedItems[device.key] = deleteItems ? nil : device.items
        record(.revoked(device: device.fingerprint))
    }

    public func pairedDevices() -> [(name: String, fingerprint: String, kinds: [ItemKind])] {
        devices.values.map { ($0.name, $0.fingerprint, $0.kinds) }.sorted { $0.fingerprint < $1.fingerprint }
    }

    public func items(ofDevice fingerprint: String) -> [Item] {
        if let device = devices.values.first(where: { $0.fingerprint == fingerprint }) {
            return device.items.values.sorted { $0.id < $1.id }
        }
        let retained = retainedItems.first { IntegrationV1.fingerprint(of: $0.key) == fingerprint }?.value ?? [:]
        return retained.values.sorted { $0.id < $1.id }
    }

    public func lastSeq(ofDevice fingerprint: String) -> UInt64? {
        devices.values.first { $0.fingerprint == fingerprint }?.lastSeq
    }

    // MARK: Helpers

    private static func now() -> TimeInterval {
        ProcessInfo.processInfo.systemUptime
    }

    private func record(_ event: Event) {
        events.append(event)
        if events.count > 10_000 { events.removeFirst(1000) }
        eventHandler?(event)
    }

    private var totalDiskBytes: Int {
        let live = devices.values.reduce(0) { $0 + $1.diskBytes }
        let retained = retainedItems.values.reduce(0) { sum, items in
            sum + items.values.reduce(0) { $0 + cost(of: $1) }
        }
        return live + retained
    }

    /// Every way a connection can fail before proving itself ends here: the same pause,
    /// the same penalty, and nothing sent. That is what keeps a scanner from learning anything.
    private func closeSilently(_ stream: ByteStream, address: String, reason: String) async {
        record(.silentlyDropped(reason: reason))
        notePreAuthFailure(address)
        let delay = Double.random(in: options.silentDelay)
        try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
        stream.cancel()
    }

    private func notePreAuthFailure(_ address: String) {
        guard options.addressPenaltyBase > 0 else { return }
        let now = Self.now()
        if addressPenalty.count > 1024 {
            addressPenalty = addressPenalty.filter { $0.value.until > now }
        }
        let current = addressPenalty[address]?.seconds ?? options.addressPenaltyBase
        addressPenalty[address] = (now + current, min(current * 2, 60))
    }

    /// An address under a penalty still gets read, because a good device may share it with a failing
    /// program, and its first message arrives at once. It just gets one slot and little time.
    private func admit(_ address: String) -> Admission? {
        let isPenalised = (addressPenalty[address]?.until ?? 0) > Self.now()
        let perAddress = isPenalised ? 1 : options.maxPreAuthConnectionsPerAddress
        guard preAuthTotal < options.maxPreAuthConnections,
              preAuthByAddress[address, default: 0] < perAddress
        else { return nil }
        preAuthTotal += 1
        preAuthByAddress[address, default: 0] += 1
        return Admission(address: address, isPenalised: isPenalised)
    }

    private func leavePreAuth(_ admission: Admission) {
        guard admission.isHeld else { return }
        admission.isHeld = false
        preAuthTotal -= 1
        let remaining = preAuthByAddress[admission.address, default: 1] - 1
        preAuthByAddress[admission.address] = remaining > 0 ? remaining : nil
    }

    // MARK: Connections

    private func serve(_ stream: ByteStream) async {
        let streamID = stream.identifier
        openStreams[streamID] = stream
        defer { openStreams[streamID] = nil }

        do {
            try await stream.start(timeout: options.handshakeTimeout)
        } catch {
            stream.cancel()
            return
        }
        let address = stream.remoteAddress?.host ?? "unknown"
        guard let admission = admit(address) else {
            // Beyond the limits nothing is read at all.
            stream.cancel()
            record(.silentlyDropped(reason: "pre-auth limit"))
            return
        }
        defer { leavePreAuth(admission) }

        stream.armDeadline(admission.isPenalised ? min(options.penalisedHandshakeTimeout, options.handshakeTimeout) : options.handshakeTimeout)
        let arrivedAt = Self.now()

        let mode: IntegrationV1.Mode
        do {
            guard let parsed = IntegrationV1.parsePreamble(try await stream.read(exactly: 5)) else {
                await closeSilently(stream, address: address, reason: "unknown preamble")
                return
            }
            mode = parsed
        } catch {
            // Connecting and promptly leaving, having sent nothing, costs nothing. Staying any
            // longer holds a slot someone else may need, so it counts as a failed attempt.
            if case ByteStream.Failure.closed = error, stream.bufferedByteCount == 0, Self.now() - arrivedAt < 1 {
                stream.cancel()
                return
            }
            await closeSilently(stream, address: address, reason: "incomplete handshake")
            return
        }

        // The state of the receiver is looked at only after the whole first message has arrived or the
        // deadline has passed. A check made any earlier would show in how long the connection stays open.
        let first: Data
        do {
            first = try await stream.readFrame(maxLength: IntegrationV1.maxHandshakeFrameLength)
        } catch {
            await closeSilently(stream, address: address, reason: "incomplete handshake")
            return
        }
        guard !isPaused else {
            await closeSilently(stream, address: address, reason: "paused")
            return
        }
        if admission.isPenalised { stream.armDeadline(options.handshakeTimeout) }

        switch mode {
        case .connect:
            guard let established = await establishConnection(stream, first: first, address: address) else { return }
            leavePreAuth(admission)
            await runOperations(on: established.connection, device: established.device)
        case .pair:
            await runPairing(stream, first: first, admission: admission, address: address)
        }
    }

    private func establishConnection(_ stream: ByteStream, first: Data, address: String) async -> (device: Device, connection: LiveConnection)? {
        var proven = false
        do {
            guard first.count == IntegrationV1.connectFirstMessageLength else {
                await closeSilently(stream, address: address, reason: "malformed handshake")
                return nil
            }
            let handshake = try NoiseHandshake(
                pattern: .ik,
                isInitiator: false,
                prologue: IntegrationV1.preamble(.connect),
                staticKey: staticKey
            )
            let payload: Data
            do {
                payload = try handshake.readMessage(first)
            } catch {
                await closeSilently(stream, address: address, reason: "handshake failed")
                return nil
            }
            guard let key = handshake.remoteStaticKey, let device = devices[key] else {
                await closeSilently(stream, address: address, reason: "device not allowed")
                return nil
            }
            guard payload.count == 8 else {
                await closeSilently(stream, address: address, reason: "malformed counter")
                return nil
            }
            let counter = payload.reduce(UInt64(0)) { $0 << 8 | UInt64($1) }
            // A replayed first message fails here, so it cannot be used to recognise this receiver.
            guard counter > device.lastCounter, counter <= IntegrationV1.maxSequence else {
                await closeSilently(stream, address: address, reason: "replayed handshake")
                return nil
            }
            device.lastCounter = counter
            guard device.ignoredUntil <= Self.now() else {
                await closeSilently(stream, address: address, reason: "device penalty")
                return nil
            }

            try await stream.sendFrame(try handshake.writeMessage())
            let transport = try handshake.split()

            // The hello proves the device holds its key, because a replayed first message
            // cannot produce it. Only now may an older connection be replaced.
            let helloFrame = try await stream.readFrame()
            let hello = try Wire.decodeHello(try transport.open(helloFrame))
            stream.disarmDeadline()

            // Things may have changed while waiting for the hello.
            guard devices[key] === device, !isPaused, device.ignoredUntil <= Self.now() else {
                stream.cancel()
                return nil
            }
            addressPenalty[address] = nil
            proven = true

            guard let version = hello.versions.filter(options.acceptedVersions.contains).max() else {
                let reply = ReceiverMessage.error(ReceiverError(
                    code: ErrorCode.versionUnsupported.rawValue,
                    supported: options.acceptedVersions,
                    fatal: true
                ))
                try await stream.sendFrame(try transport.seal(Wire.encode(reply)))
                stream.cancel()
                return nil
            }

            if let previous = device.connection {
                send(.error(ReceiverError(code: ErrorCode.replaced.rawValue, fatal: true)), thenCloseOn: previous)
            }
            device.upload = nil
            let connection = LiveConnection(stream: stream, transport: transport)
            device.connection = connection
            record(.connected(device: device.fingerprint))

            let welcome = Welcome(version: version, lastSeq: device.lastSeq, kinds: device.kinds, limits: options.limits)
            let sealed = try connection.transport.seal(Wire.encode(.welcome(welcome)))
            stream.armDeadline(options.sendTimeout)
            try await stream.sendFrame(sealed)
            stream.disarmDeadline()
            return (device, connection)
        } catch {
            if !proven { notePreAuthFailure(address) }
            stream.cancel()
            return nil
        }
    }

    private func runPairing(_ stream: ByteStream, first: Data, admission: Admission, address: String) async {
        do {
            guard let current = window, current.expiresAt > Self.now() else {
                await closeSilently(stream, address: address, reason: "no pairing window")
                return
            }
            guard first.count == IntegrationV1.pairFirstMessageLength else {
                await closeSilently(stream, address: address, reason: "malformed pairing handshake")
                return
            }
            let handshake = try NoiseHandshake(
                pattern: .xxPSK0,
                isInitiator: false,
                prologue: IntegrationV1.preamble(.pair),
                staticKey: staticKey,
                psk: current.code.presharedKey()
            )
            do {
                _ = try handshake.readMessage(first)
            } catch {
                // The code stays valid, so a stranger cannot spoil it by guessing badly.
                await closeSilently(stream, address: address, reason: "wrong pairing code")
                return
            }

            // A real attempt always uses a new ephemeral key, so one seen before is a replay.
            let ephemeral = Data(first.prefix(Noise.dhLength))
            guard window?.id == current.id, window?.seenEphemeralKeys.contains(ephemeral) == false else {
                await closeSilently(stream, address: address, reason: "replayed handshake")
                return
            }
            // Oldest forgotten first, so memory stays bounded and nothing ever goes silent for a real device.
            window?.seenEphemeralKeys.insert(ephemeral)
            window?.seenOrder.append(ephemeral)
            if let count = window?.seenOrder.count, count > Self.rememberedEphemeralKeys,
               let oldest = window?.seenOrder.removeFirst() {
                window?.seenEphemeralKeys.remove(oldest)
            }

            try await stream.sendFrame(try handshake.writeMessage())
            let third = try await stream.readFrame(maxLength: IntegrationV1.maxHandshakeFrameLength)
            let payload = try handshake.readMessage(third)
            let transport = try handshake.split()
            // X25519 ignores the top bit of a key, so without this one key could pose as two devices.
            guard let key = handshake.remoteStaticKey, Noise.isCanonicalPublicKey(key) else {
                stream.cancel()
                return
            }
            let request = try Wire.decodePairRequest(payload)
            stream.disarmDeadline()

            guard window?.id == current.id, !isPaused else {
                stream.cancel()
                return
            }
            addressPenalty[address] = nil
            // From here the peer has proven it holds the code, and waiting for a
            // person must not use up the slots other connections need.
            leavePreAuth(admission)

            guard request.versions.contains(where: options.acceptedVersions.contains) else {
                let reply = ReceiverMessage.error(ReceiverError(
                    code: ErrorCode.versionUnsupported.rawValue,
                    supported: options.acceptedVersions,
                    fatal: true
                ))
                try await stream.sendFrame(try transport.seal(Wire.encode(reply)))
                stream.cancel()
                return
            }

            switch pairingAnswer(key: key, request: request, address: address) {
            case .reused:
                let reply = ReceiverMessage.error(ReceiverError(code: ErrorCode.codeReused.rawValue, fatal: true))
                try await stream.sendFrame(try transport.seal(Wire.encode(reply)))
            case .final(let status):
                try await stream.sendFrame(try transport.seal(Wire.encode(.pairStatus(status))))
            case .wait(let status):
                try await stream.sendFrame(try transport.seal(Wire.encode(.pairStatus(.pending))))
                let final = await status.value
                try await stream.sendFrame(try transport.seal(Wire.encode(.pairStatus(final))))
            }
            stream.cancel()
        } catch {
            if admission.isHeld { notePreAuthFailure(address) }
            stream.cancel()
        }
    }

    private static let rememberedEphemeralKeys = 4096

    /// The code has been misused: warn the person, undo anything done with it, and stop using it.
    private func burnWindow() {
        guard let use = window?.use else { return }
        var firstKey: Data?
        switch use {
        case .pending(let key, _, let race):
            race.finish(.deny)
            firstKey = key
        case .approved(let key, _):
            if let first = devices[key] { remove(first, deleteItems: false) }
            firstKey = key
        case .denied(let key):
            firstKey = key
        case .unused, .burned:
            break
        }
        window?.use = .burned
        record(.codeReusedAlarm(firstDevice: firstKey.map(IntegrationV1.fingerprint(of:)) ?? "none"))
    }

    private enum PairingAnswer {
        case reused
        case final(PairStatus)
        case wait(Task<PairStatus, Never>)
    }

    /// Decides what a device holding the right code is told. Runs without suspending, so two
    /// devices arriving together cannot both be treated as the first to use the code.
    private func pairingAnswer(key: Data, request: PairRequest, address: String) -> PairingAnswer {
        guard let current = window else { return .reused }

        switch current.use {
        case .unused:
            let name = TextRules.sanitizeDeviceName(request.name)
            let approval = ApprovalRequest(
                deviceName: name.isEmpty ? "Unnamed device" : name,
                deviceFingerprint: IntegrationV1.fingerprint(of: key),
                requestedKinds: ItemKind.canonicalList(request.kinds),
                address: address,
                wasRemovedBefore: removedKeys.contains(key)
            )
            record(.pairingRequested(device: approval.deviceFingerprint))

            let race = ApprovalRace()
            let handler = approvalHandler
            let timeout = options.approvalTimeout
            let windowID = current.id
            // Not a task group: a group waits for all its tasks, and a handler may never finish.
            let answer = Task {
                if let handler {
                    race.finish(await handler(approval))
                } else {
                    race.finish(.allow(kinds: approval.requestedKinds, name: nil))
                }
            }
            let timer = Task {
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                if !Task.isCancelled { race.finish(.deny) }
            }
            let status = Task<PairStatus, Never> {
                let decision = await race.wait()
                answer.cancel()
                timer.cancel()
                return self.finishPairing(key: key, windowID: windowID, approval: approval, decision: decision)
            }
            window?.use = .pending(key: key, status: status, race: race)
            return .wait(status)

        case .pending(let firstKey, let status, let race):
            guard firstKey == key else {
                race.finish(.deny)
                burnWindow()
                return .reused
            }
            // The same device trying again after losing its connection.
            return .wait(status)

        case .approved(let firstKey, let kinds):
            guard firstKey == key else {
                burnWindow()
                return .reused
            }
            // It may have been removed since. The code does not bring it back.
            guard let device = devices[firstKey] else { return .final(.denied) }
            return .final(.approved(kinds: kinds, counter: device.lastCounter))

        case .denied(let firstKey):
            guard firstKey == key else {
                burnWindow()
                return .reused
            }
            return .final(.denied)

        case .burned:
            return .reused
        }
    }

    private func finishPairing(key: Data, windowID: UUID, approval: ApprovalRequest, decision: ApprovalDecision) -> PairStatus {
        // The window may have been burned or replaced while the person was deciding.
        guard let current = window, current.id == windowID, case .pending = current.use else {
            record(.pairingDenied(device: approval.deviceFingerprint))
            return .denied
        }
        guard !isPaused, case .allow(let kinds, let name) = decision else {
            window?.use = .denied(key: key)
            record(.pairingDenied(device: approval.deviceFingerprint))
            return .denied
        }
        let allowed = ItemKind.canonicalList(kinds).filter(approval.requestedKinds.contains)
        guard !allowed.isEmpty else {
            window?.use = .denied(key: key)
            record(.pairingDenied(device: approval.deviceFingerprint))
            return .denied
        }

        let finalName = name.map(TextRules.sanitizeDeviceName) ?? approval.deviceName
        let device = Device(key: key, name: finalName, kinds: allowed, limits: options.limits, now: Self.now())
        if let previous = devices[key] {
            // Paired again while still on the list. Its items and numbering carry over.
            previous.connection?.stream.cancel()
            device.items = previous.items
            device.diskBytes = previous.diskBytes
            device.lastSeq = previous.lastSeq
            device.lastDigest = previous.lastDigest
            device.lastOutcome = previous.lastOutcome
            device.lastCounter = previous.lastCounter
        } else {
            if let retained = retainedItems.removeValue(forKey: key) {
                device.items = retained
                device.diskBytes = retained.values.reduce(0) { $0 + cost(of: $1) }
            }
            device.lastCounter = removedCounters.removeValue(forKey: key) ?? 0
        }
        devices[key] = device
        removedKeys.remove(key)
        window?.use = .approved(key: key, kinds: allowed)
        record(.paired(device: approval.deviceFingerprint, kinds: allowed))
        return .approved(kinds: allowed, counter: device.lastCounter)
    }

    private func runOperations(on connection: LiveConnection, device: Device) async {
        let stream = connection.stream
        defer {
            if device.connection === connection {
                device.connection = nil
                device.upload = nil
                record(.disconnected(device: device.fingerprint))
            }
        }
        do {
            while true {
                stream.armDeadline(options.idleTimeout)
                let frame = try await stream.readFrame()
                stream.disarmDeadline()
                guard device.connection === connection else { break }
                // A message that fails authentication means the session cannot be trusted any more.
                let plaintext = try connection.transport.open(frame)

                let reply: ReceiverMessage
                switch process(plaintext, device: device) {
                case .answer(let answer):
                    reply = answer
                case .decodePhoto(let job):
                    let decoded = await decodePhoto(job, deviceKey: device.key, streamID: stream.identifier)
                    // The device may have been removed, or may have reconnected, during the decode.
                    guard device.connection === connection, devices[device.key] === device else {
                        stream.cancel()
                        return
                    }
                    reply = finishPhoto(job, decoded: decoded, device: device)
                }

                // A peer that stops reading must not be able to hold this connection open.
                stream.armDeadline(options.sendTimeout)
                let sealed = try connection.transport.seal(Wire.encode(reply))
                try await stream.sendFrame(sealed)
                stream.disarmDeadline()

                if case .error(let error) = reply, error.fatal {
                    stream.cancel()
                    break
                }
            }
        } catch {
            stream.cancel()
        }
    }

    // MARK: Requests

    private enum Step {
        case answer(ReceiverMessage)
        case decodePhoto(PhotoJob)
    }

    private struct PhotoJob: Sendable {
        let header: PhotoHeader
        let data: Data
        let seq: UInt64
        let digest: Data
    }

    /// Counts a rejection against the device, and penalises it if there have been too many.
    private func reject(_ device: Device, code: ErrorCode, seq: UInt64?, retryAfterMS: UInt64? = nil, lastSeq: UInt64? = nil) -> ReceiverMessage {
        record(.rejected(device: device.fingerprint, seq: seq, code: code.rawValue))
        let now = Self.now()
        device.rejections = device.rejections.filter { now - $0 < 60 }
        device.rejections.append(now)
        if device.rejections.count > options.maxRejectionsPerMinute {
            let penalty = device.nextPenalty
            device.ignoredUntil = now + penalty
            device.nextPenalty = min(penalty * 2, 3600)
            device.rejections.removeAll()
            return .error(ReceiverError(
                code: ErrorCode.tooManyErrors.rawValue,
                seq: seq,
                retryAfterMS: UInt64(penalty * 1000),
                fatal: true
            ))
        }
        return .error(ReceiverError(code: code.rawValue, seq: seq, lastSeq: lastSeq, retryAfterMS: retryAfterMS))
    }

    /// Being told to slow down is not a fault, unless the device has not waited as it was asked to.
    private func rateLimited(_ device: Device, wait rawWait: TimeInterval, seq: UInt64?, forPoints: Bool = false) -> ReceiverMessage {
        let now = Self.now()
        let wait = min(rawWait, 3600)
        let milliseconds = UInt64((wait * 1000).rounded(.up))
        let cameTooSoon = forPoints
            ? device.pointRetry.arrivalCameTooSoon(now: now, wait: wait)
            : device.messageRetry.arrivalCameTooSoon(now: now, wait: wait)
        if cameTooSoon {
            return reject(device, code: .rateLimited, seq: seq, retryAfterMS: milliseconds)
        }
        record(.rejected(device: device.fingerprint, seq: seq, code: ErrorCode.rateLimited.rawValue))
        return .error(ReceiverError(code: ErrorCode.rateLimited.rawValue, seq: seq, retryAfterMS: milliseconds))
    }

    private func process(_ plaintext: Data, device: Device) -> Step {
        // The rate limit comes first and does not use up the sequence number.
        if let wait = device.messageBucket.take(now: Self.now()) {
            return .answer(rateLimited(device, wait: wait, seq: nil))
        }

        let envelope: RequestEnvelope
        do {
            envelope = try Wire.decodeRequest(plaintext, limits: options.limits)
        } catch let error as WireError {
            return .answer(reject(device, code: error.code, seq: nil))
        } catch {
            return .answer(reject(device, code: .invalidMessage, seq: nil))
        }

        let digest = Data(SHA256.hash(data: plaintext))
        if envelope.seq == device.lastSeq {
            guard digest == device.lastDigest else {
                return .answer(reject(device, code: .seqConflict, seq: envelope.seq, lastSeq: device.lastSeq))
            }
            switch device.lastOutcome {
            case .ok:
                return .answer(.ack(seq: envelope.seq, duplicate: true))
            case .failed(let error):
                return .answer(.error(error))
            }
        }
        guard envelope.seq > device.lastSeq else {
            return .answer(reject(device, code: .staleSeq, seq: envelope.seq, lastSeq: device.lastSeq))
        }

        if case .photoEnd(let id) = envelope.request {
            do {
                let upload = try takeFinishedUpload(id: id, device: device)
                return .decodePhoto(PhotoJob(header: upload.header, data: upload.received, seq: envelope.seq, digest: digest))
            } catch {
                return .answer(settle(error: error, seq: envelope.seq, digest: digest, device: device))
            }
        }

        do {
            let summary = try apply(envelope.request, device: device)
            return .answer(settle(summary: summary, seq: envelope.seq, digest: digest, device: device))
        } catch let failure as RetryLater {
            return .answer(rateLimited(device, wait: failure.seconds, seq: envelope.seq, forPoints: true))
        } catch {
            return .answer(settle(error: error, seq: envelope.seq, digest: digest, device: device))
        }
    }

    private func settle(summary: String, seq: UInt64, digest: Data, device: Device) -> ReceiverMessage {
        device.lastSeq = seq
        device.lastDigest = digest
        device.lastOutcome = .ok
        record(.applied(device: device.fingerprint, seq: seq, summary: summary))
        return .ack(seq: seq, duplicate: false)
    }

    private func settle(error: Error, seq: UInt64, digest: Data, device: Device) -> ReceiverMessage {
        let code = (error as? WireError)?.code ?? .internalError
        if !code.isRetryable {
            // The number is used up, and a resend gets the same answer without running again.
            device.lastSeq = seq
            device.lastDigest = digest
            device.lastOutcome = .failed(ReceiverError(code: code.rawValue, seq: seq))
        }
        return reject(device, code: code, seq: seq)
    }

    private struct RetryLater: Error {
        let seconds: TimeInterval
    }

    // MARK: Photos

    private func takeFinishedUpload(id: ItemID, device: Device) throws -> Upload {
        try requireKind(.photo, device)
        guard let upload = device.upload, upload.header.id == id else {
            throw WireError(.invalidState, "no upload is in progress for this id")
        }
        device.upload = nil
        guard upload.received.count == upload.header.size,
              upload.nextIndex == upload.header.chunks,
              Data(SHA256.hash(data: upload.received)).hexString == upload.header.sha256
        else {
            throw WireError(.photoInvalid, "the data does not match the declared size and hash")
        }
        return upload
    }

    private func isCurrent(deviceKey: Data, streamID: UUID) -> Bool {
        devices[deviceKey]?.connection?.stream.identifier == streamID
    }

    /// Decodes away from this actor, so a slow image cannot hold up other devices or a removal,
    /// and one at a time, so several images cannot add up to a lot of memory. A decode nobody is
    /// waiting for any more is skipped, so a device cannot leave a backlog behind by reconnecting.
    private func decodePhoto(_ job: PhotoJob, deviceKey: Data, streamID: UUID) async -> Result<Data, WireError> {
        let previous = photoDecodeTail
        let decode = Task.detached(priority: .utility) { [self] () -> Result<Data, WireError> in
            await previous?.value
            guard await self.isCurrent(deviceKey: deviceKey, streamID: streamID) else {
                return .failure(WireError(.internalError, "the connection ended"))
            }
            do {
                return .success(try PhotoPipeline.process(job.data, declared: job.header.type))
            } catch let error as WireError {
                return .failure(error)
            } catch {
                return .failure(WireError(.internalError, "the image could not be processed"))
            }
        }
        photoDecodeTail = Task.detached(priority: .utility) { _ = await decode.value }
        return await decode.value
    }

    private func finishPhoto(_ job: PhotoJob, decoded: Result<Data, WireError>, device: Device) -> ReceiverMessage {
        do {
            let stored = try decoded.get()
            // The id may have been taken by a `put` since the upload began.
            let id = job.header.id.rawValue
            if let existing = device.items[id] {
                guard existing.kind == .photo else {
                    throw WireError(.kindConflict, "this id is already used by another kind of item")
                }
            } else {
                let count = device.items.values.filter { $0.kind == .photo }.count
                guard count < options.limits.photos else { throw WireError(.limitPhotos, "the photo limit was reached") }
            }
            var item = Self.blankItem(id: job.header.id, kind: .photo, at: job.header.at)
            item.title = job.header.title
            item.photo = stored
            try store(item, device: device)
            return settle(summary: "photo stored", seq: job.seq, digest: job.digest, device: device)
        } catch {
            return settle(error: error, seq: job.seq, digest: job.digest, device: device)
        }
    }

    // MARK: Items

    private func requireKind(_ kind: ItemKind, _ device: Device) throws {
        guard device.kinds.contains(kind) else {
            throw WireError(.notPermitted, "this device may not send this kind")
        }
    }

    private func charge(_ bytes: Int, device: Device) throws {
        guard device.diskBytes + bytes <= options.limits.diskBytes, totalDiskBytes + bytes <= options.diskBytesTotal else {
            throw WireError(.limitDisk, "the storage limit was reached")
        }
        device.diskBytes += bytes
    }

    private func cost(of item: Item) -> Int {
        var total = 256 + (item.title?.utf8.count ?? 0) + (item.text?.utf8.count ?? 0)
        total += item.photo?.count ?? 0
        total += item.points.count * 32
        return total
    }

    private func store(_ item: Item, device: Device) throws {
        let previous = device.items[item.id].map(cost(of:)) ?? 0
        let next = cost(of: item)
        if next > previous {
            try charge(next - previous, device: device)
        } else {
            device.diskBytes -= previous - next
        }
        device.items[item.id] = item
    }

    private func discard(id: String, device: Device) {
        if let item = device.items.removeValue(forKey: id) {
            device.diskBytes -= cost(of: item)
        }
    }

    private func putBasic(_ item: Item, device: Device) throws {
        try requireKind(item.kind, device)
        if let existing = device.items[item.id] {
            guard existing.kind == item.kind else {
                throw WireError(.kindConflict, "this id is already used by another kind of item")
            }
        } else {
            let count = device.items.values.filter { $0.kind.usesPut }.count
            guard count < options.limits.items else { throw WireError(.limitItems, "the item limit was reached") }
        }
        try store(item, device: device)
    }

    private static func blankItem(id: ItemID, kind: ItemKind, at coordinate: Coordinate?) -> Item {
        Item(
            id: id.rawValue,
            kind: kind,
            latitude: coordinate?.latitude,
            longitude: coordinate?.longitude,
            title: nil,
            text: nil,
            color: nil,
            photo: nil,
            points: [],
            isFinished: false
        )
    }

    /// Applies one request to this device's items, and to nothing else. Returns a description for the event log.
    private func apply(_ request: Request, device: Device) throws -> String {
        switch request {
        case .ping:
            return "ping"

        case .putPin(let id, let at, let title):
            var item = Self.blankItem(id: id, kind: .pin, at: at)
            item.title = title
            try putBasic(item, device: device)
            return "put pin"

        case .putMarker(let id, let at, let title, let color):
            var item = Self.blankItem(id: id, kind: .marker, at: at)
            item.title = title
            item.color = color?.rawValue
            try putBasic(item, device: device)
            return "put marker"

        case .putNote(let id, let at, let text):
            var item = Self.blankItem(id: id, kind: .note, at: at)
            item.text = text
            try putBasic(item, device: device)
            return "put note"

        case .move(let id, let to):
            guard var item = device.items[id.rawValue] else { throw WireError(.notFound, "no such item") }
            guard item.kind != .track else { throw WireError(.invalidState, "a track cannot be moved") }
            try requireKind(item.kind, device)
            item.latitude = to.latitude
            item.longitude = to.longitude
            device.items[id.rawValue] = item
            return "move"

        case .delete(let id):
            discard(id: id.rawValue, device: device)
            if device.upload?.header.id == id { device.upload = nil }
            return "delete"

        case .deleteAll:
            device.items.removeAll()
            device.upload = nil
            device.diskBytes = 0
            return "delete all"

        case .photoBegin(let header):
            try requireKind(.photo, device)
            if let existing = device.items[header.id.rawValue] {
                guard existing.kind == .photo else { throw WireError(.kindConflict, "this id is already used by another kind of item") }
            } else {
                let count = device.items.values.filter { $0.kind == .photo }.count
                guard count < options.limits.photos else { throw WireError(.limitPhotos, "the photo limit was reached") }
            }
            device.upload = Upload(header: header)
            return "photo begin"

        case .photoChunk(let id, let index, let data):
            try requireKind(.photo, device)
            guard var upload = device.upload, upload.header.id == id else {
                throw WireError(.invalidState, "no upload is in progress for this id")
            }
            guard index == upload.nextIndex else {
                device.upload = nil
                throw WireError(.invalidState, "photo chunks must arrive in order")
            }
            guard upload.received.count + data.count <= upload.header.size else {
                device.upload = nil
                throw WireError(.photoInvalid, "more data arrived than was declared")
            }
            upload.received.append(data)
            upload.nextIndex += 1
            device.upload = upload
            return "photo chunk"

        case .photoEnd:
            // Handled before this point, because decoding happens away from the actor.
            throw WireError(.internalError, "unexpected request")

        case .trackAppend(let id, let title, let points):
            try requireKind(.track, device)
            if let wait = device.pointBucket.take(points.count, now: Self.now()) {
                throw RetryLater(seconds: wait)
            }
            var item: Item
            if let existing = device.items[id.rawValue] {
                guard existing.kind == .track else { throw WireError(.kindConflict, "this id is already used by another kind of item") }
                guard !existing.isFinished else { throw WireError(.invalidState, "this track is already finished") }
                item = existing
            } else {
                let tracks = device.items.values.filter { $0.kind == .track }
                guard tracks.count < options.limits.tracks else { throw WireError(.limitTracks, "too many tracks") }
                guard tracks.filter({ !$0.isFinished }).count < options.limits.openTracks else {
                    throw WireError(.limitTracks, "too many open tracks")
                }
                item = Self.blankItem(id: id, kind: .track, at: nil)
            }
            if let title { item.title = title }
            item.points.append(contentsOf: points)
            // Oldest points go first once a track is full, so long recordings keep working.
            if item.points.count > options.limits.trackPoints {
                item.points.removeFirst(item.points.count - options.limits.trackPoints)
            }
            try store(item, device: device)
            return "track append"

        case .trackFinish(let id):
            try requireKind(.track, device)
            guard var item = device.items[id.rawValue] else { throw WireError(.notFound, "no such item") }
            guard item.kind == .track else { throw WireError(.kindConflict, "this id is not a track") }
            item.isFinished = true
            device.items[id.rawValue] = item
            return "track finish"
        }
    }
}
