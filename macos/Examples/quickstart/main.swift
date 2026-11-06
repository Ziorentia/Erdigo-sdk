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
// because arguments end up in the process list and in shell history.

import ErdigoSDK
import Foundation

func run() async throws {
    guard CommandLine.arguments.count == 3, let port = UInt16(CommandLine.arguments[2]) else {
        print("usage: erdigo-example-quickstart <host> <port>")
        return
    }
    let target = ReceiverTarget.address(host: CommandLine.arguments[1], port: port)

    // A real program would use FileDeviceStore, so the pairing survives a restart.
    let store = InMemoryDeviceStore()
    let identity = try store.loadOrCreateIdentity()

    guard let secret = getpass("Pairing code: ") else { return }
    let ticket = try PairingTicket(parsing: String(cString: secret))

    print("Confirm the request on the receiver...")
    let pairing = try await Pairing.pair(
        target: target,
        ticket: ticket,
        identity: identity,
        deviceName: "Quickstart example",
        kinds: [.pin, .marker, .note, .track]
    )
    try store.save(pairing)

    // A typed code proves only that the other side knows the code. The fingerprint in a QR
    // code would prove more, so ask the person to compare before trusting this pairing.
    if ticket.fingerprint == nil {
        print("Receiver key: \(IntegrationV1.shortFingerprint(pairing.fingerprint))")
        print("Check this matches the key shown on the receiver, then press Return to continue.")
        _ = readLine()
    }

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
} catch {
    print("Failed: \(error.localizedDescription)")
    exit(1)
}
