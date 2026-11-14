import Foundation
import Network

/// Resumes a continuation at most once, whichever callback gets there first.
public final class OnceContinuation<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<T, Error>?

    public init(_ continuation: CheckedContinuation<T, Error>) {
        self.continuation = continuation
    }

    public func finish(_ result: Result<T, Error>) {
        lock.lock()
        let pending = continuation
        continuation = nil
        lock.unlock()
        pending?.resume(with: result)
    }
}

/// An ordered byte stream over TCP with the integration-v1 framing:
/// a two-byte big-endian length followed by one Noise message.
///
/// One task at a time may read, and one task at a time may write.
public final class ByteStream: @unchecked Sendable {
    public enum Failure: Error, Equatable, Sendable {
        case closed
        case timedOut
        case frameTooLarge
        case connectionFailed
    }

    /// Identifies this stream in a receiver's bookkeeping.
    public let identifier = UUID()
    private let connection: NWConnection
    private let queue = DispatchQueue(label: "erdigo.bytestream")
    private let lock = NSLock()
    private var buffer = Data()
    private var deadlineExpired = false
    private var deadlineGeneration = 0

    public init(connection: NWConnection) {
        self.connection = connection
    }

    deinit {
        // A connection left running would otherwise stay open until the peer closes it.
        connection.cancel()
    }

    public convenience init(endpoint: NWEndpoint) {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        self.init(connection: NWConnection(to: endpoint, using: NWParameters(tls: nil, tcp: tcp)))
    }

    public convenience init(host: String, port: UInt16) throws {
        guard let nwPort = NWEndpoint.Port(rawValue: port), port != 0 else { throw Failure.connectionFailed }
        self.init(endpoint: .hostPort(host: NWEndpoint.Host(host), port: nwPort))
    }

    /// Waits until the connection is usable, or fails. A connection the network layer would
    /// keep retrying counts as failed, so that callers can apply their own retry policy.
    public func start(timeout: TimeInterval) async throws {
        let connection = self.connection
        let queue = self.queue
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            let once = OnceContinuation(continuation)
            // Weak, because the connection keeps this handler for life, and a strong
            // reference back would keep every connection ever made in memory.
            connection.stateUpdateHandler = { [weak connection] state in
                switch state {
                case .ready:
                    once.finish(.success(()))
                case .waiting, .failed:
                    once.finish(.failure(Failure.connectionFailed))
                    connection?.cancel()
                case .cancelled:
                    once.finish(.failure(Failure.closed))
                default:
                    break
                }
            }
            connection.start(queue: queue)
            queue.asyncAfter(deadline: .now() + timeout) {
                once.finish(.failure(Failure.timedOut))
                if connection.state != .ready { connection.cancel() }
            }
        }
    }

    /// Cancels the connection unless `disarmDeadline` is called first. Pending reads then fail with `timedOut`.
    public func armDeadline(_ seconds: TimeInterval) {
        lock.lock()
        deadlineGeneration += 1
        let generation = deadlineGeneration
        lock.unlock()
        queue.asyncAfter(deadline: .now() + seconds) { [weak self] in
            guard let self else { return }
            self.lock.lock()
            let isCurrent = generation == self.deadlineGeneration
            if isCurrent { self.deadlineExpired = true }
            self.lock.unlock()
            if isCurrent { self.connection.cancel() }
        }
    }

    public func disarmDeadline() {
        lock.lock()
        deadlineGeneration += 1
        lock.unlock()
    }

    public func cancel() {
        connection.cancel()
    }

    private func mapFailure() -> Failure {
        lock.lock()
        defer { lock.unlock() }
        return deadlineExpired ? .timedOut : .closed
    }

    public func send(_ data: Data) async throws {
        let connection = self.connection
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                connection.send(content: data, completion: .contentProcessed { [weak self] error in
                    if error != nil {
                        continuation.resume(throwing: self?.mapFailure() ?? Failure.closed)
                    } else {
                        continuation.resume()
                    }
                })
            }
        } onCancel: {
            connection.cancel()
        }
    }

    private func receiveSome() async throws -> Data {
        let connection = self.connection
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Data, Error>) in
                connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
                    if let data, !data.isEmpty {
                        continuation.resume(returning: data)
                    } else if error != nil || isComplete {
                        continuation.resume(throwing: self?.mapFailure() ?? Failure.closed)
                    } else {
                        continuation.resume(returning: Data())
                    }
                }
            }
        } onCancel: {
            connection.cancel()
        }
    }

    /// Bytes received but not yet read. After a failed read, this says whether the peer sent anything.
    public var bufferedByteCount: Int { buffer.count }

    public func read(exactly count: Int) async throws -> Data {
        while buffer.count < count {
            buffer.append(try await receiveSome())
        }
        let head = Data(buffer.prefix(count))
        buffer = Data(buffer.dropFirst(count))
        return head
    }

    /// Reads one frame and returns its body. Refuses a frame longer than `maxLength` before reading it.
    public func readFrame(maxLength: Int = IntegrationV1.maxFrameLength) async throws -> Data {
        let header = try await read(exactly: 2)
        let length = Int(header[header.startIndex]) << 8 | Int(header[header.startIndex + 1])
        guard length <= maxLength else { throw Failure.frameTooLarge }
        return try await read(exactly: length)
    }

    public static func frame(_ body: Data) throws -> Data {
        guard body.count <= IntegrationV1.maxFrameLength else { throw Failure.frameTooLarge }
        var output = Data([UInt8(body.count >> 8), UInt8(body.count & 0xFF)])
        output.append(body)
        return output
    }

    public func sendFrame(_ body: Data) async throws {
        try await send(ByteStream.frame(body))
    }

    /// The peer's address, for per-address accounting and for remembering where a receiver was found.
    public var remoteAddress: (host: String, port: UInt16)? {
        let endpoint = connection.currentPath?.remoteEndpoint ?? connection.endpoint
        guard case .hostPort(let host, let port) = endpoint else { return nil }
        var text = "\(host)"
        if let percent = text.firstIndex(of: "%") { text = String(text[..<percent]) }
        return (text, port.rawValue)
    }
}
