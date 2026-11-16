import Darwin
import ErdigoCore
import Foundation
import XCTest

/// The example relay against docs/cloud-relay.md and docs/cloud-relay-cases.json, over real sockets.
final class CloudRelayTests: XCTestCase {
    private var relays: [RelayProcess] = []

    override func tearDown() async throws {
        for relay in relays { relay.child.stop() }
        relays.removeAll()
    }

    private func startRelay(listen: String = "127.0.0.1:0", extra: [String: String] = [:]) async throws -> RelayProcess {
        let relay = try await RelayProcess.start(listen: listen, extra: extra)
        relays.append(relay)
        return relay
    }

    private static func pin(_ id: String = "p1") -> String {
        #"{"op":"pin","id":"\#(id)","lat":37.5663,"lon":126.9779}"#
    }

    /// A photo op whose data decodes to `size` bytes, starting like a PNG. The relay checks no more than that.
    private static func photo(id: String, size: Int) -> String {
        var bytes = Data([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
        bytes.append(Data(count: size - bytes.count))
        return #"{"op":"photo","id":"\#(id)","lat":0,"lon":0,"type":"image/png","data":"\#(bytes.base64EncodedString())"}"#
    }

    private func background<T>(_ body: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            DispatchQueue.global().async { continuation.resume(with: Result { try body() }) }
        }
    }

    // MARK: The shared cases

    func testEveryCaseInTheSharedFile() async throws {
        let relay = try await startRelay()
        let cases = try CloudExample.cases()
        guard case .array(let valid)? = cases["valid_ops"], case .array(let invalid)? = cases["invalid_ops"],
              case .array(let raw)? = cases["raw_bodies"]
        else { return XCTFail("the cases file does not have valid_ops, invalid_ops and raw_bodies") }
        XCTAssertGreaterThan(valid.count, 0)
        XCTAssertGreaterThan(invalid.count, 0)
        XCTAssertGreaterThan(raw.count, 0)

        var sent: [JSONValue] = []
        for entry in valid {
            guard case .object(let item) = entry, case .string(let name)? = item["name"], let op = item["op"] else {
                XCTFail("a valid case is malformed")
                continue
            }
            let reply = try relay.http.post(#"{"ops":[\#(CloudExample.jsonText(op))]}"#, token: relay.sourceToken)
            XCTAssertEqual(reply.status, 200, name)
            XCTAssertEqual(reply.integer("accepted"), 1, name)
            XCTAssertEqual(reply.integer("last_seq"), Int64(sent.count + 1), name)
            sent.append(op)
        }

        for entry in invalid {
            guard case .object(let item) = entry, case .string(let name)? = item["name"], let op = item["op"] else {
                XCTFail("an invalid case is malformed")
                continue
            }
            let reply = try relay.http.post(#"{"ops":[\#(CloudExample.jsonText(op))]}"#, token: relay.sourceToken)
            XCTAssertEqual(reply.status, 400, name)
            XCTAssertEqual(reply.error, "invalid_op", name)
            XCTAssertEqual(reply.integer("index"), 0, name)
            XCTAssertNotNil(reply.string("reason"), name)
            XCTAssertEqual(reply.headers["content-type"], "application/json", name)
        }

        for entry in raw {
            guard case .object(let item) = entry, case .string(let name)? = item["name"], case .array(let errors)? = item["errors"] else {
                XCTFail("a raw case is malformed")
                continue
            }
            let body: Data
            if case .string(let text)? = item["body"] {
                body = Data(text.utf8)
            } else if case .string(let encoded)? = item["body_base64"], let decoded = Data(base64Encoded: encoded) {
                body = decoded
            } else {
                XCTFail("\(name): no body")
                continue
            }
            let reply = try relay.http.post(body, token: relay.sourceToken)
            XCTAssertEqual(reply.status, 400, name)
            XCTAssertTrue(errors.contains(.string(reply.error ?? "-")), "\(name): got \(reply.error ?? "no error")")
        }

        // Only the valid ones were taken, in order, and they come back as they were sent.
        let fetched = try relay.http.get("limit=100", token: relay.gatewayToken)
        XCTAssertEqual(fetched.seqs, (1...Int64(sent.count)).map { $0 })
        guard case .array(let entries)? = fetched.json["ops"] else { return XCTFail("no ops") }
        for (entry, original) in zip(entries, sent) {
            guard case .object(let object) = entry else { return XCTFail("an entry is not an object") }
            XCTAssertEqual(object["op"], original)
        }
    }

    // MARK: The order of checks

    func testTokensAreCheckedForEachRole() async throws {
        let relay = try await startRelay()
        let body = #"{"ops":[\#(Self.pin())]}"#
        for token in relay.sourceTokens {
            XCTAssertEqual(try relay.http.post(body, token: token).status, 200)
        }
        let strangers: [String?] = [nil, relay.gatewayToken, CloudExample.token(), relay.sourceToken + "x", String(relay.sourceToken.dropLast())]
        for token in strangers {
            let reply = try relay.http.post(body, token: token)
            XCTAssertEqual(reply.status, 401)
            XCTAssertEqual(reply.error, "unauthorized")
        }

        XCTAssertEqual(try relay.http.get(token: relay.gatewayToken).status, 200)
        for token in [nil, relay.sourceTokens[0], relay.sourceTokens[1], CloudExample.token()] as [String?] {
            let reply = try relay.http.get(token: token)
            XCTAssertEqual(reply.status, 401)
            XCTAssertEqual(reply.error, "unauthorized")
        }
        let basic = try relay.http.request("GET", "/v1/ops", headers: [("Authorization", "Basic \(relay.gatewayToken)")])
        XCTAssertEqual(basic.status, 401)
        let twice = try relay.http.request("GET", "/v1/ops", headers: [
            ("Authorization", "Bearer \(relay.gatewayToken)"), ("Authorization", "Bearer \(relay.gatewayToken)"),
        ])
        XCTAssertEqual(twice.status, 401)
        // The scheme's name is not case-sensitive.
        let lower = try relay.http.request("GET", "/v1/ops", headers: [("Authorization", "bearer \(relay.gatewayToken)")])
        XCTAssertEqual(lower.status, 200)

        let health = try relay.http.request("GET", "/v1/health")
        XCTAssertEqual(health.status, 200)
        XCTAssertEqual(health.flag("ok"), true)
        XCTAssertEqual(health.headers["content-type"], "application/json")

        // A refused token took nothing.
        XCTAssertEqual(try relay.http.get(token: relay.gatewayToken).opsCount, 2)
    }

    func testUnknownPathsComeBeforeMethodsAndMethodsBeforeTokens() async throws {
        let relay = try await startRelay()
        for (method, target) in [("GET", "/"), ("GET", "/v1"), ("GET", "/v1/ops/"), ("POST", "/v1/opsx"), ("GET", "/v2/ops"), ("DELETE", "/elsewhere"), ("GET", "/V1/OPS")] {
            let reply = try relay.http.request(method, target)
            XCTAssertEqual(reply.status, 404, "\(method) \(target)")
            XCTAssertEqual(reply.error, "not_found", "\(method) \(target)")
            XCTAssertEqual(reply.headers["content-type"], "application/json")
        }
        for (method, target) in [("PUT", "/v1/ops"), ("DELETE", "/v1/ops"), ("PATCH", "/v1/ops"), ("OPTIONS", "/v1/ops"), ("POST", "/v1/health"), ("DELETE", "/v1/health")] {
            let reply = try relay.http.request(method, target)
            XCTAssertEqual(reply.status, 405, "\(method) \(target)")
            XCTAssertEqual(reply.error, "method_not_allowed", "\(method) \(target)")
        }
        // A good token does not change the order.
        XCTAssertEqual(try relay.http.request("DELETE", "/v1/ops", headers: [("Authorization", "Bearer \(relay.sourceToken)")]).status, 405)
        XCTAssertEqual(try relay.http.request("GET", "/v1/nothing", headers: [("Authorization", "Bearer \(relay.gatewayToken)")]).status, 404)
        // The query is not part of the path.
        XCTAssertEqual(try relay.http.request("GET", "/v1/health?probe=1").status, 200)

        XCTAssertEqual(try relay.http.exchange(Data("NONSENSE\r\n\r\n".utf8)).status, 400)
        XCTAssertEqual(try relay.http.exchange(Data("GET /v1/health HTTP/2\r\n\r\n".utf8)).status, 400)
        let huge = "GET /v1/health HTTP/1.1\r\nX-Filler: \(String(repeating: "a", count: 20_000))\r\n\r\n"
        XCTAssertEqual(try relay.http.exchange(Data(huge.utf8)).status, 431)
    }

    // MARK: Bodies

    func testContentTypeLengthAndSize() async throws {
        let relay = try await startRelay()
        let body = Data(#"{"ops":[\#(Self.pin())]}"#.utf8)
        for type in ["text/plain", nil, "application/json-seq", "application/json; charset=latin1", "application/x-www-form-urlencoded"] as [String?] {
            let reply = try relay.http.post(body, token: relay.sourceToken, contentType: type)
            XCTAssertEqual(reply.status, 415, type ?? "no type")
            XCTAssertEqual(reply.error, "unsupported_media_type")
        }
        for type in ["application/json; charset=utf-8", "Application/JSON", "application/json;charset=UTF-8"] {
            XCTAssertEqual(try relay.http.post(body, token: relay.sourceToken, contentType: type).status, 200, type)
        }

        let auth = ("Authorization", "Bearer \(relay.sourceToken)")
        let json = ("Content-Type", "application/json")
        let unannounced = try relay.http.request("POST", "/v1/ops", headers: [auth, json])
        XCTAssertEqual(unannounced.status, 411)
        XCTAssertEqual(unannounced.error, "length_required")
        let chunked = try relay.http.request("POST", "/v1/ops", headers: [auth, json, ("Transfer-Encoding", "chunked")], body: Data("5\r\nhello\r\n0\r\n\r\n".utf8))
        XCTAssertEqual(chunked.status, 411)

        // Announced as too large: refused at once, without waiting for a body that never comes.
        let started = Date()
        let announced = try relay.http.request("POST", "/v1/ops", headers: [auth, json, ("Content-Length", "2097153")], timeout: 5)
        XCTAssertEqual(announced.status, 413)
        XCTAssertEqual(announced.error, "too_large")
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)
        // And when the body is sent anyway, the answer still arrives.
        var tooLarge = body
        tooLarge.append(Data(repeating: 0x20, count: 2_097_153 - body.count))
        XCTAssertEqual(try relay.http.post(tooLarge, token: relay.sourceToken).status, 413)

        let absurd = try relay.http.request("POST", "/v1/ops", headers: [auth, json, ("Content-Length", String(repeating: "9", count: 30))], timeout: 5)
        XCTAssertEqual(absurd.status, 413)
        XCTAssertEqual(try relay.http.request("POST", "/v1/ops", headers: [auth, json, ("Content-Length", "12, 12")]).status, 400)

        var exactly = body
        exactly.append(Data(repeating: 0x20, count: 2_097_152 - body.count))
        XCTAssertEqual(try relay.http.post(exactly, token: relay.sourceToken).status, 200)

        // A client that asks before sending its body is told to go on.
        let socket = try relay.http.connect(timeout: 5)
        defer { close(socket) }
        let head = "POST /v1/ops HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer \(relay.sourceToken)\r\nContent-Type: application/json\r\nContent-Length: \(body.count)\r\nExpect: 100-continue\r\n\r\n"
        try RawHTTP.sendAll(socket, Data(head.utf8))
        var interim = [UInt8](repeating: 0, count: 25)
        XCTAssertEqual(recv(socket, &interim, interim.count, MSG_WAITALL), 25)
        XCTAssertEqual(String(decoding: interim, as: UTF8.self), "HTTP/1.1 100 Continue\r\n\r\n")
        try RawHTTP.sendAll(socket, body)
        XCTAssertEqual(try RawHTTP.parse(RawHTTP.readUntilClosed(socket)).status, 200)
    }

    func testBatchLimitsAndAllOrNothing() async throws {
        let relay = try await startRelay()
        let hundred = (0..<100).map { Self.pin("p\($0)") }.joined(separator: ",")
        let accepted = try relay.post(hundred)
        XCTAssertEqual(accepted.status, 200)
        XCTAssertEqual(accepted.integer("accepted"), 100)
        XCTAssertEqual(accepted.integer("last_seq"), 100)
        let tooMany = try relay.post(hundred + "," + Self.pin())
        XCTAssertEqual(tooMany.status, 400)
        XCTAssertEqual(tooMany.error, "invalid_body")

        XCTAssertEqual(try relay.post(Self.photo(id: "largest", size: 1_048_576)).status, 200)
        let overPhoto = try relay.post(Self.photo(id: "over", size: 1_048_577))
        XCTAssertEqual(overPhoto.status, 400)
        XCTAssertEqual(overPhoto.error, "invalid_op")

        let mixed = try relay.post([Self.pin("a"), Self.pin("b"), #"{"op":"pin","id":"c","lat":91,"lon":0}"#].joined(separator: ","))
        XCTAssertEqual(mixed.status, 400)
        XCTAssertEqual(mixed.error, "invalid_op")
        XCTAssertEqual(mixed.integer("index"), 2)

        // Nothing refused was taken or used up a number.
        XCTAssertEqual(try relay.post(Self.pin("after")).integer("last_seq"), 102)
        let first = try relay.http.get(token: relay.gatewayToken)
        XCTAssertEqual(first.seqs, (1...100).map { Int64($0) })
        let epoch = try XCTUnwrap(first.string("epoch"))
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=100", token: relay.gatewayToken).seqs, [101, 102])
    }

    func testAFullQueueRefusesTheWholeBatch() async throws {
        let relay = try await startRelay(extra: ["ERDIGO_RELAY_TEST_MAX_OPS": "3"])
        XCTAssertEqual(try relay.post([Self.pin("a"), Self.pin("b")].joined(separator: ",")).status, 200)
        let full = try relay.post([Self.pin("c"), Self.pin("d")].joined(separator: ","))
        XCTAssertEqual(full.status, 503)
        XCTAssertEqual(full.error, "queue_full")
        XCTAssertEqual(full.headers["retry-after"], "30")
        XCTAssertEqual(try relay.post(Self.pin("c")).integer("last_seq"), 3)
        XCTAssertEqual(try relay.post(Self.pin("e")).status, 503)
        // What the gateway is done with makes room.
        let epoch = try XCTUnwrap(relay.http.get(token: relay.gatewayToken).string("epoch"))
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=3", token: relay.gatewayToken).opsCount, 0)
        XCTAssertEqual(try relay.post([Self.pin("f"), Self.pin("g"), Self.pin("h")].joined(separator: ",")).integer("last_seq"), 6)

        // The same for the bytes the operations take: a pin is stored as 52 bytes of JSON.
        let small = try await startRelay(extra: ["ERDIGO_RELAY_TEST_MAX_BYTES": "120"])
        XCTAssertEqual(try small.post(Self.pin("a")).status, 200)
        XCTAssertEqual(try small.post([Self.pin("b"), Self.pin("c")].joined(separator: ",")).status, 503)
        XCTAssertEqual(try small.post(Self.pin("b")).integer("last_seq"), 2)
    }

    func testASlowClientIsCutOff() async throws {
        let relay = try await startRelay()
        let socket = try relay.http.connect(timeout: 20)
        defer { close(socket) }
        let started = Date()
        try RawHTTP.sendAll(socket, Data("GET /v1/health HTTP/1.1\r\nHost: x\r\n".utf8))
        let reply = try RawHTTP.parse(RawHTTP.readUntilClosed(socket))
        XCTAssertEqual(reply.status, 408)
        XCTAssertGreaterThan(Date().timeIntervalSince(started), 9)
        XCTAssertLessThan(Date().timeIntervalSince(started), 15)
    }

    // MARK: Fetching

    func testEpochsResetsAndForgetting() async throws {
        let relay = try await startRelay()
        for id in ["a", "b", "c"] { XCTAssertEqual(try relay.post(Self.pin(id)).status, 200) }
        let token = relay.gatewayToken

        let first = try relay.http.get(token: token)
        XCTAssertEqual(first.flag("reset"), true)
        XCTAssertEqual(first.flag("missed"), false)
        XCTAssertEqual(first.seqs, [1, 2, 3])
        let epoch = try XCTUnwrap(first.string("epoch"))
        XCTAssertTrue((16...64).contains(epoch.count) && epoch.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-" || $0 == "_") })
        guard case .array(let entries)? = first.json["ops"], case .object(let entry)? = entries.first, case .integer(let received)? = entry["received_ms"] else {
            return XCTFail("no received_ms")
        }
        XCTAssertLessThan(abs(Double(received) - Date().timeIntervalSince1970 * 1000), 60_000)

        let same = try relay.http.get("epoch=\(epoch)&after=0", token: token)
        XCTAssertEqual(same.flag("reset"), false)
        XCTAssertEqual(same.seqs, [1, 2, 3])
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=2", token: token).seqs, [3])
        // Forgotten, so not even a reset brings them back.
        let again = try relay.http.get(token: token)
        XCTAssertEqual(again.flag("reset"), true)
        XCTAssertEqual(again.seqs, [3])
        // Another epoch forgets nothing.
        let other = try relay.http.get("epoch=AAAAAAAAAAAAAAAAAAAAAA&after=3", token: token)
        XCTAssertEqual(other.flag("reset"), true)
        XCTAssertEqual(other.seqs, [3])
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=4", token: token).error, "invalid_query")
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=3", token: token).opsCount, 0)
        XCTAssertEqual(try relay.http.get(token: token).opsCount, 0)

        let bad = ["after=1&after=2", "foo=1", "limit=0", "limit=101", "wait=31", "after=-1", "after=1.5", "after=", "after",
                   "limit=abc", "epoch=short", "epoch=" + String(repeating: "a", count: 65), "epoch=bad!characters_here", "epoch=\(epoch)&epoch=\(epoch)",
                   "after=9007199254740992", "&", "wait=1e1"]
        for query in bad {
            let reply = try relay.http.get(query, token: token)
            XCTAssertEqual(reply.status, 400, query)
            XCTAssertEqual(reply.error, "invalid_query", query)
        }
        for query in ["limit=1", "limit=100", "wait=0", "after=0", "epoch=\(String(repeating: "a", count: 64))"] {
            XCTAssertEqual(try relay.http.get(query, token: token).status, 200, query)
        }

        // A relay that starts again has a new numbering.
        let stopped = await relay.stop()
        XCTAssertEqual(stopped, 0)
        let restarted = try await startRelay()
        let fresh = try restarted.http.get("epoch=\(epoch)&after=3", token: restarted.gatewayToken)
        XCTAssertEqual(fresh.flag("reset"), true)
        XCTAssertNotEqual(fresh.string("epoch"), epoch)
    }

    func testExpiredOperationsAreReportedAsMissed() async throws {
        let relay = try await startRelay(extra: ["ERDIGO_RELAY_TEST_KEEP_SECONDS": "1"])
        let token = relay.gatewayToken
        XCTAssertEqual(try relay.post(Self.pin("a")).status, 200)
        let epoch = try XCTUnwrap(relay.http.get(token: token).string("epoch"))
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=1", token: token).flag("missed"), false)

        XCTAssertEqual(try relay.post([Self.pin("b"), Self.pin("c")].joined(separator: ",")).status, 200)
        try await Task.sleep(nanoseconds: 1_500_000_000)
        XCTAssertEqual(try relay.post(Self.pin("d")).integer("last_seq"), 4)
        let gap = try relay.http.get("epoch=\(epoch)&after=1", token: token)
        XCTAssertEqual(gap.flag("missed"), true)
        XCTAssertEqual(gap.seqs, [4])
        XCTAssertEqual(try relay.http.get(token: token).flag("missed"), true)
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=4", token: token).flag("missed"), false)

        // An operation the gateway is done with does not count, even if it expires before the relay hears so.
        XCTAssertEqual(try relay.post(Self.pin("e")).status, 200)
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=4", token: token).seqs, [5])
        try await Task.sleep(nanoseconds: 1_500_000_000)
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=5", token: token).flag("missed"), false)
    }

    func testPagingByCountAndBySize() async throws {
        let relay = try await startRelay()
        let token = relay.gatewayToken
        XCTAssertEqual(try relay.post((1...5).map { Self.pin("p\($0)") }.joined(separator: ",")).status, 200)
        let first = try relay.http.get("limit=2", token: token)
        XCTAssertEqual(first.seqs, [1, 2])
        let epoch = try XCTUnwrap(first.string("epoch"))
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=2&limit=2", token: token).seqs, [3, 4])
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=4&limit=2", token: token).seqs, [5])

        // Four photos of a million bytes are about 5.3 MB as base64; three fit under 4 MiB.
        for index in 1...4 { XCTAssertEqual(try relay.post(Self.photo(id: "big\(index)", size: 1_000_000)).status, 200) }
        let large = try relay.http.get("epoch=\(epoch)&after=5", token: token)
        XCTAssertEqual(large.seqs, [6, 7, 8])
        XCTAssertLessThanOrEqual(large.body.count, 4 * 1024 * 1024)
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=8", token: token).seqs, [9])
    }

    func testAWaitingFetchAnswersWhenSomethingArrives() async throws {
        let relay = try await startRelay()
        let token = relay.gatewayToken
        let http = relay.http
        let epoch = try XCTUnwrap(http.get(token: token).string("epoch"))

        let started = Date()
        let waiting = Task { try await self.background { try http.get("epoch=\(epoch)&after=0&wait=20", token: token, timeout: 30) } }
        try await Task.sleep(nanoseconds: 500_000_000)
        // Other requests are not held up meanwhile.
        let healthStarted = Date()
        XCTAssertEqual(try http.request("GET", "/v1/health").status, 200)
        XCTAssertLessThan(Date().timeIntervalSince(healthStarted), 1)
        XCTAssertEqual(try relay.post(Self.pin()).status, 200)
        let answer = try await waiting.value
        XCTAssertEqual(answer.seqs, [1])
        XCTAssertLessThan(Date().timeIntervalSince(started), 5)

        let emptyStarted = Date()
        let empty = try await background { try http.get("epoch=\(epoch)&after=1&wait=1", token: token) }
        XCTAssertEqual(empty.status, 200)
        XCTAssertEqual(empty.opsCount, 0)
        XCTAssertGreaterThan(Date().timeIntervalSince(emptyStarted), 0.8)
    }

    // MARK: The program

    func testStartUpRefusesMistakes() async throws {
        let executable = try CloudExample.executable("erdigo-example-relay")
        let good = CloudExample.token()
        let gateway = CloudExample.token()
        func run(_ arguments: [String], sources: String?, gateway: String?) async throws -> (status: Int32?, output: String) {
            var environment = ["PATH": "/usr/bin:/bin"]
            if let sources { environment["ERDIGO_RELAY_SOURCE_TOKENS"] = sources }
            if let gateway { environment["ERDIGO_RELAY_GATEWAY_TOKEN"] = gateway }
            let child = try ChildProcess(executable, arguments, environment: environment)
            if await child.waitForLine(startingWith: "Relay listening on ", timeout: 5) != nil {
                child.signal(SIGINT)
            }
            return (await child.exitStatus(timeout: 10), child.output)
        }

        let tokenCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~"
        let refused: [(String, [String], String?, String?)] = [
            ("no source tokens", [], nil, gateway),
            ("empty source tokens", [], "", gateway),
            ("a 31-character token", [], String(repeating: "a", count: 31), gateway),
            ("a 257-character token", [], String(repeating: "a", count: 257), gateway),
            ("a token with '+'", [], String(good.dropLast()) + "+", gateway),
            ("an empty token in the list", [], "\(good),,\(CloudExample.token())", gateway),
            ("no gateway token", [], good, nil),
            ("a short gateway token", [], good, "short"),
            ("the gateway token is a source token", [], "\(CloudExample.token()),\(good)", good),
            ("a public address without --behind-proxy", ["--listen", "0.0.0.0:0"], good, gateway),
            ("another address without --behind-proxy", ["--listen", "192.0.2.1:0"], good, gateway),
            ("IPv6 without brackets", ["--listen", "::1:0"], good, gateway),
            ("no port", ["--listen", "127.0.0.1"], good, gateway),
            ("keep-hours 0", ["--keep-hours", "0"], good, gateway),
            ("keep-hours 169", ["--keep-hours", "169"], good, gateway),
            ("keep-hours 1.5", ["--keep-hours", "1.5"], good, gateway),
            ("an unknown option", ["--port", "0"], good, gateway),
            ("a token as an argument", ["--listen", "127.0.0.1:0", good], good, gateway),
        ]
        for (name, arguments, sources, gatewayToken) in refused {
            let result = try await run(arguments.isEmpty ? ["--listen", "127.0.0.1:0"] : arguments, sources: sources, gateway: gatewayToken)
            XCTAssertEqual(result.status, 1, "\(name): \(result.output)")
            XCTAssertFalse(result.output.contains("Relay listening"), name)
            XCTAssertFalse(result.output.contains(good), "\(name): a token was repeated")
        }

        let shortest = String(repeating: "a", count: 32)
        let longest = String((0..<256).map { tokenCharacters[tokenCharacters.index(tokenCharacters.startIndex, offsetBy: $0 % tokenCharacters.count)] })
        let accepted: [(String, [String], String)] = [
            ("tokens of 32 and 256 characters", ["--listen", "127.0.0.1:0"], "\(shortest), \(longest)"),
            ("keep-hours 1", ["--listen", "127.0.0.1:0", "--keep-hours", "1"], good),
            ("keep-hours 168", ["--listen", "127.0.0.1:0", "--keep-hours", "168"], good),
            ("localhost", ["--listen", "localhost:0"], good),
            // A non-loopback address could make the macOS firewall ask a question, so the command-line checks cover it.
            ("loopback behind a proxy", ["--listen", "127.0.0.1:0", "--behind-proxy"], good),
        ]
        for (name, arguments, sources) in accepted {
            let result = try await run(arguments, sources: sources, gateway: gateway)
            XCTAssertEqual(result.status, 0, "\(name): \(result.output)")
            XCTAssertTrue(result.output.contains("Relay listening on http://"), name)
        }
        let localhost = try await run(["--listen", "localhost:0"], sources: good, gateway: gateway)
        XCTAssertTrue(localhost.output.contains("Relay listening on http://127.0.0.1:"))
    }

    func testIPv6Loopback() async throws {
        let relay: RelayProcess
        do {
            relay = try await startRelay(listen: "[::1]:0")
        } catch {
            throw XCTSkip("IPv6 loopback is not available here")
        }
        XCTAssertTrue(relay.child.output.contains("Relay listening on http://[::1]:\(relay.port)\n"))
        let http = RawHTTP(port: relay.port, host: "::1")
        XCTAssertEqual(try http.request("GET", "/v1/health").status, 200)
    }

    func testTheLogHoldsNoTokensOrContentsAndAStopIsClean() async throws {
        let relay = try await startRelay()
        let wrong = CloudExample.token()
        XCTAssertEqual(try relay.post(#"{"op":"note","id":"secret-id","lat":12.3456,"lon":65.4321,"text":"Private words"},{"op":"pin","id":"p2","lat":0,"lon":0,"title":"Hidden title"}"#).status, 200)
        XCTAssertEqual(try relay.http.post(#"{"ops":[\#(Self.pin())]}"#, token: wrong).status, 401)
        let epoch = try XCTUnwrap(relay.http.get(token: relay.gatewayToken).string("epoch"))
        XCTAssertEqual(try relay.http.get("epoch=\(epoch)&after=2", token: relay.gatewayToken).status, 200)
        XCTAssertEqual(try relay.http.request("GET", "/\(wrong)").status, 404)
        XCTAssertEqual(try relay.http.post(Data("{\"ops\":[{\"op\":\"pin\",\"id\":\"x\",\"lat\":0,\"lon\":0,\"title\":\"Hidden title\"}]".utf8), token: relay.sourceToken).status, 400)
        // A waiting fetch whose client leaves is logged too.
        XCTAssertThrowsError(try relay.http.get("epoch=\(epoch)&after=2&wait=5", token: relay.gatewayToken, timeout: 0.5))
        try await Task.sleep(nanoseconds: 300_000_000)

        let stopped = await relay.stop()
        XCTAssertEqual(stopped, 0)
        let log = relay.child.output
        XCTAssertEqual(log.split(separator: "\n").filter { $0.contains(" /v1/ops ") || $0.contains(" (other) ") }.count, 7, log)
        XCTAssertTrue(log.contains(" GET /v1/ops - (closed before an answer)\n"), log)
        for secret in relay.sourceTokens + [relay.gatewayToken, wrong, epoch, "secret-id", "Private words", "Hidden title", "12.3456", "65.4321"] {
            XCTAssertFalse(log.contains(secret), "the log holds something it should not")
        }

        // Ctrl+C stops it as cleanly as SIGTERM.
        let other = try await startRelay()
        other.child.signal(SIGINT)
        let interrupted = await other.child.exitStatus(timeout: 10)
        XCTAssertEqual(interrupted, 0)
    }
}
