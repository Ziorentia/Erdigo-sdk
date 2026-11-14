// A complete small program: pair with a receiver once, then write a few items.
//
// Start the mock receiver in one terminal:
//
//     swift run erdigo-send serve --mock
//
// and run this in another, using the port and code it prints:
//
//     swift run erdigo-example-quickstart 127.0.0.1 <port>
//
// The code is read from the keyboard. It is never passed as a command-line argument,
// because arguments end up in the process list and in shell history. If the code was typed
// rather than copied from the QR text, you then compare the receiver's key and type yes.
// The receiver is not asked to pair, no pairing is saved and no items are sent before that.
// That needs standard input and output to be a terminal. Without one, a typed code is refused, so use the QR text.

import ErdigoSDK
import Foundation

func stop(_ message: String) -> Never {
    fputs("\(message)\n", stderr)
    exit(1)
}

func run() async throws {
    guard CommandLine.arguments.count == 3, let port = UInt16(CommandLine.arguments[2]) else {
        print("usage: erdigo-example-quickstart <host> <port>")
        return
    }
    let target = ReceiverTarget.address(host: CommandLine.arguments[1], port: port)

    // A real program would use FileDeviceStore, so the pairing survives a restart.
    let store = InMemoryDeviceStore()
    let identity = try store.loadOrCreateIdentity()

    guard let secret = getpass("Pairing code: ") else { stop("No pairing code was read.") }
    let ticket = try PairingTicket(parsing: String(cString: secret))

    // Only a person can compare the key. Whatever a pipe or a file holds was written before the key
    // existed, so it is no answer, and a question sent to a file would be answered unseen. The
    // receiver is not contacted when no answer is possible.
    if ticket.fingerprint == nil, isatty(STDIN_FILENO) == 0 || isatty(STDOUT_FILENO) == 0 {
        stop("A typed code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text.")
    }

    print("Contacting the receiver. If it asks, confirm the request there.")
    let pairing = try await Pairing.pair(
        target: target,
        ticket: ticket,
        identity: identity,
        deviceName: "Quickstart example",
        kinds: [.pin, .marker, .note, .track],
        // A typed code proves only that the other side knows the code, so a person has to compare the
        // receiver's key. This runs before the receiver is asked to pair. A QR code carries the key,
        // and Pairing.pair compares it without asking.
        confirmKey: { key in
            // Only an answer given after seeing the key counts, so anything typed before it is discarded.
            tcflush(STDIN_FILENO, TCIFLUSH)
            print("Receiver key: \(key)")
            print("If this matches the key shown on the receiver, type yes: ", terminator: "")
            fflush(stdout)
            // End of input and every other answer count as no.
            let confirmed = readLine()?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == "yes"
            if !confirmed { print("") }
            return confirmed
        }
    )
    try store.save(pairing)

    let client = ErdigoClient(identity: identity, pairing: pairing, store: store, target: target)

    let cityHall = try Coordinate(latitude: 37.5663, longitude: 126.9779)
    try await client.putPin(id: "city-hall", at: cityHall, title: "Seoul City Hall")

    let palace = try Coordinate(latitude: 37.5796, longitude: 126.9770)
    try await client.putMarker(id: "palace", at: palace, title: "Gyeongbokgung Palace", color: .red)
    try await client.putNote(id: "meeting-point", at: palace, text: "Meet at the main gate.")

    // Walk from the palace to City Hall in a straight line.
    let points = try (0...4).map { step -> TrackPoint in
        let fraction = Double(step) / 4
        return try TrackPoint(coordinate: Coordinate(
            latitude: palace.latitude + (cityHall.latitude - palace.latitude) * fraction,
            longitude: palace.longitude + (cityHall.longitude - palace.longitude) * fraction
        ))
    }
    try await client.appendTrack(id: "walk", points: points, title: "Walk to City Hall")
    try await client.finishTrack(id: "walk")

    await client.close()
    print("Done.")
}

do {
    try await run()
} catch let error as WireError {
    print("Failed: \(error.detail)")
    exit(1)
} catch {
    print("Failed: \(error.localizedDescription)")
    exit(1)
}
