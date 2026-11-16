import Darwin
import ErdigoCore
@_spi(TestVectors) import ErdigoMockReceiver
import Foundation
import XCTest

/// The example gateway end to end: a relay and the gateway run as programs, and the mock receiver
/// runs here, so that the test can see exactly what reached it.
final class CloudGatewayTests: XCTestCase {
    private var receivers: [MockReceiver] = []
    private var relays: [RelayProcess] = []
    private var children: [ChildProcess] = []
    private var fakes: [FakeRelay] = []
    private var proxies: [LosingProxy] = []
    private var folders: [URL] = []

    override func tearDown() async throws {
        for receiver in receivers { await receiver.stop() }
        for relay in relays { relay.child.stop() }
        for child in children { child.stop() }
        for fake in fakes { fake.stop() }
        for proxy in proxies { proxy.stop() }
        for folder in folders { try? FileManager.default.removeItem(at: folder) }
        receivers.removeAll()
        relays.removeAll()
        children.removeAll()
        fakes.removeAll()
        proxies.removeAll()
        folders.removeAll()
    }

    private static let png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="

    private func folder() throws -> URL {
        let url = try CloudExample.temporaryDirectory()
        folders.append(url)
        return url
    }

    private func startReceiver(_ adjust: (inout MockReceiver.Options) -> Void = { _ in }) async throws -> (MockReceiver, UInt16) {
        var options = MockReceiver.Options()
        options.silentDelay = 0...0.01
        options.addressPenaltyBase = 0
        options.handshakeTimeout = 3
        options.maxPreAuthConnectionsPerAddress = 8
        options.limits.messagesPerSecond = 10_000
        options.limits.messageBurst = 10_000
        options.limits.pointsPerSecond = 10_000
        options.limits.pointBurst = 10_000
        options.maxRejectionsPerMinute = 10_000
        adjust(&options)
        let receiver = try MockReceiver(options: options)
        receivers.append(receiver)
        return (receiver, try await receiver.start())
    }

    private func startRelay(listen: String = "127.0.0.1:0", sources: [String]? = nil, gateway: String? = nil, extra: [String: String] = [:]) async throws -> RelayProcess {
        let relay = try await RelayProcess.start(
            listen: listen,
            sourceTokens: sources ?? [CloudExample.token()],
            gatewayToken: gateway ?? CloudExample.token(),
            extra: extra
        )
        relays.append(relay)
        return relay
    }

    /// Starts the gateway with nothing from this process's environment but the token given.
    private func launch(_ arguments: [String], token: String?) throws -> ChildProcess {
        let home = try folder()
        var environment = ["PATH": "/usr/bin:/bin", "HOME": home.path, "CFFIXED_USER_HOME": home.path]
        if let token { environment["ERDIGO_RELAY_TOKEN"] = token }
        let child = try ChildProcess(try CloudExample.executable("erdigo-example-gateway"), arguments, environment: environment)
        children.append(child)
        return child
    }

    private func gateway(_ arguments: [String], token: String? = nil, timeout: TimeInterval = 60) async throws -> (status: Int32?, output: String) {
        let child = try launch(arguments, token: token)
        let status = await child.exitStatus(timeout: timeout)
        return (status, child.output)
    }

    private func runOnce(_ relay: RelayProcess, state: URL, port: UInt16, token: String? = nil, timeout: TimeInterval = 60) async throws -> (status: Int32?, output: String) {
        try await gateway(
            ["run", "--relay", relay.url, "--host", "127.0.0.1", "--port", String(port), "--once", "--state-dir", state.path],
            token: token ?? relay.gatewayToken,
            timeout: timeout
        )
    }

    private func writePrivate(_ text: String, to url: URL) throws {
        try Data(text.utf8).write(to: url)
        chmod(url.path, 0o600)
    }

    /// Pairs a gateway through a code file, with the QR text or with the bare code and --fingerprint.
    private func pairGateway(_ receiver: MockReceiver, port: UInt16, name: String, kinds: String? = nil, withFingerprintOption: Bool = false) async throws -> (state: URL, device: String) {
        let work = try folder()
        let state = work.appendingPathComponent("state")
        let codeFile = work.appendingPathComponent("code")
        let ticket = try await receiver.openPairingWindow()
        var arguments = ["pair", "--name", name, "--host", "127.0.0.1", "--port", String(port), "--code-file", codeFile.path, "--state-dir", state.path]
        if withFingerprintOption {
            try writePrivate(ticket.code.display, to: codeFile)
            arguments += ["--fingerprint", await receiver.fingerprint]
        } else {
            try writePrivate(try XCTUnwrap(ticket.uri), to: codeFile)
        }
        if let kinds { arguments += ["--kinds", kinds] }
        let result = try await gateway(arguments)
        XCTAssertEqual(result.status, 0, result.output)
        XCTAssertTrue(result.output.contains("Paired."), result.output)
        let devices = await receiver.pairedDevices()
        let device = try XCTUnwrap(devices.first { $0.name == name })
        return (state, device.fingerprint)
    }

    private func place(_ state: URL) throws -> [String: JSONValue] {
        let data = try Data(contentsOf: state.appendingPathComponent("relay-state.json"))
        guard case .object(let object) = try StrictJSON.parse(data) else { throw RawHTTP.Failure(description: "not an object") }
        return object
    }

    // MARK: Delivering

    func testEveryKindOfOperationReachesTheAppOnce() async throws {
        let (receiver, port) = try await startReceiver()
        let relay = try await startRelay()
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")

        XCTAssertEqual(try relay.post([
            #"{"op":"pin","id":"drone-1","lat":37.5663,"lon":126.9779,"title":"Drone 1"}"#,
            #"{"op":"marker","id":"m1","lat":1.5,"lon":2.5,"color":"blue","title":"Gate"}"#,
            #"{"op":"note","id":"n1","lat":0,"lon":0,"text":"line one\nline\ttwo"}"#,
            #"{"op":"photo","id":"ph1","lat":0,"lon":0,"type":"image/png","data":"\#(Self.png)","title":"View"}"#,
            #"{"op":"track","id":"t1","title":"Flight 7","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":120},{"lat":37.5664,"lon":126.978}]}"#,
        ].joined(separator: ",")).status, 200)
        XCTAssertEqual(try relay.post([
            #"{"op":"track","id":"t1","points":[{"lat":37.5665,"lon":126.9781,"ts":1700000002000}]}"#,
            #"{"op":"finish_track","id":"t1"}"#,
            #"{"op":"move","id":"drone-1","lat":-12.25,"lon":45.5}"#,
            #"{"op":"pin","id":"gone","lat":0,"lon":0}"#,
            #"{"op":"delete","id":"gone"}"#,
        ].joined(separator: ",")).status, 200)

        let first = try await runOnce(relay, state: state, port: port)
        XCTAssertEqual(first.status, 0, first.output)
        XCTAssertTrue(first.output.contains("Done with 10 operations, up to 10\n"), first.output)
        XCTAssertFalse(first.output.contains("Refused"), first.output)

        let items = await receiver.items(ofDevice: device)
        XCTAssertEqual(items.map(\.id), ["drone-1", "m1", "n1", "ph1", "t1"])
        let pin = try XCTUnwrap(items.first { $0.id == "drone-1" })
        XCTAssertEqual(pin.kind, .pin)
        XCTAssertEqual(pin.latitude, -12.25)
        XCTAssertEqual(pin.longitude, 45.5)
        XCTAssertEqual(pin.title, "Drone 1")
        let marker = try XCTUnwrap(items.first { $0.id == "m1" })
        XCTAssertEqual(marker.color, "blue")
        XCTAssertEqual(marker.title, "Gate")
        XCTAssertEqual(items.first { $0.id == "n1" }?.text, "line one\nline\ttwo")
        let photo = try XCTUnwrap(items.first { $0.id == "ph1" })
        XCTAssertEqual(photo.kind, .photo)
        XCTAssertNotNil(photo.photo)
        XCTAssertEqual(photo.title, "View")
        let track = try XCTUnwrap(items.first { $0.id == "t1" })
        XCTAssertEqual(track.title, "Flight 7")
        XCTAssertEqual(track.points.count, 3)
        XCTAssertEqual(track.points.first?.timestampMS, 1_700_000_000_000)
        XCTAssertEqual(track.points.first?.altitudeM, 120)
        XCTAssertTrue(track.isFinished)

        // The place is saved privately, and the relay forgot what was done on the next fetch.
        var info = stat()
        XCTAssertEqual(stat(state.appendingPathComponent("relay-state.json").path, &info), 0)
        XCTAssertEqual(info.st_mode & 0o777, 0o600)
        let saved = try place(state)
        XCTAssertEqual(saved["relay"], .string(relay.url))
        XCTAssertEqual(saved["after"], .integer(10))
        XCTAssertNotNil(saved["epoch"])
        XCTAssertEqual(try relay.http.get(token: relay.gatewayToken).opsCount, 0)

        // Running again applies nothing twice.
        let lastSeq = await receiver.lastSeq(ofDevice: device)
        let second = try await runOnce(relay, state: state, port: port)
        XCTAssertEqual(second.status, 0, second.output)
        XCTAssertFalse(second.output.contains("Done with"), second.output)
        let lastSeqAfter = await receiver.lastSeq(ofDevice: device)
        XCTAssertEqual(lastSeqAfter, lastSeq)
        let itemsAfter = await receiver.items(ofDevice: device)
        XCTAssertEqual(itemsAfter, items)

        // What it prints names no ids, places, titles, text or tokens.
        for secret in ["drone-1", "Drone 1", "37.5663", "Gate", "line one", "Flight 7", relay.gatewayToken, relay.sourceToken] {
            XCTAssertFalse((first.output + second.output).contains(secret), "the output holds something it should not")
        }
    }

    func testAnOperationWhoseAnswerWasLostIsAppliedOnce() async throws {
        let (receiver, port) = try await startReceiver()
        let relay = try await startRelay()
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        // Between the gateway and the app, the app's first answer is lost after the app has applied the request.
        let proxy = try LosingProxy(to: port)
        proxies.append(proxy)
        XCTAssertEqual(try relay.post(#"{"op":"track","id":"t1","title":"Flight 7","points":[{"lat":1,"lon":2},{"lat":1.5,"lon":2.5}]}"#).status, 200)

        let result = try await runOnce(relay, state: state, port: proxy.port)
        XCTAssertEqual(result.status, 0, result.output)
        XCTAssertTrue(proxy.lostAnAnswer, "the proxy never lost an answer")
        XCTAssertTrue(result.output.contains("Done with 1 operation, up to 1\n"), result.output)
        XCTAssertFalse(result.output.contains("Refused"), result.output)

        // The SDK sent the same request again with the same sequence number, so the app applied it once.
        let items = await receiver.items(ofDevice: device)
        let track = try XCTUnwrap(items.first { $0.id == "t1" }, result.output)
        XCTAssertEqual(track.points.count, 2, result.output)
        let lastSeq = await receiver.lastSeq(ofDevice: device)
        XCTAssertEqual(lastSeq, 1, result.output)
        XCTAssertEqual(try relay.http.get(token: relay.gatewayToken).opsCount, 0)
    }

    func testKindsThePersonDidNotAllowAreRefusedAndSkipped() async throws {
        let (receiver, port) = try await startReceiver()
        let relay = try await startRelay()
        // Paired with a bare code and the receiver's key given in full, which needs no terminal.
        let (state, device) = try await pairGateway(receiver, port: port, name: "Pins and notes", kinds: "pin,note", withFingerprintOption: true)
        XCTAssertEqual(try relay.post([
            #"{"op":"marker","id":"m1","lat":0,"lon":0}"#,
            #"{"op":"pin","id":"p1","lat":0,"lon":0}"#,
            #"{"op":"photo","id":"ph1","lat":0,"lon":0,"type":"image/png","data":"\#(Self.png)"}"#,
            #"{"op":"track","id":"t1","points":[{"lat":1,"lon":2}]}"#,
            #"{"op":"note","id":"n1","lat":0,"lon":0,"text":"kept"}"#,
            #"{"op":"move","id":"nowhere","lat":1,"lon":1}"#,
        ].joined(separator: ",")).status, 200)

        let result = try await runOnce(relay, state: state, port: port)
        XCTAssertEqual(result.status, 0, result.output)
        for line in ["Refused operation 1 (marker): not_permitted", "Refused operation 3 (photo): not_permitted",
                     "Refused operation 4 (track): not_permitted", "Refused operation 6 (move): not_found",
                     "Done with 6 operations, up to 6 (4 refused)"] {
            XCTAssertTrue(result.output.contains(line + "\n"), "missing '\(line)' in: \(result.output)")
        }
        let ids = await receiver.items(ofDevice: device).map(\.id)
        XCTAssertEqual(ids, ["n1", "p1"])
        XCTAssertEqual(try place(state)["after"], .integer(6))
        XCTAssertEqual(try relay.http.get(token: relay.gatewayToken).opsCount, 0)
    }

    /// Ways of failing for good while `--once` runs. They run side by side, because each takes 30 seconds or more.
    func testWithOnceTheGatewayGivesUpAfterAboutThirtySecondsAndKeepsTheOperations() async throws {
        // The app is closed.
        let (closed, closedPort) = try await startReceiver()
        let closedRelay = try await startRelay()
        let (closedState, _) = try await pairGateway(closed, port: closedPort, name: "Cloud gateway")
        let (silentState, _) = try await pairGateway(closed, port: closedPort, name: "Another gateway")
        await closed.stop()
        XCTAssertEqual(try closedRelay.post(#"{"op":"pin","id":"p1","lat":0,"lon":0}"#).status, 200)

        // The app sends a welcome that breaks a rule the SDK checks, so connecting fails before any operation is sent.
        let (odd, oddPort) = try await startReceiver { $0.limits.pointBurst = 50 }
        let oddRelay = try await startRelay()
        let (oddState, oddDevice) = try await pairGateway(odd, port: oddPort, name: "Cloud gateway")
        XCTAssertEqual(try oddRelay.post([
            #"{"op":"pin","id":"p1","lat":0,"lon":0}"#,
            #"{"op":"note","id":"n1","lat":0,"lon":0,"text":"kept"}"#,
            #"{"op":"delete","id":"p1"}"#,
        ].joined(separator: ",")).status, 200)

        // The app takes every connection, but each answer is lost on the way back. The SDK counts its time from the
        // last connection it made, so it would never give up by itself.
        let (lossy, lossyPort) = try await startReceiver()
        let lossyRelay = try await startRelay()
        let (lossyState, lossyDevice) = try await pairGateway(lossy, port: lossyPort, name: "Cloud gateway")
        let losing = try LosingProxy(to: lossyPort, mode: .always)
        proxies.append(losing)
        XCTAssertEqual(try lossyRelay.post(#"{"op":"track","id":"t1","points":[{"lat":1,"lon":2},{"lat":1.5,"lon":2.5}]}"#).status, 200)

        // The app takes every connection and never answers. The SDK notices after its 15 second answer timeout.
        let (mute, mutePort) = try await startReceiver()
        let muteRelay = try await startRelay()
        let (muteState, _) = try await pairGateway(mute, port: mutePort, name: "Cloud gateway")
        let holding = try LosingProxy(to: mutePort, mode: .hold)
        proxies.append(holding)
        XCTAssertEqual(try muteRelay.post(#"{"op":"pin","id":"p1","lat":0,"lon":0}"#).status, 200)

        // The relay takes the connection and never answers.
        let silent = try FakeRelay { _ in nil }
        fakes.append(silent)

        let started = Date()
        let closedRun = try launch(["run", "--relay", closedRelay.url, "--host", "127.0.0.1", "--port", String(closedPort), "--once", "--state-dir", closedState.path], token: closedRelay.gatewayToken)
        let oddRun = try launch(["run", "--relay", oddRelay.url, "--host", "127.0.0.1", "--port", String(oddPort), "--once", "--state-dir", oddState.path], token: oddRelay.gatewayToken)
        let lossyRun = try launch(["run", "--relay", lossyRelay.url, "--host", "127.0.0.1", "--port", String(losing.port), "--once", "--state-dir", lossyState.path], token: lossyRelay.gatewayToken)
        let muteRun = try launch(["run", "--relay", muteRelay.url, "--host", "127.0.0.1", "--port", String(holding.port), "--once", "--state-dir", muteState.path], token: muteRelay.gatewayToken)
        let silentRun = try launch(["run", "--relay", "http://127.0.0.1:\(silent.port)", "--host", "127.0.0.1", "--port", String(closedPort), "--once", "--state-dir", silentState.path], token: CloudExample.token())
        async let closedEnd = Self.end(of: closedRun, startedAt: started)
        async let oddEnd = Self.end(of: oddRun, startedAt: started)
        async let lossyEnd = Self.end(of: lossyRun, startedAt: started)
        async let muteEnd = Self.end(of: muteRun, startedAt: started)
        async let silentEnd = Self.end(of: silentRun, startedAt: started)
        // About 30 seconds from the first sign of trouble. For an app that never answers, that comes 15 seconds in.
        let ends = await [("app closed", closedEnd, 45.0), ("welcome not usable", oddEnd, 45), ("answers lost", lossyEnd, 45),
                          ("app never answers", muteEnd, 60), ("relay silent", silentEnd, 45)]

        for (name, end, longest) in ends {
            XCTAssertEqual(end.status, 3, "\(name): \(end.output)")
            XCTAssertGreaterThan(end.seconds, 25, name)
            XCTAssertLessThan(end.seconds, longest, name)
            XCTAssertFalse(end.output.contains("Refused"), "\(name): \(end.output)")
        }
        for (name, end, _) in ends.prefix(4) {
            XCTAssertTrue(end.output.contains("The Erdigo app could not be reached. The operations stay on the relay.\n"), "\(name): \(end.output)")
        }
        // The SDK waits for the app, and does not say when it tries next, so the gateway says once that it waits.
        for (name, run) in [("app closed", closedRun), ("answers lost", lossyRun), ("app never answers", muteRun)] {
            XCTAssertEqual(run.output.components(separatedBy: "No answer from the Erdigo app; trying again.\n").count, 2, "\(name): \(run.output)")
        }
        XCTAssertGreaterThan(losing.lostAnswers, 1)
        XCTAssertGreaterThan(holding.lostAnswers, 0)
        // A welcome the SDK cannot use is not waited out by the SDK, so the gateway waits itself and says how long.
        XCTAssertTrue(oddRun.output.contains("No answer from the Erdigo app; trying again in 1 second.\n"), oddRun.output)
        XCTAssertTrue(silentRun.output.contains("The relay could not be reached; trying again in 1 second.\n"), silentRun.output)
        XCTAssertTrue(silentRun.output.hasSuffix("The relay could not be reached.\n"), silentRun.output)

        // Nothing was acknowledged, so the relays still hold every operation.
        XCTAssertEqual(try closedRelay.http.get(token: closedRelay.gatewayToken).seqs, [1])
        XCTAssertEqual(try oddRelay.http.get(token: oddRelay.gatewayToken).seqs, [1, 2, 3])
        XCTAssertEqual(try lossyRelay.http.get(token: lossyRelay.gatewayToken).seqs, [1])
        XCTAssertEqual(try muteRelay.http.get(token: muteRelay.gatewayToken).seqs, [1])
        let oddItems = await odd.items(ofDevice: oddDevice)
        XCTAssertEqual(oddItems, [])
        // However often the lost answer made the SDK send the track again, it kept its sequence number.
        let lossyItems = await lossy.items(ofDevice: lossyDevice)
        XCTAssertEqual(lossyItems.first { $0.id == "t1" }?.points.count, 2, lossyRun.output)
    }

    private static func end(of child: ChildProcess, startedAt started: Date) async -> (status: Int32?, output: String, seconds: TimeInterval) {
        let status = await child.exitStatus(timeout: 90)
        return (status, child.output, Date().timeIntervalSince(started))
    }

    func testAnAnswerTheSDKCannotUseIsTriedAgainRatherThanRefused() async throws {
        let (receiver, port) = try await startReceiver()
        let relay = try await startRelay()
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        // The first pin is answered with an acknowledgement of a request that was never sent.
        let spoiled = OneTime()
        await receiver.setAnswerOverride { envelope in
            guard case .putPin = envelope.request, spoiled.take() else { return nil }
            return .ack(seq: envelope.seq + 1000, duplicate: false)
        }
        XCTAssertEqual(try relay.post([
            #"{"op":"pin","id":"p1","lat":0,"lon":0}"#,
            #"{"op":"note","id":"n1","lat":0,"lon":0,"text":"after it"}"#,
        ].joined(separator: ",")).status, 200)

        let result = try await runOnce(relay, state: state, port: port)
        XCTAssertEqual(result.status, 0, result.output)
        XCTAssertTrue(result.output.contains("No answer from the Erdigo app; trying again in 1 second.\n"), result.output)
        XCTAssertTrue(result.output.contains("Done with 2 operations, up to 2\n"), result.output)
        XCTAssertFalse(result.output.contains("Refused"), result.output)
        let ids = await receiver.items(ofDevice: device).map(\.id)
        XCTAssertEqual(ids, ["n1", "p1"])
    }

    func testARelayThatAnswersAtOnceIsNotAskedAgainStraightAway() async throws {
        let (receiver, port) = try await startReceiver()
        let (state, _) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        // Like a relay that cannot hold a fetch open: every fetch gets an empty answer at once.
        let epoch = "AAAAAAAAAAAAAAAAAAAAAA"
        let requests = Moments()
        let fake = try FakeRelay { requestLine in
            requests.note()
            let reset = requestLine.contains("epoch=") ? "false" : "true"
            return FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":\#(reset),"missed":false,"ops":[]}"#)
        }
        fakes.append(fake)
        let running = try launch(["run", "--relay", "http://127.0.0.1:\(fake.port)", "--host", "127.0.0.1", "--port", String(port), "--state-dir", state.path], token: CloudExample.token())

        let giveUp = Date().addingTimeInterval(15)
        while requests.all.count < 2, Date() < giveUp, running.process.isRunning {
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        let times = requests.all
        XCTAssertGreaterThanOrEqual(times.count, 2, running.output)
        if times.count >= 2 {
            XCTAssertGreaterThan(times[1] - times[0], 4.5, "the gateway asked again too soon")
            XCTAssertLessThan(times[1] - times[0], 7, "the gateway did not ask again")
        }
        running.signal(SIGTERM)
        let status = await running.exitStatus(timeout: 10)
        XCTAssertEqual(status, 0, running.output)
    }

    func testAConnectionTheAppClosedWhileIdleIsOpenedAgainQuietly() async throws {
        let (receiver, port) = try await startReceiver { $0.idleTimeout = 1 }
        let relay = try await startRelay()
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        let running = try launch(["run", "--relay", relay.url, "--host", "127.0.0.1", "--port", String(port), "--state-dir", state.path], token: relay.gatewayToken)
        for id in ["first", "second"] {
            XCTAssertEqual(try relay.post(#"{"op":"pin","id":"\#(id)","lat":0,"lon":0}"#).status, 200)
            let giveUp = Date().addingTimeInterval(10)
            while Date() < giveUp, await !receiver.items(ofDevice: device).contains(where: { $0.id == id }) {
                try await Task.sleep(nanoseconds: 50_000_000)
            }
            // Longer than the app keeps an idle connection open.
            try await Task.sleep(nanoseconds: 2_000_000_000)
        }
        let ids = await receiver.items(ofDevice: device).map(\.id)
        XCTAssertEqual(ids, ["first", "second"], running.output)
        XCTAssertFalse(running.output.contains("No answer"), running.output)
        running.signal(SIGTERM)
        let status = await running.exitStatus(timeout: 10)
        XCTAssertEqual(status, 0, running.output)
    }

    func testARelayThatStartsAgainOrIsReplacedIsFollowed() async throws {
        let (receiver, port) = try await startReceiver()
        let sources = [CloudExample.token()]
        let gatewayToken = CloudExample.token()
        let first = try await startRelay(sources: sources, gateway: gatewayToken)
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        XCTAssertEqual(try first.post(#"{"op":"pin","id":"a","lat":0,"lon":0}"#).status, 200)
        let before = try await runOnce(first, state: state, port: port)
        XCTAssertEqual(before.status, 0, before.output)
        let firstEpoch = try place(state)["epoch"]

        // The same address, a new numbering.
        let stopped = await first.stop()
        XCTAssertEqual(stopped, 0)
        let again = try await startRelay(listen: "127.0.0.1:\(first.port)", sources: sources, gateway: gatewayToken)
        XCTAssertEqual(try again.post(#"{"op":"pin","id":"b","lat":0,"lon":0}"#).status, 200)
        let after = try await runOnce(again, state: state, port: port)
        XCTAssertEqual(after.status, 0, after.output)
        XCTAssertTrue(after.output.contains("The relay started a new numbering; starting from its oldest operation.\n"), after.output)
        XCTAssertTrue(after.output.contains("Done with 1 operation, up to 1\n"), after.output)
        XCTAssertNotEqual(try place(state)["epoch"], firstEpoch)

        // Another address starts from that relay's oldest operation, without a word about numbering.
        let other = try await startRelay(sources: sources, gateway: gatewayToken)
        XCTAssertEqual(try other.post(#"{"op":"pin","id":"c","lat":0,"lon":0}"#).status, 200)
        let moved = try await runOnce(other, state: state, port: port)
        XCTAssertEqual(moved.status, 0, moved.output)
        XCTAssertFalse(moved.output.contains("new numbering"), moved.output)
        XCTAssertEqual(try place(state)["relay"], .string(other.url))
        let ids = await receiver.items(ofDevice: device).map(\.id)
        XCTAssertEqual(ids, ["a", "b", "c"])
    }

    func testOperationsThatExpiredAreReportedOnce() async throws {
        let (receiver, port) = try await startReceiver()
        let relay = try await startRelay(extra: ["ERDIGO_RELAY_TEST_KEEP_SECONDS": "1"])
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        XCTAssertEqual(try relay.post(#"{"op":"pin","id":"a","lat":0,"lon":0}"#).status, 200)
        let first = try await runOnce(relay, state: state, port: port)
        XCTAssertEqual(first.status, 0, first.output)
        XCTAssertFalse(first.output.contains("expired"), first.output)

        XCTAssertEqual(try relay.post(#"{"op":"pin","id":"b","lat":0,"lon":0}"#).status, 200)
        try await Task.sleep(nanoseconds: 1_500_000_000)
        XCTAssertEqual(try relay.post(#"{"op":"pin","id":"c","lat":0,"lon":0}"#).status, 200)
        let second = try await runOnce(relay, state: state, port: port)
        XCTAssertEqual(second.status, 0, second.output)
        XCTAssertEqual(second.output.components(separatedBy: "Some operations expired on the relay before they were fetched.\n").count, 2, second.output)
        let ids = await receiver.items(ofDevice: device).map(\.id)
        XCTAssertEqual(ids, ["a", "c"])
    }

    func testAGatewayLeftRunningDeliversAtOnceAndStopsCleanly() async throws {
        let (receiver, port) = try await startReceiver()
        let relay = try await startRelay()
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        let running = try launch(["run", "--relay", relay.url, "--host", "127.0.0.1", "--port", String(port), "--state-dir", state.path], token: relay.gatewayToken)
        // Let it reach its waiting fetch, then send.
        try await Task.sleep(nanoseconds: 1_000_000_000)
        XCTAssertEqual(try relay.post(#"{"op":"pin","id":"live","lat":0,"lon":0}"#).status, 200)
        let sent = Date()
        var delivered = false
        while Date().timeIntervalSince(sent) < 10 {
            if await receiver.items(ofDevice: device).contains(where: { $0.id == "live" }) {
                delivered = true
                break
            }
            try await Task.sleep(nanoseconds: 50_000_000)
        }
        XCTAssertTrue(delivered, running.output)
        XCTAssertLessThan(Date().timeIntervalSince(sent), 5)

        // A second gateway with the same key would fight the first for the connection.
        let second = try await runOnce(relay, state: state, port: port)
        XCTAssertEqual(second.status, 1, second.output)
        XCTAssertTrue(second.output.contains("another gateway is already running"), second.output)

        // Without --once the SDK waits for an app it cannot reach for as long as it takes, and stopping still
        // ends that wait at once. The operation was never acknowledged, so it stays on the relay.
        await receiver.stop()
        XCTAssertEqual(try relay.post(#"{"op":"pin","id":"later","lat":0,"lon":0}"#).status, 200)
        let waiting = await running.waitForLine(startingWith: "No answer from the Erdigo app", timeout: 10)
        XCTAssertEqual(waiting, "No answer from the Erdigo app; trying again.", running.output)
        let stopping = Date()
        running.signal(SIGTERM)
        let status = await running.exitStatus(timeout: 10)
        XCTAssertEqual(status, 0, running.output)
        XCTAssertLessThan(Date().timeIntervalSince(stopping), 4, running.output)
        XCTAssertEqual(try relay.http.get(token: relay.gatewayToken).seqs, [2])
        XCTAssertEqual(try place(state)["after"], .integer(1))
    }

    // MARK: Mistakes

    func testMistakesInTheCommandLineOrEnvironment() async throws {
        let (receiver, port) = try await startReceiver()
        let relay = try await startRelay()
        let (state, _) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        let address = ["--host", "127.0.0.1", "--port", String(port), "--state-dir", state.path]
        let token = relay.gatewayToken

        let mistakes: [(String, [String], String?, String)] = [
            ("plain http to another computer", ["run", "--relay", "http://relay.example.com", "--once"] + address, token, "must start with https://"),
            ("another scheme", ["run", "--relay", "ftp://127.0.0.1/", "--once"] + address, token, "must start with https://"),
            ("a query in the address", ["run", "--relay", "https://relay.example.com/?a=1", "--once"] + address, token, "--relay must be"),
            ("no token", ["run", "--relay", relay.url, "--once"] + address, nil, "ERDIGO_RELAY_TOKEN is not set"),
            ("a token too short", ["run", "--relay", relay.url, "--once"] + address, "short", "ERDIGO_RELAY_TOKEN is not"),
            ("a token as an argument", ["run", "--relay", relay.url, "--once", "--token", token] + address, token, "not accepted as arguments"),
            ("no relay", ["run", "--once"] + address, token, "--relay is required"),
            ("a host without a port", ["run", "--relay", relay.url, "--host", "127.0.0.1", "--state-dir", state.path], token, "go together"),
            ("both a profile and a folder", ["run", "--relay", relay.url, "--profile", "x"] + address, token, "cannot be used together"),
            ("an unknown command", ["send"], token, "unknown command"),
        ]
        for (name, arguments, environmentToken, wanted) in mistakes {
            let result = try await gateway(arguments, token: environmentToken)
            XCTAssertEqual(result.status, 1, "\(name): \(result.output)")
            XCTAssertTrue(result.output.contains(wanted), "\(name): \(result.output)")
            XCTAssertFalse(result.output.contains(token), "\(name): the token was repeated")
        }

        let unpaired = try folder().appendingPathComponent("state")
        let noPairing = try await gateway(["run", "--relay", relay.url, "--once", "--state-dir", unpaired.path], token: token)
        XCTAssertEqual(noPairing.status, 1, noPairing.output)
        XCTAssertTrue(noPairing.output.contains("no pairing"), noPairing.output)

        let wrong = CloudExample.token()
        let refused = try await gateway(["run", "--relay", relay.url, "--once"] + address, token: wrong)
        XCTAssertEqual(refused.status, 2, refused.output)
        XCTAssertTrue(refused.output.contains("The relay refused the token."), refused.output)
        XCTAssertFalse(refused.output.contains(wrong))

        // Pairing mistakes stop before the receiver is contacted.
        let work = try folder()
        let codeFile = work.appendingPathComponent("code")
        let ticket = try await receiver.openPairingWindow()
        try writePrivate(ticket.code.display, to: codeFile)
        let pairing = ["pair", "--name", "Second", "--host", "127.0.0.1", "--port", String(port), "--code-file", codeFile.path]
        let bare = try await gateway(pairing + ["--state-dir", work.appendingPathComponent("bare").path])
        XCTAssertEqual(bare.status, 1, bare.output)
        XCTAssertTrue(bare.output.contains("not a terminal"), bare.output)
        chmod(codeFile.path, 0o644)
        let readable = try await gateway(pairing + ["--state-dir", work.appendingPathComponent("readable").path])
        XCTAssertEqual(readable.status, 1, readable.output)
        XCTAssertTrue(readable.output.contains("mode 0600"), readable.output)
        try writePrivate(try XCTUnwrap(ticket.uri), to: codeFile)
        let twice = try await gateway(pairing + ["--state-dir", state.path])
        XCTAssertEqual(twice.status, 1, twice.output)
        XCTAssertTrue(twice.output.contains("already paired"), twice.output)
        let asArgument = try await gateway(["pair", "--name", "x", "--code", ticket.code.display, "--state-dir", work.appendingPathComponent("argument").path])
        XCTAssertEqual(asArgument.status, 1)
        XCTAssertFalse(asArgument.output.contains(ticket.code.display))
        let devices = await receiver.pairedDevices()
        XCTAssertEqual(devices.map(\.name), ["Cloud gateway"])
    }

    func testAnswersOutsideTheRelayAPIStopTheGateway() async throws {
        let (receiver, port) = try await startReceiver()
        let (state, device) = try await pairGateway(receiver, port: port, name: "Cloud gateway")
        let epoch = "AAAAAAAAAAAAAAAAAAAAAA"
        let entry = #"{"seq":1,"received_ms":1700000000000,"op":{"op":"pin","id":"p1","lat":0,"lon":0}}"#
        let broken: [(String, Data)] = [
            ("not JSON", FakeRelay.answer(200, "<html></html>", type: "text/html")),
            ("JSON said to be text", FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":true,"missed":false,"ops":[]}"#, type: "text/plain")),
            ("a list", FakeRelay.answer(200, "[]")),
            ("a short epoch", FakeRelay.answer(200, #"{"epoch":"short","reset":true,"missed":false,"ops":[]}"#)),
            ("an extra field", FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":true,"missed":false,"ops":[],"more":1}"#)),
            ("no reset without an epoch", FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":false,"missed":false,"ops":[]}"#)),
            ("reset as a string", FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":"true","missed":false,"ops":[]}"#)),
            ("sequence number 0", FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":true,"missed":false,"ops":[\#(entry.replacingOccurrences(of: "\"seq\":1", with: "\"seq\":0"))]}"#)),
            ("numbers going down", FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":true,"missed":false,"ops":[\#(entry.replacingOccurrences(of: "\"seq\":1", with: "\"seq\":2")),\#(entry)]}"#)),
            ("an operation that is not an object", FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":true,"missed":false,"ops":[{"seq":1,"received_ms":0,"op":"pin"}]}"#)),
            ("not found", FakeRelay.answer(404, #"{"error":"not_found"}"#)),
            ("a redirect", FakeRelay.answer(302, "", extra: "Location: http://127.0.0.1:9/v1/ops\r\n")),
            ("over 8 MiB", Data("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 9000000\r\nConnection: close\r\n\r\n{".utf8)),
        ]
        for (name, answer) in broken {
            let fake = try FakeRelay { _ in answer }
            fakes.append(fake)
            let result = try await gateway(
                ["run", "--relay", "http://127.0.0.1:\(fake.port)", "--host", "127.0.0.1", "--port", String(port), "--once", "--state-dir", state.path],
                token: CloudExample.token()
            )
            XCTAssertEqual(result.status, 2, "\(name): \(result.output)")
            XCTAssertTrue(result.output.contains("does not follow the relay API"), "\(name): \(result.output)")
        }

        // An operation it cannot use is refused alone, and the gateway goes on.
        let mixed = try FakeRelay { requestLine in
            if requestLine.contains("epoch=") {
                return FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":false,"missed":false,"ops":[]}"#)
            }
            return FakeRelay.answer(200, #"{"epoch":"\#(epoch)","reset":true,"missed":false,"ops":[{"seq":1,"received_ms":0,"op":{"op":"circle","id":"c"}},{"seq":2,"received_ms":0,"op":{"op":"pin","id":"bad id","lat":0,"lon":0}},{"seq":3,"received_ms":0,"op":{"op":"pin","id":"ok","lat":0,"lon":0,"speed":3}},{"seq":4,"received_ms":0,"op":{"op":"pin","id":"good","lat":0,"lon":0}}]}"#)
        }
        fakes.append(mixed)
        let result = try await gateway(
            ["run", "--relay", "http://127.0.0.1:\(mixed.port)", "--host", "127.0.0.1", "--port", String(port), "--once", "--state-dir", state.path],
            token: CloudExample.token()
        )
        XCTAssertEqual(result.status, 0, result.output)
        for line in ["Refused operation 1 (unknown): invalid_op", "Refused operation 2 (pin): invalid_id", "Refused operation 3 (pin): invalid_op", "Done with 4 operations, up to 4 (3 refused)"] {
            XCTAssertTrue(result.output.contains(line + "\n"), "missing '\(line)' in: \(result.output)")
        }
        let ids = await receiver.items(ofDevice: device).map(\.id)
        XCTAssertEqual(ids, ["good"])
    }
}

/// Moments something happened, from any thread.
final class Moments: @unchecked Sendable {
    private let lock = NSLock()
    private var times: [TimeInterval] = []

    func note() {
        lock.lock()
        times.append(ProcessInfo.processInfo.systemUptime)
        lock.unlock()
    }

    var all: [TimeInterval] {
        lock.lock()
        defer { lock.unlock() }
        return times
    }
}

/// True the first time only, from any thread.
final class OneTime: @unchecked Sendable {
    private let lock = NSLock()
    private var used = false

    func take() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !used else { return false }
        used = true
        return true
    }
}

/// A server that gives the same kind of answer to every request, to show what the gateway does with
/// answers a real relay never gives. With no answer, it holds the connection open and says nothing.
final class FakeRelay: @unchecked Sendable {
    let port: UInt16
    private let listening: Int32
    private let lock = NSLock()
    private var held: [Int32] = []

    static func answer(_ status: Int, _ body: String, type: String = "application/json", extra: String = "") -> Data {
        Data("HTTP/1.1 \(status) Answer\r\nContent-Type: \(type)\r\nContent-Length: \(body.utf8.count)\r\nConnection: close\r\n\(extra)\r\n\(body)".utf8)
    }

    init(_ respond: @escaping @Sendable (String) -> Data?) throws {
        let listening = socket(AF_INET, SOCK_STREAM, 0)
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let bound = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { pointer -> Bool in
                bind(listening, pointer, length) == 0 && listen(listening, 16) == 0 && getsockname(listening, pointer, &length) == 0
            }
        }
        guard bound else {
            close(listening)
            throw RawHTTP.Failure(description: "the fake relay could not listen")
        }
        port = UInt16(bigEndian: address.sin_port)
        self.listening = listening
        Thread {
            while true {
                let client = accept(listening, nil, nil)
                guard client >= 0 else { return }
                var interval = timeval(tv_sec: 5, tv_usec: 0)
                setsockopt(client, SOL_SOCKET, SO_RCVTIMEO, &interval, socklen_t(MemoryLayout<timeval>.size))
                var one: Int32 = 1
                setsockopt(client, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
                var request = Data()
                var buffer = [UInt8](repeating: 0, count: 4096)
                while request.range(of: Data("\r\n\r\n".utf8)) == nil {
                    let count = recv(client, &buffer, buffer.count, 0)
                    if count <= 0 { break }
                    request.append(buffer, count: count)
                }
                let firstLine = String(decoding: request, as: UTF8.self).components(separatedBy: "\r\n").first ?? ""
                guard let answer = respond(firstLine) else {
                    self.lock.lock()
                    self.held.append(client)
                    self.lock.unlock()
                    continue
                }
                try? RawHTTP.sendAll(client, answer)
                close(client)
            }
        }.start()
    }

    func stop() {
        shutdown(listening, SHUT_RDWR)
        close(listening)
        lock.lock()
        for client in held { close(client) }
        held.removeAll()
        lock.unlock()
    }
}

/// A TCP proxy in front of a receiver that loses answers. It passes the device's bytes on, and passes the receiver's
/// frames back, except the answer to the first request on a connection, as `Mode` says. So the receiver has applied
/// the request, and the device never hears. The frames are encrypted, so they are counted rather than read: the
/// receiver's third frame on a connection, after the handshake reply and the welcome, answers the first request.
final class LosingProxy: @unchecked Sendable {
    enum Mode {
        /// Loses that answer once, on the first connection that gets that far, and closes both connections.
        case once
        /// Loses it on every connection, and closes both connections each time.
        case always
        /// Holds it back on every connection, and keeps both connections open without a word.
        case hold
    }

    let port: UInt16
    private let mode: Mode
    private let listening: Int32
    private let lock = NSLock()
    private var losses = 0
    private var pairs: [Pair] = []

    /// One device connection and its connection to the receiver. The thread that ends last closes both, so a
    /// descriptor is never closed while the other thread may still use it.
    private final class Pair: @unchecked Sendable {
        let device: Int32
        let receiver: Int32
        private let lock = NSLock()
        private var running = 2
        private var closed = false

        init(device: Int32, receiver: Int32) {
            self.device = device
            self.receiver = receiver
        }

        func cut() {
            lock.lock()
            defer { lock.unlock() }
            guard !closed else { return }
            shutdown(device, SHUT_RDWR)
            shutdown(receiver, SHUT_RDWR)
        }

        func finished() {
            lock.lock()
            defer { lock.unlock() }
            guard !closed else { return }
            shutdown(device, SHUT_RDWR)
            shutdown(receiver, SHUT_RDWR)
            running -= 1
            if running == 0 {
                close(device)
                close(receiver)
                closed = true
            }
        }
    }

    /// How many answers it has lost or held back.
    var lostAnswers: Int {
        lock.lock()
        defer { lock.unlock() }
        return losses
    }

    var lostAnAnswer: Bool { lostAnswers > 0 }

    init(to receiverPort: UInt16, mode: Mode = .once) throws {
        self.mode = mode
        let listening = socket(AF_INET, SOCK_STREAM, 0)
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_addr.s_addr = inet_addr("127.0.0.1")
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let bound = withUnsafeMutablePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { pointer -> Bool in
                bind(listening, pointer, length) == 0 && listen(listening, 16) == 0 && getsockname(listening, pointer, &length) == 0
            }
        }
        guard bound else {
            close(listening)
            throw RawHTTP.Failure(description: "the proxy could not listen")
        }
        port = UInt16(bigEndian: address.sin_port)
        self.listening = listening
        Thread {
            while true {
                let device = accept(listening, nil, nil)
                guard device >= 0 else { return }
                var one: Int32 = 1
                setsockopt(device, SOL_SOCKET, SO_NOSIGPIPE, &one, socklen_t(MemoryLayout<Int32>.size))
                guard let receiver = try? RawHTTP(port: receiverPort).connect(timeout: 120) else {
                    close(device)
                    continue
                }
                let pair = Pair(device: device, receiver: receiver)
                self.lock.lock()
                self.pairs.append(pair)
                self.lock.unlock()
                Thread { self.passRequests(pair) }.start()
                Thread { self.passAnswers(pair) }.start()
            }
        }.start()
    }

    private func passRequests(_ pair: Pair) {
        defer { pair.finished() }
        var buffer = [UInt8](repeating: 0, count: 65536)
        while true {
            let count = recv(pair.device, &buffer, buffer.count, 0)
            guard count > 0, (try? RawHTTP.sendAll(pair.receiver, Data(buffer[0..<count]))) != nil else { return }
        }
    }

    private func passAnswers(_ pair: Pair) {
        defer { pair.finished() }
        var buffer = [UInt8](repeating: 0, count: 65536)
        var pending = Data()
        var frames = 0
        while true {
            let count = recv(pair.receiver, &buffer, buffer.count, 0)
            guard count > 0 else { return }
            pending.append(contentsOf: buffer[0..<count])
            // Each frame is a two-byte length and that many bytes.
            while pending.count >= 2 {
                let size = 2 + (Int(pending[pending.startIndex]) << 8 | Int(pending[pending.startIndex + 1]))
                guard pending.count >= size else { break }
                let frame = pending.prefix(size)
                pending = Data(pending.dropFirst(size))
                frames += 1
                if frames == 3 && takeTheLoss() {
                    guard mode == .hold else {
                        pair.cut()
                        return
                    }
                    // Say nothing more, until the device gives up on this connection or the proxy stops.
                    while recv(pair.receiver, &buffer, buffer.count, 0) > 0 {}
                    return
                }
                guard (try? RawHTTP.sendAll(pair.device, Data(frame))) != nil else { return }
            }
        }
    }

    /// Whether to lose this answer, and counts it if so.
    private func takeTheLoss() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard mode != .once || losses == 0 else { return false }
        losses += 1
        return true
    }

    func stop() {
        shutdown(listening, SHUT_RDWR)
        close(listening)
        lock.lock()
        let open = pairs
        pairs.removeAll()
        lock.unlock()
        for pair in open { pair.cut() }
    }
}
