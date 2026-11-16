import Foundation
import Security

/// The operations waiting for the gateway, kept in memory. Only the server's queue touches it.
final class RelayQueue {
    struct Limits {
        var maxOperations = 10_000
        var maxBytes = 64 * 1024 * 1024
        var answerBytes = 4 * 1024 * 1024
        var keepSeconds: Double = 24 * 3600
    }

    enum Taken {
        case accepted(count: Int, lastSeq: UInt64)
        case full
    }

    struct Answer {
        let body: Data
        let count: Int
    }

    private struct Stored {
        let seq: UInt64
        let receivedMS: Int64
        let expiresAt: Double
        let json: Data
    }

    /// A new one each time the relay starts, because the numbering starts again with it.
    let epoch: String
    private let limits: Limits
    private let clock: () -> Double
    private var stored: [Stored] = []
    private var bytes = 0
    private var lastSeq: UInt64 = 0
    /// The highest number dropped because it waited too long.
    private var expiredUpTo: UInt64 = 0

    init(limits: Limits, clock: @escaping () -> Double = RelayQueue.monotonicSeconds) {
        self.limits = limits
        self.clock = clock
        epoch = Self.newEpoch()
    }

    /// Counts the time a computer spends asleep, unlike uptime.
    static func monotonicSeconds() -> Double {
        Double(clock_gettime_nsec_np(CLOCK_MONOTONIC)) / 1_000_000_000
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

    private static func newEpoch() -> String {
        var random = [UInt8](repeating: 0, count: 16)
        if SecRandomCopyBytes(kSecRandomDefault, random.count, &random) != errSecSuccess {
            var generator = SystemRandomNumberGenerator()
            random = random.map { _ in UInt8.random(in: 0...255, using: &generator) }
        }
        return Data(random).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    private func expire() {
        let now = clock()
        var dropped = 0
        while dropped < stored.count, stored[dropped].expiresAt <= now {
            expiredUpTo = max(expiredUpTo, stored[dropped].seq)
            bytes -= stored[dropped].json.count
            dropped += 1
        }
        if dropped > 0 { stored.removeFirst(dropped) }
    }

    /// Takes a whole batch, or nothing if it would not fit.
    func take(_ batch: [Data], receivedMS: Int64) -> Taken {
        expire()
        let added = batch.reduce(0) { $0 + $1.count }
        guard stored.count + batch.count <= limits.maxOperations, bytes + added <= limits.maxBytes else { return .full }
        let expiresAt = clock() + limits.keepSeconds
        for json in batch {
            lastSeq += 1
            stored.append(Stored(seq: lastSeq, receivedMS: receivedMS, expiresAt: expiresAt, json: json))
        }
        bytes += added
        return .accepted(count: batch.count, lastSeq: lastSeq)
    }

    /// Answers a fetch, forgetting what the gateway is done with when the epoch matches.
    /// Returns nil when `after` is beyond the last number given out.
    func fetch(epoch requested: String?, after: UInt64, limit: Int) -> Answer? {
        expire()
        let reset = requested != epoch
        if !reset {
            guard after <= lastSeq else { return nil }
            let done = stored.prefix { $0.seq <= after }.count
            if done > 0 {
                bytes -= stored.prefix(done).reduce(0) { $0 + $1.json.count }
                stored.removeFirst(done)
            }
        }
        let missed = expiredUpTo > (reset ? 0 : after)

        var body = Data("{\"epoch\":\"\(epoch)\",\"reset\":\(reset),\"missed\":\(missed),\"ops\":[".utf8)
        var size = body.count + 2
        var count = 0
        for op in stored {
            guard count < limit else { break }
            let head = Data("\(count > 0 ? "," : ""){\"seq\":\(op.seq),\"received_ms\":\(op.receivedMS),\"op\":".utf8)
            let entrySize = head.count + op.json.count + 1
            // One operation always goes, however large, so that nothing can get stuck.
            if count > 0, size + entrySize > limits.answerBytes { break }
            body.append(head)
            body.append(op.json)
            body.append(UInt8(ascii: "}"))
            size += entrySize
            count += 1
        }
        body.append(contentsOf: "]}".utf8)
        return Answer(body: body, count: count)
    }
}
