import CryptoKit
import ErdigoSDK
import Foundation
import Network

/// The relay API of docs/cloud-relay.md over plain HTTP/1.1, one request per connection.
///
/// Everything that touches the queue or a connection runs on `queue`. Only parsing a body runs
/// elsewhere, so that a large body does not hold up other requests.
final class RelayServer {
    static let maxBodyBytes = 2 * 1024 * 1024
    static let maxHeaderBytes = 16 * 1024
    static let maxHeaderLines = 64
    static let maxOperationsPerPost = 100
    static let maxConnections = 128
    static let maxWaiting = 16
    static let maxWaitSeconds = 30
    /// How long a client has to send its request line and headers.
    static let headerSeconds: Double = 10
    /// How long a body may go without a byte arriving, and how long it may take in all.
    static let bodyIdleSeconds: Double = 30
    static let bodySeconds: Double = 120

    enum Role {
        case source, gateway
    }

    let queue = DispatchQueue(label: "erdigo.relay")
    private let parsing = DispatchQueue(label: "erdigo.relay.parsing", attributes: .concurrent)
    private let store: RelayQueue
    private let sourceDigests: [Data]
    private let gatewayDigest: Data
    private var listener: NWListener?
    private var exchanges: [ObjectIdentifier: Exchange] = [:]
    private var waiting: [ObjectIdentifier: (exchange: Exchange, query: Query)] = [:]
    private let timestamps: ISO8601DateFormatter = {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime]
        return formatter
    }()

    init(sourceTokens: [String], gatewayToken: String, limits: RelayQueue.Limits) {
        store = RelayQueue(limits: limits)
        sourceDigests = sourceTokens.map(Self.digest)
        gatewayDigest = Self.digest(gatewayToken)
    }

    // MARK: Listening

    func start(host: String, port: UInt16, ready: @escaping (Result<UInt16, Error>) -> Void) throws {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: nil, tcp: tcp)
        parameters.allowLocalEndpointReuse = true
        parameters.requiredLocalEndpoint = .hostPort(
            host: NWEndpoint.Host(host),
            port: port == 0 ? .any : NWEndpoint.Port(rawValue: port) ?? .any
        )
        let listener = try NWListener(using: parameters)
        var reported = false
        listener.stateUpdateHandler = { state in
            guard !reported else { return }
            switch state {
            case .ready:
                reported = true
                ready(.success(listener.port?.rawValue ?? port))
            case .failed(let error), .waiting(let error):
                reported = true
                ready(.failure(error))
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return connection.cancel() }
            guard self.exchanges.count < Self.maxConnections else { return connection.cancel() }
            let exchange = Exchange(connection: connection, server: self)
            self.exchanges[ObjectIdentifier(exchange)] = exchange
            exchange.start()
        }
        self.listener = listener
        listener.start(queue: queue)
    }

    func stop() {
        listener?.cancel()
        listener = nil
        for exchange in exchanges.values { exchange.close() }
        exchanges.removeAll()
        waiting.removeAll()
    }

    fileprivate func forget(_ exchange: Exchange) {
        exchanges[ObjectIdentifier(exchange)] = nil
        waiting[ObjectIdentifier(exchange)] = nil
    }

    fileprivate func stopWaiting(_ exchange: Exchange) {
        waiting[ObjectIdentifier(exchange)] = nil
    }

    // MARK: Requests

    /// Called once the request line and headers are in. Checks run in the order the guide gives.
    fileprivate func route(_ exchange: Exchange, _ request: Request) {
        let allowed: [String]
        switch request.path {
        case "/v1/ops": allowed = ["GET", "POST"]
        case "/v1/health": allowed = ["GET"]
        default: return exchange.respond(404, error: "not_found")
        }
        guard allowed.contains(request.method) else {
            return exchange.respond(405, error: "method_not_allowed", headers: [("Allow", allowed.joined(separator: ", "))])
        }
        if request.path == "/v1/health" {
            return exchange.respond(200, body: Data(#"{"ok":true}"#.utf8))
        }
        guard isAuthorized(request, as: request.method == "POST" ? .source : .gateway) else {
            return exchange.respond(401, error: "unauthorized", headers: [("WWW-Authenticate", "Bearer")])
        }
        if request.method == "GET" { return fetch(exchange, request) }

        guard Self.isJSON(request.values("content-type")) else { return exchange.respond(415, error: "unsupported_media_type") }
        let lengths = request.values("content-length")
        // Without a length the body could only be read as chunks, which this relay does not do.
        guard request.values("transfer-encoding").isEmpty, !lengths.isEmpty else { return exchange.respond(411, error: "length_required") }
        guard lengths.count == 1, !lengths[0].isEmpty, lengths[0].utf8.allSatisfy({ (UInt8(ascii: "0")...UInt8(ascii: "9")).contains($0) }) else {
            return exchange.respond(400, error: "bad_request")
        }
        guard let length = Self.wholeNumber(lengths[0], max: UInt64(Self.maxBodyBytes)) else { return exchange.respond(413, error: "too_large") }
        exchange.readBody(length: Int(length), sendContinue: request.values("expect").contains { $0.lowercased() == "100-continue" }) { [weak self] body in
            self?.send(exchange, body)
        }
    }

    private func send(_ exchange: Exchange, _ body: Data) {
        parsing.async {
            let outcome = Self.parseBatch(body)
            self.queue.async {
                switch outcome {
                case .failure(let refusal):
                    exchange.respond(400, body: refusal.body)
                case .success(let batch):
                    switch self.store.take(batch, receivedMS: Int64(Date().timeIntervalSince1970 * 1000)) {
                    case .full:
                        exchange.respond(503, error: "queue_full", headers: [("Retry-After", "30")])
                    case .accepted(let count, let lastSeq):
                        exchange.respond(200, body: Data(#"{"accepted":\#(count),"last_seq":\#(lastSeq)}"#.utf8), note: "\(count) operation\(count == 1 ? "" : "s")")
                        self.wakeWaiting()
                    }
                }
            }
        }
    }

    struct Refusal: Error {
        let body: Data
    }

    static func parseBatch(_ body: Data) -> Result<[Data], Refusal> {
        func refuse(_ fields: [(String, JSONWriter.Value)]) -> Result<[Data], Refusal> {
            .failure(Refusal(body: JSONWriter.encode(.object(fields))))
        }
        // Deep enough for any valid body; deeper nesting is refused before it can exhaust the stack.
        guard let value = try? StrictJSON.parse(body, maxDepth: 32), case .object(let top) = value else {
            return refuse([("error", .string("invalid_json"))])
        }
        guard top.count == 1, case .array(let list)? = top["ops"] else {
            return refuse([("error", .string("invalid_body")), ("reason", .string("the body must be {\"ops\":[...]} and nothing else"))])
        }
        guard (1...maxOperationsPerPost).contains(list.count) else {
            return refuse([("error", .string("invalid_body")), ("reason", .string("ops must hold 1 to \(maxOperationsPerPost) operations"))])
        }
        var batch: [Data] = []
        for (index, op) in list.enumerated() {
            do {
                batch.append(try Operations.validate(op))
            } catch let invalid as Operations.Invalid {
                return refuse([("error", .string("invalid_op")), ("index", .integer(Int64(index))), ("reason", .string(invalid.reason))])
            } catch {
                return refuse([("error", .string("invalid_op")), ("index", .integer(Int64(index))), ("reason", .string("not a valid operation"))])
            }
        }
        return .success(batch)
    }

    struct Query {
        var epoch: String?
        var after: UInt64 = 0
        var limit = 100
        var wait = 0
    }

    static func parseQuery(_ text: String?) -> Query? {
        var query = Query()
        guard let text, !text.isEmpty else { return query }
        var seen = Set<String>()
        for piece in text.split(separator: "&", omittingEmptySubsequences: false) {
            let parts = piece.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
            guard parts.count == 2, seen.insert(parts[0]).inserted else { return nil }
            switch parts[0] {
            case "epoch":
                guard RelayQueue.isValidEpoch(parts[1]) else { return nil }
                query.epoch = parts[1]
            case "after":
                guard let value = wholeNumber(parts[1], max: (1 << 53) - 1) else { return nil }
                query.after = value
            case "limit":
                guard let value = wholeNumber(parts[1], max: 100), value >= 1 else { return nil }
                query.limit = Int(value)
            case "wait":
                guard let value = wholeNumber(parts[1], max: UInt64(maxWaitSeconds)) else { return nil }
                query.wait = Int(value)
            default:
                return nil
            }
        }
        return query
    }

    private func fetch(_ exchange: Exchange, _ request: Request) {
        guard let query = Self.parseQuery(request.query), let answer = store.fetch(epoch: query.epoch, after: query.after, limit: query.limit) else {
            return exchange.respond(400, error: "invalid_query")
        }
        if answer.count > 0 || query.wait == 0 || waiting.count >= Self.maxWaiting {
            return exchange.respond(200, body: answer.body, note: "\(answer.count) operation\(answer.count == 1 ? "" : "s")")
        }
        waiting[ObjectIdentifier(exchange)] = (exchange, query)
        exchange.wait(seconds: Double(query.wait)) { [weak self] in
            self?.answerWaiting(exchange, evenIfEmpty: true)
        }
    }

    private func answerWaiting(_ exchange: Exchange, evenIfEmpty: Bool) {
        guard let query = waiting[ObjectIdentifier(exchange)]?.query else { return }
        guard let answer = store.fetch(epoch: query.epoch, after: query.after, limit: query.limit) else {
            waiting[ObjectIdentifier(exchange)] = nil
            return exchange.respond(400, error: "invalid_query")
        }
        guard answer.count > 0 || evenIfEmpty else { return }
        waiting[ObjectIdentifier(exchange)] = nil
        exchange.respond(200, body: answer.body, note: "\(answer.count) operation\(answer.count == 1 ? "" : "s")")
    }

    private func wakeWaiting() {
        for entry in waiting.values { answerWaiting(entry.exchange, evenIfEmpty: false) }
    }

    // MARK: Checks

    private static func digest(_ token: String) -> Data {
        Data(SHA256.hash(data: Data(token.utf8)))
    }

    /// Compares digests, so that the time taken says nothing about a token's length or how much of it matched.
    private func isAuthorized(_ request: Request, as role: Role) -> Bool {
        let values = request.values("authorization")
        guard values.count == 1 else { return false }
        let value = values[0]
        guard value.utf8.count > 7, value.prefix(7).lowercased() == "bearer " else { return false }
        let presented = [UInt8](Self.digest(value.dropFirst(7).trimmingCharacters(in: .whitespaces)))
        var found: UInt8 = 0
        for candidate in role == .source ? sourceDigests : [gatewayDigest] {
            var difference: UInt8 = 0
            for (index, byte) in candidate.enumerated() { difference |= byte ^ presented[index] }
            found |= difference == 0 ? 1 : 0
        }
        return found == 1
    }

    /// `application/json`, with at most a `charset=utf-8` parameter.
    private static func isJSON(_ values: [String]) -> Bool {
        guard values.count == 1 else { return false }
        let parts = values[0].split(separator: ";", omittingEmptySubsequences: false).map { $0.trimmingCharacters(in: .whitespaces).lowercased() }
        guard parts[0] == "application/json" else { return false }
        return parts.dropFirst().allSatisfy { $0 == "charset=utf-8" || $0 == "charset=\"utf-8\"" }
    }

    static func wholeNumber(_ text: String, max: UInt64) -> UInt64? {
        guard (1...19).contains(text.utf8.count), text.utf8.allSatisfy({ (UInt8(ascii: "0")...UInt8(ascii: "9")).contains($0) }),
              let value = UInt64(text), value <= max
        else { return nil }
        return value
    }

    fileprivate func log(_ request: Request?, status: Int?, note: String?) {
        // Never the query, the headers or the body: tokens and operations stay out of the log.
        let method = request.map { ["GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH"].contains($0.method) ? $0.method : "OTHER" } ?? "-"
        let path = request.map { ["/v1/ops", "/v1/health"].contains($0.path) ? $0.path : "(other)" } ?? "-"
        print("\(timestamps.string(from: Date())) \(method) \(path) \(status.map(String.init) ?? "-")\(note.map { " (\($0))" } ?? "")")
    }
}

/// A parsed request line and headers.
struct Request {
    let method: String
    let path: String
    let query: String?
    let headers: [(name: String, value: String)]

    func values(_ name: String) -> [String] {
        headers.filter { $0.name == name }.map(\.value)
    }

    /// nil for anything that is not a plain HTTP/1.x request in origin form.
    init?(_ head: Data) {
        guard let text = String(data: head, encoding: .isoLatin1) else { return nil }
        let lines = text.components(separatedBy: "\r\n")
        guard lines.count <= RelayServer.maxHeaderLines + 1, let first = lines.first else { return nil }
        let parts = first.split(separator: " ", omittingEmptySubsequences: false).map(String.init)
        guard parts.count == 3, ["HTTP/1.1", "HTTP/1.0"].contains(parts[2]),
              !parts[0].isEmpty, parts[0].utf8.count <= 16, parts[0].utf8.allSatisfy({ (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains($0) }),
              parts[1].hasPrefix("/"), parts[1].utf8.allSatisfy({ (0x21...0x7E).contains($0) }), !parts[1].contains("#")
        else { return nil }
        method = parts[0]
        if let mark = parts[1].firstIndex(of: "?") {
            path = String(parts[1][..<mark])
            query = String(parts[1][parts[1].index(after: mark)...])
        } else {
            path = parts[1]
            query = nil
        }
        var headers: [(String, String)] = []
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { return nil }
            let name = String(line[..<colon])
            let tokenCharacters = Set("!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ".utf8)
            guard !name.isEmpty, name.utf8.allSatisfy(tokenCharacters.contains) else { return nil }
            let value = String(line[line.index(after: colon)...])
            guard !value.contains("\r"), !value.contains("\n"), !value.contains("\0") else { return nil }
            headers.append((name.lowercased(), value.trimmingCharacters(in: CharacterSet(charactersIn: " \t"))))
        }
        self.headers = headers
    }
}

/// One connection, carrying one request and its answer.
private final class Exchange {
    private enum Phase {
        case head
        case body(length: Int, then: (Data) -> Void)
        case working
        case waiting
        case finishing
        case closed
    }

    private let connection: NWConnection
    private unowned let server: RelayServer
    private var buffer = Data()
    private var bodyStart = 0
    private var phase = Phase.head
    private var request: Request?
    private var timer: DispatchSourceTimer?
    private var bodyDeadline: DispatchTime = .distantFuture
    private var sent = false
    private var peerFinished = false

    init(connection: NWConnection, server: RelayServer) {
        self.connection = connection
        self.server = server
    }

    func start() {
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed, .cancelled: self?.close()
            default: break
            }
        }
        connection.start(queue: server.queue)
        arm(after: RelayServer.headerSeconds) { [weak self] in self?.respond(408, error: "timeout") }
        receive()
    }

    private func arm(after seconds: Double, _ handler: @escaping () -> Void) {
        timer?.cancel()
        let timer = DispatchSource.makeTimerSource(queue: server.queue)
        timer.schedule(deadline: min(.now() + seconds, bodyDeadline))
        timer.setEventHandler(handler: handler)
        timer.resume()
        self.timer = timer
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, isComplete, error in
            guard let self else { return }
            if let data, !data.isEmpty { self.received(data) }
            if error != nil { return self.close() }
            guard isComplete else { return self.receive() }
            self.peerFinished = true
            switch self.phase {
            case .working:
                // The request is complete and its answer is on the way.
                break
            case .finishing:
                if self.sent { self.close() }
            default:
                // The client went away, or stopped sending before its request was complete.
                self.close()
            }
        }
    }

    private func received(_ data: Data) {
        switch phase {
        case .head:
            let searchFrom = max(0, buffer.count - 3)
            buffer.append(data)
            if let end = buffer.range(of: Data("\r\n\r\n".utf8), in: searchFrom..<buffer.count) {
                guard end.upperBound <= RelayServer.maxHeaderBytes else { return respond(431, error: "headers_too_large") }
                bodyStart = end.upperBound
                phase = .working
                guard let request = Request(buffer.subdata(in: 0..<end.lowerBound)) else { return respond(400, error: "bad_request") }
                self.request = request
                server.route(self, request)
            } else if buffer.count > RelayServer.maxHeaderBytes {
                respond(431, error: "headers_too_large")
            }
        case .body(let length, let then):
            buffer.append(data)
            if buffer.count - bodyStart >= length {
                phase = .working
                timer?.cancel()
                bodyDeadline = .distantFuture
                then(buffer.subdata(in: bodyStart..<(bodyStart + length)))
            } else {
                arm(after: RelayServer.bodyIdleSeconds) { [weak self] in self?.respond(408, error: "timeout") }
            }
        case .working, .waiting, .finishing, .closed:
            // Anything sent after the request is not read.
            break
        }
    }

    func readBody(length: Int, sendContinue: Bool, then: @escaping (Data) -> Void) {
        if buffer.count - bodyStart >= length {
            timer?.cancel()
            return then(buffer.subdata(in: bodyStart..<(bodyStart + length)))
        }
        if sendContinue {
            connection.send(content: Data("HTTP/1.1 100 Continue\r\n\r\n".utf8), completion: .contentProcessed { _ in })
        }
        phase = .body(length: length, then: then)
        bodyDeadline = .now() + RelayServer.bodySeconds
        arm(after: RelayServer.bodyIdleSeconds) { [weak self] in self?.respond(408, error: "timeout") }
    }

    func wait(seconds: Double, then: @escaping () -> Void) {
        phase = .waiting
        arm(after: seconds) { then() }
    }

    func respond(_ status: Int, error: String, headers: [(String, String)] = []) {
        respond(status, body: Data(#"{"error":"\#(error)"}"#.utf8), headers: headers)
    }

    func respond(_ status: Int, body: Data, headers: [(String, String)] = [], note: String? = nil) {
        switch phase {
        case .finishing, .closed: return
        default: break
        }
        phase = .finishing
        timer?.cancel()
        bodyDeadline = .distantFuture
        server.stopWaiting(self)
        server.log(request, status: status, note: status == 200 ? note : nil)
        var head = "HTTP/1.1 \(status) \(Self.reason(status))\r\n"
        head += "Content-Type: application/json\r\nContent-Length: \(body.count)\r\nCache-Control: no-store\r\nConnection: close\r\n"
        for (name, value) in headers { head += "\(name): \(value)\r\n" }
        head += "\r\n"
        // An answer to HEAD has no body, only its length.
        let content = request?.method == "HEAD" ? Data(head.utf8) : Data(head.utf8) + body
        connection.send(content: content, contentContext: .finalMessage, isComplete: true, completion: .contentProcessed { [weak self] _ in
            guard let self else { return }
            self.sent = true
            if self.peerFinished { return self.close() }
            // Reading on for a moment lets the client see the answer, rather than a reset for what it is still sending.
            self.arm(after: 2) { [weak self] in self?.close() }
        })
    }

    func close() {
        var unanswered = request != nil
        switch phase {
        case .closed: return
        case .finishing: unanswered = false
        default: break
        }
        phase = .closed
        if unanswered { server.log(request, status: nil, note: "closed before an answer") }
        timer?.cancel()
        timer = nil
        connection.cancel()
        server.forget(self)
    }

    private static func reason(_ status: Int) -> String {
        switch status {
        case 100: return "Continue"
        case 200: return "OK"
        case 400: return "Bad Request"
        case 401: return "Unauthorized"
        case 404: return "Not Found"
        case 405: return "Method Not Allowed"
        case 408: return "Request Timeout"
        case 411: return "Length Required"
        case 413: return "Content Too Large"
        case 415: return "Unsupported Media Type"
        case 431: return "Request Header Fields Too Large"
        case 503: return "Service Unavailable"
        default: return "Error"
        }
    }
}
