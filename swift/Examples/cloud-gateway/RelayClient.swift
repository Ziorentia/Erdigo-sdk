import ErdigoSDK
import Foundation

/// Fetches from a relay, as docs/cloud-relay.md describes `GET /v1/ops`, and checks every answer strictly.
final class RelayClient: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    /// Far more than the 4 MiB a relay puts in one answer, plus one large photo.
    static let maxAnswerBytes = 8 * 1024 * 1024
    static let waitSeconds = 25
    static let maxSeq: UInt64 = (1 << 53) - 1

    struct Entry {
        let seq: UInt64
        let op: JSONValue
    }

    struct Answer {
        let epoch: String
        let reset: Bool
        let missed: Bool
        let entries: [Entry]
    }

    enum Problem: Error {
        /// No answer, a timeout or a server error. Worth trying again later.
        case unreachable
        case refusedToken
        /// The answer does not follow the relay API. The reason holds nothing from the answer itself.
        case brokeAPI(String)
    }

    private struct Pending {
        var data = Data()
        var tooLarge = false
        var timedOut = false
        let continuation: CheckedContinuation<(HTTPURLResponse, Data), Error>
    }

    private let base: String
    private let token: String
    private let lock = NSLock()
    private var pending: [Int: Pending] = [:]
    private var session: URLSession!

    /// `idleSeconds` is how long a fetch may go without a byte arriving.
    init(base: String, token: String, idleSeconds: TimeInterval) {
        self.base = base
        self.token = token
        super.init()
        let configuration = URLSessionConfiguration.ephemeral
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.waitsForConnectivity = false
        configuration.timeoutIntervalForRequest = idleSeconds
        configuration.timeoutIntervalForResource = 90
        session = URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }

    /// `limit` cuts the fetch short after that many seconds, as if the relay could not be reached.
    func fetch(epoch: String?, after: UInt64, wait: Int, limit: TimeInterval? = nil) async throws -> Answer {
        var query = "after=\(after)&limit=100&wait=\(wait)"
        if let epoch { query = "epoch=\(epoch)&" + query }
        guard let url = URL(string: "\(base)/v1/ops?\(query)") else { throw Problem.brokeAPI("the relay address cannot be used") }
        var request = URLRequest(url: url)
        request.httpMethod = "GET"
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (response, body) = try await get(request, limit: limit)
        switch response.statusCode {
        case 200:
            break
        case 401:
            throw Problem.refusedToken
        case 408, 429, 500...599:
            throw Problem.unreachable
        default:
            throw Problem.brokeAPI("it answered with HTTP status \(response.statusCode)")
        }
        let type = (response.value(forHTTPHeaderField: "Content-Type") ?? "").lowercased()
        guard type.split(separator: ";").first?.trimmingCharacters(in: .whitespaces) == "application/json" else {
            throw Problem.brokeAPI("the answer is not application/json")
        }
        return try Self.check(body, sentEpoch: epoch, after: after)
    }

    /// Everything the guide promises about an answer. An operation's own fields are left to the caller,
    /// which refuses one it cannot use without stopping.
    static func check(_ body: Data, sentEpoch: String?, after: UInt64) throws -> Answer {
        // Deeper than any valid operation needs, so that a bad operation is refused alone rather than the whole answer.
        guard let value = try? StrictJSON.parse(body, maxDepth: 16), case .object(let top) = value else {
            throw Problem.brokeAPI("the answer is not one JSON object")
        }
        guard Set(top.keys) == ["epoch", "reset", "missed", "ops"] else {
            throw Problem.brokeAPI("the answer does not have exactly epoch, reset, missed and ops")
        }
        guard case .string(let epoch)? = top["epoch"], isValidEpoch(epoch) else {
            throw Problem.brokeAPI("the epoch is not 16 to 64 letters, digits, '-' or '_'")
        }
        guard case .bool(let reset)? = top["reset"], case .bool(let missed)? = top["missed"] else {
            throw Problem.brokeAPI("reset and missed must be true or false")
        }
        // A matching epoch is never a reset, and anything else always is.
        guard reset == (sentEpoch == nil || sentEpoch != epoch) else {
            throw Problem.brokeAPI(reset ? "it reset although the epoch matched" : "it did not reset although the epoch changed")
        }
        guard case .array(let list)? = top["ops"], list.count <= 100 else {
            throw Problem.brokeAPI("ops must be a list of at most 100 operations")
        }
        var entries: [Entry] = []
        var previous = reset ? 0 : after
        for item in list {
            guard case .object(let entry) = item, Set(entry.keys) == ["seq", "received_ms", "op"] else {
                throw Problem.brokeAPI("an entry does not have exactly seq, received_ms and op")
            }
            guard case .integer(let seq)? = entry["seq"], seq > 0, UInt64(seq) <= maxSeq, UInt64(seq) > previous else {
                throw Problem.brokeAPI(reset ? "the sequence numbers do not go up" : "the sequence numbers do not go up from 'after'")
            }
            guard case .integer(let received)? = entry["received_ms"], received >= 0 else {
                throw Problem.brokeAPI("received_ms must be a whole number")
            }
            guard let op = entry["op"], case .object = op else { throw Problem.brokeAPI("an operation is not an object") }
            previous = UInt64(seq)
            entries.append(Entry(seq: UInt64(seq), op: op))
        }
        return Answer(epoch: epoch, reset: reset, missed: missed, entries: entries)
    }

    static func isValidEpoch(_ text: String) -> Bool {
        (16...64).contains(text.utf8.count) && text.utf8.allSatisfy { byte in
            switch byte {
            case UInt8(ascii: "A")...UInt8(ascii: "Z"), UInt8(ascii: "a")...UInt8(ascii: "z"),
                 UInt8(ascii: "0")...UInt8(ascii: "9"), UInt8(ascii: "-"), UInt8(ascii: "_"):
                return true
            default:
                return false
            }
        }
    }

    // MARK: Transport

    private func get(_ request: URLRequest, limit: TimeInterval?) async throws -> (HTTPURLResponse, Data) {
        let task = session.dataTask(with: request)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                lock.lock()
                pending[task.taskIdentifier] = Pending(continuation: continuation)
                lock.unlock()
                if let limit {
                    DispatchQueue.global().asyncAfter(deadline: .now() + max(0, limit)) { [weak self] in self?.timeOut(task) }
                }
                task.resume()
            }
        } onCancel: {
            task.cancel()
        }
    }

    private func timeOut(_ task: URLSessionTask) {
        lock.lock()
        let waiting = pending[task.taskIdentifier] != nil
        if waiting { pending[task.taskIdentifier]?.timedOut = true }
        lock.unlock()
        if waiting { task.cancel() }
    }

    private func finish(_ task: URLSessionTask, _ result: Result<(HTTPURLResponse, Data), Error>) {
        lock.lock()
        let entry = pending.removeValue(forKey: task.taskIdentifier)
        lock.unlock()
        entry?.continuation.resume(with: result)
    }

    /// A redirect is not followed: the token is meant for the relay alone.
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        if response.expectedContentLength > Int64(Self.maxAnswerBytes) {
            markTooLarge(dataTask)
            return completionHandler(.cancel)
        }
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        var over = false
        if var entry = pending[dataTask.taskIdentifier] {
            entry.data.append(data)
            over = entry.data.count > Self.maxAnswerBytes
            if over { entry.tooLarge = true }
            pending[dataTask.taskIdentifier] = entry
        }
        lock.unlock()
        if over { dataTask.cancel() }
    }

    private func markTooLarge(_ task: URLSessionTask) {
        lock.lock()
        pending[task.taskIdentifier]?.tooLarge = true
        lock.unlock()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        let entry = pending[task.taskIdentifier]
        lock.unlock()
        guard let entry else { return }
        if entry.tooLarge {
            return finish(task, .failure(Problem.brokeAPI("the answer is larger than \(Self.maxAnswerBytes) bytes")))
        }
        if entry.timedOut { return finish(task, .failure(Problem.unreachable)) }
        if let error {
            if (error as? URLError)?.code == .cancelled { return finish(task, .failure(CancellationError())) }
            return finish(task, .failure(Problem.unreachable))
        }
        guard let response = task.response as? HTTPURLResponse else { return finish(task, .failure(Problem.unreachable)) }
        finish(task, .success((response, entry.data)))
    }
}
