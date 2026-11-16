import Darwin
import ErdigoCore
import Foundation
import XCTest

/// Helpers for the cloud relay and gateway tests. Both examples run as separate programs, exactly as a
/// person runs them, and are spoken to over loopback sockets on ports the system picks.
enum CloudExample {
    /// The built examples sit next to the test bundle. `swift build` makes them.
    static func executable(_ name: String) throws -> URL {
        let directory = Bundle(for: CloudRelayTests.self).bundleURL.deletingLastPathComponent()
        let url = directory.appendingPathComponent(name)
        guard FileManager.default.isExecutableFile(atPath: url.path) else {
            throw RawHTTP.Failure(description: "\(name) was not found next to the tests. Run 'swift build' first.")
        }
        return url
    }

    static var repositoryRoot: URL {
        URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
    }

    static func cases() throws -> [String: JSONValue] {
        let data = try Data(contentsOf: repositoryRoot.appendingPathComponent("docs/cloud-relay-cases.json"))
        guard case .object(let object) = try StrictJSON.parse(data, maxDepth: 16) else { throw RawHTTP.Failure(description: "the cases file is not an object") }
        return object
    }

    /// A fresh token for one test, in the form the guide asks for.
    static func token() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    static func temporaryDirectory() throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("erdigo-cloud-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        return url
    }

    /// Writes JSON text for a value read from the cases file, keeping integers apart from fractions.
    static func jsonText(_ value: JSONValue) -> String {
        switch value {
        case .null: return "null"
        case .bool(let flag): return flag ? "true" : "false"
        case .integer(let number): return String(number)
        case .number(let number): return number.description
        case .string(let text):
            var output = "\""
            for scalar in text.unicodeScalars {
                switch scalar {
                case "\"": output += "\\\""
                case "\\": output += "\\\\"
                case _ where scalar.value < 0x20: output += String(format: "\\u%04x", scalar.value)
                default: output.unicodeScalars.append(scalar)
                }
            }
            return output + "\""
        case .array(let list): return "[" + list.map(jsonText).joined(separator: ",") + "]"
        case .object(let object): return "{" + object.map { jsonText(.string($0.key)) + ":" + jsonText($0.value) }.joined(separator: ",") + "}"
        }
    }
}

/// A program started for a test, with its standard output and standard error collected together.
final class ChildProcess: @unchecked Sendable {
    let process = Process()
    private let lock = NSLock()
    private var collected = Data()

    init(_ executable: URL, _ arguments: [String], environment: [String: String]) throws {
        process.executableURL = executable
        process.arguments = arguments
        // Nothing from the test runner's own environment, so no token leaks in from it.
        process.environment = environment
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        process.standardInput = FileHandle.nullDevice
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            // At the end of the output the handler would otherwise be called again and again.
            if data.isEmpty { handle.readabilityHandler = nil }
            guard let self, !data.isEmpty else { return }
            self.lock.lock()
            self.collected.append(data)
            self.lock.unlock()
        }
        try process.run()
    }

    var output: String {
        lock.lock()
        defer { lock.unlock() }
        return String(decoding: collected, as: UTF8.self)
    }

    func waitForLine(startingWith prefix: String, timeout: TimeInterval = 15) async -> String? {
        let giveUp = Date().addingTimeInterval(timeout)
        while Date() < giveUp {
            if let line = output.split(separator: "\n").first(where: { $0.hasPrefix(prefix) }) { return String(line) }
            if !process.isRunning { return nil }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        return nil
    }

    /// The exit status, or nil if it was still running after `timeout` seconds, when it is killed.
    func exitStatus(timeout: TimeInterval) async -> Int32? {
        let giveUp = Date().addingTimeInterval(timeout)
        while process.isRunning {
            guard Date() < giveUp else {
                process.terminate()
                kill(process.processIdentifier, SIGKILL)
                return nil
            }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
        // Let the last of the output arrive.
        try? await Task.sleep(nanoseconds: 50_000_000)
        return process.terminationStatus
    }

    func signal(_ number: Int32) {
        kill(process.processIdentifier, number)
    }

    /// Polls rather than calling waitUntilExit, which can wait forever outside the main run loop.
    func stop() {
        guard process.isRunning else { return }
        process.terminate()
        for _ in 0..<500 where process.isRunning { usleep(10_000) }
        if process.isRunning { kill(process.processIdentifier, SIGKILL) }
    }
}

struct HTTPReply {
    let status: Int
    let headers: [String: String]
    let body: Data

    var json: [String: JSONValue] {
        guard case .object(let object)? = try? StrictJSON.parse(body, maxDepth: 16) else { return [:] }
        return object
    }

    var error: String? {
        guard case .string(let code)? = json["error"] else { return nil }
        return code
    }

    var opsCount: Int? {
        guard case .array(let list)? = json["ops"] else { return nil }
        return list.count
    }

    var seqs: [Int64] {
        guard case .array(let list)? = json["ops"] else { return [] }
        return list.compactMap { entry in
            guard case .object(let object) = entry, case .integer(let seq)? = object["seq"] else { return nil }
            return seq
        }
    }

    func string(_ key: String) -> String? {
        guard case .string(let text)? = json[key] else { return nil }
        return text
    }

    func flag(_ key: String) -> Bool? {
        guard case .bool(let value)? = json[key] else { return nil }
        return value
    }

    func integer(_ key: String) -> Int64? {
        guard case .integer(let value)? = json[key] else { return nil }
        return value
    }
}

/// A plain socket client, so that a test controls every byte, including what an HTTP library would not send.
struct RawHTTP {
    let port: UInt16
    var host = "127.0.0.1"

    struct Failure: Error, CustomStringConvertible {
        let description: String
    }

    func connect(timeout: TimeInterval) throws -> Int32 {
        let ipv6 = host.contains(":")
        let socketDescriptor = socket(ipv6 ? AF_INET6 : AF_INET, SOCK_STREAM, 0)
        guard socketDescriptor >= 0 else { throw Failure(description: "socket failed") }
        var interval = timeval(tv_sec: Int(timeout), tv_usec: Int32((timeout - floor(timeout)) * 1_000_000))
        setsockopt(socketDescriptor, SOL_SOCKET, SO_RCVTIMEO, &interval, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(socketDescriptor, SOL_SOCKET, SO_SNDTIMEO, &interval, socklen_t(MemoryLayout<timeval>.size))
        var one: Int32 = 1
        setsockopt(socketDescriptor, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
        let result: Int32
        if ipv6 {
            var address = sockaddr_in6()
            address.sin6_len = UInt8(MemoryLayout<sockaddr_in6>.size)
            address.sin6_family = sa_family_t(AF_INET6)
            address.sin6_port = port.bigEndian
            inet_pton(AF_INET6, host, &address.sin6_addr)
            result = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(socketDescriptor, $0, socklen_t(MemoryLayout<sockaddr_in6>.size)) }
            }
        } else {
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = port.bigEndian
            address.sin_addr.s_addr = inet_addr(host)
            result = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.connect(socketDescriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
            }
        }
        guard result == 0 else {
            close(socketDescriptor)
            throw Failure(description: "could not connect")
        }
        return socketDescriptor
    }

    static func sendAll(_ socketDescriptor: Int32, _ data: Data) throws {
        var offset = 0
        while offset < data.count {
            let sent = data.withUnsafeBytes { Darwin.send(socketDescriptor, $0.baseAddress! + offset, data.count - offset, 0) }
            guard sent > 0 else { throw Failure(description: "send failed") }
            offset += sent
        }
    }

    static func readUntilClosed(_ socketDescriptor: Int32) -> Data {
        var received = Data()
        var buffer = [UInt8](repeating: 0, count: 65536)
        while true {
            let count = recv(socketDescriptor, &buffer, buffer.count, 0)
            if count <= 0 { break }
            received.append(buffer, count: count)
        }
        return received
    }

    static func parse(_ data: Data) throws -> HTTPReply {
        guard let end = data.range(of: Data("\r\n\r\n".utf8)) else { throw Failure(description: "no complete answer") }
        let head = String(decoding: data[..<end.lowerBound], as: UTF8.self).components(separatedBy: "\r\n")
        let statusParts = head[0].split(separator: " ")
        guard statusParts.count >= 2, let status = Int(statusParts[1]) else { throw Failure(description: "no status line") }
        var headers: [String: String] = [:]
        for line in head.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            headers[line[..<colon].lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        let body = data[end.upperBound...]
        if let length = headers["content-length"].flatMap(Int.init), body.count != length {
            throw Failure(description: "the body has \(body.count) bytes, not the \(length) announced")
        }
        return HTTPReply(status: status, headers: headers, body: Data(body))
    }

    /// Sends the bytes and reads the answer until the relay closes the connection.
    func exchange(_ request: Data, timeout: TimeInterval = 15) throws -> HTTPReply {
        let socketDescriptor = try connect(timeout: timeout)
        defer { close(socketDescriptor) }
        try Self.sendAll(socketDescriptor, request)
        return try Self.parse(Self.readUntilClosed(socketDescriptor))
    }

    func request(_ method: String, _ target: String, headers: [(String, String)] = [], body: Data? = nil, timeout: TimeInterval = 15) throws -> HTTPReply {
        var head = "\(method) \(target) HTTP/1.1\r\nHost: \(host.contains(":") ? "[\(host)]" : host):\(port)\r\nConnection: close\r\n"
        for (name, value) in headers { head += "\(name): \(value)\r\n" }
        if let body, !headers.contains(where: { $0.0.lowercased() == "content-length" }) { head += "Content-Length: \(body.count)\r\n" }
        return try exchange(Data((head + "\r\n").utf8) + (body ?? Data()), timeout: timeout)
    }

    func post(_ body: Data, token: String?, contentType: String? = "application/json") throws -> HTTPReply {
        var headers: [(String, String)] = []
        if let token { headers.append(("Authorization", "Bearer \(token)")) }
        if let contentType { headers.append(("Content-Type", contentType)) }
        return try request("POST", "/v1/ops", headers: headers, body: body)
    }

    func post(_ text: String, token: String?) throws -> HTTPReply {
        try post(Data(text.utf8), token: token)
    }

    func get(_ query: String = "", token: String?, timeout: TimeInterval = 15) throws -> HTTPReply {
        var headers: [(String, String)] = []
        if let token { headers.append(("Authorization", "Bearer \(token)")) }
        return try request("GET", query.isEmpty ? "/v1/ops" : "/v1/ops?\(query)", headers: headers, timeout: timeout)
    }
}

/// A relay started for one test.
final class RelayProcess {
    let child: ChildProcess
    let port: UInt16
    let sourceTokens: [String]
    let gatewayToken: String
    let http: RawHTTP

    var url: String { "http://127.0.0.1:\(port)" }

    private init(child: ChildProcess, port: UInt16, sourceTokens: [String], gatewayToken: String) {
        self.child = child
        self.port = port
        self.sourceTokens = sourceTokens
        self.gatewayToken = gatewayToken
        http = RawHTTP(port: port)
    }

    var sourceToken: String { sourceTokens[0] }

    static func environment(sources: [String], gateway: String, extra: [String: String] = [:]) -> [String: String] {
        var environment = ["PATH": "/usr/bin:/bin", "ERDIGO_RELAY_SOURCE_TOKENS": sources.joined(separator: ","), "ERDIGO_RELAY_GATEWAY_TOKEN": gateway]
        environment.merge(extra) { $1 }
        return environment
    }

    static func start(
        listen: String = "127.0.0.1:0",
        sourceTokens: [String] = [CloudExample.token(), CloudExample.token()],
        gatewayToken: String = CloudExample.token(),
        extra: [String: String] = [:]
    ) async throws -> RelayProcess {
        let child = try ChildProcess(
            try CloudExample.executable("erdigo-example-relay"),
            ["--listen", listen],
            environment: environment(sources: sourceTokens, gateway: gatewayToken, extra: extra)
        )
        guard let line = await child.waitForLine(startingWith: "Relay listening on http://"),
              let port = line.split(separator: ":").last.flatMap({ UInt16($0) })
        else {
            child.stop()
            throw RawHTTP.Failure(description: "the relay did not start: \(child.output)")
        }
        return RelayProcess(child: child, port: port, sourceTokens: sourceTokens, gatewayToken: gatewayToken)
    }

    func post(_ ops: String) throws -> HTTPReply {
        try http.post(#"{"ops":[\#(ops)]}"#, token: sourceToken)
    }

    /// Stops it with SIGTERM, as a service manager would, and returns its exit status.
    func stop() async -> Int32? {
        child.signal(SIGTERM)
        return await child.exitStatus(timeout: 10)
    }
}
