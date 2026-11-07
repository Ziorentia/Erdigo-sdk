import CryptoKit
@testable @_spi(ErdigoInternal) import ErdigoCore
import Foundation
import XCTest

/// A handshake yields one transport, and a transport uses each key and nonce pair once, however it
/// is shared. Reusing a pair would let someone who sees two messages recover their contents.
final class NoiseTransportTests: XCTestCase {
    private func completedHandshakes() throws -> (device: NoiseHandshake, receiver: NoiseHandshake) {
        let deviceKey = Curve25519.KeyAgreement.PrivateKey()
        let receiverKey = Curve25519.KeyAgreement.PrivateKey()
        let prologue = Data("test".utf8)
        let device = try NoiseHandshake(
            pattern: .ik, isInitiator: true, prologue: prologue, staticKey: deviceKey,
            remoteStaticKey: receiverKey.publicKey.rawRepresentation
        )
        let receiver = try NoiseHandshake(pattern: .ik, isInitiator: false, prologue: prologue, staticKey: receiverKey)
        _ = try receiver.readMessage(try device.writeMessage())
        _ = try device.readMessage(try receiver.writeMessage())
        return (device, receiver)
    }

    private func establishedPair() throws -> (device: NoiseTransport, receiver: NoiseTransport) {
        let (device, receiver) = try completedHandshakes()
        return (try device.split(), try receiver.split())
    }

    /// Runs `body` on many threads at once. Results go straight into their own slots, so that
    /// nothing here orders the threads and hides a race in the code under test.
    private func concurrentResults<T>(_ count: Int, _ body: (Int) -> T) -> [T] {
        var results = [T?](repeating: nil, count: count)
        results.withUnsafeMutableBufferPointer { buffer in
            let slots = buffer
            DispatchQueue.concurrentPerform(iterations: count) { index in
                slots[index] = body(index)
            }
        }
        return results.map { $0! }
    }

    /// Runs `body` on real threads that all begin at the same moment. Each one spins until the last
    /// has arrived, so a check and an update in the code under test can actually overlap. Threads are
    /// created here because `concurrentPerform` may run fewer at once than there are iterations, and
    /// then spinning for the others would never end.
    private func simultaneousResults<T>(_ count: Int, _ body: @escaping () -> T) -> [T] {
        let lock = NSLock()
        var waiting = count
        var results: [T] = []
        let finished = DispatchGroup()
        for _ in 0..<count {
            finished.enter()
            let thread = Thread {
                lock.lock()
                waiting -= 1
                lock.unlock()
                var everyoneIsHere = false
                while !everyoneIsHere {
                    lock.lock()
                    everyoneIsHere = waiting == 0
                    lock.unlock()
                }
                let result = body()
                lock.lock()
                results.append(result)
                lock.unlock()
                finished.leave()
            }
            thread.qualityOfService = .userInteractive
            thread.start()
        }
        finished.wait()
        return results
    }

    // MARK: Splitting

    func testSecondSplitIsRefused() throws {
        let (device, receiver) = try completedHandshakes()
        for handshake in [device, receiver] {
            XCTAssertNoThrow(try handshake.split())
            for _ in 0..<2 {
                XCTAssertThrowsError(try handshake.split()) { XCTAssertEqual($0 as? NoiseError, .alreadySplit) }
            }
        }
    }

    func testSplitTooEarlyDoesNotUseUpTheSplit() throws {
        let deviceKey = Curve25519.KeyAgreement.PrivateKey()
        let receiverKey = Curve25519.KeyAgreement.PrivateKey()
        let device = try NoiseHandshake(
            pattern: .ik, isInitiator: true, prologue: Data(), staticKey: deviceKey,
            remoteStaticKey: receiverKey.publicKey.rawRepresentation
        )
        let receiver = try NoiseHandshake(pattern: .ik, isInitiator: false, prologue: Data(), staticKey: receiverKey)
        XCTAssertThrowsError(try device.split()) { XCTAssertEqual($0 as? NoiseError, .wrongHandshakeState) }
        _ = try receiver.readMessage(try device.writeMessage())
        _ = try device.readMessage(try receiver.writeMessage())
        XCTAssertNoThrow(try device.split())
    }

    func testConcurrentSplitsSucceedOnce() throws {
        // The overlap this looks for happens in about one round in 500, so many rounds are needed.
        for _ in 0..<10_000 {
            let (device, _) = try completedHandshakes()
            let outcomes = simultaneousResults(4) { () -> Result<NoiseTransport, Error> in
                Result { try device.split() }
            }
            var successes = 0
            for outcome in outcomes {
                switch outcome {
                case .success:
                    successes += 1
                case .failure(let error):
                    XCTAssertEqual(error as? NoiseError, .alreadySplit)
                }
            }
            XCTAssertEqual(successes, 1)
            if successes != 1 { return }
        }
    }

    // MARK: Sharing a transport

    func testTwoReferencesShareOneState() throws {
        let (device, receiver) = try establishedPair()
        let alias = device
        let plaintext = Data("same message".utf8)

        let first = try device.seal(plaintext)
        let second = try alias.seal(plaintext)

        XCTAssertNotEqual(first, second)
        XCTAssertEqual(try receiver.open(first), plaintext)
        XCTAssertEqual(try receiver.open(second), plaintext)

        // Traffic the other way goes on from the same state, through either reference.
        XCTAssertEqual(try alias.open(try receiver.seal(plaintext)), plaintext)
        XCTAssertEqual(try device.open(try receiver.seal(plaintext)), plaintext)
    }

    func testAReferenceHeldInACopiedValueSharesTheState() throws {
        struct Holder { let transport: NoiseTransport }
        let (device, receiver) = try establishedPair()
        let original = Holder(transport: device)
        let copy = original
        let plaintext = Data("same message".utf8)

        let first = try original.transport.seal(plaintext)
        let second = try copy.transport.seal(plaintext)

        XCTAssertNotEqual(first, second)
        XCTAssertEqual(try receiver.open(first), plaintext)
        XCTAssertEqual(try receiver.open(second), plaintext)
    }

    func testConcurrentSealsNeverRepeatANonce() throws {
        let (device, _) = try establishedPair()
        let alias = device
        let plaintext = Data("same message".utf8)
        let iterations = 20_000

        let sealed = concurrentResults(iterations) { index in
            try? (index % 2 == 0 ? device : alias).seal(plaintext)
        }

        let distinct = Set(sealed.compactMap { $0 })
        XCTAssertEqual(sealed.compactMap { $0 }.count, iterations)
        XCTAssertEqual(distinct.count, iterations, "two messages used one nonce")
    }

    func testConcurrentSealsUseEveryNonceInTurn() throws {
        let (device, receiver) = try establishedPair()
        let alias = device
        let plaintext = Data("same message".utf8)
        let iterations = 400

        var waiting = concurrentResults(iterations) { index in
            try? (index % 2 == 0 ? device : alias).seal(plaintext)
        }.compactMap { $0 }
        XCTAssertEqual(waiting.count, iterations)

        // The peer opens messages only in order, so each step must find exactly one that fits.
        var opened = 0
        while let next = waiting.firstIndex(where: { (try? receiver.open($0)) == plaintext }) {
            waiting.remove(at: next)
            opened += 1
        }
        XCTAssertEqual(opened, iterations)
    }

    func testConcurrentOpensAcceptAMessageOnce() throws {
        let (device, receiver) = try establishedPair()
        let plaintext = Data("same message".utf8)
        let alias = receiver

        for round in 0..<5000 {
            let sealed = try device.seal(plaintext)
            let opened = concurrentResults(8) { index in
                try? (index % 2 == 0 ? receiver : alias).open(sealed)
            }
            let accepted = opened.compactMap { $0 }
            XCTAssertEqual(accepted, [plaintext], "round \(round)")
            if accepted.count != 1 { return }
        }
    }

    func testSealingAndOpeningTogetherKeepBothDirectionsIntact() throws {
        let (device, receiver) = try establishedPair()
        let count = 5000
        let incoming = try (0..<count).map { try receiver.seal(Data("to device \($0)".utf8)) }

        // One thread seals while another opens, on a single transport.
        let outcomes = concurrentResults(2) { side -> (sealed: [Data], opened: [Data]) in
            var sealed: [Data] = []
            var opened: [Data] = []
            for index in 0..<count {
                if side == 0 {
                    if let frame = try? device.seal(Data("to receiver \(index)".utf8)) { sealed.append(frame) }
                } else {
                    if let text = try? device.open(incoming[index]) { opened.append(text) }
                }
            }
            return (sealed, opened)
        }

        XCTAssertEqual(outcomes[1].opened, (0..<count).map { Data("to device \($0)".utf8) })
        let received = try outcomes[0].sealed.map { try receiver.open($0) }
        XCTAssertEqual(received, (0..<count).map { Data("to receiver \($0)".utf8) })
    }

    // MARK: Limits

    func testTheNonceCounterStopsRatherThanWrappingAround() throws {
        let forward = Data(repeating: 0x11, count: 32)
        let backward = Data(repeating: 0x22, count: 32)
        func transport(sendKey: Data, receiveKey: Data) -> NoiseTransport {
            var send = NoiseCipherState()
            var receive = NoiseCipherState()
            send.initialize(key: sendKey, counter: UInt64.max - 1)
            receive.initialize(key: receiveKey, counter: UInt64.max - 1)
            return NoiseTransport(sendCipher: send, receiveCipher: receive, handshakeHash: Data())
        }
        let sender = transport(sendKey: forward, receiveKey: backward)
        let peer = transport(sendKey: backward, receiveKey: forward)
        let plaintext = Data("last message".utf8)

        // The last number the protocol allows still works.
        let last = try sender.seal(plaintext)
        XCTAssertEqual(try peer.open(last), plaintext)

        // After that there is no next number, however often it is tried.
        for _ in 0..<2 {
            XCTAssertThrowsError(try sender.seal(plaintext)) { XCTAssertEqual($0 as? NoiseError, .nonceExhausted) }
            XCTAssertThrowsError(try peer.open(last)) { XCTAssertEqual($0 as? NoiseError, .nonceExhausted) }
        }
    }

    func testSizeChecksStillApply() throws {
        let (device, receiver) = try establishedPair()
        let largest = Data(repeating: 0x20, count: Noise.maxMessageLength - Noise.tagLength)

        let sealed = try device.seal(largest)
        XCTAssertEqual(sealed.count, Noise.maxMessageLength)
        XCTAssertEqual(try receiver.open(sealed), largest)

        XCTAssertThrowsError(try device.seal(largest + Data([0x20]))) { XCTAssertEqual($0 as? NoiseError, .messageTooLarge) }
        XCTAssertThrowsError(try receiver.open(Data(repeating: 0, count: Noise.maxMessageLength + 1))) {
            XCTAssertEqual($0 as? NoiseError, .messageTooLarge)
        }
    }
}
