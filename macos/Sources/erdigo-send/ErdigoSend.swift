import Darwin
import ErdigoMockReceiver
import ErdigoSDK
import Foundation

private let usage = """
erdigo-send: write pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network.

Pairing
  pair --name NAME --kinds pin,marker,note,photo,track [--label LABEL]
       [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
      The code is read from the keyboard, or from a file only you can read.
      It is never accepted as a command-line argument.
      A bare code means you compare the receiver's key and type yes before the receiver is asked to pair.
      That needs standard input and output to be a terminal, so a bare code is refused without one.
      The QR text or --fingerprint compares the key for you, so nothing is asked.
  list
  forget --to LABEL
  identity
      Prints this device's fingerprint.

Writing
  ping
  pin          --id ID --lat N --lon N [--title TEXT]
  marker       --id ID --lat N --lon N [--title TEXT] [--color COLOR]
  note         --id ID --lat N --lon N (--text TEXT | --text-file FILE)
  photo        --id ID --lat N --lon N --file FILE [--title TEXT]
  track-add    --id ID --lat N --lon N [--ts MILLISECONDS] [--alt METRES] [--title TEXT]
  track-finish --id ID
  move         --id ID --lat N --lon N
  delete       --id ID
  delete-all   --yes
      Options: --to LABEL (needed with several receivers), --host HOST --port PORT, --retry-for SECONDS

Testing without the Erdigo app
  serve --mock [--port PORT] [--lan] [--bonjour] [--code-file FILE] [--ask | --auto-approve] [--redact]
      Runs an in-memory receiver. By default only this computer can reach it, and it allows every
      pairing request. Use --ask to answer each request yourself, as a person would in the app.
      With --lan anyone on the network can reach it, so it asks you about each request unless
      --auto-approve is given, for a test that runs unattended.
      --bonjour advertises it on the local network with Bonjour.
      Where its output is saved or shared, use --code-file FILE, which only you can read, instead of
      showing the pairing code, and --redact to hide codes, keys, device names and what items hold.
      Pairing questions go to standard error and always show the device's name and address.
      Type 'help' while it runs to see its commands.

For pairing and writing commands
  --profile NAME    Use a separate identity and pairings (default: "default").
  --state-dir DIR   Keep state in DIR instead of Application Support.
  --keychain        Keep the device key in the login keychain instead of a 0600 file.
  --redact          Hide labels, keys and other identifying output, for pasting into a bug report.
                    With pair, it needs the QR text or --fingerprint, because a hidden key cannot be compared.

"""

private struct UsageError: Error, LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// Every option the tool has. Errors name an option only if it is on this list, because
/// anything else that was typed might be a pairing code.
private let knownOptions: Set<String> = [
    "alt", "ask", "auto-approve", "bonjour", "code-file", "color", "file", "fingerprint", "host", "id", "keychain", "kinds", "label", "lan",
    "lat", "lon", "mock", "name", "port", "profile", "redact", "retry-for", "state-dir", "text", "text-file", "title", "to", "ts", "yes",
]

private func optionDescription(_ name: String) -> String {
    knownOptions.contains(name) ? "--\(name)" : "an unknown option"
}

/// A deliberately small option parser: `--name value` pairs and bare flags, nothing else.
private struct Options {
    private var values: [String: String] = [:]
    private var flags = Set<String>()
    private var used = Set<String>()

    init(_ arguments: [String], flags allowedFlags: Set<String>) throws {
        var index = 0
        while index < arguments.count {
            let argument = arguments[index]
            // Errors here never repeat what was typed, because it might be a pairing code.
            guard argument.hasPrefix("--") else {
                throw UsageError(message: "unexpected argument. Options start with --, and a pairing code is never given as an argument.")
            }
            let name = String(argument.dropFirst(2))
            if name == "code" || name.hasPrefix("code=") {
                throw UsageError(message: "the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. Type it when asked, or use --code-file.")
            }
            if let equals = name.firstIndex(of: "=") {
                throw UsageError(message: "write \(optionDescription(String(name[..<equals]))) and its value as two separate arguments")
            }
            if allowedFlags.contains(name) {
                flags.insert(name)
                index += 1
            } else {
                guard knownOptions.contains(name) else { throw UsageError(message: "an unknown option was given. See 'erdigo-send help'.") }
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

    mutating func requiredDouble(_ name: String) throws -> Double {
        let text = try required(name)
        guard let value = Double(text), value.isFinite else { throw UsageError(message: "--\(name) must be a number") }
        return value
    }

    mutating func optionalPort() throws -> UInt16? {
        guard let text = optional("port") else { return nil }
        guard let value = UInt16(text) else { throw UsageError(message: "--port must be between 0 and 65535") }
        return value
    }

    /// Call after reading every option the command understands.
    func finish() throws {
        let unknown = Set(values.keys).union(flags).subtracting(used)
        if let first = unknown.sorted().first { throw UsageError(message: "--\(first) does not apply to this command") }
    }
}

/// Reads a file only if it cannot be read by anyone else, as ssh does for its keys.
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

private struct Context {
    let store: FileDeviceStore
    let redact: Bool

    func shown(_ text: String) -> String { redact ? "<redacted>" : text }
}

private func makeContext(_ options: inout Options) throws -> Context {
    let profile = options.optional("profile") ?? "default"
    guard !profile.isEmpty, profile.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.contains($0) || $0 == "-" || $0 == "_" }) else {
        throw UsageError(message: "--profile may contain only letters, digits, '-' and '_'")
    }
    let directory: URL
    if let path = options.optional("state-dir") {
        guard options.optional("profile") == nil else {
            throw UsageError(message: "--state-dir and --profile cannot be used together, because a state folder holds one identity")
        }
        directory = URL(fileURLWithPath: path, isDirectory: true)
    } else {
        directory = try FileDeviceStore.defaultDirectory(profile: profile)
    }
    let location: FileDeviceStore.KeyLocation = options.flag("keychain") ? .keychain(service: "ErdigoSDK", account: profile) : .file
    return Context(store: try FileDeviceStore(directory: directory, keyLocation: location), redact: options.flag("redact"))
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

private func target(_ options: inout Options) throws -> ReceiverTarget {
    let host = options.optional("host")
    let port = try options.optionalPort()
    switch (host, port) {
    case (nil, nil):
        return .discover
    case (let host?, let port?):
        return .address(host: host, port: port)
    default:
        throw UsageError(message: "--host and --port go together")
    }
}

private func selectPairing(_ context: Context, label: String?) throws -> ReceiverPairing {
    let pairings = try context.store.pairings()
    if let label {
        let found = pairings.filter { $0.label == label }
        guard found.count == 1 else {
            throw UsageError(message: found.isEmpty
                ? "no receiver is paired under that label. See 'erdigo-send list'."
                : "several receivers have that label. Forget one of them first.")
        }
        return found[0]
    }
    switch pairings.count {
    case 0:
        throw UsageError(message: "no receiver is paired yet. Run 'erdigo-send pair' first.")
    case 1:
        return pairings[0]
    default:
        throw UsageError(message: "several receivers are paired. Choose one with --to LABEL.")
    }
}

private func coordinate(_ options: inout Options) throws -> Coordinate {
    do {
        return try Coordinate(latitude: try options.requiredDouble("lat"), longitude: try options.requiredDouble("lon"))
    } catch let error as WireError {
        throw UsageError(message: error.detail)
    }
}

private func runPair(_ arguments: [String]) async throws {
    var options = try Options(arguments, flags: ["keychain", "redact"])
    let context = try makeContext(&options)
    let name = try options.required("name")
    let kinds = try parseKinds(try options.required("kinds"))
    let label = options.optional("label")
    let codeFile = options.optional("code-file")
    let fingerprint = options.optional("fingerprint")?.lowercased()
    let where_ = try target(&options)
    try options.finish()

    if let fingerprint, fingerprint.count != 64 || Data(hexString: fingerprint) == nil {
        throw UsageError(message: "--fingerprint must be 64 hexadecimal digits")
    }
    if let label, try context.store.pairings().contains(where: { $0.label == label }) {
        throw UsageError(message: "a receiver is already paired under that label. Choose another, or use 'erdigo-send forget --to LABEL' first.")
    }
    let text = try codeFile.map(readPrivateFile) ?? readCodeFromKeyboard()
    var ticket: PairingTicket
    do {
        ticket = try PairingTicket(parsing: text)
    } catch let error as WireError {
        throw UsageError(message: error.detail)
    }
    if let fingerprint {
        if let fromQR = ticket.fingerprint, fromQR != fingerprint {
            throw ErdigoError.fingerprintMismatch
        }
        ticket = PairingTicket(code: ticket.code, fingerprint: fingerprint)
    }
    if ticket.fingerprint == nil, context.redact {
        throw UsageError(message: "--redact hides the receiver's key, so it cannot be compared for a bare code. Use the QR text, or give the full 64-digit key with --fingerprint.")
    }
    // Only a person can compare the key. Whatever a pipe or a file holds was written before the key
    // existed, so it is no answer, and a question sent to a file would be answered unseen. The
    // receiver is not contacted when no answer is possible.
    if ticket.fingerprint == nil, isatty(STDIN_FILENO) == 0 || isatty(STDOUT_FILENO) == 0 {
        throw UsageError(message: "a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.")
    }

    let waiting = "Waiting for the request to be confirmed on the receiver..."
    let personCompares = ticket.fingerprint == nil
    print(personCompares ? "Checking the code with the receiver..." : waiting)
    let identity = try context.store.loadOrCreateIdentity()
    let pairing = try await Pairing.pair(
        target: where_,
        ticket: ticket,
        identity: identity,
        deviceName: name,
        kinds: kinds,
        label: label,
        // A bare code proves only that the other side knows the code, and only a person can tell
        // whether this is the right receiver. This runs before the receiver is asked to pair.
        confirmKey: { key in
            // Only an answer given after seeing the key counts, so anything typed before it is
            // discarded. Standard input is a terminal here, because a bare code is refused otherwise.
            tcflush(STDIN_FILENO, TCIFLUSH)
            print("Receiver key: \(key)")
            print("If this matches the key shown on the receiver, type yes: ", terminator: "")
            fflush(stdout)
            guard readLine()?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "yes" else {
                print("")
                return false
            }
            print(waiting)
            return true
        }
    )
    try context.store.save(pairing)
    print("Paired as '\(context.shown(pairing.label))'. Allowed: \(pairing.kinds.map(\.rawValue).joined(separator: ", ")).")
    if !personCompares {
        print("Receiver key: \(context.shown(IntegrationV1.shortFingerprint(pairing.fingerprint)))")
    }
}

private func runList(_ arguments: [String]) throws {
    var options = try Options(arguments, flags: ["keychain", "redact"])
    let context = try makeContext(&options)
    try options.finish()
    let pairings = try context.store.pairings()
    if pairings.isEmpty {
        print("No receivers are paired.")
    }
    for pairing in pairings {
        let kinds = pairing.kinds.map(\.rawValue).joined(separator: ",")
        print("\(context.shown(pairing.label))  key \(context.shown(IntegrationV1.shortFingerprint(pairing.fingerprint)))  allowed \(kinds)")
    }
}

private func runForget(_ arguments: [String]) throws {
    var options = try Options(arguments, flags: ["keychain", "redact"])
    let context = try makeContext(&options)
    let label = try options.required("to")
    try options.finish()
    let pairing = try selectPairing(context, label: label)
    try context.store.removePairing(fingerprint: pairing.fingerprint)
    print("Forgotten here. To remove this device on the receiver, remove it from the list of connected devices there.")
}

private func runIdentity(_ arguments: [String]) throws {
    var options = try Options(arguments, flags: ["keychain", "redact"])
    let context = try makeContext(&options)
    try options.finish()
    print(context.shown(try context.store.loadOrCreateIdentity().fingerprint))
}

/// Everything common to the writing commands. `prepare` reads its own options and returns the work to do.
private func runWrite(
    _ arguments: [String],
    flags extraFlags: Set<String> = [],
    _ prepare: (inout Options) throws -> (@Sendable (ErdigoClient) async throws -> Void)
) async throws {
    var options = try Options(arguments, flags: extraFlags.union(["keychain", "redact"]))
    let context = try makeContext(&options)
    let label = options.optional("to")
    let where_ = try target(&options)
    var retry = RetryPolicy.upTo(30)
    if let text = options.optional("retry-for") {
        guard let seconds = Double(text), seconds >= 0, seconds.isFinite else {
            throw UsageError(message: "--retry-for must be a number of seconds")
        }
        retry = seconds == 0 ? .none : .upTo(seconds)
    }
    let body = try prepare(&options)
    try options.finish()

    let pairing = try selectPairing(context, label: label)
    let identity = try context.store.loadOrCreateIdentity()
    let client = ErdigoClient(identity: identity, pairing: pairing, store: context.store, target: where_, retry: retry)
    try await body(client)
    await client.close()
    print("OK")
}

/// Whether the mock asks a person about each pairing request. Anyone who can reach a mock on the network
/// and has a code could pair, so there a person decides unless the test says it runs unattended. On this
/// computer alone, allowing every request stays the default.
func asksAboutPairing(localNetwork: Bool, ask: Bool, autoApprove: Bool) throws -> Bool {
    if ask, autoApprove {
        throw UsageError(message: "--ask and --auto-approve cannot be used together")
    }
    return ask || (localNetwork && !autoApprove)
}

private func runServe(_ arguments: [String]) async throws {
    var options = try Options(arguments, flags: ["mock", "lan", "bonjour", "ask", "auto-approve", "redact"])
    guard options.flag("mock") else {
        throw UsageError(message: "'serve' only runs a mock receiver for testing, so --mock is required")
    }
    var settings = MockReceiver.Options()
    settings.port = try options.optionalPort() ?? 0
    settings.acceptsLocalNetwork = options.flag("lan")
    settings.advertises = options.flag("bonjour")
    if settings.advertises, !settings.acceptsLocalNetwork {
        throw UsageError(message: "--bonjour needs --lan, because a loopback-only receiver cannot be reached from the network")
    }
    let askGiven = options.flag("ask")
    let autoApprove = options.flag("auto-approve")
    let redact = options.flag("redact")
    let codeFile = options.optional("code-file")
    try options.finish()
    let ask = try asksAboutPairing(localNetwork: settings.acceptsLocalNetwork, ask: askGiven, autoApprove: autoApprove)
    if redact, codeFile == nil {
        throw UsageError(message: "--redact hides the pairing code, so it needs --code-file to give you the code another way")
    }

    @Sendable func shown(_ text: String) -> String { redact ? "<redacted>" : text }

    let receiver = try MockReceiver(options: settings)
    await receiver.setEventHandler { event in
        switch event {
        case .listening, .pairingWindowOpened:
            break
        case .pairingRequested(let device):
            print("[mock] pairing request from \(shown(String(device.prefix(16))))")
        case .paired(let device, let kinds):
            print("[mock] paired \(shown(String(device.prefix(16)))): \(kinds.map(\.rawValue).joined(separator: ","))")
        case .pairingDenied:
            print("[mock] pairing denied")
        case .codeReusedAlarm:
            print("[mock] WARNING: this code was used by another device. The pairing made with it was removed.")
        case .silentlyDropped(let reason):
            print("[mock] dropped a connection without answering: \(reason)")
        case .connected(let device):
            print("[mock] connected \(shown(String(device.prefix(16))))")
        case .disconnected(let device):
            print("[mock] disconnected \(shown(String(device.prefix(16))))")
        case .applied(_, let seq, let summary):
            print("[mock] #\(seq) \(summary)")
        case .rejected(_, let seq, let code):
            print("[mock] rejected #\(seq.map(String.init) ?? "-"): \(code)")
        case .revoked(let device):
            print("[mock] removed \(shown(String(device.prefix(16))))")
        }
    }

    if ask {
        await receiver.setApprovalHandler { request in
            let kinds = request.requestedKinds.map(\.rawValue).joined(separator: ", ")
            let address = request.address ?? "an unknown address"
            let note = request.wasRemovedBefore ? " You removed this device earlier." : ""
            // The name and address are what a person decides on, so --redact does not hide them here. The
            // question goes to standard error, so that standard output saved to a file does not hold them.
            fputs("[mock] '\(request.deviceName)' at \(address) wants to put \(kinds) on your map.\(note) Allow? Type 'y' or 'n':\n", stderr)
            let answer = await Console.shared.nextDecision()
            return answer ? .allow(kinds: request.requestedKinds, name: nil) : .deny
        }
    }

    func publish(_ ticket: PairingTicket) throws {
        guard let uri = ticket.uri else { throw UsageError(message: "the mock's key could not be put in the pairing text") }
        if let codeFile {
            let descriptor = open(codeFile, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW | O_CLOEXEC, 0o600)
            guard descriptor >= 0 else { throw UsageError(message: "could not write the code file") }
            defer { close(descriptor) }
            guard fchmod(descriptor, 0o600) == 0 else { throw UsageError(message: "could not protect the code file") }
            FileHandle(fileDescriptor: descriptor, closeOnDealloc: false).write(Data(uri.utf8))
            print("Pairing text written to the code file. It is valid for 10 minutes.")
        } else {
            print("Pairing code (valid for 10 minutes): \(ticket.code.display)")
            print("QR text: \(uri)")
        }
    }

    let port = try await receiver.start()
    print("This is a mock receiver for testing. It stores nothing on disk and draws no map.")
    print("Listening on \(settings.acceptsLocalNetwork ? "the local network" : "127.0.0.1"), port \(port)")
    print("Receiver key: \(shown(IntegrationV1.shortFingerprint(await receiver.fingerprint)))")
    if ask {
        print("Each pairing request is asked about here.")
    } else if settings.acceptsLocalNetwork {
        print("Every pairing request with a valid code is allowed without asking, from anywhere on the network.")
    } else {
        print("Every pairing request with a valid code is allowed without asking.")
    }
    try publish(try await receiver.openPairingWindow())
    print("Type 'help' for commands.")

    for await line in Console.shared.lines() {
        let words = line.split(separator: " ").map(String.init)
        switch words.first {
        case nil:
            break
        case "help":
            print("code | devices | items N | remove N | pause | resume | quit")
        case "code":
            try publish(try await receiver.openPairingWindow())
        case "devices":
            let list = await receiver.pairedDevices()
            if list.isEmpty { print("No devices.") }
            for (index, device) in list.enumerated() {
                print("\(index + 1). '\(shown(device.name))' \(shown(String(device.fingerprint.prefix(16)))) \(device.kinds.map(\.rawValue).joined(separator: ","))")
            }
        case "items":
            guard let device = await chooseDevice(receiver, words) else { break }
            for item in await receiver.items(ofDevice: device) {
                let position = redact ? "<redacted>" : "\(item.latitude.map { "\($0)" } ?? "-"),\(item.longitude.map { "\($0)" } ?? "-")"
                print("\(item.kind.rawValue) \(shown(item.id)) at \(position) title=\(shown(item.title ?? "-")) points=\(item.points.count)")
            }
        case "remove":
            guard let device = await chooseDevice(receiver, words) else { break }
            await receiver.revoke(deviceFingerprint: device)
        case "pause":
            await receiver.setPaused(true)
            print("Paused. All connections were closed.")
        case "resume":
            await receiver.setPaused(false)
            print("Resumed.")
        case "quit":
            await receiver.stop()
            return
        default:
            print("Unknown command. Type 'help'.")
        }
    }

    // Standard input ended, for example when started from a script. Keep serving until the process is stopped.
    while true {
        try await Task.sleep(nanoseconds: 3_600_000_000_000)
    }
}

private func chooseDevice(_ receiver: MockReceiver, _ words: [String]) async -> String? {
    let list = await receiver.pairedDevices()
    guard words.count == 2, let number = Int(words[1]), number >= 1, number <= list.count else {
        print("Give a device number from 'devices'.")
        return nil
    }
    return list[number - 1].fingerprint
}

/// Standard input for the mock's commands, shared with the pairing question.
actor Console {
    static let shared = Console { readLine() }
    private static let nobodyCanAnswer = "[mock] nothing more can be typed here, so the request was denied."

    private let readNextLine: @Sendable () -> String?
    private var pendingDecisions: [(id: UUID, continuation: CheckedContinuation<Bool, Never>)] = []
    private var inputEnded = false

    /// `readNextLine` blocks until a line is typed, and returns nil once nothing more can be.
    init(readNextLine: @escaping @Sendable () -> String?) {
        self.readNextLine = readNextLine
    }

    /// How many questions are waiting for an answer.
    var waitingQuestions: Int { pendingDecisions.count }

    /// Waits for 'y' or 'n'. If the request expires first, the answer is no, and the next
    /// line typed is an ordinary command rather than an answer to a prompt that has gone.
    /// Once nothing more can be typed, as when the mock was started from a script, every
    /// answer is no at once.
    func nextDecision() async -> Bool {
        if inputEnded {
            print(Self.nobodyCanAnswer)
            return false
        }
        let id = UUID()
        return await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
                pendingDecisions.append((id, continuation))
            }
        } onCancel: {
            Task { await self.cancelDecision(id) }
        }
    }

    private func cancelDecision(_ id: UUID) {
        guard let index = pendingDecisions.firstIndex(where: { $0.id == id }) else { return }
        pendingDecisions.remove(at: index).continuation.resume(returning: false)
        print("[mock] nobody answered in time, so the request was denied.")
    }

    fileprivate func answerPending(_ line: String) -> Bool {
        guard !pendingDecisions.isEmpty else { return false }
        let answer = line.trimmingCharacters(in: .whitespaces).lowercased()
        pendingDecisions.removeFirst().continuation.resume(returning: answer == "y" || answer == "yes")
        return true
    }

    private func endInput() {
        inputEnded = true
        let waiting = pendingDecisions
        pendingDecisions.removeAll()
        for decision in waiting {
            decision.continuation.resume(returning: false)
            print(Self.nobodyCanAnswer)
        }
    }

    /// Starts reading. Lines that answer a question are not commands, so they are not in the stream.
    nonisolated func lines() -> AsyncStream<String> {
        AsyncStream { continuation in
            let thread = Thread {
                while let line = self.readNextLine() {
                    let finished = DispatchSemaphore(value: 0)
                    Task {
                        let wasAnswer = await self.answerPending(line)
                        if !wasAnswer { continuation.yield(line) }
                        finished.signal()
                    }
                    finished.wait()
                }
                let ended = DispatchSemaphore(value: 0)
                Task {
                    await self.endInput()
                    ended.signal()
                }
                ended.wait()
                continuation.finish()
            }
            thread.start()
        }
    }
}

@main
struct ErdigoSend {
    static func main() async {
        setvbuf(stdout, nil, _IOLBF, 0)
        var arguments = Array(CommandLine.arguments.dropFirst())
        guard let command = arguments.first, !["help", "--help", "-h"].contains(command) else {
            print(usage)
            return
        }
        arguments.removeFirst()

        do {
            switch command {
            case "pair":
                try await runPair(arguments)
            case "list":
                try runList(arguments)
            case "forget":
                try runForget(arguments)
            case "identity":
                try runIdentity(arguments)
            case "serve":
                try await runServe(arguments)
            case "ping":
                try await runWrite(arguments) { _ in { try await $0.ping() } }
            case "pin":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    let at = try coordinate(&options)
                    let title = options.optional("title")
                    return { try await $0.putPin(id: id, at: at, title: title) }
                }
            case "marker":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    let at = try coordinate(&options)
                    let title = options.optional("title")
                    var color: MarkerColor?
                    if let text = options.optional("color") {
                        guard let parsed = MarkerColor(rawValue: text) else {
                            let names = MarkerColor.allCases.map(\.rawValue).joined(separator: ", ")
                            throw UsageError(message: "--color must be one of: \(names)")
                        }
                        color = parsed
                    }
                    let chosen = color
                    return { try await $0.putMarker(id: id, at: at, title: title, color: chosen) }
                }
            case "note":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    let at = try coordinate(&options)
                    let inline = options.optional("text")
                    let file = options.optional("text-file")
                    let text: String
                    switch (inline, file) {
                    case (let value?, nil):
                        text = value
                    case (nil, let path?):
                        text = noteFromFile(try String(contentsOfFile: path, encoding: .utf8))
                    default:
                        throw UsageError(message: "give either --text or --text-file")
                    }
                    return { try await $0.putNote(id: id, at: at, text: text) }
                }
            case "photo":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    let at = try coordinate(&options)
                    let title = options.optional("title")
                    let path = try options.required("file")
                    let attributes = try FileManager.default.attributesOfItem(atPath: path)
                    guard (attributes[.size] as? Int ?? .max) <= Limits.v1.photoBytes else {
                        throw UsageError(message: "a photo is at most \(Limits.v1.photoBytes) bytes")
                    }
                    let data = try Data(contentsOf: URL(fileURLWithPath: path))
                    return { try await $0.putPhoto(id: id, at: at, data: data, title: title) }
                }
            case "track-add":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    let at = try coordinate(&options)
                    let title = options.optional("title")
                    var timestamp: Int64?
                    if let text = options.optional("ts") {
                        guard let value = Int64(text) else { throw UsageError(message: "--ts must be whole milliseconds") }
                        timestamp = value
                    }
                    var altitude: Double?
                    if let text = options.optional("alt") {
                        guard let value = Double(text) else { throw UsageError(message: "--alt must be a number") }
                        altitude = value
                    }
                    let point: TrackPoint
                    do {
                        point = try TrackPoint(coordinate: at, timestampMS: timestamp, altitudeM: altitude)
                    } catch let error as WireError {
                        throw UsageError(message: error.detail)
                    }
                    return { try await $0.appendTrack(id: id, points: [point], title: title) }
                }
            case "track-finish":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    return { try await $0.finishTrack(id: id) }
                }
            case "move":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    let at = try coordinate(&options)
                    return { try await $0.move(id: id, to: at) }
                }
            case "delete":
                try await runWrite(arguments) { options in
                    let id = try options.required("id")
                    return { try await $0.delete(id: id) }
                }
            case "delete-all":
                try await runWrite(arguments, flags: ["yes"]) { options in
                    guard options.flag("yes") else {
                        throw UsageError(message: "this deletes every item this device put on the receiver. Add --yes to confirm.")
                    }
                    return { try await $0.deleteAll() }
                }
            default:
                print(usage)
                throw UsageError(message: "unknown command")
            }
        } catch {
            fputs("erdigo-send: \(error.localizedDescription)\n", stderr)
            exit(1)
        }
    }
}

/// The note in a file's text. Two things editors add are taken out, so that a note written in Notepad can be sent as
/// it is: a byte order mark at the start, and the carriage return of each Windows line ending (CR LF becomes LF).
/// Nothing else is changed: a trailing newline stays, and a carriage return on its own is left for the note rules to
/// refuse.
func noteFromFile(_ text: String) -> String {
    var scalars = Substring(text).unicodeScalars
    if scalars.first == "\u{FEFF}" {
        scalars.removeFirst()
    }
    var note = String.UnicodeScalarView()
    var index = scalars.startIndex
    while index < scalars.endIndex {
        let scalar = scalars[index]
        let next = scalars.index(after: index)
        if scalar == "\r", next < scalars.endIndex, scalars[next] == "\n" {
            index = next
            continue
        }
        note.append(scalar)
        index = next
    }
    return String(note)
}
