import CryptoKit
@_spi(ErdigoInternal) import ErdigoCore
import Foundation
import Network

/// How long a request keeps trying while the receiver cannot be reached,
/// for example while the Mac is asleep or the app is closed.
public enum RetryPolicy: Equatable, Sendable {
    /// Fail at the first connection problem.
    case none
    /// Keep trying for this many seconds, counted from when the connection was lost.
    case upTo(TimeInterval)
    /// Keep trying until the task is cancelled.
    case forever
}

/// Writes items to one paired receiver.
///
/// Requests run strictly one at a time, in the order they were made. A request returns once the
/// receiver has acknowledged it. If the connection breaks, the request is sent again with the same
/// sequence number, so the receiver applies it at most once.
public actor ErdigoClient {
    private final class Session {
        let stream: ByteStream
        let transport: NoiseTransport
        var lastSeq: UInt64
        let welcome: Welcome

        init(stream: ByteStream, transport: NoiseTransport, welcome: Welcome) {
            self.stream = stream
            self.transport = transport
            self.welcome = welcome
            lastSeq = welcome.lastSeq
        }
    }

    /// A problem with the connection, as opposed to an answer from the receiver.
    private enum Interruption: Error {
        case connectionLost
    }

    /// How many `rate_limited` answers one request will accept before giving up.
    /// An honest receiver needs only one or two, so this only stops a receiver that never relents.
    private static let maxRateLimitedAnswers = 20
    private static let minimumRateLimitWait: TimeInterval = 0.05

    private let identity: DeviceIdentity
    private var pairing: ReceiverPairing
    private let store: any DeviceStore
    private let target: ReceiverTarget
    private let retry: RetryPolicy
    private let connectTimeout: TimeInterval
    private let ackTimeout: TimeInterval
    private var session: Session?
    private var isBusy = false
    private var waiters: [(id: UUID, continuation: CheckedContinuation<Void, Error>)] = []
    private var lastSessionAt: TimeInterval = 0

    // These outlive a connection, because the receiver's own count does too.
    private var pacedLimits: Limits?
    private var messageBucket: TokenBucket?
    private var pointBucket: TokenBucket?

    public init(
        identity: DeviceIdentity,
        pairing: ReceiverPairing,
        store: any DeviceStore,
        target: ReceiverTarget = .discover,
        retry: RetryPolicy = .upTo(30),
        connectTimeout: TimeInterval = 5,
        ackTimeout: TimeInterval = 15
    ) {
        self.identity = identity
        self.pairing = pairing
        self.store = store
        self.target = target
        self.retry = retry
        self.connectTimeout = connectTimeout
        self.ackTimeout = ackTimeout
    }

    // MARK: Public requests

    /// Connects now rather than at the first request. Throws `ErdigoError.noAnswer` if the retry policy runs out.
    public func connect() async throws {
        try await serialised {
            try await self.retrying { _ = try await self.currentSession() }
        }
    }

    /// Closes the connection. A later request connects again. Do not call this while a request
    /// is still running, because that request would then reconnect.
    public func close() {
        session?.stream.cancel()
        session = nil
    }

    /// The limits the receiver announced on the current connection.
    public var limits: Limits? { session?.welcome.limits }

    /// The item kinds the person allowed, as announced on the current connection.
    public var allowedKinds: [ItemKind]? { session?.welcome.kinds }

    public func ping() async throws {
        try await perform(.ping)
    }

    /// Creates the pin, or replaces it if this device already has one with this id.
    public func putPin(id: String, at coordinate: Coordinate, title: String? = nil) async throws {
        let itemID = try validated { try ItemID(id) }
        try validateTitle(title)
        try await perform(.putPin(id: itemID, at: coordinate, title: title), kind: .pin)
    }

    public func putMarker(id: String, at coordinate: Coordinate, title: String? = nil, color: MarkerColor? = nil) async throws {
        let itemID = try validated { try ItemID(id) }
        try validateTitle(title)
        try await perform(.putMarker(id: itemID, at: coordinate, title: title, color: color), kind: .marker)
    }

    /// A note is plain text. The receiver shows links as text and never opens them.
    public func putNote(id: String, at coordinate: Coordinate, text: String) async throws {
        let itemID = try validated { try ItemID(id) }
        try validated { try TextRules.validateNote(text) }
        try await perform(.putNote(id: itemID, at: coordinate, text: text), kind: .note)
    }

    public func move(id: String, to coordinate: Coordinate) async throws {
        let itemID = try validated { try ItemID(id) }
        try await perform(.move(id: itemID, to: coordinate))
    }

    /// Deletes one of this device's items. Deleting an id that does not exist succeeds.
    public func delete(id: String) async throws {
        let itemID = try validated { try ItemID(id) }
        try await perform(.delete(id: itemID))
    }

    /// Deletes every item this device created on this receiver, and nothing else.
    public func deleteAll() async throws {
        try await perform(.deleteAll)
    }

    /// Appends points to a track, creating it if needed. Long arrays are split into several
    /// messages, and no other request runs in between.
    public func appendTrack(id: String, points: [TrackPoint], title: String? = nil) async throws {
        let itemID = try validated { try ItemID(id) }
        try validateTitle(title)
        guard !points.isEmpty else { throw ErdigoError.invalidArgument("a track append needs at least one point") }
        try await serialised {
            var remaining = points[...]
            var pendingTitle = title
            while !remaining.isEmpty {
                let batch = Array(remaining.prefix(IntegrationV1.maxPointsPerMessage))
                remaining = remaining.dropFirst(batch.count)
                try await self.performHoldingLock(.trackAppend(id: itemID, title: pendingTitle, points: batch), kind: .track, points: batch.count)
                pendingTitle = nil
            }
        }
    }

    public func finishTrack(id: String) async throws {
        let itemID = try validated { try ItemID(id) }
        try await perform(.trackFinish(id: itemID), kind: .track)
    }

    /// Sends a JPEG or PNG photo. The format is detected from the data, and the receiver
    /// removes metadata such as EXIF location when it stores the photo.
    public func putPhoto(id: String, at coordinate: Coordinate, data: Data, title: String? = nil) async throws {
        let itemID = try validated { try ItemID(id) }
        try validateTitle(title)
        guard let type = PhotoType.detect(data) else {
            throw ErdigoError.invalidArgument("a photo must be JPEG or PNG data")
        }
        guard data.count <= Limits.v1.photoBytes else {
            throw ErdigoError.invalidArgument("a photo is at most \(Limits.v1.photoBytes) bytes")
        }

        let chunkSize = IntegrationV1.maxChunkBytes
        let chunkCount = (data.count + chunkSize - 1) / chunkSize
        let header = PhotoHeader(
            id: itemID,
            at: coordinate,
            title: title,
            type: type,
            size: data.count,
            sha256: Data(SHA256.hash(data: data)).hexString,
            chunks: chunkCount
        )
        let bytes = Data(data)

        try await serialised {
            // The receiver drops a partial upload when the connection ends, so restart from the beginning.
            try await self.retrying {
                let session = try await self.currentSession()
                guard session.welcome.kinds.contains(.photo) else { throw ErdigoError.notPermitted(.photo) }
                try await self.exchangeNext(.photoBegin(header), session: session)
                for index in 0..<chunkCount {
                    let start = index * chunkSize
                    let slice = bytes.subdata(in: start..<min(start + chunkSize, bytes.count))
                    try await self.exchangeNext(.photoChunk(id: itemID, index: index, data: slice), session: session)
                }
                try await self.exchangeNext(.photoEnd(id: itemID), session: session)
            }
        }
    }

    // MARK: Validation

    private func validateTitle(_ title: String?) throws {
        guard let title else { return }
        try validated { try TextRules.validateTitle(title) }
    }

    @discardableResult
    private func validated<T>(_ body: () throws -> T) throws -> T {
        do {
            return try body()
        } catch let error as WireError {
            throw ErdigoError.invalidArgument(error.detail)
        }
    }

    // MARK: Ordering

    private func acquire() async throws {
        try Task.checkCancellation()
        if !isBusy {
            isBusy = true
            return
        }
        let id = UUID()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                waiters.append((id, continuation))
            }
        } onCancel: {
            Task { await self.cancelWaiter(id) }
        }
    }

    private func cancelWaiter(_ id: UUID) {
        guard let index = waiters.firstIndex(where: { $0.id == id }) else { return }
        waiters.remove(at: index).continuation.resume(throwing: CancellationError())
    }

    private func release() {
        if waiters.isEmpty {
            isBusy = false
        } else {
            waiters.removeFirst().continuation.resume()
        }
    }

    private func serialised<T>(_ body: () async throws -> T) async throws -> T {
        try await acquire()
        defer { release() }
        return try await body()
    }

    // MARK: Requests

    private func perform(_ request: Request, kind: ItemKind? = nil, points: Int = 0) async throws {
        try await serialised {
            try await self.performHoldingLock(request, kind: kind, points: points)
        }
    }

    private func performHoldingLock(_ request: Request, kind: ItemKind?, points: Int) async throws {
        var assignedSeq: UInt64?
        // A resend is the same request, so it does not wait for its allowance a second time.
        var pointsArePaid = false
        try await retrying {
            let session = try await self.currentSession()
            if let kind, !session.welcome.kinds.contains(kind) { throw ErdigoError.notPermitted(kind) }
            // A request keeps its number across reconnections, so the receiver can recognise a resend.
            if let assigned = assignedSeq, session.lastSeq > assigned {
                throw ErdigoError.sequenceConflict(lastSeq: session.lastSeq)
            }
            if !pointsArePaid {
                try await self.pacePoints(points)
                pointsArePaid = true
            }
            let seq = assignedSeq ?? (session.lastSeq + 1)
            assignedSeq = seq
            try await self.exchange(request, seq: seq, session: session)
        }
    }

    /// Waits as long as asked, up to the longest wait a receiver may ask for.
    private static func sleep(seconds: TimeInterval) async throws {
        try await Task.sleep(nanoseconds: UInt64(max(0, min(seconds, 3600)) * 1_000_000_000))
    }

    private func pacePoints(_ count: Int) async throws {
        guard count > 0 else { return }
        while let wait = pointBucket?.take(count, now: Self.now()) {
            try await Self.sleep(seconds: wait)
        }
    }

    private func paceMessage() async throws {
        while let wait = messageBucket?.take(now: Self.now()) {
            try await Self.sleep(seconds: wait)
        }
    }

    private func exchangeNext(_ request: Request, session: Session) async throws {
        try await exchange(request, seq: session.lastSeq + 1, session: session)
    }

    /// Sends one request and waits for its answer, honouring the receiver's rate limit.
    private func exchange(_ request: Request, seq: UInt64, session: Session) async throws {
        guard seq <= IntegrationV1.maxSequence else {
            throw ErdigoError.invalidArgument("the sequence number is exhausted; remove this device on the receiver and pair it again")
        }
        let plaintext = Wire.encode(RequestEnvelope(seq: seq, request: request))
        var rateLimitedAnswers = 0
        while true {
            try await paceMessage()

            let answer: ReceiverMessage
            do {
                let sealed = try session.transport.seal(plaintext)
                session.stream.armDeadline(ackTimeout)
                try await session.stream.sendFrame(sealed)
                let frame = try await session.stream.readFrame()
                session.stream.disarmDeadline()
                answer = try Wire.decodeReceiverMessage(try session.transport.open(frame))
            } catch let error as WireError {
                dropSession()
                throw ErdigoError.protocolViolation(error.detail)
            } catch {
                dropSession()
                if error is CancellationError { throw error }
                throw Interruption.connectionLost
            }

            switch answer {
            case .ack(let acknowledged, _):
                guard acknowledged == seq else {
                    dropSession()
                    throw ErdigoError.protocolViolation("the acknowledgement has the wrong sequence number")
                }
                session.lastSeq = seq
                return
            case .error(let error) where error.code == ErrorCode.rateLimited.rawValue:
                rateLimitedAnswers += 1
                guard rateLimitedAnswers <= Self.maxRateLimitedAnswers else {
                    dropSession()
                    throw ErdigoError.rejected(error)
                }
                let wait = Double(error.retryAfterMS ?? 1000) / 1000
                try await Self.sleep(seconds: max(wait, Self.minimumRateLimitWait))
            case .error(let error):
                try handle(error, seq: seq, session: session)
                return
            default:
                dropSession()
                throw ErdigoError.protocolViolation("unexpected message in answer to a request")
            }
        }
    }

    private func handle(_ error: ReceiverError, seq: UInt64, session: Session) throws {
        if let last = error.lastSeq { session.lastSeq = max(session.lastSeq, last) }

        switch error.code {
        case ErrorCode.paused.rawValue:
            dropSession()
            throw Interruption.connectionLost
        case ErrorCode.replaced.rawValue:
            dropSession()
            throw ErdigoError.replaced
        case ErrorCode.staleSeq.rawValue, ErrorCode.seqConflict.rawValue:
            if error.fatal { dropSession() }
            throw ErdigoError.sequenceConflict(lastSeq: session.lastSeq)
        default:
            if error.fatal {
                dropSession()
            } else if error.seq == seq, let code = ErrorCode(rawValue: error.code), !code.isRetryable {
                // The receiver used up this number on the failed request.
                session.lastSeq = seq
            }
            throw ErdigoError.rejected(error)
        }
    }

    // MARK: Connection

    private func dropSession() {
        session?.stream.cancel()
        session = nil
    }

    private static func now() -> TimeInterval {
        ProcessInfo.processInfo.systemUptime
    }

    private func currentSession() async throws -> Session {
        if let session { return session }

        // The remembered address comes first, because looking around the network takes a moment.
        if let created = try await firstAnswer(from: try Discovery.knownEndpoints(target: target, pairing: pairing)) {
            return created
        }
        if target == .discover, let created = try await firstAnswer(from: await Discovery.browse()) {
            return created
        }
        throw Interruption.connectionLost
    }

    /// Tries each endpoint in turn. A receiver that stays silent is simply not the right one.
    private func firstAnswer(from endpoints: [NWEndpoint]) async throws -> Session? {
        for endpoint in endpoints {
            do {
                let created = try await handshake(endpoint: endpoint)
                session = created
                lastSessionAt = Self.now()
                preparePacing(for: created.welcome.limits)
                try rememberAddress(of: created.stream)
                return created
            } catch Interruption.connectionLost {
                continue
            }
        }
        return nil
    }

    private func preparePacing(for limits: Limits) {
        guard pacedLimits != limits else { return }
        pacedLimits = limits
        let now = Self.now()
        messageBucket = TokenBucket(capacity: limits.messageBurst, refillPerSecond: limits.messagesPerSecond, now: now)
        pointBucket = TokenBucket(capacity: limits.pointBurst, refillPerSecond: limits.pointsPerSecond, now: now)
    }

    private func rememberAddress(of stream: ByteStream) throws {
        guard let address = stream.remoteAddress else { return }
        guard pairing.lastHost != address.host || pairing.lastPort != address.port else { return }
        pairing.lastHost = address.host
        pairing.lastPort = address.port
        try store.updateAddress(receiverFingerprint: pairing.fingerprint, host: address.host, port: address.port)
    }

    private func handshake(endpoint: NWEndpoint) async throws -> Session {
        let stream = ByteStream(endpoint: endpoint)
        do {
            try await stream.start(timeout: connectTimeout)
            let prologue = IntegrationV1.preamble(.connect)
            let handshake = try NoiseHandshake(
                pattern: .ik,
                isInitiator: true,
                prologue: prologue,
                staticKey: identity.privateKey,
                remoteStaticKey: pairing.receiverKey
            )
            // Written to disk first: the receiver ignores a counter it has already seen.
            let counter = try store.nextConnectCounter(
                receiverFingerprint: pairing.fingerprint,
                nowMS: UInt64(Date().timeIntervalSince1970 * 1000)
            )
            guard counter <= IntegrationV1.maxSequence else {
                throw ErdigoError.storage("this device key has used up its connection counter. Create a new key and pair again.")
            }
            var counterBytes = Data()
            withUnsafeBytes(of: counter.bigEndian) { counterBytes.append(contentsOf: $0) }
            let first = try handshake.writeMessage(payload: counterBytes)

            stream.armDeadline(connectTimeout)
            try await stream.send(prologue + ByteStream.frame(first))
            let second = try await stream.readFrame(maxLength: IntegrationV1.maxHandshakeFrameLength)
            guard try handshake.readMessage(second).isEmpty else {
                throw ErdigoError.protocolViolation("the handshake reply has an unexpected payload")
            }
            let transport = try handshake.split()

            let hello = Hello(versions: [IntegrationV1.version], sdk: Pairing.sdkName)
            try await stream.sendFrame(try transport.seal(Wire.encode(hello)))
            let frame = try await stream.readFrame()
            stream.disarmDeadline()

            switch try Wire.decodeReceiverMessage(try transport.open(frame)) {
            case .welcome(let welcome):
                guard welcome.version == IntegrationV1.version else {
                    throw ErdigoError.protocolViolation("the receiver chose a version that was not offered")
                }
                return Session(stream: stream, transport: transport, welcome: welcome)
            case .error(let error) where error.code == ErrorCode.versionUnsupported.rawValue:
                throw ErdigoError.versionUnsupported(supported: error.supported ?? [])
            case .error(let error) where error.code == ErrorCode.paused.rawValue:
                throw Interruption.connectionLost
            case .error(let error):
                throw ErdigoError.rejected(error)
            default:
                throw ErdigoError.protocolViolation("the receiver did not send welcome")
            }
        } catch {
            stream.cancel()
            if error is CancellationError { throw error }
            if let error = error as? ErdigoError { throw error }
            if let error = error as? WireError { throw ErdigoError.protocolViolation(error.detail) }
            throw Interruption.connectionLost
        }
    }

    /// Runs `body` again after connection problems until the retry policy runs out.
    /// The time allowed counts from the first failure, so a long paced wait does not use it up.
    private func retrying(_ body: () async throws -> Void) async throws {
        var outageStart: TimeInterval?
        var attempt = 0
        while true {
            do {
                try await body()
                return
            } catch Interruption.connectionLost {
                try Task.checkCancellation()
                let now = Self.now()
                // Having connected since then, this is a new outage.
                if let begun = outageStart, lastSessionAt > begun {
                    outageStart = nil
                    attempt = 0
                }
                let start = outageStart ?? now
                outageStart = start
                var delay = Self.backoff(attempt: attempt)
                attempt += 1
                switch retry {
                case .none:
                    throw ErdigoError.noAnswer
                case .upTo(let seconds):
                    // Use all the time allowed, with a last try at the end of it.
                    let remaining = seconds - (now - start)
                    guard remaining > 0 else { throw ErdigoError.noAnswer }
                    delay = min(delay, remaining)
                case .forever:
                    break
                }
                try await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            }
        }
    }

    /// 1, 2, 4 ... up to 60 seconds, with 20 percent jitter so several devices do not retry in step.
    static func backoff(attempt: Int) -> TimeInterval {
        let base = min(60.0, pow(2.0, Double(min(attempt, 6))))
        return base * Double.random(in: 0.8...1.2)
    }
}
