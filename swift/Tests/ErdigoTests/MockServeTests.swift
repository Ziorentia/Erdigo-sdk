@testable import erdigo_send
import Foundation
import XCTest

/// How `erdigo-send serve --mock` decides about pairing requests.
final class MockServeTests: XCTestCase {
    func testWhoDecides() throws {
        let cases: [(localNetwork: Bool, ask: Bool, autoApprove: Bool, asks: Bool)] = [
            // On this computer alone, every request is allowed unless --ask is given.
            (false, false, false, false),
            (false, true, false, true),
            (false, false, true, false),
            // On the network, a person is asked unless --auto-approve is given.
            (true, false, false, true),
            (true, true, false, true),
            (true, false, true, false),
        ]
        for choice in cases {
            let asks = try asksAboutPairing(localNetwork: choice.localNetwork, ask: choice.ask, autoApprove: choice.autoApprove)
            XCTAssertEqual(asks, choice.asks, "\(choice)")
        }
    }

    func testAskingAndAllowingEverythingCannotBothBeChosen() {
        for localNetwork in [false, true] {
            XCTAssertThrowsError(try asksAboutPairing(localNetwork: localNetwork, ask: true, autoApprove: true)) { error in
                XCTAssertTrue(error.localizedDescription.contains("cannot be used together"), error.localizedDescription)
            }
        }
    }

    /// Standard input that a test types into, line by line, and can close.
    private final class TypedInput: @unchecked Sendable {
        private let condition = NSCondition()
        private var lines: [String?] = []

        func type(_ line: String) { add(line) }

        func end() { add(nil) }

        func next() -> String? {
            condition.lock()
            defer { condition.unlock() }
            while lines.isEmpty { condition.wait() }
            return lines.removeFirst()
        }

        private func add(_ line: String?) {
            condition.lock()
            lines.append(line)
            condition.signal()
            condition.unlock()
        }
    }

    private func waitUntil(_ console: Console, hasWaiting count: Int, file: StaticString = #filePath, line: UInt = #line) async throws {
        let giveUp = Date().addingTimeInterval(5)
        while await console.waitingQuestions != count {
            guard Date() < giveUp else {
                return XCTFail("expected \(count) questions waiting", file: file, line: line)
            }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
    }

    /// Waits for the answer. After five seconds the question is given up on, which also makes it no,
    /// so the time it took tells the two apart.
    private func timedAnswer(_ question: Task<Bool, Never>) async -> (answer: Bool, seconds: TimeInterval) {
        let started = Date()
        let watchdog = Task {
            do { try await Task.sleep(nanoseconds: 5_000_000_000) } catch { return }
            question.cancel()
        }
        let answer = await question.value
        watchdog.cancel()
        return (answer, Date().timeIntervalSince(started))
    }

    private func commands(_ stream: AsyncStream<String>) async -> [String] {
        var typed: [String] = []
        for await line in stream { typed.append(line) }
        return typed
    }

    func testAnswersAreTakenInOrderAndOtherLinesAreCommands() async throws {
        let input = TypedInput()
        let console = Console { input.next() }
        let stream = console.lines()
        let first = Task { await console.nextDecision() }
        try await waitUntil(console, hasWaiting: 1)
        let second = Task { await console.nextDecision() }
        try await waitUntil(console, hasWaiting: 2)

        input.type(" Y ")
        input.type("no")
        let firstAnswer = await first.value
        let secondAnswer = await second.value
        XCTAssertTrue(firstAnswer)
        XCTAssertFalse(secondAnswer)

        input.type("devices")
        input.end()
        let typed = await commands(stream)
        XCTAssertEqual(typed, ["devices"])
    }

    func testOnceInputEndsEveryQuestionIsAnsweredNoAtOnce() async throws {
        let input = TypedInput()
        let console = Console { input.next() }
        let stream = console.lines()
        let waiting = Task { await console.nextDecision() }
        try await waitUntil(console, hasWaiting: 1)

        input.end()
        let first = await timedAnswer(waiting)
        XCTAssertFalse(first.answer)
        XCTAssertLessThan(first.seconds, 2)
        // And so is any question after that, without waiting for the approval time to run out.
        let later = await timedAnswer(Task { await console.nextDecision() })
        XCTAssertFalse(later.answer)
        XCTAssertLessThan(later.seconds, 2)
        let typed = await commands(stream)
        XCTAssertEqual(typed, [])
    }

    func testAQuestionThatTimesOutIsNo() async throws {
        let input = TypedInput()
        let console = Console { input.next() }
        let stream = console.lines()
        let question = Task { await console.nextDecision() }
        try await waitUntil(console, hasWaiting: 1)

        question.cancel()
        let answer = await question.value
        XCTAssertFalse(answer)
        // The next line is a command, not an answer to the question that went away.
        input.type("help")
        input.end()
        let typed = await commands(stream)
        XCTAssertEqual(typed, ["help"])
    }
}
