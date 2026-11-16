// An example gateway for Erdigo, as docs/cloud-relay.md describes it. It runs on the app's local network,
// fetches operations from a cloud relay over outgoing HTTPS, and writes them to the app as a paired device.
//
//     swift run erdigo-example-gateway pair --name "Cloud gateway"
//     ERDIGO_RELAY_TOKEN=... swift run erdigo-example-gateway run --relay https://relay.example.com
//
// The pairing code and the token are never taken as command-line arguments, which other users can see.

import Darwin
import ErdigoSDK
import Foundation

private let usage = """
erdigo-example-gateway: fetch operations from a cloud relay and write them to the Erdigo app on this local network.

  pair --name NAME [--kinds LIST] [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
       [--profile NAME] [--state-dir DIR]
      Pairs once, as 'erdigo-send pair' does. The code or the QR text is read from the keyboard, or from
      --code-file, a file only you can read. --kinds is pin,marker,note,photo,track by default.
  run --relay URL [--host HOST --port PORT] [--once] [--profile NAME] [--state-dir DIR]
      Fetches from the relay and writes to the app until stopped. The address must start with https://;
      plain http:// is accepted only for 127.0.0.1, [::1] or localhost. The gateway token is read from
      the environment variable ERDIGO_RELAY_TOKEN. --once stops when the relay has nothing more.

  --host, --port   The app's address. Without them the gateway finds the app on the local network.
  --profile NAME   Where the gateway's key, its pairing and its place in the relay's queue are kept
                   (default: "cloud-gateway").
  --state-dir DIR  Keep them in DIR instead.

run exits with 0 when stopped or, with --once, when nothing more is waiting; 1 for a mistake in the command
line or the environment, or no pairing; 2 when the relay refuses the token or answers outside its API; 3 when,
with --once, the Erdigo app or the relay could not be reached.
"""

private struct UsageError: Error, LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// Ends `run` with this exit code and message.
private struct Exit: Error {
    let code: Int32
    let message: String
}

private let knownOptions: Set<String> = ["code-file", "fingerprint", "host", "kinds", "name", "once", "port", "profile", "relay", "state-dir"]

/// `--name value` pairs and bare flags, as erdigo-send takes them. Errors never repeat what was typed,
/// because it might be a pairing code.
private struct Options {
    private var values: [String: String] = [:]
    private var flags = Set<String>()
    private var used = Set<String>()

    init(_ arguments: [String], flags allowedFlags: Set<String>) throws {
        var index = 0
        while index < arguments.count {
            let argument = arguments[index]
            guard argument.hasPrefix("--") else {
                throw UsageError(message: "unexpected argument. Options start with --, and a pairing code or a token is never given as an argument.")
            }
            let name = String(argument.dropFirst(2))
            if name == "code" || name.hasPrefix("code=") || name == "token" || name.hasPrefix("token=") {
                throw UsageError(message: "codes and tokens are not accepted as arguments, because arguments appear in the process list and shell history.")
            }
            if let equals = name.firstIndex(of: "=") {
                let option = String(name[..<equals])
                throw UsageError(message: "write \(knownOptions.contains(option) ? "--\(option)" : "an option") and its value as two separate arguments")
            }
            if allowedFlags.contains(name) {
                flags.insert(name)
                index += 1
            } else {
                guard knownOptions.contains(name) else { throw UsageError(message: "an unknown option was given. See 'erdigo-example-gateway help'.") }
                guard index + 1 < arguments.count else { throw UsageError(message: "--\(name) needs a value") }
                guard values[name] == nil else { throw UsageError(message: "--\(name) was given twice") }
                values[name] = arguments[index + 1]
                index += 2
            }
        }
    }

    mutating func flag(_ name: String) -> Bool {
        used.insert(name)
        return flags.contains(name)
    }

    mutating func optional(_ name: String) -> String? {
        used.insert(name)
        return values[name]
    }

    mutating func required(_ name: String) throws -> String {
        guard let value = optional(name) else { throw UsageError(message: "--\(name) is required") }
        return value
    }

    func finish() throws {
        let unknown = Set(values.keys).union(flags).subtracting(used)
        if let first = unknown.sorted().first { throw UsageError(message: "--\(first) does not apply to this command") }
    }
}

/// Reads a file only if nobody else can read it, as erdigo-send does with its code file.
private func readPrivateFile(_ path: String) throws -> String {
    let descriptor = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
    guard descriptor >= 0 else { throw UsageError(message: "could not open the code file") }
    defer { close(descriptor) }
    var info = stat()
    guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_uid == getuid() else {
        throw UsageError(message: "the code file must be a regular file you own")
    }
    guard info.st_mode & 0o077 == 0 else {
        throw UsageError(message: "the code file must have mode 0600, so that nobody else can read it")
    }
    guard info.st_size <= 1024 else { throw UsageError(message: "the code file is too large") }
    let data = FileHandle(fileDescriptor: descriptor, closeOnDealloc: false).readDataToEndOfFile()
    guard let text = String(data: data, encoding: .utf8) else { throw UsageError(message: "the code file is not text") }
    return text
}

private func readCodeFromKeyboard() throws -> String {
    var buffer = [CChar](repeating: 0, count: 256)
    guard readpassphrase("Pairing code: ", &buffer, buffer.count, RPP_REQUIRE_TTY) != nil else {
        throw UsageError(message: "no terminal to read the code from. Use --code-file with a file of mode 0600.")
    }
    defer { memset_s(&buffer, buffer.count, 0, buffer.count) }
    return String(cString: buffer)
}

/// The folder for the key, the pairing and the place. It is created only when the store is opened.
private func stateDirectory(_ options: inout Options) throws -> URL {
    let profile = options.optional("profile")
    if let path = options.optional("state-dir") {
        guard profile == nil else {
            throw UsageError(message: "--state-dir and --profile cannot be used together, because a state folder holds one identity")
        }
        return URL(fileURLWithPath: path, isDirectory: true)
    }
    let name = profile ?? "cloud-gateway"
    guard !name.isEmpty, name.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) || $0 == "-" || $0 == "_" }) else {
        throw UsageError(message: "--profile may contain only letters, digits, '-' and '_'")
    }
    return try FileDeviceStore.defaultDirectory(profile: name)
}

private func target(_ options: inout Options) throws -> ReceiverTarget {
    let host = options.optional("host")
    let portText = options.optional("port")
    switch (host, portText) {
    case (nil, nil):
        return .discover
    case (let host?, let text?):
        guard let port = UInt16(text), port != 0 else { throw UsageError(message: "--port must be between 1 and 65535") }
        return .address(host: host, port: port)
    default:
        throw UsageError(message: "--host and --port go together")
    }
}

private func parseKinds(_ text: String) throws -> Set<ItemKind> {
    var kinds = Set<ItemKind>()
    for piece in text.split(separator: ",") {
        guard let kind = ItemKind(rawValue: piece.trimmingCharacters(in: .whitespaces)) else {
            throw UsageError(message: "--kinds takes pin, marker, note, photo and track, separated by commas.")
        }
        kinds.insert(kind)
    }
    guard !kinds.isEmpty else { throw UsageError(message: "--kinds needs at least one item kind") }
    return kinds
}

// MARK: pair

private func runPair(_ arguments: [String]) async throws {
    var options = try Options(arguments, flags: [])
    let directory = try stateDirectory(&options)
    let name = try options.required("name")
    let kinds = try parseKinds(options.optional("kinds") ?? "pin,marker,note,photo,track")
    let codeFile = options.optional("code-file")
    let fingerprint = options.optional("fingerprint")?.lowercased()
    let where_ = try target(&options)
    try options.finish()

    if let fingerprint, fingerprint.count != 64 || Data(hexString: fingerprint) == nil {
        throw UsageError(message: "--fingerprint must be 64 hexadecimal digits")
    }
    // A gateway's profile holds exactly one pairing, so that it is always clear where operations go.
    let store = try FileDeviceStore(directory: directory)
    guard try store.pairings().isEmpty else {
        throw UsageError(message: "this profile is already paired. Use another --profile or --state-dir, or remove that folder to pair again.")
    }
    let text = try codeFile.map(readPrivateFile) ?? readCodeFromKeyboard()
    var ticket: PairingTicket
    do {
        ticket = try PairingTicket(parsing: text)
    } catch let error as WireError {
        throw UsageError(message: error.detail)
    }
    if let fingerprint {
        if let fromQR = ticket.fingerprint, fromQR != fingerprint { throw ErdigoError.fingerprintMismatch }
        ticket = PairingTicket(code: ticket.code, fingerprint: fingerprint)
    }
    // Only a person can compare the key, and the receiver is not contacted when no answer is possible.
    if ticket.fingerprint == nil, isatty(STDIN_FILENO) == 0 || isatty(STDOUT_FILENO) == 0 {
        throw UsageError(message: "a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.")
    }

    let waiting = "Waiting for the request to be confirmed in the Erdigo app..."
    let personCompares = ticket.fingerprint == nil
    print(personCompares ? "Checking the code with the Erdigo app..." : waiting)
    let identity = try store.loadOrCreateIdentity()
    let pairing = try await Pairing.pair(
        target: where_,
        ticket: ticket,
        identity: identity,
        deviceName: name,
        kinds: kinds,
        confirmKey: { key in
            // Only an answer given after seeing the key counts, so anything typed before it is discarded.
            tcflush(STDIN_FILENO, TCIFLUSH)
            print("Receiver key: \(key)")
            print("If this matches the key shown in the app, type yes: ", terminator: "")
            fflush(stdout)
            guard readLine()?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "yes" else {
                print("")
                return false
            }
            print(waiting)
            return true
        }
    )
    try store.save(pairing)
    print("Paired. Allowed: \(pairing.kinds.map(\.rawValue).joined(separator: ", ")).")
    if !personCompares { print("Receiver key: \(IntegrationV1.shortFingerprint(pairing.fingerprint))") }
}

// MARK: run

private func isValidToken(_ token: String) -> Bool {
    (32...256).contains(token.utf8.count) && token.utf8.allSatisfy { byte in
        switch byte {
        case UInt8(ascii: "A")...UInt8(ascii: "Z"), UInt8(ascii: "a")...UInt8(ascii: "z"), UInt8(ascii: "0")...UInt8(ascii: "9"),
             UInt8(ascii: "-"), UInt8(ascii: "_"), UInt8(ascii: "."), UInt8(ascii: "~"):
            return true
        default:
            return false
        }
    }
}

/// The relay's address without a trailing slash. A path is kept, for a relay behind a proxy under one.
private func relayBase(_ text: String) throws -> String {
    guard let components = URLComponents(string: text), let scheme = components.scheme?.lowercased(),
          let host = components.host, !host.isEmpty,
          components.user == nil, components.password == nil, components.query == nil, components.fragment == nil
    else { throw UsageError(message: "--relay must be the relay's address, such as https://relay.example.com") }
    let loopback = ["127.0.0.1", "::1", "[::1]", "localhost"].contains(host.lowercased())
    guard scheme == "https" || (scheme == "http" && loopback) else {
        throw UsageError(message: "--relay must start with https://. Plain http:// is accepted only for 127.0.0.1, [::1] or localhost, for testing on one computer.")
    }
    var base = text
    while base.hasSuffix("/") { base.removeLast() }
    return base
}

private func uptime() -> TimeInterval {
    ProcessInfo.processInfo.systemUptime
}

/// Waits 1, 2, 4 ... up to 60 seconds with about 20 percent variation, as spec section 13 says.
private struct Backoff {
    /// When the first failed try began.
    private(set) var started: TimeInterval?
    private var attempt = 0

    mutating func reset() {
        started = nil
        attempt = 0
    }

    /// The next wait, or nil once `limit` seconds have passed since the first failed try began.
    /// Counting from the start of that try means a try that hangs uses up the time too.
    mutating func next(giveUpAfter limit: TimeInterval?, tryBegan: TimeInterval) -> TimeInterval? {
        let now = uptime()
        let start = started ?? tryBegan
        started = start
        var delay = min(60, pow(2, Double(min(attempt, 6)))) * Double.random(in: 0.8...1.2)
        attempt += 1
        if let limit {
            guard now - start < limit else { return nil }
            delay = min(delay, limit - (now - start))
        }
        return delay
    }

    static func describe(_ delay: TimeInterval) -> String {
        let seconds = max(1, Int(delay.rounded()))
        return seconds == 1 ? "1 second" : "\(seconds) seconds"
    }
}

private func sleep(seconds: TimeInterval) async throws {
    try await Task.sleep(nanoseconds: UInt64(max(0, seconds) * 1_000_000_000))
}

/// What the gateway knows about one operation's trouble reaching the app, across all the tries for it. The SDK
/// counts its own time from the last connection it made, so an app that takes every connection but never answers, or
/// whose answers are all lost, would keep it trying for ever. This counts from the first sign of trouble until the
/// app takes the operation, however many connections that takes.
private final class Outage: @unchecked Sendable {
    private let lock = NSLock()
    private var began: TimeInterval?
    private var sending = false
    private var said = false

    /// When the first sign of trouble showed, or nil while there has been none.
    var since: TimeInterval? {
        lock.lock()
        defer { lock.unlock() }
        return began
    }

    /// Notes trouble that showed at `moment`, and returns when the trouble began.
    @discardableResult
    func note(at moment: TimeInterval) -> TimeInterval {
        lock.lock()
        defer { lock.unlock() }
        let start = min(began ?? moment, moment)
        began = start
        return start
    }

    /// True once the SDK has a connection for this try and is sending the operation over it. From then on the SDK drops
    /// that connection only when sending failed.
    var isSending: Bool {
        get {
            lock.lock()
            defer { lock.unlock() }
            return sending
        }
        set {
            lock.lock()
            sending = newValue
            lock.unlock()
        }
    }

    /// True the first time only, so that the gateway says once per operation that the app does not answer.
    func takeNotice() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !said else { return false }
        said = true
        return true
    }
}

private final class Gateway {
    private struct AppUnreachable: Error {}

    private enum Outcome {
        case refused(String)
        /// An answer that says "not now", or one the SDK cannot use. The SDK does not wait these out, so the
        /// gateway does, and then sends the operation again as a new request.
        case notNow
        /// With --once, the app did not take the operation for as long as the gateway may wait.
        case gaveUp
        case fatal(String)
    }

    /// How one try ended: with what the SDK said, or because the time with --once ran out first.
    private enum TryEnd {
        case finished(Outcome?)
        case outOfTime
    }

    /// About as long as a one-shot command keeps trying, as spec section 13 suggests.
    static let onceGiveUpSeconds: TimeInterval = 30
    /// An empty answer that comes sooner than this is followed by a pause, because a relay that cannot hold
    /// a fetch open answers at once, and asking again straight away would flood it.
    private static let pollSeconds: TimeInterval = 5
    /// How long trouble, or a request without a connection to the app, lasts before the gateway says so. Shorter
    /// waits are usually a connection the app closed while it was idle, which the SDK opens again after about a second.
    private static let noAnswerNoticeSeconds: TimeInterval = 2

    private let client: ErdigoClient
    private let relay: RelayClient
    private let directory: URL
    private let base: String
    private let once: Bool
    private var place: Place?
    private var reportedMissed: (epoch: String, after: UInt64)?

    init(client: ErdigoClient, relay: RelayClient, directory: URL, base: String, once: Bool, place: Place?) {
        self.client = client
        self.relay = relay
        self.directory = directory
        self.base = base
        self.once = once
        self.place = place
    }

    func run() async throws -> Int32 {
        var relayWaits = Backoff()
        while true {
            try Task.checkCancellation()
            let began = uptime()
            // With --once, a try during an outage may not run past the moment the gateway gives up.
            var limit: TimeInterval?
            if once, let since = relayWaits.started { limit = max(1, Self.onceGiveUpSeconds - (began - since)) }
            let answer: RelayClient.Answer
            do {
                answer = try await relay.fetch(epoch: place?.epoch, after: place?.after ?? 0, wait: once ? 0 : RelayClient.waitSeconds, limit: limit)
                relayWaits.reset()
            } catch RelayClient.Problem.unreachable {
                guard let delay = relayWaits.next(giveUpAfter: once ? Self.onceGiveUpSeconds : nil, tryBegan: began) else {
                    print("The relay could not be reached.")
                    return 3
                }
                print("The relay could not be reached; trying again in \(Backoff.describe(delay)).")
                try await sleep(seconds: delay)
                continue
            } catch RelayClient.Problem.refusedToken {
                throw Exit(code: 2, message: "The relay refused the token.")
            } catch RelayClient.Problem.brokeAPI(let reason) {
                throw Exit(code: 2, message: "The relay's answer does not follow the relay API: \(reason).")
            }

            var current = place ?? Place(relay: base, epoch: nil, after: 0)
            if answer.reset {
                if place?.epoch != nil { print("The relay started a new numbering; starting from its oldest operation.") }
                current = Place(relay: base, epoch: answer.epoch, after: 0)
                try current.save(in: directory)
                place = current
            }
            // The relay says so on every fetch until the gateway is past the gap, so say it once.
            if answer.missed, reportedMissed?.epoch != answer.epoch || reportedMissed?.after != current.after {
                print("Some operations expired on the relay before they were fetched.")
                reportedMissed = (answer.epoch, current.after)
            }
            if answer.entries.isEmpty {
                if once { return 0 }
                let pause = Self.pollSeconds - (uptime() - began)
                if pause > 0 { try await sleep(seconds: pause) }
                continue
            }

            var refused = 0
            for entry in answer.entries {
                let code: String?
                do {
                    code = try await apply(entry.op)
                } catch is AppUnreachable {
                    print("The Erdigo app could not be reached. The operations stay on the relay.")
                    return 3
                }
                if let code {
                    print("Refused operation \(entry.seq) (\(Operation.name(of: entry.op))): \(code)")
                    refused += 1
                }
                // Saved before the next operation, so a restart repeats at most the one in flight.
                current.after = entry.seq
                try current.save(in: directory)
                place = current
            }
            let count = answer.entries.count
            print("Done with \(count) operation\(count == 1 ? "" : "s"), up to \(current.after)\(refused > 0 ? " (\(refused) refused)" : "")")
        }
    }

    /// Applies one operation. Returns the code of a refusal.
    ///
    /// While the app cannot be reached the SDK waits 1, 2, 4 ... up to 60 seconds and sends the same request again
    /// with the same sequence number, so an operation the app applied but whose answer was lost is not applied twice
    /// (spec sections 7 and 13). With --once the gateway gives up when the app has not taken the operation about 30
    /// seconds after the first sign of trouble, even if the app takes every new connection.
    private func apply(_ value: JSONValue) async throws -> String? {
        let operation: Operation
        do {
            operation = try Operation(value)
        } catch let refused as Operation.Refused {
            return refused.code
        }
        let outage = Outage()
        // Only for the answers the SDK does not wait out itself.
        var waits = Backoff()
        while true {
            let began = uptime()
            guard let outcome = try await attempt(operation, outage: outage) else { return nil }
            switch outcome {
            case .refused(let code):
                return code
            case .fatal(let message):
                throw Exit(code: 1, message: message)
            case .gaveUp:
                throw AppUnreachable()
            case .notNow:
                let start = outage.note(at: began)
                guard let delay = waits.next(giveUpAfter: once ? Self.onceGiveUpSeconds : nil, tryBegan: start) else { throw AppUnreachable() }
                // This line says when, so the one without a time is not needed for this operation any more.
                _ = outage.takeNotice()
                print("No answer from the Erdigo app; trying again in \(Backoff.describe(delay)).")
                try await sleep(seconds: delay)
            }
        }
    }

    /// One try, nil when the app took the operation. A watcher runs beside it, and with --once ends it when the time
    /// is up. Ctrl+C or SIGTERM cancels both.
    private func attempt(_ operation: Operation, outage: Outage) async throws -> Outcome? {
        let client = self.client
        let once = self.once
        outage.isSending = false
        let end = try await withThrowingTaskGroup(of: TryEnd.self) { group -> TryEnd in
            group.addTask { .finished(try await Self.send(operation, with: client, outage: outage)) }
            group.addTask {
                try await Self.watch(client, outage: outage, once: once)
                return .outOfTime
            }
            // Whichever ends first decides, and the other is stopped. A request stopped half way may have been
            // applied, which at-least-once delivery allows: the operation stays on the relay.
            defer { group.cancelAll() }
            guard let first = try await group.next() else { throw CancellationError() }
            return first
        }
        switch end {
        case .finished(let outcome):
            return outcome
        case .outOfTime:
            // Stopping, which may have come at the same moment, ends the run with 0 rather than 3.
            try Task.checkCancellation()
            return .gaveUp
        }
    }

    /// Sends the operation through the SDK, nil when the app took it. Connecting first keeps a failure of the
    /// connection, such as a welcome this SDK cannot use or an error answer to hello, apart from an answer about this
    /// operation. Only such an answer may refuse it.
    private static func send(_ operation: Operation, with client: ErdigoClient, outage: Outage) async throws -> Outcome? {
        do {
            try await client.connect()
        } catch {
            if error is CancellationError || Task.isCancelled { throw CancellationError() }
            return classifyConnection(error)
        }
        outage.isSending = true
        do {
            try await operation.apply(with: client)
            return nil
        } catch {
            if error is CancellationError || Task.isCancelled { throw CancellationError() }
            return classifyAnswer(error)
        }
    }

    /// Watches one try from outside the SDK, which does not say how it is doing.
    ///
    /// A connection the SDK made for this try and then lost means sending failed, and the trouble began then. While
    /// the trouble lasts, or while the try goes on without any connection to the app, the watcher says once per
    /// operation that the app does not answer. The SDK does not tell when it tries next, so neither does the line. A
    /// request waiting for the app's rate limit keeps its connection, so that wait is not mistaken for this.
    ///
    /// With --once it returns once the trouble has lasted `onceGiveUpSeconds`. Otherwise it runs until it is cancelled.
    private static func watch(_ client: ErdigoClient, outage: Outage, once: Bool) async throws {
        var unconnectedSince: TimeInterval?
        while true {
            try await Task.sleep(nanoseconds: 250_000_000)
            let connected = await client.limits != nil
            let now = uptime()
            if connected {
                unconnectedSince = nil
            } else {
                unconnectedSince = unconnectedSince ?? now
                if outage.isSending { outage.note(at: now) }
            }
            let troubled = outage.since.map { now - $0 }
            let unconnected = unconnectedSince.map { now - $0 }
            if !Task.isCancelled, max(troubled ?? 0, unconnected ?? 0) >= noAnswerNoticeSeconds, outage.takeNotice() {
                print("No answer from the Erdigo app; trying again.")
            }
            if once, let troubled, troubled >= onceGiveUpSeconds { return }
        }
    }

    /// Nothing about this operation was sent yet, so nothing here can refuse it.
    private static func classifyConnection(_ error: Error) -> Outcome {
        switch error as? ErdigoError {
        case .noAnswer?, .versionUnsupported?, .storage?:
            return classifyAnswer(error)
        default:
            return .notNow
        }
    }

    /// An error from sending this operation. A lost connection never comes here: the SDK waits for the app and
    /// sends the request again, until it is cancelled or, with --once, gives up with `noAnswer` (or the watcher
    /// gives up first).
    private static func classifyAnswer(_ error: Error) -> Outcome {
        guard let error = error as? ErdigoError else { return .notNow }
        switch error {
        case .noAnswer:
            return .gaveUp
        case .notPermitted:
            return .refused("not_permitted")
        case .rejected(let answer):
            // These say "not now" rather than "no": rate_limited, internal_error and paused leave the request
            // unapplied (spec section 7), and too_many_errors comes instead of running it.
            if ["rate_limited", "internal_error", "too_many_errors", "paused"].contains(answer.code) { return .notNow }
            let printable = (1...40).contains(answer.code.utf8.count)
                && answer.code.utf8.allSatisfy { (UInt8(ascii: "a")...UInt8(ascii: "z")).contains($0) || $0 == UInt8(ascii: "_") }
            return .refused(printable ? answer.code : "unknown_error")
        case .invalidArgument:
            return .refused("invalid_argument")
        case .protocolViolation:
            // The app answered in a way the SDK cannot use, which is not a refusal. The operation is kept,
            // and may have been applied, which at-least-once delivery allows.
            return .notNow
        case .replaced, .sequenceConflict:
            // Sending the operation again as a new request could apply it twice.
            return .fatal("Another program is writing with this gateway's key. Run one gateway per profile.")
        case .versionUnsupported:
            return .fatal("The Erdigo app speaks no protocol version this SDK knows. Update the SDK.")
        case .storage(let detail):
            return .fatal(detail)
        case .fingerprintMismatch, .keyNotConfirmed, .pairingDenied, .pairingTimedOut, .codeReused:
            // Only pairing gives these.
            return .notNow
        }
    }
}

private func runGateway(_ arguments: [String]) async throws -> Int32 {
    var options = try Options(arguments, flags: ["once"])
    let directory = try stateDirectory(&options)
    let relayText = try options.required("relay")
    let where_ = try target(&options)
    let once = options.flag("once")
    try options.finish()

    let base = try relayBase(relayText)
    // Never repeat the token in a message: messages end up in logs.
    guard let token = ProcessInfo.processInfo.environment["ERDIGO_RELAY_TOKEN"], !token.isEmpty else {
        throw UsageError(message: "ERDIGO_RELAY_TOKEN is not set. Put the gateway token in it.")
    }
    guard isValidToken(token) else {
        throw UsageError(message: "ERDIGO_RELAY_TOKEN is not 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'")
    }
    let store = try FileDeviceStore(directory: directory)
    let pairings = try store.pairings()
    guard pairings.count == 1 else {
        throw UsageError(message: pairings.isEmpty
            ? "this profile has no pairing. Run 'erdigo-example-gateway pair' first."
            : "this profile holds more than one pairing, and a gateway writes to exactly one receiver. Pair it in a profile of its own.")
    }
    // Two gateways with one key would take each other's connection and repeat each other's work.
    let lock = open(store.directory.appendingPathComponent(".gateway.lock").path, O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
    guard lock >= 0, flock(lock, LOCK_EX | LOCK_NB) == 0 else {
        throw UsageError(message: "another gateway is already running with this profile")
    }

    var place = try Place.load(from: store.directory)
    if place?.relay != base { place = nil }
    let client = ErdigoClient(
        identity: try store.loadOrCreateIdentity(),
        pairing: pairings[0],
        store: store,
        target: where_,
        // The SDK waits out an app it cannot reach and sends a request whose answer was lost again with the same
        // sequence number, so the app applies it once. Without --once it waits until the gateway is stopped.
        retry: once ? .upTo(Gateway.onceGiveUpSeconds) : .forever
    )
    let gateway = Gateway(
        client: client,
        // Well above the 25 seconds a relay may hold a fetch open, and with --once, which never asks it to wait,
        // short enough that a relay that takes the connection and never answers is given up on in time.
        relay: RelayClient(base: base, token: token, idleSeconds: once ? 15 : 45),
        directory: store.directory,
        base: base,
        once: once,
        place: place
    )
    return try await gateway.run()
}

// MARK: main

setvbuf(stdout, nil, _IOLBF, 0)
var arguments = Array(CommandLine.arguments.dropFirst())
guard let command = arguments.first, !["help", "--help", "-h"].contains(command) else {
    print(usage)
    exit(0)
}
arguments.removeFirst()

switch command {
case "pair":
    do {
        try await runPair(arguments)
    } catch {
        fputs("erdigo-example-gateway: \(error.localizedDescription)\n", stderr)
        exit(1)
    }
case "run":
    let work = Task { () -> Int32 in
        do {
            return try await runGateway(arguments)
        } catch is CancellationError {
            return 0
        } catch let stop as Exit {
            fputs("erdigo-example-gateway: \(stop.message)\n", stderr)
            return stop.code
        } catch {
            fputs("erdigo-example-gateway: \(error.localizedDescription)\n", stderr)
            return 1
        }
    }
    signal(SIGINT, SIG_IGN)
    signal(SIGTERM, SIG_IGN)
    let signals = DispatchQueue(label: "erdigo.gateway.signals")
    let sources = [SIGINT, SIGTERM].map { number -> DispatchSourceSignal in
        let source = DispatchSource.makeSignalSource(signal: number, queue: signals)
        source.setEventHandler {
            work.cancel()
            // Everything saved so far is whole, so stopping a little later than asked loses nothing.
            signals.asyncAfter(deadline: .now() + 5) { exit(0) }
        }
        source.resume()
        return source
    }
    let code = await work.value
    withExtendedLifetime(sources) {}
    exit(code)
default:
    print(usage)
    fputs("erdigo-example-gateway: unknown command\n", stderr)
    exit(1)
}
