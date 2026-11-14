import ErdigoCore
@_spi(TestVectors) import ErdigoMockReceiver
import ErdigoSDK
import Foundation
import XCTest

/// How the client takes error answers, including ones the mock receiver would not give by itself.
/// Each test replaces the receiver's answer to chosen requests and records the numbers it was sent.
final class AnswerHandlingTests: XCTestCase {
    /// Answers the first `times` requests as told, then lets requests run.
    private final class Script: @unchecked Sendable {
        private let lock = NSLock()
        private var seen: [UInt64] = []
        private let reply: @Sendable (RequestEnvelope) -> ReceiverError
        private let times: Int

        init(times: Int = 1, _ reply: @escaping @Sendable (RequestEnvelope) -> ReceiverError) {
            self.reply = reply
            self.times = times
        }

        var numbersSeen: [UInt64] {
            lock.lock()
            defer { lock.unlock() }
            return seen
        }

        func answer(_ request: RequestEnvelope) -> ReceiverMessage? {
            lock.lock()
            defer { lock.unlock() }
            seen.append(request.seq)
            return seen.count <= times ? .error(reply(request)) : nil
        }
    }

    private struct Scripted {
        let receiver: MockReceiver
        let client: ErdigoClient
        let fingerprint: String
    }

    private static let palace = try! Coordinate(latitude: 37.5796, longitude: 126.9770)
    private var receivers: [MockReceiver] = []

    override func tearDown() async throws {
        for receiver in receivers { await receiver.stop() }
        receivers.removeAll()
    }

    /// Pairs a device with a new receiver, then lets `script` answer for the receiver.
    private func scripted(_ script: Script, retry: RetryPolicy = .upTo(10)) async throws -> Scripted {
        var options = MockReceiver.Options()
        options.silentDelay = 0...0.01
        options.addressPenaltyBase = 0
        // These tests are not about pacing.
        options.limits.messagesPerSecond = 10_000
        options.limits.messageBurst = 10_000
        let receiver = try MockReceiver(options: options)
        receivers.append(receiver)
        let port = try await receiver.start()

        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let pairing = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port),
            ticket: try await receiver.openPairingWindow(),
            identity: identity,
            deviceName: "Test device",
            kinds: Set(ItemKind.allCases),
            handshakeTimeout: 1
        )
        try store.save(pairing)
        // Retrying for a while means a wrongly classified answer would show up as a second connection.
        let client = ErdigoClient(
            identity: identity,
            pairing: pairing,
            store: store,
            target: .address(host: "127.0.0.1", port: port),
            retry: retry,
            connectTimeout: 1,
            ackTimeout: 3
        )
        await receiver.setAnswerOverride { script.answer($0) }
        return Scripted(receiver: receiver, client: client, fingerprint: identity.fingerprint)
    }

    private func thrown(_ body: () async throws -> Void) async -> Error? {
        do {
            try await body()
            return nil
        } catch {
            return error
        }
    }

    private func connections(_ device: Scripted) async -> Int {
        await device.receiver.events.filter { $0 == .connected(device: device.fingerprint) }.count
    }

    /// Waits until the receiver has noted the end of the connection. A fixed pause is not enough on a busy machine.
    private func waitForDisconnection(_ device: Scripted, file: StaticString = #filePath, line: UInt = #line) async throws {
        let giveUp = Date().addingTimeInterval(5)
        while Date() < giveUp {
            if await device.receiver.events.contains(.disconnected(device: device.fingerprint)) { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("the receiver did not note the end of the connection", file: file, line: line)
    }

    // MARK: Answers that end the connection

    func testFatalRateLimitedEndsTheConnectionWithOneError() async throws {
        let script = Script { ReceiverError(code: ErrorCode.rateLimited.rawValue, seq: $0.seq, retryAfterMS: 50, fatal: true) }
        let device = try await scripted(script)

        let error = await thrown { try await device.client.ping() }
        XCTAssertEqual(error as? ErdigoError, .rejected(ReceiverError(code: ErrorCode.rateLimited.rawValue, seq: 1, retryAfterMS: 50, fatal: true)))

        // Sent once, never resent on the closed connection or on a new one.
        XCTAssertEqual(script.numbersSeen, [1])
        let connected = await connections(device)
        XCTAssertEqual(connected, 1)
        let limits = await device.client.limits
        XCTAssertNil(limits)
        try await waitForDisconnection(device)
        let last = await device.receiver.lastSeq(ofDevice: device.fingerprint)
        XCTAssertEqual(last, 0)
    }

    /// A fatal answer is about the connection, so a number it carries for another request changes nothing.
    func testFatalAnswersEndTheConnectionAsTheirCodeSays() async throws {
        let cases: [(code: String, numberForAnotherRequest: Bool)] = [
            ("rate_limited", false),
            ("rate_limited", true),
            ("too_many_errors", false),
            ("too_many_errors", true),
            ("internal_error", false),
            ("replaced", false),
            ("replaced", true),
            ("seq_conflict", false),
            ("stale_seq", true),
            ("future_code", true),
        ]
        for (code, numberForAnotherRequest) in cases {
            let context = "\(code), number for another request: \(numberForAnotherRequest)"
            let answer: @Sendable (UInt64) -> ReceiverError = { seq in
                ReceiverError(
                    code: code,
                    seq: numberForAnotherRequest ? seq + 7 : seq,
                    lastSeq: numberForAnotherRequest ? 1000 : nil,
                    retryAfterMS: 50,
                    fatal: true
                )
            }
            let script = Script { answer($0.seq) }
            let device = try await scripted(script)

            let error = await thrown { try await device.client.ping() }
            let expected: ErdigoError
            switch code {
            case ErrorCode.replaced.rawValue:
                expected = .replaced
            case ErrorCode.seqConflict.rawValue, ErrorCode.staleSeq.rawValue:
                // The number in an answer to another request is not taken as where the receiver stands.
                expected = .sequenceConflict(lastSeq: 0)
            default:
                expected = .rejected(answer(1))
            }
            XCTAssertEqual(error as? ErdigoError, expected, context)
            XCTAssertEqual(script.numbersSeen, [1], context)
            let connected = await connections(device)
            XCTAssertEqual(connected, 1, context)
            let limits = await device.client.limits
            XCTAssertNil(limits, context)
        }
    }

    func testPausedStillMeansReconnectAndResendWithTheSameNumber() async throws {
        let script = Script { _ in ReceiverError(code: ErrorCode.paused.rawValue, fatal: true) }
        let device = try await scripted(script)

        try await device.client.ping()
        XCTAssertEqual(script.numbersSeen, [1, 1])
        let connected = await connections(device)
        XCTAssertEqual(connected, 2)
        let last = await device.receiver.lastSeq(ofDevice: device.fingerprint)
        XCTAssertEqual(last, 1)
    }

    func testPausedWithoutRetryingReportsNoAnswer() async throws {
        let script = Script { _ in ReceiverError(code: ErrorCode.paused.rawValue, fatal: true) }
        let device = try await scripted(script, retry: .none)

        let error = await thrown { try await device.client.ping() }
        XCTAssertEqual(error as? ErdigoError, .noAnswer)
        XCTAssertEqual(script.numbersSeen, [1])
    }

    // MARK: Answers that keep the connection

    func testRateLimitedIsResentWithTheSameNumberOnTheSameConnection() async throws {
        // A receiver leaves out the number when it limits a message before reading it.
        for answerCarriesTheNumber in [true, false] {
            let context = "answer carries the number: \(answerCarriesTheNumber)"
            let script = Script(times: 2) {
                ReceiverError(code: ErrorCode.rateLimited.rawValue, seq: answerCarriesTheNumber ? $0.seq : nil, retryAfterMS: 20)
            }
            let device = try await scripted(script)

            try await device.client.ping()
            try await device.client.ping()
            XCTAssertEqual(script.numbersSeen, [1, 1, 1, 2], context)
            let connected = await connections(device)
            XCTAssertEqual(connected, 1, context)
            let last = await device.receiver.lastSeq(ofDevice: device.fingerprint)
            XCTAssertEqual(last, 2, context)
        }
    }

    func testReceiverThatNeverStopsLimitingIsGivenUpOn() async throws {
        let script = Script(times: .max) { ReceiverError(code: ErrorCode.rateLimited.rawValue, seq: $0.seq, retryAfterMS: 1) }
        let device = try await scripted(script)

        let error = await thrown { try await device.client.ping() }
        XCTAssertEqual(error as? ErdigoError, .rejected(ReceiverError(code: ErrorCode.rateLimited.rawValue, seq: 1, retryAfterMS: 1)))
        // The first answer and twenty more, all for the same number.
        XCTAssertEqual(script.numbersSeen, Array(repeating: 1, count: 21))
        let connected = await connections(device)
        XCTAssertEqual(connected, 1)
    }

    func testAnswerForAnotherNumberIsAProtocolViolationAndChangesNoNumbers() async throws {
        for code in [ErrorCode.notFound, .rateLimited, .staleSeq] {
            let context = code.rawValue
            let script = Script { ReceiverError(code: code.rawValue, seq: $0.seq + 5, lastSeq: 1000, retryAfterMS: 20) }
            let device = try await scripted(script)

            let error = await thrown { try await device.client.ping() }
            guard case .protocolViolation? = error as? ErdigoError else {
                XCTFail("\(context): expected a protocol violation, got \(String(describing: error))")
                continue
            }
            let limits = await device.client.limits
            XCTAssertNil(limits, context)

            // The next request starts from what the receiver says on a new connection, not from the stray number.
            try await device.client.ping()
            XCTAssertEqual(script.numbersSeen, [1, 1], context)
            let last = await device.receiver.lastSeq(ofDevice: device.fingerprint)
            XCTAssertEqual(last, 1, context)
        }
    }

    func testStaleNumberForThisRequestMovesTheNumberOn() async throws {
        let script = Script { ReceiverError(code: ErrorCode.staleSeq.rawValue, seq: $0.seq, lastSeq: 10) }
        let device = try await scripted(script)

        let error = await thrown { try await device.client.ping() }
        XCTAssertEqual(error as? ErdigoError, .sequenceConflict(lastSeq: 10))

        try await device.client.ping()
        XCTAssertEqual(script.numbersSeen, [1, 11])
        let connected = await connections(device)
        XCTAssertEqual(connected, 1)
        let last = await device.receiver.lastSeq(ofDevice: device.fingerprint)
        XCTAssertEqual(last, 11)
    }

    func testRejectionKeepsTheConnectionAndUsesUpTheNumberOnlyWhenItSaysWhichRequest() async throws {
        for answerCarriesTheNumber in [true, false] {
            let context = "answer carries the number: \(answerCarriesTheNumber)"
            let script = Script { ReceiverError(code: ErrorCode.notFound.rawValue, seq: answerCarriesTheNumber ? $0.seq : nil) }
            let device = try await scripted(script)

            let error = await thrown { try await device.client.move(id: "missing", to: Self.palace) }
            let answer = ReceiverError(code: ErrorCode.notFound.rawValue, seq: answerCarriesTheNumber ? 1 : nil)
            XCTAssertEqual(error as? ErdigoError, .rejected(answer), context)
            let limits = await device.client.limits
            XCTAssertNotNil(limits, context)

            try await device.client.ping()
            XCTAssertEqual(script.numbersSeen, answerCarriesTheNumber ? [1, 2] : [1, 1], context)
            let connected = await connections(device)
            XCTAssertEqual(connected, 1, context)
        }
    }
}
