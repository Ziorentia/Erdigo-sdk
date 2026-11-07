import CoreGraphics
import CryptoKit
@_spi(TestVectors) @_spi(ErdigoInternal) import ErdigoCore
@_spi(TestVectors) import ErdigoMockReceiver
import ErdigoSDK
import Foundation
import ImageIO
import Network
import XCTest

/// Runs the SDK against the mock receiver over real TCP on the loopback interface.
final class EndToEndTests: XCTestCase {
    private var receivers: [MockReceiver] = []
    private var rawCounter: UInt64 = 0

    override func tearDown() async throws {
        for receiver in receivers { await receiver.stop() }
        receivers.removeAll()
    }

    private func fastOptions() -> MockReceiver.Options {
        var options = MockReceiver.Options()
        options.silentDelay = 0...0.01
        options.addressPenaltyBase = 0
        options.handshakeTimeout = 3
        options.maxPreAuthConnectionsPerAddress = 8
        // Most tests are not about pacing.
        options.limits.messagesPerSecond = 10_000
        options.limits.messageBurst = 10_000
        options.limits.pointsPerSecond = 10_000
        options.limits.pointBurst = 10_000
        options.maxRejectionsPerMinute = 10_000
        return options
    }

    private func startReceiver(_ options: MockReceiver.Options? = nil) async throws -> (MockReceiver, UInt16) {
        let receiver = try MockReceiver(options: options ?? fastOptions())
        receivers.append(receiver)
        let port = try await receiver.start()
        return (receiver, port)
    }

    private struct Paired {
        let store: InMemoryDeviceStore
        let identity: DeviceIdentity
        let pairing: ReceiverPairing
        let client: ErdigoClient
    }

    private func pairedDevice(
        _ receiver: MockReceiver,
        port: UInt16,
        kinds: Set<ItemKind> = Set(ItemKind.allCases),
        retry: RetryPolicy = .none
    ) async throws -> Paired {
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let ticket = try await receiver.openPairingWindow()
        let pairing = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port),
            ticket: ticket,
            identity: identity,
            deviceName: "Test device",
            kinds: kinds,
            handshakeTimeout: 1
        )
        try store.save(pairing)
        let client = ErdigoClient(
            identity: identity,
            pairing: pairing,
            store: store,
            target: .address(host: "127.0.0.1", port: port),
            retry: retry,
            connectTimeout: 1,
            ackTimeout: 3
        )
        return Paired(store: store, identity: identity, pairing: pairing, client: client)
    }

    private let palace = try! Coordinate(latitude: 37.5796, longitude: 126.9770)
    private let cityHall = try! Coordinate(latitude: 37.5663, longitude: 126.9779)

    /// Reads whatever the peer sends until it closes, and reports the byte count.
    private func bytesUntilClose(_ stream: ByteStream, timeout: TimeInterval = 2) async -> Int {
        stream.armDeadline(timeout)
        var total = 0
        while true {
            do {
                total += try await stream.read(exactly: 1).count
            } catch {
                return total
            }
        }
    }

    private func rawConnect(_ port: UInt16) async throws -> ByteStream {
        let stream = try ByteStream(host: "127.0.0.1", port: port)
        try await stream.start(timeout: 2)
        return stream
    }

    /// Builds a first message for an ordinary connection.
    private func connectMessage(
        deviceKey: Curve25519.KeyAgreement.PrivateKey,
        receiverKey: Data,
        counter: UInt64
    ) throws -> (NoiseHandshake, Data) {
        let handshake = try NoiseHandshake(
            pattern: .ik,
            isInitiator: true,
            prologue: IntegrationV1.preamble(.connect),
            staticKey: deviceKey,
            remoteStaticKey: receiverKey
        )
        var bytes = Data()
        withUnsafeBytes(of: counter.bigEndian) { bytes.append(contentsOf: $0) }
        let message = try handshake.writeMessage(payload: bytes)
        return (handshake, IntegrationV1.preamble(.connect) + (try ByteStream.frame(message)))
    }

    private func assertRejected(_ code: ErrorCode, file: StaticString = #filePath, line: UInt = #line, _ body: () async throws -> Void) async {
        do {
            try await body()
            XCTFail("expected \(code.rawValue)", file: file, line: line)
        } catch ErdigoError.rejected(let error) {
            XCTAssertEqual(error.code, code.rawValue, file: file, line: line)
        } catch {
            XCTFail("expected \(code.rawValue), got \(error)", file: file, line: line)
        }
    }

    // MARK: Everything working

    func testPairThenEveryOperation() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let fingerprint = device.identity.fingerprint
        let receiverFingerprint = await receiver.fingerprint
        XCTAssertEqual(device.pairing.fingerprint, receiverFingerprint)

        try await device.client.ping()
        try await device.client.putPin(id: "pin-1", at: palace, title: "Palace")
        try await device.client.putMarker(id: "marker-1", at: palace, title: "Zürich €", color: .blue)
        try await device.client.putNote(id: "note-1", at: palace, text: "First line\nhttps://example.com/ stays plain text")
        try await device.client.move(id: "pin-1", to: cityHall)
        let points = try (0..<250).map { try TrackPoint(coordinate: palace, timestampMS: Int64($0)) }
        try await device.client.appendTrack(id: "walk", points: points, title: "Walk")
        try await device.client.finishTrack(id: "walk")
        try await device.client.putPhoto(id: "photo-1", at: palace, data: Self.jpeg(), title: "View")

        let items = await receiver.items(ofDevice: fingerprint)
        XCTAssertEqual(items.map(\.id), ["marker-1", "note-1", "photo-1", "pin-1", "walk"])
        let pin = try XCTUnwrap(items.first { $0.id == "pin-1" })
        XCTAssertEqual(pin.latitude, cityHall.latitude)
        XCTAssertEqual(pin.title, "Palace")
        let walk = try XCTUnwrap(items.first { $0.id == "walk" })
        XCTAssertEqual(walk.points.count, 250)
        XCTAssertTrue(walk.isFinished)

        try await device.client.delete(id: "pin-1")
        try await device.client.delete(id: "pin-1")
        let remaining = await receiver.items(ofDevice: fingerprint)
        XCTAssertEqual(remaining.count, 4)
        try await device.client.deleteAll()
        let afterDeleteAll = await receiver.items(ofDevice: fingerprint)
        XCTAssertEqual(afterDeleteAll, [])
        await device.client.close()
    }

    func testPersonMayAllowFewerKindsAndRename() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.setApprovalHandler { request in
            XCTAssertEqual(request.deviceName, "Test device")
            return .allow(kinds: [.pin, .photo], name: "Renamed")
        }
        let device = try await pairedDevice(receiver, port: port, kinds: [.pin, .marker])
        XCTAssertEqual(device.pairing.kinds, [.pin])
        let listed = await receiver.pairedDevices()
        XCTAssertEqual(listed.first?.name, "Renamed")

        try await device.client.putPin(id: "a", at: palace)
        do {
            try await device.client.putMarker(id: "b", at: palace)
            XCTFail("the kind should not be allowed")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .notPermitted(.marker))
        }
    }

    func testReceiverRefusesKindsTheDeviceWasNotAllowed() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port, kinds: [.pin])
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let reply = try await raw.send(#"{"t":"put","seq":1,"kind":"marker","id":"m","lat":1,"lon":1}"#)
        XCTAssertEqual(reply.code, ErrorCode.notPermitted.rawValue)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items, [])
        raw.stream.cancel()
    }

    func testDeviceNameIsTreatedAsUntrusted() async throws {
        let (receiver, port) = try await startReceiver()
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let ticket = try await receiver.openPairingWindow()

        // Skips the SDK, which would have cleaned the name itself.
        let stream = try await rawConnect(port)
        let handshake = try NoiseHandshake(
            pattern: .xxPSK0,
            isInitiator: true,
            prologue: IntegrationV1.preamble(.pair),
            staticKey: Curve25519.KeyAgreement.PrivateKey(rawRepresentation: identity.rawPrivateKeyForStorage()),
            psk: ticket.code.presharedKey()
        )
        try await stream.send(IntegrationV1.preamble(.pair) + (try ByteStream.frame(try handshake.writeMessage())))
        _ = try handshake.readMessage(try await stream.readFrame())
        let hostile = "Bank\u{202E}cod.exe\u{0007}\n" + String(repeating: "x", count: 60)
        let request = PairRequest(versions: [1], sdk: "test/1", name: hostile, kinds: [.pin])
        try await stream.sendFrame(try handshake.writeMessage(payload: Wire.encode(request)))
        _ = await bytesUntilClose(stream)

        let listed = await receiver.pairedDevices()
        let name = try XCTUnwrap(listed.first?.name)
        XCTAssertEqual(name.unicodeScalars.count, 40)
        XCTAssertFalse(name.unicodeScalars.contains { $0.value == 0x202E || $0.value == 0x07 || $0.value == 0x0A })
    }

    // MARK: Silence towards strangers

    func testUnknownDeviceReceivesNothing() async throws {
        let (receiver, port) = try await startReceiver()
        let stream = try await rawConnect(port)
        let (_, message) = try connectMessage(
            deviceKey: Curve25519.KeyAgreement.PrivateKey(),
            receiverKey: await receiver.publicKey,
            counter: 1
        )
        try await stream.send(message)
        let count = await bytesUntilClose(stream)
        XCTAssertEqual(count, 0)
    }

    func testDeviceThatDoesNotKnowTheReceiverKeyReceivesNothing() async throws {
        let (_, port) = try await startReceiver()
        let stream = try await rawConnect(port)
        let (_, message) = try connectMessage(
            deviceKey: Curve25519.KeyAgreement.PrivateKey(),
            receiverKey: Curve25519.KeyAgreement.PrivateKey().publicKey.rawRepresentation,
            counter: 1
        )
        try await stream.send(message)
        let count = await bytesUntilClose(stream)
        XCTAssertEqual(count, 0)
    }

    func testGarbageReceivesNothing() async throws {
        let (_, port) = try await startReceiver()
        for garbage in [
            Data("GET / HTTP/1.1\r\n\r\n".utf8),
            Data(repeating: 0, count: 5),
            IntegrationV1.preamble(.connect) + Data([0xFF, 0xFF]) + Data(repeating: 0xAA, count: 200),
            IntegrationV1.preamble(.connect) + (try ByteStream.frame(Data(repeating: 0, count: 104))),
            IntegrationV1.preamble(.pair) + (try ByteStream.frame(Data(repeating: 0, count: 48))),
        ] {
            let stream = try await rawConnect(port)
            try await stream.send(garbage)
            let count = await bytesUntilClose(stream)
            XCTAssertEqual(count, 0)
        }
    }

    func testLowOrderEphemeralKeyReceivesNothing() async throws {
        let (_, port) = try await startReceiver()
        let stream = try await rawConnect(port)
        let frame = try ByteStream.frame(Data(repeating: 0, count: 32) + Data(repeating: 0x11, count: 72))
        try await stream.send(IntegrationV1.preamble(.connect) + frame)
        let count = await bytesUntilClose(stream)
        XCTAssertEqual(count, 0)
    }

    func testReplayedHandshakeReceivesNothing() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let deviceKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: device.identity.rawPrivateKeyForStorage())
        let (_, captured) = try connectMessage(deviceKey: deviceKey, receiverKey: await receiver.publicKey, counter: 5000)

        let first = try await rawConnect(port)
        try await first.send(captured)
        let answer = try await first.readFrame()
        XCTAssertGreaterThan(answer.count, 0)
        first.cancel()

        let replay = try await rawConnect(port)
        try await replay.send(captured)
        let count = await bytesUntilClose(replay)
        XCTAssertEqual(count, 0, "a replayed message must not reveal that this receiver knows the device")
    }

    func testReplayCannotDisplaceALiveConnection() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        try await device.client.ping()

        let deviceKey = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: device.identity.rawPrivateKeyForStorage())
        let farFuture = UInt64(Date().timeIntervalSince1970 * 1000) + 10_000_000
        let (_, message) = try connectMessage(deviceKey: deviceKey, receiverKey: await receiver.publicKey, counter: farFuture)
        let attacker = try await rawConnect(port)
        try await attacker.send(message)
        _ = try await attacker.readFrame()
        // The attacker cannot produce a valid hello, so the old connection must survive.
        try await attacker.sendFrame(Data(repeating: 0x41, count: 64))
        _ = await bytesUntilClose(attacker)

        try await device.client.putPin(id: "still-here", at: palace)
    }

    func testRetryPolicyNoneReportsNoAnswer() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.stop()
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let pairing = ReceiverPairing(label: "x", receiverKey: await receiver.publicKey, kinds: [.pin])
        let client = ErdigoClient(identity: identity, pairing: pairing, store: store, target: .address(host: "127.0.0.1", port: port), retry: .none, connectTimeout: 0.5)
        do {
            try await client.ping()
            XCTFail("expected no answer")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }
    }

    // MARK: Pairing rules

    func testWrongCodeLeavesTheRightCodeWorking() async throws {
        let (receiver, port) = try await startReceiver()
        let right = try await receiver.openPairingWindow()

        let stranger = try await rawConnect(port)
        let wrong = try PairingCode.generate()
        let handshake = try NoiseHandshake(
            pattern: .xxPSK0, isInitiator: true, prologue: IntegrationV1.preamble(.pair),
            staticKey: Curve25519.KeyAgreement.PrivateKey(), psk: wrong.presharedKey()
        )
        try await stranger.send(IntegrationV1.preamble(.pair) + (try ByteStream.frame(try handshake.writeMessage())))
        let count = await bytesUntilClose(stranger)
        XCTAssertEqual(count, 0, "someone without the code must not learn the receiver key")

        let store = InMemoryDeviceStore()
        let pairing = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port),
            ticket: right,
            identity: try store.loadOrCreateIdentity(),
            deviceName: "Real device",
            kinds: [.pin],
            handshakeTimeout: 1
        )
        XCTAssertEqual(pairing.kinds, [.pin])
    }

    func testClosedWindowIsSilentAndAsksNobody() async throws {
        let (receiver, port) = try await startReceiver()
        let asked = Flag()
        await receiver.setApprovalHandler { _ in
            asked.set()
            return .deny
        }
        let ticket = try await receiver.openPairingWindow()
        await receiver.closePairingWindow()
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: .generate(),
                deviceName: "Late",
                kinds: [.pin],
                handshakeTimeout: 0.5
            )
            XCTFail("expected no answer")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }
        XCTAssertFalse(asked.isSet)
    }

    func testExpiredWindowIsSilent() async throws {
        var options = fastOptions()
        options.pairingWindow = 0.05
        let (receiver, port) = try await startReceiver(options)
        let ticket = try await receiver.openPairingWindow()
        try await Task.sleep(nanoseconds: 100_000_000)
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: .generate(),
                deviceName: "Late",
                kinds: [.pin],
                handshakeTimeout: 0.5
            )
            XCTFail("expected no answer")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }
    }

    func testDeniedRequestStoresNothing() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.setApprovalHandler { _ in .deny }
        let ticket = try await receiver.openPairingWindow()
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: .generate(),
                deviceName: "Unwanted",
                kinds: [.pin],
                handshakeTimeout: 1
            )
            XCTFail("expected a denial")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .pairingDenied)
        }
        let devices = await receiver.pairedDevices()
        XCTAssertTrue(devices.isEmpty)
    }

    func testNobodyAnsweringDenies() async throws {
        var options = fastOptions()
        options.approvalTimeout = 0.1
        let (receiver, port) = try await startReceiver(options)
        await receiver.setApprovalHandler { _ in
            try? await Task.sleep(nanoseconds: 5_000_000_000)
            return .allow(kinds: [.pin], name: nil)
        }
        let ticket = try await receiver.openPairingWindow()
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: .generate(),
                deviceName: "Nobody home",
                kinds: [.pin],
                handshakeTimeout: 1
            )
            XCTFail("expected a denial")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .pairingDenied)
        }
    }

    func testCodeUsedTwiceRaisesAlarmAndRemovesThePairing() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        let thief = DeviceIdentity.generate()
        _ = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port),
            ticket: ticket,
            identity: thief,
            deviceName: "Thief",
            kinds: [.pin],
            handshakeTimeout: 1
        )
        let firstList = await receiver.pairedDevices()
        XCTAssertEqual(firstList.count, 1)

        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: .generate(),
                deviceName: "Owner",
                kinds: [.pin],
                handshakeTimeout: 1
            )
            XCTFail("expected the alarm")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .codeReused)
        }

        let secondList = await receiver.pairedDevices()
        XCTAssertTrue(secondList.isEmpty, "the pairing made with the stolen code must be removed")
        let events = await receiver.events
        XCTAssertTrue(events.contains(.codeReusedAlarm(firstDevice: thief.fingerprint)))
    }

    /// Someone holding the code must not be able to switch off the alarm by trying many times.
    func testManyAttemptsWithTheCodeRaiseTheAlarmRatherThanHideIt() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        let thief = DeviceIdentity.generate()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        _ = try await Pairing.pair(target: target, ticket: ticket, identity: thief, deviceName: "Thief", kinds: [.pin], handshakeTimeout: 1)

        for _ in 0..<70 {
            let handshake = try NoiseHandshake(
                pattern: .xxPSK0,
                isInitiator: true,
                prologue: IntegrationV1.preamble(.pair),
                staticKey: Curve25519.KeyAgreement.PrivateKey(),
                psk: ticket.code.presharedKey()
            )
            let stream = try await rawConnect(port)
            try await stream.send(IntegrationV1.preamble(.pair) + (try ByteStream.frame(try handshake.writeMessage())))
            _ = try? await stream.readFrame()
            stream.cancel()
        }

        do {
            _ = try await Pairing.pair(target: target, ticket: ticket, identity: .generate(), deviceName: "Owner", kinds: [.pin], handshakeTimeout: 1)
            XCTFail("the owner should be told")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .codeReused)
        }
        let events = await receiver.events
        XCTAssertTrue(events.contains(.codeReusedAlarm(firstDevice: thief.fingerprint)))
        let devices = await receiver.pairedDevices()
        XCTAssertTrue(devices.isEmpty, "the pairing made with that code must be gone")
    }

    /// Without the code, recordings must not be able to raise the alarm or remove a real pairing.
    func testReplayingManyRecordingsCannotRemoveAPairing() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        var recordings: [Data] = []
        for _ in 0..<70 {
            let handshake = try NoiseHandshake(
                pattern: .xxPSK0,
                isInitiator: true,
                prologue: IntegrationV1.preamble(.pair),
                staticKey: Curve25519.KeyAgreement.PrivateKey(),
                psk: ticket.code.presharedKey()
            )
            recordings.append(IntegrationV1.preamble(.pair) + (try ByteStream.frame(try handshake.writeMessage())))
        }
        let identity = DeviceIdentity.generate()
        _ = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port),
            ticket: ticket,
            identity: identity,
            deviceName: "Real",
            kinds: [.pin],
            handshakeTimeout: 1
        )
        for recording in recordings {
            let stream = try await rawConnect(port)
            try await stream.send(recording)
            _ = try? await stream.readFrame()
            stream.cancel()
        }
        let devices = await receiver.pairedDevices()
        XCTAssertEqual(devices.map(\.fingerprint), [identity.fingerprint])
        let events = await receiver.events
        XCTAssertFalse(events.contains { if case .codeReusedAlarm = $0 { return true } else { return false } })
    }

    func testAStuckRetryLoopOnOneDeviceIsNotAnAlarm() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        let identity = DeviceIdentity.generate()
        for _ in 0..<70 {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: identity,
                deviceName: "Looping",
                kinds: [.pin],
                handshakeTimeout: 1
            )
        }
        let devices = await receiver.pairedDevices()
        XCTAssertEqual(devices.count, 1)
    }

    func testStallingAfterTheHandshakeReplyIsPenalised() async throws {
        var options = fastOptions()
        options.addressPenaltyBase = 30
        options.handshakeTimeout = 0.3
        options.penalisedHandshakeTimeout = 0.05
        let (receiver, port) = try await startReceiver(options)
        let ticket = try await receiver.openPairingWindow()
        let handshake = try NoiseHandshake(
            pattern: .xxPSK0,
            isInitiator: true,
            prologue: IntegrationV1.preamble(.pair),
            staticKey: Curve25519.KeyAgreement.PrivateKey(),
            psk: ticket.code.presharedKey()
        )
        let stream = try await rawConnect(port)
        try await stream.send(IntegrationV1.preamble(.pair) + (try ByteStream.frame(try handshake.writeMessage())))
        _ = try await stream.readFrame()
        _ = await bytesUntilClose(stream)
        // Now under a penalty: the next quiet connection gets the short deadline.
        let seconds = try await secondsUntilClose(port: port, sending: IntegrationV1.preamble(.connect))
        XCTAssertLessThan(seconds, 0.25)
    }

    func testSameDeviceRetryingPairingIsNotAnAlarm() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        let identity = DeviceIdentity.generate()
        for _ in 0..<2 {
            let pairing = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: identity,
                deviceName: "Retry",
                kinds: [.pin],
                handshakeTimeout: 1
            )
            XCTAssertEqual(pairing.kinds, [.pin])
        }
        let events = await receiver.events
        XCTAssertFalse(events.contains { if case .codeReusedAlarm = $0 { return true } else { return false } })
    }

    func testTwoDevicesRacingWithOneCodeRaiseAlarm() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.setApprovalHandler { request in
            try? await Task.sleep(nanoseconds: 200_000_000)
            return .allow(kinds: request.requestedKinds, name: nil)
        }
        let ticket = try await receiver.openPairingWindow()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)

        async let first: Bool = Self.tryPair(target: target, ticket: ticket)
        async let second: Bool = Self.tryPair(target: target, ticket: ticket)
        _ = await (first, second)

        let list = await receiver.pairedDevices()
        XCTAssertTrue(list.isEmpty, "neither device may stay paired after the code was used twice")
        let events = await receiver.events
        XCTAssertTrue(events.contains { if case .codeReusedAlarm = $0 { return true } else { return false } })
    }

    private static func tryPair(target: ReceiverTarget, ticket: PairingTicket) async -> Bool {
        do {
            _ = try await Pairing.pair(target: target, ticket: ticket, identity: .generate(), deviceName: "Racer", kinds: [.pin], handshakeTimeout: 1)
            return true
        } catch {
            return false
        }
    }

    func testFingerprintMismatchStopsBeforeTheDeviceIdentifiesItself() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        let wrong = PairingTicket(code: ticket.code, fingerprint: String(repeating: "0", count: 64))
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: wrong,
                identity: .generate(),
                deviceName: "Careful",
                kinds: [.pin],
                handshakeTimeout: 1
            )
            XCTFail("expected a mismatch")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .fingerprintMismatch)
        }
        try await assertHeardNoRequest(receiver)
    }

    /// Waits first, because a request sent a moment ago would still be on its way.
    private func assertHeardNoRequest(_ receiver: MockReceiver, file: StaticString = #filePath, line: UInt = #line) async throws {
        try await Task.sleep(nanoseconds: 500_000_000)
        let events = await receiver.events
        XCTAssertFalse(events.contains { if case .pairingRequested = $0 { return true } else { return false } }, file: file, line: line)
        let devices = await receiver.pairedDevices()
        XCTAssertEqual(devices.count, 0, file: file, line: line)
    }

    /// Records what a person was shown, and answers as told.
    private final class KeyQuestion: @unchecked Sendable {
        private let lock = NSLock()
        private var asked: [String] = []
        private let answer: Bool
        private let duringQuestion: (@Sendable () async -> Void)?

        init(answer: Bool, duringQuestion: (@Sendable () async -> Void)? = nil) {
            self.answer = answer
            self.duringQuestion = duringQuestion
        }

        var shown: [String] {
            lock.lock()
            defer { lock.unlock() }
            return asked
        }

        private func record(_ key: String) {
            lock.lock()
            defer { lock.unlock() }
            asked.append(key)
        }

        @Sendable func callAsFunction(_ key: String) async -> Bool {
            record(key)
            await duringQuestion?()
            return answer
        }
    }

    func testATypedCodeAsksBeforeTheReceiverHearsAnything() async throws {
        let (receiver, port) = try await startReceiver()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let code = try await receiver.openPairingWindow().code
        let receiverFingerprint = await receiver.fingerprint
        let shortKey = IntegrationV1.shortFingerprint(receiverFingerprint)

        // Nothing has reached the person at the receiver while the question is on the screen.
        let mock = receiver
        let yes = KeyQuestion(answer: true) {
            try? await Task.sleep(nanoseconds: 500_000_000)
            let events = await mock.events
            XCTAssertFalse(events.contains { if case .pairingRequested = $0 { return true } else { return false } })
            let devices = await mock.pairedDevices()
            XCTAssertEqual(devices.count, 0)
        }
        let identity = DeviceIdentity.generate()
        let pairing = try await Pairing.pair(
            target: target,
            ticket: PairingTicket(code: code),
            identity: identity,
            deviceName: "Careful",
            kinds: [.pin],
            handshakeTimeout: 1,
            confirmKey: yes.callAsFunction
        )

        XCTAssertEqual(yes.shown, [shortKey])
        XCTAssertEqual(pairing.fingerprint, receiverFingerprint)
        // Only the key that really pairs is known to the receiver.
        let devices = await receiver.pairedDevices()
        XCTAssertEqual(devices.map(\.fingerprint), [identity.fingerprint])
    }

    func testSayingNoLeavesNothingBehindAndTheCodeStillWorks() async throws {
        let (receiver, port) = try await startReceiver()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let code = try await receiver.openPairingWindow().code
        let shortKey = IntegrationV1.shortFingerprint(await receiver.fingerprint)

        let no = KeyQuestion(answer: false)
        for _ in 0..<2 {
            do {
                _ = try await Pairing.pair(
                    target: target, ticket: PairingTicket(code: code), identity: .generate(), deviceName: "Careful",
                    kinds: [.pin], handshakeTimeout: 1, confirmKey: no.callAsFunction
                )
                XCTFail("expected a refusal")
            } catch {
                XCTAssertEqual(error as? ErdigoError, .keyNotConfirmed)
            }
        }
        XCTAssertEqual(no.shown, [shortKey, shortKey])
        try await assertHeardNoRequest(receiver)

        // The person looks again and says yes, and the same code works.
        let yes = KeyQuestion(answer: true)
        let pairing = try await Pairing.pair(
            target: target, ticket: PairingTicket(code: code), identity: .generate(), deviceName: "Careful",
            kinds: [.pin], handshakeTimeout: 1, confirmKey: yes.callAsFunction
        )
        XCTAssertEqual(IntegrationV1.shortFingerprint(pairing.fingerprint), shortKey)
    }

    func testSayingNoWhenPairingAgainLeavesTheReceiversRecordAlone() async throws {
        let (receiver, port) = try await startReceiver()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let identity = DeviceIdentity.generate()
        let first = try await receiver.openPairingWindow()
        _ = try await Pairing.pair(
            target: target, ticket: first, identity: identity, deviceName: "Careful", kinds: [.pin, .marker], handshakeTimeout: 1
        )

        let second = try await receiver.openPairingWindow().code
        let no = KeyQuestion(answer: false)
        do {
            _ = try await Pairing.pair(
                target: target, ticket: PairingTicket(code: second), identity: identity, deviceName: "Careful",
                kinds: [.pin], handshakeTimeout: 1, confirmKey: no.callAsFunction
            )
            XCTFail("expected a refusal")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .keyNotConfirmed)
        }
        try await Task.sleep(nanoseconds: 500_000_000)
        let devices = await receiver.pairedDevices()
        XCTAssertEqual(devices.map(\.kinds), [[.pin, .marker]])
    }

    func testATypedCodeCannotPairWithoutTheQuestion() async throws {
        let (receiver, port) = try await startReceiver()
        let code = try await receiver.openPairingWindow().code
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port), ticket: PairingTicket(code: code), identity: .generate(),
                deviceName: "Careless", kinds: [.pin], handshakeTimeout: 1
            )
            XCTFail("expected a refusal")
        } catch {
            guard case .invalidArgument = error as? ErdigoError else { return XCTFail("unexpected \(error)") }
        }
        try await assertHeardNoRequest(receiver)
    }

    func testATicketWithAFingerprintAsksNothing() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        let question = KeyQuestion(answer: false)
        let pairing = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port), ticket: ticket, identity: .generate(), deviceName: "Scanner",
            kinds: [.pin], handshakeTimeout: 1, confirmKey: question.callAsFunction
        )
        XCTAssertEqual(question.shown, [])
        XCTAssertEqual(pairing.fingerprint, ticket.fingerprint)
    }

    /// Someone else who knows the code takes the receiver's place between the question and the pairing.
    func testAKeyOtherThanTheOneConfirmedDoesNotPair() async throws {
        let code = try PairingCode(parsing: "TEST0-0NLYC-0DE00-00000")
        let (real, port) = try await startReceiver()
        _ = await real.openPairingWindow(code: code)
        var impostorOptions = fastOptions()
        impostorOptions.port = port
        let impostor = try MockReceiver(options: impostorOptions)
        receivers.append(impostor)
        _ = await impostor.openPairingWindow(code: code)

        let swap = KeyQuestion(answer: true) {
            await real.stop()
            // The port takes a moment to come free.
            for _ in 0..<100 {
                if (try? await impostor.start()) != nil { return }
                try? await Task.sleep(nanoseconds: 50_000_000)
            }
            XCTFail("the second receiver could not take over the port")
        }
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port), ticket: PairingTicket(code: code), identity: .generate(),
                deviceName: "Careful", kinds: [.pin], handshakeTimeout: 1, confirmKey: swap.callAsFunction
            )
            XCTFail("expected a mismatch")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .fingerprintMismatch)
        }
        XCTAssertEqual(swap.shown.count, 1)
        try await assertHeardNoRequest(impostor)
    }

    func testNobodyAnswersForTheWrongCodeOrWithNoWindowAndNothingIsAsked() async throws {
        let (receiver, port) = try await startReceiver()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let wrong = try PairingCode(parsing: "TEST0-0NLYC-0DE00-00000")
        let question = KeyQuestion(answer: true)

        // No window is open at first, and then one with another code.
        for _ in 0..<2 {
            do {
                _ = try await Pairing.pair(
                    target: target, ticket: PairingTicket(code: wrong), identity: .generate(), deviceName: "Careful",
                    kinds: [.pin], handshakeTimeout: 1, confirmKey: question.callAsFunction
                )
                XCTFail("expected no answer")
            } catch {
                XCTAssertEqual(error as? ErdigoError, .noAnswer)
            }
            _ = try await receiver.openPairingWindow()
        }
        XCTAssertEqual(question.shown, [])
        try await assertHeardNoRequest(receiver)
    }

    func testQRTextFromTheReceiverPairsAndAChangedOneIsRefused() async throws {
        let (receiver, port) = try await startReceiver()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)

        let first = try await receiver.openPairingWindow()
        let scanned = try PairingTicket(parsing: try XCTUnwrap(first.uri))
        XCTAssertEqual(scanned, first)
        let pairing = try await Pairing.pair(
            target: target, ticket: scanned, identity: .generate(), deviceName: "Scanner", kinds: [.pin], handshakeTimeout: 1
        )
        let receiverFingerprint = await receiver.fingerprint
        XCTAssertEqual(pairing.fingerprint, receiverFingerprint)

        let second = try await receiver.openPairingWindow()
        let changed = try PairingTicket(parsing: "erdigo-pair:1?code=\(second.code.display)&fp=\(String(repeating: "0", count: 64))")
        do {
            _ = try await Pairing.pair(
                target: target, ticket: changed, identity: .generate(), deviceName: "Scanner", kinds: [.pin], handshakeTimeout: 1
            )
            XCTFail("expected a mismatch")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .fingerprintMismatch)
        }
        // A request sent a moment ago would still be on its way.
        try await Task.sleep(nanoseconds: 500_000_000)
        let devices = await receiver.pairedDevices()
        XCTAssertEqual(devices.count, 1, "only the correct QR text may lead to a pairing")
    }

    // MARK: Removing a device

    func testRemovedDeviceIsCutOffWithinOneSecondAndStaysOut() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)

        let started = Date()
        await receiver.revoke(deviceFingerprint: device.identity.fingerprint)
        let count = await bytesUntilClose(raw.stream, timeout: 3)
        XCTAssertLessThan(Date().timeIntervalSince(started), 1)
        XCTAssertEqual(count, 0)

        do {
            try await device.client.ping()
            XCTFail("a removed device must not get back in")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }
    }

    func testRemovedDeviceCanReturnOnlyWithANewCodeAndConfirmation() async throws {
        let (receiver, port) = try await startReceiver()
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let firstTicket = try await receiver.openPairingWindow()
        let pairing = try await Pairing.pair(target: target, ticket: firstTicket, identity: identity, deviceName: "D", kinds: [.pin], handshakeTimeout: 1)
        try store.save(pairing)

        let client = ErdigoClient(identity: identity, pairing: pairing, store: store, target: target, retry: .none, connectTimeout: 1)
        try await client.putPin(id: "kept", at: palace)
        await client.close()
        await receiver.revoke(deviceFingerprint: identity.fingerprint)
        let retained = await receiver.items(ofDevice: identity.fingerprint)
        XCTAssertEqual(retained.map(\.id), ["kept"], "items stay unless the person asked to delete them")

        let sawRemoved = Flag()
        await receiver.setApprovalHandler { request in
            if request.wasRemovedBefore { sawRemoved.set() }
            return .allow(kinds: request.requestedKinds, name: nil)
        }
        let secondTicket = try await receiver.openPairingWindow()
        _ = try await Pairing.pair(target: target, ticket: secondTicket, identity: identity, deviceName: "D", kinds: [.pin], handshakeTimeout: 1)
        XCTAssertTrue(sawRemoved.isSet)

        let again = ErdigoClient(identity: identity, pairing: pairing, store: store, target: target, retry: .none, connectTimeout: 1)
        try await again.ping()
        let restored = await receiver.items(ofDevice: identity.fingerprint)
        XCTAssertEqual(restored.map(\.id), ["kept"])
    }

    func testPauseClosesEverythingAndResumeWorks() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        try await device.client.ping()
        await receiver.setPaused(true)
        do {
            try await device.client.ping()
            XCTFail("expected no answer while paused")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }
        await receiver.setPaused(false)
        try await device.client.ping()
    }

    // MARK: Isolation between devices

    func testDeviceCannotTouchAnotherDevicesItems() async throws {
        let (receiver, port) = try await startReceiver()
        let alice = try await pairedDevice(receiver, port: port)
        let bob = try await pairedDevice(receiver, port: port)

        try await alice.client.putPin(id: "shared-name", at: palace, title: "Alice")
        try await bob.client.putPin(id: "shared-name", at: cityHall, title: "Bob")
        await assertRejected(.notFound) { try await bob.client.move(id: "only-alice", to: self.cityHall) }

        try await bob.client.delete(id: "shared-name")
        try await bob.client.deleteAll()

        let aliceItems = await receiver.items(ofDevice: alice.identity.fingerprint)
        XCTAssertEqual(aliceItems.first?.title, "Alice")
        XCTAssertEqual(aliceItems.first?.latitude, palace.latitude)
        let bobItems = await receiver.items(ofDevice: bob.identity.fingerprint)
        XCTAssertEqual(bobItems, [])
    }

    // MARK: Sequence numbers

    func testLostAcknowledgementIsSafeToResend() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let request = #"{"t":"track_append","seq":1,"id":"walk","points":[{"lat":1,"lon":1}]}"#

        let first = try await raw.send(request)
        XCTAssertEqual(first.ack, 1)
        XCTAssertEqual(first.duplicate, false)

        let second = try await raw.send(request)
        XCTAssertEqual(second.ack, 1)
        XCTAssertEqual(second.duplicate, true)

        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.first?.points.count, 1, "a resend must not apply twice")
    }

    func testSameNumberWithDifferentContentIsRejected() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        _ = try await raw.send(#"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1}"#)
        let reply = try await raw.send(#"{"t":"put","seq":1,"kind":"pin","id":"a","lat":2,"lon":2}"#)
        XCTAssertEqual(reply.code, ErrorCode.seqConflict.rawValue)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.first?.latitude, 1)
    }

    func testOldNumberIsRejectedAndReportsTheLatest() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        _ = try await raw.send(#"{"t":"ping","seq":10}"#)
        let reply = try await raw.send(#"{"t":"ping","seq":9}"#)
        XCTAssertEqual(reply.code, ErrorCode.staleSeq.rawValue)
        XCTAssertEqual(reply.lastSeq, 10)
    }

    func testRejectedRequestKeepsItsAnswerOnResend() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let request = #"{"t":"move","seq":1,"id":"missing","lat":1,"lon":1}"#
        let first = try await raw.send(request)
        let second = try await raw.send(request)
        XCTAssertEqual(first.code, ErrorCode.notFound.rawValue)
        XCTAssertEqual(second.code, ErrorCode.notFound.rawValue)
    }

    func testNewClientWithNoMemoryContinuesNumbering() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        try await device.client.ping()
        try await device.client.ping()
        await device.client.close()

        let forgetful = ErdigoClient(
            identity: device.identity,
            pairing: device.pairing,
            store: device.store,
            target: .address(host: "127.0.0.1", port: port),
            retry: .none,
            connectTimeout: 1
        )
        try await forgetful.ping()
        let last = await receiver.lastSeq(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(last, 3)
    }

    func testClientRecoversWhenTheConnectionDropsMidway() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port, retry: .upTo(10))
        try await device.client.ping()

        await receiver.setPaused(true)
        let resume = Task {
            try await Task.sleep(nanoseconds: 300_000_000)
            await receiver.setPaused(false)
        }
        try await device.client.putPin(id: "after-sleep", at: palace)
        try await resume.value
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.map(\.id), ["after-sleep"])
    }

    func testPhotoRestartsAfterTheConnectionDrops() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port, retry: .upTo(10))
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let photo = Self.jpeg()
        let digest = Data(SHA256.hash(data: photo)).hexString
        let begin = #"{"t":"photo_begin","seq":1,"id":"p","lat":1,"lon":1,"type":"image/jpeg","size":\#(photo.count),"sha256":"\#(digest)","chunks":\#((photo.count + 47_999) / 48_000)}"#
        _ = try await raw.send(begin)
        raw.stream.cancel()
        try await Task.sleep(nanoseconds: 50_000_000)

        try await device.client.putPhoto(id: "p", at: palace, data: photo)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.first?.kind, .photo)
    }

    // MARK: Connection rules

    func testNewConnectionReplacesTheOldOne() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let old = try await authenticated(device, receiver: receiver, port: port)
        try await device.client.ping()

        let transport = old.transport
        let frame = try await old.stream.readFrame()
        let message = try Wire.decodeReceiverMessage(try transport.open(frame))
        guard case .error(let error) = message else { return XCTFail("expected an error") }
        XCTAssertEqual(error.code, ErrorCode.replaced.rawValue)
        XCTAssertTrue(error.fatal)
    }

    func testUnsupportedVersionIsReported() async throws {
        var options = fastOptions()
        options.acceptedVersions = [2]
        let (receiver, port) = try await startReceiver(options)
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()

        // Pairing would fail for the same reason, so the device is approved by a second receiver sharing its key.
        var sameKey = fastOptions()
        let receiverPrivate = Curve25519.KeyAgreement.PrivateKey()
        sameKey.receiverKey = receiverPrivate.rawRepresentation
        sameKey.port = 0
        _ = receiver

        var options2 = sameKey
        options2.acceptedVersions = [1, 2]
        let (flexible, flexiblePort) = try await startReceiver(options2)
        let ticket = try await flexible.openPairingWindow()
        let pairing = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: flexiblePort),
            ticket: ticket,
            identity: identity,
            deviceName: "Old SDK",
            kinds: [.pin],
            handshakeTimeout: 1
        )

        // An SDK that only speaks a version the receiver has dropped gets a clear answer.
        let raw = try await authenticated(identity: identity, receiverKey: pairing.receiverKey, port: flexiblePort, versions: [99], expectWelcome: false)
        let frame = try await raw.stream.readFrame()
        let transport = raw.transport
        guard case .error(let error) = try Wire.decodeReceiverMessage(try transport.open(frame)) else {
            return XCTFail("expected an error")
        }
        XCTAssertEqual(error.code, ErrorCode.versionUnsupported.rawValue)
        XCTAssertEqual(error.supported, [1, 2])
    }

    /// How long the peer keeps a connection open if this side sends `bytes` and then nothing more.
    private func secondsUntilClose(port: UInt16, sending bytes: Data, timeout: TimeInterval = 5) async throws -> TimeInterval {
        let stream = try await rawConnect(port)
        if !bytes.isEmpty { try await stream.send(bytes) }
        let started = Date()
        let count = await bytesUntilClose(stream, timeout: timeout)
        XCTAssertEqual(count, 0)
        return Date().timeIntervalSince(started)
    }

    func testLargestMessageWorksOverTCPAndALargerOneCannotBeSent() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)

        // White space after the object is valid JSON, so this is a ping of exactly the largest size.
        let ping = #"{"t":"ping","seq":1}"#
        let largest = ping + String(repeating: " ", count: IntegrationV1.maxPlaintextLength - ping.utf8.count)
        XCTAssertEqual(largest.utf8.count, IntegrationV1.maxPlaintextLength)
        let answer = try await raw.send(largest)
        XCTAssertEqual(answer.ack, 1)

        // One byte more does not fit in a frame.
        XCTAssertThrowsError(try raw.transport.seal(Data(repeating: 0x20, count: IntegrationV1.maxPlaintextLength + 1))) {
            XCTAssertEqual($0 as? NoiseError, .messageTooLarge)
        }
    }

    func testPeerThatStopsReadingIsDisconnected() async throws {
        var options = fastOptions()
        options.sendTimeout = 0.5
        // Only the peer's silence may end this connection.
        options.limits.messagesPerSecond = 10_000_000
        options.limits.messageBurst = 10_000_000
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let fingerprint = device.identity.fingerprint

        let key = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: device.identity.rawPrivateKeyForStorage())
        rawCounter += 1
        let (handshake, first) = try connectMessage(deviceKey: key, receiverKey: await receiver.publicKey, counter: rawCounter)
        let peer = try PlainSocketPeer(port: port)
        defer { peer.shut() }
        try peer.writeAll(first)
        _ = try handshake.readMessage(try peer.readFrame())
        let transport = try handshake.split()
        try peer.writeAll(try ByteStream.frame(try transport.seal(Wire.encode(Hello(versions: [1], sdk: "test/1")))))
        _ = try peer.readFrame()

        // Requests from here on and no more reading, so the answers soon have nowhere to go.
        peer.stopWaitingOnWrites()
        var unsent = Data()
        var seq: UInt64 = 1
        var wasDisconnected = false
        let giveUp = Date().addingTimeInterval(20)
        while !wasDisconnected, Date() < giveUp {
            while unsent.count < 16_384 {
                unsent.append(try ByteStream.frame(try transport.seal(Data(#"{"t":"ping","seq":\#(seq)}"#.utf8))))
                seq += 1
            }
            if let taken = peer.writeSome(unsent), taken > 0 {
                unsent.removeFirst(taken)
                continue
            }
            try await Task.sleep(nanoseconds: 20_000_000)
            let events = await receiver.events
            wasDisconnected = events.contains(.disconnected(device: fingerprint))
        }
        XCTAssertTrue(wasDisconnected, "the receiver must not wait for ever on a peer that has stopped reading")
    }

    func testPreAuthLimitPerAddress() async throws {
        var options = fastOptions()
        options.maxPreAuthConnections = 8
        options.maxPreAuthConnectionsPerAddress = 2
        options.handshakeTimeout = 3
        let (_, port) = try await startReceiver(options)

        var held: [ByteStream] = []
        for _ in 0..<2 { held.append(try await rawConnect(port)) }
        try await Task.sleep(nanoseconds: 100_000_000)

        // The first two are being waited for. The third is closed at once.
        let seconds = try await secondsUntilClose(port: port, sending: Data())
        XCTAssertLessThan(seconds, 0.5)
        for stream in held { stream.cancel() }
    }

    func testPreAuthLimitInTotal() async throws {
        var options = fastOptions()
        options.maxPreAuthConnections = 1
        options.maxPreAuthConnectionsPerAddress = 8
        options.handshakeTimeout = 3
        let (_, port) = try await startReceiver(options)

        let held = try await rawConnect(port)
        try await Task.sleep(nanoseconds: 100_000_000)
        let seconds = try await secondsUntilClose(port: port, sending: Data())
        XCTAssertLessThan(seconds, 0.5)
        held.cancel()
    }

    func testPendingApprovalDoesNotHoldASlot() async throws {
        var options = fastOptions()
        options.maxPreAuthConnections = 1
        options.maxPreAuthConnectionsPerAddress = 1
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)

        let release = Flag()
        await receiver.setApprovalHandler { _ in
            while !release.isSet { try? await Task.sleep(nanoseconds: 20_000_000) }
            return .deny
        }
        let ticket = try await receiver.openPairingWindow()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let waiting = Task { await Self.tryPair(target: target, ticket: ticket) }
        try await Task.sleep(nanoseconds: 200_000_000)

        // Someone waiting for a person must not keep a paired device out.
        try await device.client.ping()
        release.set()
        _ = await waiting.value
    }

    /// The receiver's state must not show in how long a connection stays open.
    func testCloseTimingDoesNotRevealTheReceiversState() async throws {
        var options = fastOptions()
        options.handshakeTimeout = 0.4
        let (receiver, port) = try await startReceiver(options)
        let pair = IntegrationV1.preamble(.pair)
        let connect = IntegrationV1.preamble(.connect)
        let oversized = Data([0xFF, 0xFF])

        func measure(_ bytes: Data) async throws -> TimeInterval {
            try await secondsUntilClose(port: port, sending: bytes)
        }

        // A preamble and then nothing: the deadline in every state.
        let quietNoWindow = try await measure(pair)
        _ = try await receiver.openPairingWindow()
        let quietWindow = try await measure(pair)
        await receiver.closePairingWindow()
        let quietRunning = try await measure(connect)
        await receiver.setPaused(true)
        let quietPaused = try await measure(connect)
        await receiver.setPaused(false)
        for seconds in [quietNoWindow, quietWindow, quietRunning, quietPaused] {
            XCTAssertEqual(seconds, 0.4, accuracy: 0.25)
        }

        // A frame too large to read: closed quickly in every state.
        let largeNoWindow = try await measure(pair + oversized)
        _ = try await receiver.openPairingWindow()
        let largeWindow = try await measure(pair + oversized)
        await receiver.closePairingWindow()
        let largeRunning = try await measure(connect + oversized)
        await receiver.setPaused(true)
        let largePaused = try await measure(connect + oversized)
        await receiver.setPaused(false)
        for seconds in [largeNoWindow, largeWindow, largeRunning, largePaused] {
            XCTAssertLessThan(seconds, 0.2)
        }
    }

    func testEveryFailedAttemptIsPenalised() async throws {
        var options = fastOptions()
        options.addressPenaltyBase = 30
        options.handshakeTimeout = 2
        options.penalisedHandshakeTimeout = 0.2
        for bytes in [
            Data("garbage".utf8),
            IntegrationV1.preamble(.connect) + Data([0xFF, 0xFF]),
            IntegrationV1.preamble(.pair) + (try ByteStream.frame(Data(repeating: 1, count: 48))),
        ] {
            let (receiver, port) = try await startReceiver(options)
            let first = try await rawConnect(port)
            try await first.send(bytes)
            _ = await bytesUntilClose(first)
            let events = await receiver.events
            XCTAssertTrue(events.contains { if case .silentlyDropped = $0 { return true } else { return false } })

            // The next attempt from this address gets much less time.
            let seconds = try await secondsUntilClose(port: port, sending: Data())
            XCTAssertLessThan(seconds, 1)
        }
    }

    func testTimingOutIsPenalised() async throws {
        var options = fastOptions()
        options.addressPenaltyBase = 30
        options.handshakeTimeout = 0.3
        options.penalisedHandshakeTimeout = 0.05
        let (receiver, port) = try await startReceiver(options)
        let first = try await secondsUntilClose(port: port, sending: IntegrationV1.preamble(.connect))
        XCTAssertGreaterThan(first, 0.25)
        let second = try await secondsUntilClose(port: port, sending: IntegrationV1.preamble(.connect))
        XCTAssertLessThan(second, 0.25)
        _ = receiver
    }

    func testAPenaltyDoesNotKeepOutAGoodDeviceAtTheSameAddress() async throws {
        var options = fastOptions()
        options.addressPenaltyBase = 30
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)

        // Another program at this address keeps failing.
        for _ in 0..<3 {
            let failing = try await rawConnect(port)
            try await failing.send(Data("garbage".utf8))
            _ = await bytesUntilClose(failing)
        }
        try await device.client.ping()
    }

    func testConnectingAndLeavingPromptlyCostsNothing() async throws {
        var options = fastOptions()
        options.addressPenaltyBase = 30
        options.handshakeTimeout = 1
        options.penalisedHandshakeTimeout = 0.05
        let (_, port) = try await startReceiver(options)
        let first = try await rawConnect(port)
        first.cancel()
        try await Task.sleep(nanoseconds: 100_000_000)
        let seconds = try await secondsUntilClose(port: port, sending: Data())
        XCTAssertGreaterThan(seconds, 0.5, "the ordinary deadline applies, not the penalised one")
    }

    func testLeavingAfterSendingPartOfAPreambleIsPenalised() async throws {
        var options = fastOptions()
        options.addressPenaltyBase = 30
        options.handshakeTimeout = 1
        options.penalisedHandshakeTimeout = 0.05
        let (_, port) = try await startReceiver(options)
        let first = try await rawConnect(port)
        try await first.send(Data("EI".utf8))
        try await Task.sleep(nanoseconds: 50_000_000)
        first.cancel()
        try await Task.sleep(nanoseconds: 100_000_000)
        let seconds = try await secondsUntilClose(port: port, sending: Data())
        XCTAssertLessThan(seconds, 0.5)
    }

    func testIdleConnectionIsClosed() async throws {
        var options = fastOptions()
        options.idleTimeout = 0.2
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let started = Date()
        _ = await bytesUntilClose(raw.stream, timeout: 3)
        XCTAssertLessThan(Date().timeIntervalSince(started), 1)
    }

    func testSlowHandshakeIsDroppedAtTheDeadline() async throws {
        var options = fastOptions()
        options.handshakeTimeout = 0.2
        let (_, port) = try await startReceiver(options)
        let stream = try await rawConnect(port)
        let started = Date()
        let count = await bytesUntilClose(stream, timeout: 3)
        XCTAssertEqual(count, 0)
        XCTAssertLessThan(Date().timeIntervalSince(started), 2)
    }

    func testOversizedFrameBeforeAuthenticationIsRefused() async throws {
        let (_, port) = try await startReceiver()
        let stream = try await rawConnect(port)
        var bytes = IntegrationV1.preamble(.connect)
        bytes.append(contentsOf: [0xFF, 0xFF])
        try await stream.send(bytes)
        let count = await bytesUntilClose(stream)
        XCTAssertEqual(count, 0)
    }

    // MARK: Limits

    func testItemLimit() async throws {
        var options = fastOptions()
        options.limits.items = 3
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        for index in 0..<3 {
            try await device.client.putPin(id: "pin-\(index)", at: palace)
        }
        await assertRejected(.limitItems) { try await device.client.putPin(id: "one-too-many", at: self.palace) }
        // Replacing an existing item is still fine.
        try await device.client.putPin(id: "pin-0", at: cityHall)
    }

    func testPhotoLimit() async throws {
        var options = fastOptions()
        options.limits.photos = 1
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        try await device.client.putPhoto(id: "p1", at: palace, data: Self.jpeg())
        await assertRejected(.limitPhotos) { try await device.client.putPhoto(id: "p2", at: self.palace, data: Self.jpeg()) }
    }

    func testOpenTrackLimit() async throws {
        var options = fastOptions()
        options.limits.openTracks = 1
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let point = try TrackPoint(coordinate: palace)
        try await device.client.appendTrack(id: "one", points: [point])
        await assertRejected(.limitTracks) { try await device.client.appendTrack(id: "two", points: [point]) }
        try await device.client.finishTrack(id: "one")
        try await device.client.appendTrack(id: "two", points: [point])
    }

    func testFinishedTrackCannotGrow() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let point = try TrackPoint(coordinate: palace)
        try await device.client.appendTrack(id: "walk", points: [point])
        try await device.client.finishTrack(id: "walk")
        await assertRejected(.invalidState) { try await device.client.appendTrack(id: "walk", points: [point]) }
    }

    func testTrackDropsOldestPointsWhenFull() async throws {
        var options = fastOptions()
        options.limits.trackPoints = 10
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let points = try (0..<25).map { try TrackPoint(coordinate: palace, timestampMS: Int64($0)) }
        try await device.client.appendTrack(id: "walk", points: points)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        let stored = try XCTUnwrap(items.first).points
        XCTAssertEqual(stored.count, 10)
        XCTAssertEqual(stored.first?.timestampMS, 15)
        XCTAssertEqual(stored.last?.timestampMS, 24)
    }

    func testDiskLimit() async throws {
        var options = fastOptions()
        options.limits.diskBytes = 1500
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let text = String(repeating: "x", count: 900)
        try await device.client.putNote(id: "a", at: palace, text: text)
        await assertRejected(.limitDisk) { try await device.client.putNote(id: "b", at: self.palace, text: text) }
        try await device.client.delete(id: "a")
        try await device.client.putNote(id: "b", at: palace, text: text)
    }

    func testKindConflict() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        try await device.client.putPin(id: "x", at: palace)
        await assertRejected(.kindConflict) { try await device.client.putMarker(id: "x", at: self.palace) }
    }

    func testMessageRateLimitDoesNotUseUpTheNumber() async throws {
        var options = fastOptions()
        options.limits.messagesPerSecond = 1
        options.limits.messageBurst = 1
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let accepted = try await raw.send(#"{"t":"ping","seq":1}"#)
        XCTAssertEqual(accepted.ack, 1)
        let limited = try await raw.send(#"{"t":"ping","seq":2}"#)
        XCTAssertEqual(limited.code, ErrorCode.rateLimited.rawValue)
        XCTAssertGreaterThan(limited.retryAfterMS ?? 0, 0)
        try await Task.sleep(nanoseconds: 1_100_000_000)
        let retried = try await raw.send(#"{"t":"ping","seq":2}"#)
        XCTAssertEqual(retried.ack, 2)
        XCTAssertEqual(retried.duplicate, false)
    }

    func testSDKPacesItselfBelowTheRateLimit() async throws {
        var options = fastOptions()
        options.limits.messagesPerSecond = 50
        options.limits.messageBurst = 2
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let started = Date()
        for _ in 0..<12 {
            try await device.client.ping()
        }
        XCTAssertGreaterThan(Date().timeIntervalSince(started), 0.15)
        let last = await receiver.lastSeq(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(last, 12)
    }

    func testRepeatedRejectionsDisconnectAndPenalise() async throws {
        var options = fastOptions()
        options.maxRejectionsPerMinute = 3
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        var last: RawReply?
        for _ in 0..<4 {
            last = try await raw.send(#"{"t":"nonsense","seq":1}"#)
        }
        XCTAssertEqual(last?.code, ErrorCode.tooManyErrors.rawValue)
        XCTAssertEqual(last?.fatal, true)
        do {
            try await device.client.ping()
            XCTFail("the device should be ignored for now")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }
    }

    func testMalformedMessagesChangeNothing() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let file = try VectorFiles.load("spec/vectors/integration-v1/messages.json")
        for entry in try XCTUnwrap(file["invalid"] as? [[String: String]]) {
            let reply = try await raw.send(try XCTUnwrap(entry["json"]))
            XCTAssertEqual(reply.code, entry["code"], entry["name"] ?? "")
        }
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items, [])
        let last = await receiver.lastSeq(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(last, 0, "a message that does not parse must not use up a sequence number")
    }

    // MARK: Photos

    func testExifLocationIsRemoved() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let original = Self.jpeg(withGPS: true)
        XCTAssertNotNil(Self.gpsProperties(of: original), "the test image must start with a location")

        try await device.client.putPhoto(id: "p", at: palace, data: original)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        let stored = try XCTUnwrap(items.first?.photo)
        XCTAssertNil(Self.gpsProperties(of: stored))
        XCTAssertNotEqual(stored, original)
    }

    func testPNGIsAccepted() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        try await device.client.putPhoto(id: "p", at: palace, data: Self.encoded(type: "public.png"))
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        let stored = try XCTUnwrap(items.first?.photo)
        XCTAssertEqual(PhotoType.detect(stored), .png)
    }

    func testLargeImageIsReducedTo4096Pixels() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let large = Self.encoded(type: "public.png", width: 5000, height: 10)
        try await device.client.putPhoto(id: "p", at: palace, data: large)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        let stored = try XCTUnwrap(items.first?.photo)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(stored as CFData, nil))
        let properties = try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any])
        XCTAssertLessThanOrEqual(properties[kCGImagePropertyPixelWidth] as? Int ?? .max, 4096)
    }

    /// Sends a whole upload without the SDK's own checks, and returns the answer to `photo_end`.
    private func rawUpload(_ raw: RawSession, data: Data, type: String, size: Int? = nil, hash: String? = nil, startSeq: Int = 1) async throws -> RawReply {
        let digest = hash ?? Data(SHA256.hash(data: data)).hexString
        let chunks = max(1, (data.count + 47_999) / 48_000)
        let begin = #"{"t":"photo_begin","seq":\#(startSeq),"id":"p","lat":1,"lon":1,"type":"\#(type)","size":\#(size ?? data.count),"sha256":"\#(digest)","chunks":\#(chunks)}"#
        let first = try await raw.send(begin)
        guard first.ack != nil else { return first }
        var seq = startSeq + 1
        for index in 0..<chunks {
            let slice = data.subdata(in: (index * 48_000)..<min((index + 1) * 48_000, data.count))
            _ = try await raw.send(#"{"t":"photo_chunk","seq":\#(seq),"id":"p","index":\#(index),"data":"\#(slice.base64EncodedString())"}"#)
            seq += 1
        }
        return try await raw.send(#"{"t":"photo_end","seq":\#(seq),"id":"p"}"#)
    }

    func testForbiddenPhotoFormatsAreRefused() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        var seq = 1

        // A PDF claiming to be a JPEG.
        let pdf = Data("%PDF-1.7 not an image".utf8)
        var reply = try await rawUpload(raw, data: pdf, type: "image/jpeg", startSeq: seq)
        XCTAssertEqual(reply.code, ErrorCode.photoInvalid.rawValue, "PDF")
        seq += 3

        // A real PNG claiming to be a JPEG.
        reply = try await rawUpload(raw, data: Self.encoded(type: "public.png"), type: "image/jpeg", startSeq: seq)
        XCTAssertEqual(reply.code, ErrorCode.photoInvalid.rawValue, "PNG as JPEG")
        seq += 3

        // A TIFF with its first bytes changed to look like a JPEG.
        var tiff = Self.encoded(type: "public.tiff")
        tiff.replaceSubrange(0..<3, with: [0xFF, 0xD8, 0xFF])
        reply = try await rawUpload(raw, data: tiff, type: "image/jpeg", startSeq: seq)
        XCTAssertEqual(reply.code, ErrorCode.photoInvalid.rawValue, "disguised TIFF")
        seq += 3

        // A forbidden format stated openly.
        reply = try await raw.send(#"{"t":"photo_begin","seq":\#(seq),"id":"p","lat":1,"lon":1,"type":"image/heic","size":10,"sha256":"\#(String(repeating: "0", count: 64))","chunks":1}"#)
        XCTAssertEqual(reply.code, ErrorCode.photoInvalid.rawValue, "HEIC")

        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items, [])
    }

    func testOversizedAndMismatchedPhotosAreRefused() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let tooBig = #"{"t":"photo_begin","seq":1,"id":"p","lat":1,"lon":1,"type":"image/jpeg","size":1153434,"sha256":"\#(String(repeating: "0", count: 64))","chunks":25}"#
        let first = try await raw.send(tooBig)
        XCTAssertEqual(first.code, ErrorCode.tooLarge.rawValue)

        let photo = Self.jpeg()
        let wrongHash = try await rawUpload(raw, data: photo, type: "image/jpeg", hash: String(repeating: "a", count: 64), startSeq: 2)
        XCTAssertEqual(wrongHash.code, ErrorCode.photoInvalid.rawValue)

        // Chunks out of order abandon the upload.
        _ = try await raw.send(#"{"t":"photo_begin","seq":10,"id":"q","lat":1,"lon":1,"type":"image/jpeg","size":100,"sha256":"\#(String(repeating: "0", count: 64))","chunks":1}"#)
        let outOfOrder = try await raw.send(#"{"t":"photo_chunk","seq":11,"id":"q","index":5,"data":"AAEC"}"#)
        XCTAssertEqual(outOfOrder.code, ErrorCode.invalidState.rawValue)

        // More data than declared.
        _ = try await raw.send(#"{"t":"photo_begin","seq":12,"id":"r","lat":1,"lon":1,"type":"image/jpeg","size":2,"sha256":"\#(String(repeating: "0", count: 64))","chunks":1}"#)
        let tooMuch = try await raw.send(#"{"t":"photo_chunk","seq":13,"id":"r","index":0,"data":"AAEC"}"#)
        XCTAssertEqual(tooMuch.code, ErrorCode.photoInvalid.rawValue)
    }

    func testSDKRefusesToSendANonImage() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        do {
            try await device.client.putPhoto(id: "p", at: palace, data: Data("%PDF-1.7".utf8))
            XCTFail("expected a refusal")
        } catch {
            guard case ErdigoError.invalidArgument = error else { return XCTFail("\(error)") }
        }
    }

    func testPhotoWithTooManyPixelsIsRefusedBeforeDecoding() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        // 81 million pixels of one shade: a small file that would take far more memory to expand.
        let context = CGContext(
            data: nil,
            width: 9000,
            height: 9000,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceGray(),
            bitmapInfo: CGImageAlphaInfo.none.rawValue
        )!
        let output = NSMutableData()
        let destination = CGImageDestinationCreateWithData(output, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        CGImageDestinationFinalize(destination)
        let bomb = output as Data
        XCTAssertLessThan(bomb.count, Limits.v1.photoBytes)
        await assertRejected(.photoInvalid) { try await device.client.putPhoto(id: "p", at: self.palace, data: bomb) }
    }

    func testJPEGWithTooManyScansIsRefused() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        var crafted = Self.jpeg()
        XCTAssertEqual(crafted.suffix(2), Data([0xFF, 0xD9]))
        crafted.removeLast(2)
        for _ in 0..<40 {
            crafted.append(contentsOf: [0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00])
        }
        crafted.append(contentsOf: [0xFF, 0xD9])
        await assertRejected(.photoInvalid) { try await device.client.putPhoto(id: "p", at: self.palace, data: crafted) }
    }

    /// A second start-of-image marker has no length, so a careless count would jump over what follows.
    func testScansHiddenBehindASecondStartOfImageAreRefused() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        var crafted = Self.jpeg()
        crafted.removeLast(2)
        crafted.append(contentsOf: [0xFF, 0xD8])
        for _ in 0..<40 {
            crafted.append(contentsOf: [0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00])
        }
        crafted.append(contentsOf: [0xFF, 0xD9])
        await assertRejected(.photoInvalid) { try await device.client.putPhoto(id: "p", at: self.palace, data: crafted) }
    }

    func testOrdinaryProgressiveJPEGIsAccepted() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let progressive = Self.encoded(
            type: "public.jpeg",
            width: 64,
            height: 64,
            properties: [kCGImagePropertyJFIFDictionary: [kCGImagePropertyJFIFIsProgressive: true]]
        )
        try await device.client.putPhoto(id: "p", at: palace, data: progressive)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.first?.kind, .photo)
    }

    func testAJPEGWithJunkBetweenItsSegmentsIsRefused() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        var crafted = Self.jpeg()
        // After the first segment, so the file still starts like a JPEG.
        let bytes = [UInt8](crafted)
        let afterFirstSegment = 4 + (Int(bytes[4]) << 8 | Int(bytes[5]))
        crafted.insert(contentsOf: [0x00, 0x11, 0x22], at: afterFirstSegment)
        await assertRejected(.photoInvalid) { try await device.client.putPhoto(id: "p", at: self.palace, data: crafted) }
    }

    func testColourProfileFromTheSenderIsNotKept() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let context = CGContext(
            data: nil,
            width: 16,
            height: 16,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: try XCTUnwrap(CGColorSpace(name: CGColorSpace.displayP3)),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: 16, height: 16))
        let output = NSMutableData()
        let destination = CGImageDestinationCreateWithData(output, "public.jpeg" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        CGImageDestinationFinalize(destination)
        XCTAssertEqual(Self.profileName(of: output as Data), "Display P3")

        try await device.client.putPhoto(id: "p", at: palace, data: output as Data)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(Self.profileName(of: try XCTUnwrap(items.first?.photo)), "sRGB IEC61966-2.1")
    }

    private static func profileName(of data: Data) -> String? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        else { return nil }
        return properties[kCGImagePropertyProfileName] as? String
    }

    func testPhotoCannotReplaceAnotherKindOfItem() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let photo = Self.jpeg()
        let digest = Data(SHA256.hash(data: photo)).hexString
        let begin = try await raw.send(#"{"t":"photo_begin","seq":1,"id":"x","lat":1,"lon":1,"type":"image/jpeg","size":\#(photo.count),"sha256":"\#(digest)","chunks":1}"#)
        XCTAssertEqual(begin.ack, 1)
        // The id is taken while the upload is under way.
        let pin = try await raw.send(#"{"t":"put","seq":2,"kind":"pin","id":"x","lat":1,"lon":1}"#)
        XCTAssertEqual(pin.ack, 2)
        _ = try await raw.send(#"{"t":"photo_chunk","seq":3,"id":"x","index":0,"data":"\#(photo.base64EncodedString())"}"#)
        let end = try await raw.send(#"{"t":"photo_end","seq":4,"id":"x"}"#)
        XCTAssertEqual(end.code, ErrorCode.kindConflict.rawValue)
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.first?.kind, .pin)
    }

    // MARK: Fixes found by review

    func testReplayedPairingFirstMessageGetsNothing() async throws {
        let (receiver, port) = try await startReceiver()
        let ticket = try await receiver.openPairingWindow()
        let handshake = try NoiseHandshake(
            pattern: .xxPSK0,
            isInitiator: true,
            prologue: IntegrationV1.preamble(.pair),
            staticKey: Curve25519.KeyAgreement.PrivateKey(),
            psk: ticket.code.presharedKey()
        )
        let captured = IntegrationV1.preamble(.pair) + (try ByteStream.frame(try handshake.writeMessage()))

        let original = try await rawConnect(port)
        try await original.send(captured)
        let answer = try await original.readFrame()
        XCTAssertFalse(answer.isEmpty)
        original.cancel()

        let replay = try await rawConnect(port)
        try await replay.send(captured)
        let count = await bytesUntilClose(replay)
        XCTAssertEqual(count, 0, "without the code, a recording must not get an answer")
    }

    func testPairingAgainDoesNotMakeOldRecordingsWorkAgain() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let key = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: device.identity.rawPrivateKeyForStorage())
        let (_, recording) = try connectMessage(deviceKey: key, receiverKey: await receiver.publicKey, counter: 5000)

        let first = try await rawConnect(port)
        try await first.send(recording)
        _ = try await first.readFrame()
        first.cancel()

        await receiver.revoke(deviceFingerprint: device.identity.fingerprint)
        let ticket = try await receiver.openPairingWindow()
        let again = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port),
            ticket: ticket,
            identity: device.identity,
            deviceName: "Back again",
            kinds: [.pin],
            handshakeTimeout: 1
        )
        XCTAssertEqual(again.counterFloor, 5000)

        let replay = try await rawConnect(port)
        try await replay.send(recording)
        let count = await bytesUntilClose(replay)
        XCTAssertEqual(count, 0)
    }

    /// A store that has lost its count and whose clock is wrong, as after restoring an old backup.
    private final class ForgetfulClockStore: DeviceStore, @unchecked Sendable {
        let inner = InMemoryDeviceStore()
        func loadOrCreateIdentity() throws -> DeviceIdentity { try inner.loadOrCreateIdentity() }
        func pairings() throws -> [ReceiverPairing] { try inner.pairings() }
        func save(_ pairing: ReceiverPairing) throws { try inner.save(pairing) }
        func updateAddress(receiverFingerprint: String, host: String, port: UInt16) throws {
            try inner.updateAddress(receiverFingerprint: receiverFingerprint, host: host, port: port)
        }
        func removePairing(fingerprint: String) throws { try inner.removePairing(fingerprint: fingerprint) }
        func nextConnectCounter(receiverFingerprint: String, nowMS: UInt64) throws -> UInt64 {
            try inner.nextConnectCounter(receiverFingerprint: receiverFingerprint, nowMS: 1)
        }
    }

    func testPairingAgainLetsADeviceThatLostItsCounterBackIn() async throws {
        let (receiver, port) = try await startReceiver()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let healthy = try await pairedDevice(receiver, port: port)
        try await healthy.client.ping()
        await healthy.client.close()

        // The same key, in a store with no memory and a clock stuck in 1970.
        let store = ForgetfulClockStore()
        let stale = ReceiverPairing(label: "x", receiverKey: healthy.pairing.receiverKey, kinds: [.pin])
        let locked = ErdigoClient(identity: healthy.identity, pairing: stale, store: store, target: target, retry: .none, connectTimeout: 0.5)
        do {
            try await locked.ping()
            XCTFail("a counter below the stored one must get no answer")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }

        let ticket = try await receiver.openPairingWindow()
        let pairing = try await Pairing.pair(target: target, ticket: ticket, identity: healthy.identity, deviceName: "Back", kinds: [.pin], handshakeTimeout: 1)
        XCTAssertGreaterThan(pairing.counterFloor, 1)
        try store.save(pairing)
        let restored = ErdigoClient(identity: healthy.identity, pairing: pairing, store: store, target: target, retry: .none, connectTimeout: 1)
        try await restored.ping()
    }

    func testCanonicalKeyRule() throws {
        var key = Curve25519.KeyAgreement.PrivateKey().publicKey.rawRepresentation
        XCTAssertTrue(Noise.isCanonicalPublicKey(key))
        key[key.startIndex + 31] |= 0x80
        XCTAssertFalse(Noise.isCanonicalPublicKey(key))
        let prime = try XCTUnwrap(Data(hexString: "edffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f"))
        XCTAssertFalse(Noise.isCanonicalPublicKey(prime))
        let belowPrime = try XCTUnwrap(Data(hexString: "ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f"))
        XCTAssertTrue(Noise.isCanonicalPublicKey(belowPrime))
        XCTAssertFalse(Noise.isCanonicalPublicKey(Data(repeating: 0, count: 31)))
    }

    func testBeingToldToSlowDownIsNotPunished() async throws {
        var options = fastOptions()
        options.maxRejectionsPerMinute = 2
        options.limits.messagesPerSecond = 40
        options.limits.messageBurst = 2
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)

        // Each new client starts with a full allowance of its own, while the receiver's has run down.
        for _ in 0..<4 {
            let client = ErdigoClient(
                identity: device.identity,
                pairing: device.pairing,
                store: device.store,
                target: .address(host: "127.0.0.1", port: port),
                retry: .none,
                connectTimeout: 1
            )
            for _ in 0..<4 { try await client.ping() }
            await client.close()
        }
        let last = await receiver.lastSeq(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(last, 16)
    }

    /// A long wait for track points must not make waits for ordinary messages look ignored.
    func testObeyingEveryAnswerIsNeverPunished() async throws {
        var options = fastOptions()
        options.maxRejectionsPerMinute = 2
        options.limits.messagesPerSecond = 50
        options.limits.messageBurst = 1
        options.limits.pointsPerSecond = 1
        options.limits.pointBurst = 100
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let point = #"{"lat":1,"lon":1}"#
        let points = Array(repeating: point, count: 100).joined(separator: ",")

        var seq: UInt64 = 1
        func sendObeying(_ json: (UInt64) -> String) async throws {
            while true {
                let reply = try await raw.send(json(seq))
                if let wait = reply.retryAfterMS, reply.code == ErrorCode.rateLimited.rawValue {
                    // Point waits are too long for a test, so those are skipped below.
                    if wait > 1000 { return }
                    try await Task.sleep(nanoseconds: wait * 1_000_000)
                    continue
                }
                XCTAssertNil(reply.code)
                seq += 1
                return
            }
        }
        try await sendObeying { #"{"t":"track_append","seq":\#($0),"id":"w","points":[\#(points)]}"# }
        try await sendObeying { #"{"t":"track_append","seq":\#($0),"id":"w","points":[\#(points)]}"# }
        for _ in 0..<20 {
            try await sendObeying { #"{"t":"ping","seq":\#($0)}"# }
        }
        let last = await receiver.lastSeq(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(last, 21)
    }

    func testSendingJustInsideTheSlackDoesNotAvoidPunishment() async throws {
        var options = fastOptions()
        options.maxRejectionsPerMinute = 3
        options.limits.messagesPerSecond = 1
        options.limits.messageBurst = 1
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        var seq = 1
        _ = try await raw.send(#"{"t":"ping","seq":\#(seq)}"#)
        seq += 1
        let told = try await raw.send(#"{"t":"ping","seq":\#(seq)}"#)
        let wait = try XCTUnwrap(told.retryAfterMS)
        // Arrive just before the time given, then keep going as fast as answers return.
        try await Task.sleep(nanoseconds: (wait - 30) * 1_000_000)
        var last: RawReply?
        for _ in 0..<8 {
            last = try await raw.send(#"{"t":"ping","seq":\#(seq)}"#)
            if last?.fatal == true { break }
        }
        XCTAssertEqual(last?.code, ErrorCode.tooManyErrors.rawValue)
    }

    func testIgnoringTheRateLimitIsPunished() async throws {
        var options = fastOptions()
        options.maxRejectionsPerMinute = 3
        options.limits.messagesPerSecond = 1
        options.limits.messageBurst = 1
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        var last: RawReply?
        for seq in 1...8 {
            last = try await raw.send(#"{"t":"ping","seq":\#(seq)}"#)
            if last?.fatal == true { break }
        }
        XCTAssertEqual(last?.code, ErrorCode.tooManyErrors.rawValue)
    }

    func testPointRateLimit() async throws {
        var options = fastOptions()
        options.limits.pointsPerSecond = 1
        options.limits.pointBurst = 100
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let raw = try await authenticated(device, receiver: receiver, port: port)
        let point = #"{"lat":1,"lon":1}"#
        let points = Array(repeating: point, count: 100).joined(separator: ",")
        let burst = try await raw.send(#"{"t":"track_append","seq":1,"id":"w","points":[\#(points)]}"#)
        XCTAssertEqual(burst.ack, 1)
        let next = try await raw.send(#"{"t":"track_append","seq":2,"id":"w","points":[\#(point)]}"#)
        XCTAssertEqual(next.code, ErrorCode.rateLimited.rawValue)
    }

    func testTotalTrackLimit() async throws {
        var options = fastOptions()
        options.limits.tracks = 2
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let point = try TrackPoint(coordinate: palace)
        for name in ["one", "two"] {
            try await device.client.appendTrack(id: name, points: [point])
            try await device.client.finishTrack(id: name)
        }
        await assertRejected(.limitTracks) { try await device.client.appendTrack(id: "three", points: [point]) }
        try await device.client.delete(id: "one")
        try await device.client.appendTrack(id: "three", points: [point])
    }

    func testTotalStorageLimit() async throws {
        var options = fastOptions()
        options.diskBytesTotal = 1500
        let (receiver, port) = try await startReceiver(options)
        let first = try await pairedDevice(receiver, port: port)
        let second = try await pairedDevice(receiver, port: port)
        let text = String(repeating: "x", count: 900)
        try await first.client.putNote(id: "a", at: palace, text: text)
        await assertRejected(.limitDisk) { try await second.client.putNote(id: "b", at: self.palace, text: text) }
    }

    func testAnnouncedLowerTextLimitsAreEnforced() async throws {
        var options = fastOptions()
        options.limits.noteBytes = 10
        options.limits.titleScalars = 5
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        await assertRejected(.tooLarge) { try await device.client.putNote(id: "n", at: self.palace, text: "more than ten bytes") }
        await assertRejected(.tooLarge) { try await device.client.putPin(id: "p", at: self.palace, title: "too long") }
    }

    func testAppendTrackIsNotOvertakenByALaterRequest() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let points = try (0..<250).map { try TrackPoint(coordinate: palace, timestampMS: Int64($0)) }
        let client = device.client
        let append = Task { try await client.appendTrack(id: "w", points: points) }
        try await Task.sleep(nanoseconds: 1_000_000)
        try await client.finishTrack(id: "w")
        try await append.value
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.first?.points.count, 250)
        XCTAssertEqual(items.first?.isFinished, true)
    }

    func testUpToUsesAllTheTimeItWasGiven() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.stop()
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let pairing = ReceiverPairing(label: "x", receiverKey: await receiver.publicKey, kinds: [.pin])
        let client = ErdigoClient(identity: identity, pairing: pairing, store: store, target: .address(host: "127.0.0.1", port: port), retry: .upTo(3), connectTimeout: 0.2)
        let started = Date()
        do {
            try await client.ping()
            XCTFail("expected no answer")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .noAnswer)
        }
        let elapsed = Date().timeIntervalSince(started)
        XCTAssertGreaterThan(elapsed, 2.7)
        XCTAssertLessThan(elapsed, 5)
    }

    func testAResendDoesNotWaitForItsPointsAgain() async throws {
        var options = fastOptions()
        options.limits.pointsPerSecond = 1
        options.limits.pointBurst = 100
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port, retry: .upTo(5))
        let point = try TrackPoint(coordinate: palace)
        try await device.client.appendTrack(id: "w", points: Array(repeating: point, count: 95))

        // The client now has enough allowance for 5 more, and the connection breaks.
        await receiver.setPaused(true)
        await receiver.setPaused(false)
        let started = Date()
        try await device.client.appendTrack(id: "w", points: Array(repeating: point, count: 5))
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)
    }

    func testCancellingPairingThrowsCancellation() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.setApprovalHandler { _ in
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            return .deny
        }
        let ticket = try await receiver.openPairingWindow()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let pairing = Task {
            try await Pairing.pair(target: target, ticket: ticket, identity: .generate(), deviceName: "D", kinds: [.pin], handshakeTimeout: 1)
        }
        try await Task.sleep(nanoseconds: 200_000_000)
        pairing.cancel()
        do {
            _ = try await pairing.value
            XCTFail("expected a cancellation")
        } catch {
            XCTAssertTrue(error is CancellationError, "\(error)")
        }
    }

    /// A connection that is closed must not stay in memory, or anyone connecting could grow it for ever.
    func testAClosedConnectionIsReleased() async throws {
        let (_, port) = try await startReceiver()
        weak var weakConnection: NWConnection?
        do {
            let connection = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
            weakConnection = connection
            let stream = ByteStream(connection: connection)
            try await stream.start(timeout: 2)
            stream.cancel()
        }
        for _ in 0..<50 where weakConnection != nil {
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        XCTAssertNil(weakConnection)
    }

    func testCancellingAQueuedRequestReturnsPromptly() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.stop()
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let pairing = ReceiverPairing(label: "x", receiverKey: await receiver.publicKey, kinds: [.pin])
        let client = ErdigoClient(identity: identity, pairing: pairing, store: store, target: .address(host: "127.0.0.1", port: port), retry: .forever, connectTimeout: 0.2)

        let running = Task { try await client.ping() }
        try await Task.sleep(nanoseconds: 100_000_000)
        let queued = Task { try await client.ping() }
        try await Task.sleep(nanoseconds: 100_000_000)

        let started = Date()
        queued.cancel()
        do {
            try await queued.value
            XCTFail("expected a cancellation")
        } catch {
            XCTAssertTrue(error is CancellationError, "\(error)")
        }
        XCTAssertLessThan(Date().timeIntervalSince(started), 0.5)
        running.cancel()
        _ = try? await running.value
    }

    func testRetryBudgetCountsFromTheFailureNotFromTheRequest() async throws {
        var options = fastOptions()
        options.limits.pointsPerSecond = 25
        options.limits.pointBurst = 100
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port, retry: .upTo(2))
        let points = try (0..<200).map { try TrackPoint(coordinate: palace, timestampMS: Int64($0)) }

        let client = device.client
        let append = Task { try await client.appendTrack(id: "w", points: points) }
        // The second batch waits about four seconds for its allowance, longer than the two allowed for retrying.
        try await Task.sleep(nanoseconds: 1_000_000_000)
        await receiver.setPaused(true)
        await receiver.setPaused(false)
        try await append.value
        let items = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(items.first?.points.count, 200)
    }

    func testApprovalTimeoutFiresEvenIfTheHandlerNeverAnswers() async throws {
        var options = fastOptions()
        options.approvalTimeout = 0.2
        let (receiver, port) = try await startReceiver(options)
        let stuck = StuckHandler()
        await receiver.setApprovalHandler { _ in await stuck.wait() }
        let ticket = try await receiver.openPairingWindow()
        let started = Date()
        do {
            _ = try await Pairing.pair(
                target: .address(host: "127.0.0.1", port: port),
                ticket: ticket,
                identity: .generate(),
                deviceName: "Waiting",
                kinds: [.pin],
                handshakeTimeout: 1
            )
            XCTFail("expected a denial")
        } catch {
            XCTAssertEqual(error as? ErdigoError, .pairingDenied)
        }
        XCTAssertLessThan(Date().timeIntervalSince(started), 1.5)
        await stuck.finish()
    }

    /// Ignores cancellation completely, as a dialog would.
    private actor StuckHandler {
        private var continuations: [CheckedContinuation<MockReceiver.ApprovalDecision, Never>] = []

        func wait() async -> MockReceiver.ApprovalDecision {
            await withCheckedContinuation { continuations.append($0) }
        }

        func finish() {
            for continuation in continuations { continuation.resume(returning: .deny) }
            continuations.removeAll()
        }
    }

    func testPauseDeniesAPendingPairing() async throws {
        let (receiver, port) = try await startReceiver()
        await receiver.setApprovalHandler { request in
            try? await Task.sleep(nanoseconds: 300_000_000)
            return .allow(kinds: request.requestedKinds, name: nil)
        }
        let ticket = try await receiver.openPairingWindow()
        let target = ReceiverTarget.address(host: "127.0.0.1", port: port)
        let attempt = Task { await Self.tryPair(target: target, ticket: ticket) }
        try await Task.sleep(nanoseconds: 100_000_000)
        await receiver.setPaused(true)
        let paired = await attempt.value
        XCTAssertFalse(paired)
        let devices = await receiver.pairedDevices()
        XCTAssertTrue(devices.isEmpty, "nothing may be added while paused")
    }

    func testPauseEndsHandshakesInProgress() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let key = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: device.identity.rawPrivateKeyForStorage())
        let (_, message) = try connectMessage(deviceKey: key, receiverKey: await receiver.publicKey, counter: 77)

        // Only the preamble so far. The rest arrives after the pause.
        let stream = try await rawConnect(port)
        try await stream.send(IntegrationV1.preamble(.connect))
        try await Task.sleep(nanoseconds: 100_000_000)
        await receiver.setPaused(true)
        try? await stream.send(Data(message.dropFirst(5)))
        let count = await bytesUntilClose(stream)
        XCTAssertEqual(count, 0)
    }

    func testHandshakeHeldOpenCannotFinishDuringAPenalty() async throws {
        var options = fastOptions()
        options.maxRejectionsPerMinute = 1
        let (receiver, port) = try await startReceiver(options)
        let device = try await pairedDevice(receiver, port: port)
        let key = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: device.identity.rawPrivateKeyForStorage())

        // Connection B gets as far as the handshake reply, and holds back its hello.
        rawCounter += 1
        let (handshake, message) = try connectMessage(deviceKey: key, receiverKey: await receiver.publicKey, counter: rawCounter)
        let held = try await rawConnect(port)
        try await held.send(message)
        _ = try handshake.readMessage(try await held.readFrame())
        let heldTransport = try handshake.split()

        // Connection A earns the penalty.
        let raw = try await authenticated(identity: device.identity, receiverKey: await receiver.publicKey, port: port)
        var last: RawReply?
        for _ in 0..<3 {
            last = try await raw.send(#"{"t":"nonsense","seq":1}"#)
            if last?.fatal == true { break }
        }
        XCTAssertEqual(last?.code, ErrorCode.tooManyErrors.rawValue)

        try await held.sendFrame(try heldTransport.seal(Wire.encode(Hello(versions: [1], sdk: "test/1"))))
        let count = await bytesUntilClose(held)
        XCTAssertEqual(count, 0)
    }

    func testRetainedItemsCanBeDeletedLater() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        try await device.client.putPin(id: "kept", at: palace)
        await receiver.revoke(deviceFingerprint: device.identity.fingerprint)
        let kept = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(kept.count, 1)
        await receiver.revoke(deviceFingerprint: device.identity.fingerprint, deleteItems: true)
        let gone = await receiver.items(ofDevice: device.identity.fingerprint)
        XCTAssertEqual(gone, [])
    }

    func testForgottenPairingIsNotBroughtBackByARunningClient() async throws {
        let (receiver, port) = try await startReceiver()
        let store = InMemoryDeviceStore()
        let identity = try store.loadOrCreateIdentity()
        let ticket = try await receiver.openPairingWindow()
        var pairing = try await Pairing.pair(
            target: .address(host: "127.0.0.1", port: port),
            ticket: ticket,
            identity: identity,
            deviceName: "D",
            kinds: [.pin],
            handshakeTimeout: 1
        )
        // An out-of-date address makes the client want to save the right one.
        pairing.lastHost = "192.0.2.1"
        pairing.lastPort = 1
        let client = ErdigoClient(identity: identity, pairing: pairing, store: store, target: .address(host: "127.0.0.1", port: port), retry: .none, connectTimeout: 1)
        try await client.ping()
        XCTAssertEqual(try store.pairings(), [])
    }

    func testDroppingAClientClosesItsConnection() async throws {
        let (receiver, port) = try await startReceiver()
        let device = try await pairedDevice(receiver, port: port)
        let fingerprint = device.identity.fingerprint
        do {
            let scoped = ErdigoClient(
                identity: device.identity,
                pairing: device.pairing,
                store: device.store,
                target: .address(host: "127.0.0.1", port: port),
                retry: .none,
                connectTimeout: 1
            )
            try await scoped.ping()
        }
        try await Task.sleep(nanoseconds: 500_000_000)
        let events = await receiver.events
        XCTAssertTrue(events.contains(.disconnected(device: fingerprint)))
    }

    // MARK: Raw access

    private struct RawReply {
        var ack: UInt64?
        var duplicate: Bool?
        var code: String?
        var lastSeq: UInt64?
        var retryAfterMS: UInt64?
        var fatal: Bool?
    }

    /// A plain socket. Network.framework keeps taking data on its own long after the program stops
    /// reading, so it cannot play a peer that has stopped reading.
    private final class PlainSocketPeer {
        private let descriptor: Int32

        init(port: UInt16) throws {
            descriptor = socket(AF_INET, SOCK_STREAM, 0)
            guard descriptor >= 0 else { throw ByteStream.Failure.closed }
            var on: Int32 = 1
            setsockopt(descriptor, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
            var small: Int32 = 4096
            setsockopt(descriptor, SOL_SOCKET, SO_RCVBUF, &small, socklen_t(MemoryLayout<Int32>.size))
            var patience = timeval(tv_sec: 5, tv_usec: 0)
            setsockopt(descriptor, SOL_SOCKET, SO_RCVTIMEO, &patience, socklen_t(MemoryLayout<timeval>.size))

            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = port.bigEndian
            address.sin_addr.s_addr = inet_addr("127.0.0.1")
            let descriptor = self.descriptor
            let result = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    connect(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            guard result == 0 else {
                shut()
                throw ByteStream.Failure.closed
            }
        }

        func shut() {
            close(descriptor)
        }

        func writeAll(_ data: Data) throws {
            var rest = data
            while !rest.isEmpty {
                let sent = rest.withUnsafeBytes { send(descriptor, $0.baseAddress, $0.count, 0) }
                guard sent > 0 else { throw ByteStream.Failure.closed }
                rest.removeFirst(sent)
            }
        }

        /// From here on, `writeSome` returns at once. `MSG_DONTWAIT` is not enough for this on macOS.
        func stopWaitingOnWrites() {
            _ = fcntl(descriptor, F_SETFL, fcntl(descriptor, F_GETFL) | O_NONBLOCK)
        }

        /// How many bytes were taken without waiting, or nil once the connection has gone.
        func writeSome(_ data: Data) -> Int? {
            let sent = data.withUnsafeBytes { send(descriptor, $0.baseAddress, $0.count, 0) }
            if sent >= 0 { return sent }
            return errno == EAGAIN ? 0 : nil
        }

        func readFrame() throws -> Data {
            let header = try readExactly(2)
            return try readExactly(Int(header[0]) << 8 | Int(header[1]))
        }

        private func readExactly(_ count: Int) throws -> Data {
            var bytes = [UInt8](repeating: 0, count: count)
            var filled = 0
            while filled < count {
                let got = bytes.withUnsafeMutableBytes { recv(descriptor, $0.baseAddress! + filled, count - filled, 0) }
                guard got > 0 else { throw ByteStream.Failure.closed }
                filled += got
            }
            return Data(bytes)
        }
    }

    private final class RawSession {
        let stream: ByteStream
        let transport: NoiseTransport
        var welcome: Welcome?

        init(stream: ByteStream, transport: NoiseTransport) {
            self.stream = stream
            self.transport = transport
        }

        func send(_ json: String) async throws -> RawReply {
            try await stream.sendFrame(try transport.seal(Data(json.utf8)))
            let frame = try await stream.readFrame()
            switch try Wire.decodeReceiverMessage(try transport.open(frame)) {
            case .ack(let seq, let duplicate):
                return RawReply(ack: seq, duplicate: duplicate)
            case .error(let error):
                return RawReply(code: error.code, lastSeq: error.lastSeq, retryAfterMS: error.retryAfterMS, fatal: error.fatal)
            default:
                throw ByteStream.Failure.closed
            }
        }
    }

    private func authenticated(_ device: Paired, receiver: MockReceiver, port: UInt16) async throws -> RawSession {
        try await authenticated(identity: device.identity, receiverKey: await receiver.publicKey, port: port)
    }

    /// Completes a real handshake so a test can send messages the SDK would refuse to produce.
    private func authenticated(
        identity: DeviceIdentity,
        receiverKey: Data,
        port: UInt16,
        versions: [Int] = [1],
        expectWelcome: Bool = true
    ) async throws -> RawSession {
        let key = try Curve25519.KeyAgreement.PrivateKey(rawRepresentation: identity.rawPrivateKeyForStorage())
        // Small enough that a real client, which starts from the clock, can still connect afterwards.
        rawCounter += 1
        let (handshake, message) = try connectMessage(deviceKey: key, receiverKey: receiverKey, counter: rawCounter)
        let stream = try await rawConnect(port)
        try await stream.send(message)
        _ = try handshake.readMessage(try await stream.readFrame())
        let transport = try handshake.split()
        try await stream.sendFrame(try transport.seal(Wire.encode(Hello(versions: versions, sdk: "test/1"))))
        let session = RawSession(stream: stream, transport: transport)
        if expectWelcome {
            let frame = try await stream.readFrame()
            guard case .welcome(let welcome) = try Wire.decodeReceiverMessage(try session.transport.open(frame)) else {
                throw ByteStream.Failure.closed
            }
            session.welcome = welcome
        }
        return session
    }

    // MARK: Test images

    private static func image(width: Int, height: Int) -> CGImage {
        let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        )!
        context.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.8, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()!
    }

    private static func encoded(type: String, width: Int = 32, height: Int = 32, properties: [CFString: Any]? = nil) -> Data {
        let output = NSMutableData()
        let destination = CGImageDestinationCreateWithData(output, type as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, image(width: width, height: height), properties as CFDictionary?)
        CGImageDestinationFinalize(destination)
        return output as Data
    }

    private static func jpeg(withGPS: Bool = false) -> Data {
        guard withGPS else { return encoded(type: "public.jpeg") }
        // A public place, so nothing real is revealed by this test image.
        let gps: [CFString: Any] = [
            kCGImagePropertyGPSLatitude: 37.5663,
            kCGImagePropertyGPSLatitudeRef: "N",
            kCGImagePropertyGPSLongitude: 126.9779,
            kCGImagePropertyGPSLongitudeRef: "E",
        ]
        return encoded(type: "public.jpeg", properties: [kCGImagePropertyGPSDictionary: gps])
    }

    private static func gpsProperties(of data: Data) -> [CFString: Any]? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        else { return nil }
        return properties[kCGImagePropertyGPSDictionary] as? [CFString: Any]
    }
}

final class Flag: @unchecked Sendable {
    private let lock = NSLock()
    private var value = false

    var isSet: Bool {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func set() {
        lock.lock()
        value = true
        lock.unlock()
    }
}

final class ModelTests: XCTestCase {
    func testPairingCodeParsing() throws {
        let code = try PairingCode(parsing: "abcde-fghjk mnpqr\ttvwxy")
        XCTAssertEqual(code.canonical, "ABCDEFGHJKMNPQRTVWXY".uppercased())
        XCTAssertEqual(code.display, "ABCDE-FGHJK-MNPQR-TVWXY")
        XCTAssertEqual(try PairingCode(parsing: "O0IL1-00000-00000-00000").canonical, "00111000000000000000")
        XCTAssertThrowsError(try PairingCode(parsing: "ABCDE"))
        XCTAssertThrowsError(try PairingCode(parsing: "UUUUU-UUUUU-UUUUU-UUUUU"), "U is not in the alphabet")
        XCTAssertThrowsError(try PairingCode(parsing: "ÄÖÜÄÖ-00000-00000-00000"))
        // Spaces a document or an input method might use.
        XCTAssertEqual(try PairingCode(parsing: "ABCDE\u{00A0}FGHJK\u{3000}MNPQR\u{000C}TVWXY").canonical, "ABCDEFGHJKMNPQRTVWXY")
    }

    func testPairingCodeDoesNotPrintItself() throws {
        let code = try PairingCode.generate()
        XCTAssertFalse("\(code)".contains(code.canonical))
        XCTAssertEqual(code.canonical.count, 20)
    }

    func testGeneratedCodesDiffer() throws {
        let codes = try (0..<1000).map { _ in try PairingCode.generate().canonical }
        XCTAssertEqual(Set(codes).count, 1000)
    }

    func testPairingTicketParsing() throws {
        let fingerprint = String(repeating: "ab", count: 32)
        let ticket = try PairingTicket(parsing: "erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&fp=\(fingerprint)")
        XCTAssertEqual(ticket.fingerprint, fingerprint)
        XCTAssertEqual(try PairingTicket(parsing: try XCTUnwrap(ticket.uri)), ticket)
        XCTAssertThrowsError(try PairingTicket(parsing: "erdigo-pair:1?fp=\(fingerprint)"))
        XCTAssertThrowsError(try PairingTicket(parsing: "erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&fp=zz"))
        XCTAssertThrowsError(try PairingTicket(parsing: "erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&host=192.0.2.1"))
    }

    func testQRTextNeedsExactlyOneCodeAndOneValidFingerprint() throws {
        let code = "TEST0-0NLYC-0DE00-00000"
        let fingerprint = String(repeating: "ab", count: 32)
        let other = String(repeating: "cd", count: 32)
        let cases: [(name: String, text: String)] = [
            ("nothing after the prefix", "erdigo-pair:1?"),
            ("no fingerprint", "erdigo-pair:1?code=\(code)"),
            ("no code", "erdigo-pair:1?fp=\(fingerprint)"),
            ("empty fingerprint", "erdigo-pair:1?code=\(code)&fp="),
            ("fingerprint without an equals sign", "erdigo-pair:1?code=\(code)&fp"),
            ("empty code", "erdigo-pair:1?code=&fp=\(fingerprint)"),
            ("code without an equals sign", "erdigo-pair:1?code&fp=\(fingerprint)"),
            ("fingerprint one digit short", "erdigo-pair:1?code=\(code)&fp=\(fingerprint.dropLast())"),
            ("fingerprint one digit long", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)a"),
            ("fingerprint of half the length", "erdigo-pair:1?code=\(code)&fp=\(fingerprint.prefix(32))"),
            ("fingerprint that is not hexadecimal", "erdigo-pair:1?code=\(code)&fp=\(String(repeating: "zz", count: 32))"),
            ("fingerprint with an equals sign", "erdigo-pair:1?code=\(code)&fp=\(fingerprint.dropLast())="),
            ("fingerprint in other digits", "erdigo-pair:1?code=\(code)&fp=\(String(repeating: "\u{FF11}", count: 64))"),
            ("fingerprint with a line break", "erdigo-pair:1?code=\(code)&fp=\(fingerprint.prefix(32))\n\(fingerprint.prefix(31))"),
            ("repeated fingerprint", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)&fp=\(fingerprint)"),
            ("two fingerprints", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)&fp=\(other)"),
            ("repeated code", "erdigo-pair:1?code=\(code)&code=\(code)&fp=\(fingerprint)"),
            ("repeated code after the fingerprint", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)&code=\(code)"),
            ("an address", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)&host=192.0.2.1"),
            ("an unknown field in front", "erdigo-pair:1?x=1&code=\(code)&fp=\(fingerprint)"),
            ("an unknown field with no value", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)&x"),
            ("a field with no name", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)&=1"),
            ("an equals sign before the code", "erdigo-pair:1?=code=\(code)&fp=\(fingerprint)"),
            ("an equals sign before the fingerprint", "erdigo-pair:1?code=\(code)&=fp=\(fingerprint)"),
            ("an equals sign before both", "erdigo-pair:1?=code=\(code)&=fp=\(fingerprint)"),
            ("two equals signs before the fingerprint", "erdigo-pair:1?code=\(code)&==fp=\(fingerprint)"),
            ("several equals signs before both", "erdigo-pair:1?===code=\(code)&===fp=\(fingerprint)"),
            ("nothing but an equals sign", "erdigo-pair:1?="),
            ("an empty field in the middle", "erdigo-pair:1?code=\(code)&&fp=\(fingerprint)"),
            ("an empty field in front", "erdigo-pair:1?&code=\(code)&fp=\(fingerprint)"),
            ("a trailing separator", "erdigo-pair:1?code=\(code)&fp=\(fingerprint)&"),
            ("only separators", "erdigo-pair:1?&&"),
            ("field names in capitals", "erdigo-pair:1?CODE=\(code)&FP=\(fingerprint)"),
            ("another version", "erdigo-pair:2?code=\(code)&fp=\(fingerprint)"),
            ("no question mark", "erdigo-pair:1code=\(code)&fp=\(fingerprint)"),
            ("a prefix in capitals", "ERDIGO-PAIR:1?code=\(code)&fp=\(fingerprint)"),
        ]
        for (name, text) in cases {
            XCTAssertThrowsError(try PairingTicket(parsing: text), name) {
                XCTAssertEqual(($0 as? WireError)?.code, .invalidMessage, name)
            }
        }
    }

    func testPairingTextOverTheLimitIsRefusedBeforeItIsRead() throws {
        let code = "TEST0-0NLYC-0DE00-00000"
        let fingerprint = String(repeating: "ab", count: 32)
        let limit = 1024
        let qr = "erdigo-pair:1?code=\(code)&fp=\(fingerprint)"
        XCTAssertEqual(qr.utf8.count, 110, "the length the limit's comment gives")

        // White space is the one thing a reader tolerates, so it shows exactly where the limit is.
        for text in [qr, code] {
            let atTheLimit = text + String(repeating: " ", count: limit - text.utf8.count)
            XCTAssertNoThrow(try PairingTicket(parsing: atTheLimit))
            XCTAssertThrowsError(try PairingTicket(parsing: atTheLimit + " ")) {
                XCTAssertEqual(($0 as? WireError)?.code, .invalidMessage)
            }
        }

        // The limit counts bytes, not characters.
        let wide = String(repeating: "\u{3000}", count: limit / 3 + 1)
        XCTAssertLessThan(wide.count, limit)
        XCTAssertThrowsError(try PairingTicket(parsing: code + wide))

        // Valid text with something huge inside it is not valid.
        let huge = 2_000_000
        XCTAssertThrowsError(try PairingTicket(parsing: "erdigo-pair:1?code=\(String(repeating: "-", count: huge))\(code)&fp=\(fingerprint)"))
        XCTAssertThrowsError(try PairingTicket(parsing: "erdigo-pair:1?\(String(repeating: "&", count: huge))"))
    }

    func testValidQRTextWorksAndRoundTrips() throws {
        let code = try PairingCode(parsing: "TEST0-0NLYC-0DE00-00000")
        let fingerprint = String(repeating: "ab", count: 32)

        let ticket = try PairingTicket(parsing: "  erdigo-pair:1?code=\(code.display)&fp=\(fingerprint)\n")
        XCTAssertEqual(ticket, PairingTicket(code: code, fingerprint: fingerprint))
        XCTAssertEqual(ticket.uri, "erdigo-pair:1?code=\(code.display)&fp=\(fingerprint)")
        XCTAssertEqual(try PairingTicket(parsing: try XCTUnwrap(ticket.uri)), ticket)

        // The two fields may come in either order, and digits are read as lowercase.
        XCTAssertEqual(try PairingTicket(parsing: "erdigo-pair:1?fp=\(fingerprint)&code=\(code.display)"), ticket)
        let capitals = try PairingTicket(parsing: "erdigo-pair:1?code=\(code.display)&fp=\(fingerprint.uppercased())")
        XCTAssertEqual(capitals, ticket)
        XCTAssertEqual(capitals.uri, ticket.uri)
    }

    func testABareCodeStaysTheTypedPath() throws {
        let code = try PairingCode(parsing: "TEST0-0NLYC-0DE00-00000")
        for typed in ["TEST0-0NLYC-0DE00-00000", "test0 0nlyc 0de00 00000", " TEST00NLYC0DE0000000\n"] {
            let ticket = try PairingTicket(parsing: typed)
            XCTAssertEqual(ticket.code, code, typed)
            XCTAssertNil(ticket.fingerprint, typed)
        }
        XCTAssertThrowsError(try PairingTicket(parsing: ""))
        XCTAssertThrowsError(try PairingTicket(parsing: "TEST0"))
    }

    func testOnlyAValidFingerprintBecomesQRText() throws {
        let code = try PairingCode(parsing: "TEST0-0NLYC-0DE00-00000")
        let fingerprint = String(repeating: "ab", count: 32)

        // A typed code can still be represented, but it does not become QR text.
        XCTAssertNil(PairingTicket(code: code).uri)
        XCTAssertNil(try PairingTicket(parsing: code.display).uri)

        let malformed = [
            "",
            String(fingerprint.dropLast()),
            fingerprint + "a",
            fingerprint.uppercased(),
            String(repeating: "zz", count: 32),
            String(fingerprint.dropLast(10)) + "&host=x",
            String(repeating: "\u{FF11}", count: 64),
        ]
        for text in malformed {
            XCTAssertNil(PairingTicket(code: code, fingerprint: text).uri, text)
        }
        XCTAssertNotNil(PairingTicket(code: code, fingerprint: fingerprint).uri)
    }

    func testCoordinatesAreValidated() {
        XCTAssertNoThrow(try Coordinate(latitude: -90, longitude: 180))
        XCTAssertThrowsError(try Coordinate(latitude: .nan, longitude: 0))
        XCTAssertThrowsError(try Coordinate(latitude: 0, longitude: .infinity))
        XCTAssertThrowsError(try Coordinate(latitude: 90.0001, longitude: 0))
    }

    func testRequestsRoundTrip() throws {
        let at = try Coordinate(latitude: 37.5796, longitude: 126.977)
        let id = try ItemID("a_b-1")
        let requests: [Request] = [
            .ping,
            .putPin(id: id, at: at, title: "Zürich €"),
            .putMarker(id: id, at: at, title: nil, color: .green),
            .putNote(id: id, at: at, text: "Line\nLine \"quoted\" \\ done"),
            .move(id: id, to: at),
            .delete(id: id),
            .deleteAll,
            .photoBegin(PhotoHeader(id: id, at: at, title: nil, type: .png, size: 5, sha256: String(repeating: "0", count: 64), chunks: 1)),
            .photoChunk(id: id, index: 3, data: Data([1, 2, 3])),
            .photoEnd(id: id),
            .trackAppend(id: id, title: "T", points: [try TrackPoint(coordinate: at, timestampMS: 5, altitudeM: -3.5)]),
            .trackFinish(id: id),
        ]
        for request in requests {
            let envelope = RequestEnvelope(seq: 7, request: request)
            XCTAssertEqual(try Wire.decodeRequest(Wire.encode(envelope)), envelope)
        }
    }

    func testReceiverMessagesRoundTrip() throws {
        let messages: [ReceiverMessage] = [
            .welcome(Welcome(version: 1, lastSeq: 9, kinds: [.pin], limits: .v1)),
            .ack(seq: 1, duplicate: true),
            .error(ReceiverError(code: "rate_limited", seq: 3, lastSeq: 2, retryAfterMS: 100, supported: [1], fatal: true)),
            .pairStatus(.pending),
            .pairStatus(.denied),
            .pairStatus(.approved(kinds: [.pin, .track], counter: 12)),
        ]
        for message in messages {
            XCTAssertEqual(try Wire.decodeReceiverMessage(Wire.encode(message)), message)
        }
    }

    func testSDKRejectsMalformedReceiverMessages() {
        for text in [
            #"{"t":"ack","seq":1,"dup":false,"extra":1}"#,
            #"{"t":"welcome","version":1,"last_seq":0,"kinds":[]}"#,
            #"{"t":"error","code":"BAD CODE"}"#,
            #"{"t":"pair_status","status":"approved"}"#,
            #"{"t":"pair_status","status":"approved","kinds":["pin"]}"#,
            #"{"t":"mystery"}"#,
        ] {
            XCTAssertThrowsError(try Wire.decodeReceiverMessage(Data(text.utf8)), text)
        }
    }

    func testStrictJSONRejections() {
        for text in ["", "{", "{}{}", #"{"a":1,}"#, "[1,]", "01", "1.", ".5", "+1", "1e", "tru", #""\x""#, "\"\u{01}\""] {
            XCTAssertThrowsError(try StrictJSON.parse(Data(text.utf8)), text)
        }
        XCTAssertEqual(try StrictJSON.parse(Data(#""😀""#.utf8)), .string("😀"))
        XCTAssertEqual(try StrictJSON.parse(Data("-1".utf8)), .integer(-1))
        XCTAssertEqual(try StrictJSON.parse(Data("1.5e2".utf8)), .number(150))
        // Too long for an integer, but still a number, so a field wanting an integer refuses it later.
        XCTAssertEqual(try StrictJSON.parse(Data("9223372036854775808".utf8)), .number(9223372036854775808))
        XCTAssertThrowsError(try StrictJSON.parse(Data("1e999".utf8)))
        // Six levels are allowed, and seven are not.
        XCTAssertNoThrow(try StrictJSON.parse(Data("[[[[[[1]]]]]]".utf8)))
        XCTAssertThrowsError(try StrictJSON.parse(Data("[[[[[[[1]]]]]]]".utf8)))
        // White space after the value is part of JSON. Anything else is not.
        XCTAssertNoThrow(try StrictJSON.parse(Data("{} \n".utf8)))
    }

    func testMessageSizeLimit() {
        let tooLong = Data(repeating: 0x20, count: IntegrationV1.maxPlaintextLength + 1)
        XCTAssertThrowsError(try Wire.decodeRequest(tooLong)) {
            XCTAssertEqual(($0 as? WireError)?.code, .tooLarge)
        }
    }

    func testTokenBucket() {
        var bucket = TokenBucket(capacity: 2, refillPerSecond: 1, now: 100)
        XCTAssertNil(bucket.take(now: 100))
        XCTAssertNil(bucket.take(now: 100))
        XCTAssertEqual(bucket.take(now: 100) ?? 0, 1, accuracy: 0.001)
        XCTAssertNil(bucket.take(now: 101))
        // Asking for more than the capacity never succeeds, and must not promise otherwise.
        var small = TokenBucket(capacity: 2, refillPerSecond: 1, now: 0)
        XCTAssertNotNil(small.take(5, now: 0))
    }
}

final class FileStoreTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("erdigo-tests-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: directory)
    }

    private func mode(of url: URL) throws -> Int {
        try XCTUnwrap(try FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? Int)
    }

    func testKeyIsPrivateAndSurvivesRestart() throws {
        let store = try FileDeviceStore(directory: directory)
        let identity = try store.loadOrCreateIdentity()
        XCTAssertEqual(try mode(of: directory), 0o700)
        XCTAssertEqual(try mode(of: directory.appendingPathComponent("identity.json")), 0o600)

        let again = try FileDeviceStore(directory: directory)
        XCTAssertEqual(try again.loadOrCreateIdentity().publicKey, identity.publicKey)
    }

    func testRefusesKeyFileOthersCouldRead() throws {
        let store = try FileDeviceStore(directory: directory)
        _ = try store.loadOrCreateIdentity()
        let file = directory.appendingPathComponent("identity.json")
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: file.path)
        XCTAssertThrowsError(try FileDeviceStore(directory: directory).loadOrCreateIdentity())
    }

    func testRefusesStateFolderOthersCouldRead() throws {
        _ = try FileDeviceStore(directory: directory)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: directory.path)
        XCTAssertThrowsError(try FileDeviceStore(directory: directory))
    }

    func testRefusesSymbolicLinkForKeyFile() throws {
        let store = try FileDeviceStore(directory: directory)
        let target = directory.appendingPathComponent("elsewhere")
        FileManager.default.createFile(atPath: target.path, contents: Data("{}".utf8), attributes: [.posixPermissions: 0o600])
        try FileManager.default.createSymbolicLink(at: directory.appendingPathComponent("identity.json"), withDestinationURL: target)
        XCTAssertThrowsError(try store.loadOrCreateIdentity())
    }

    func testPairingsAndCountersPersist() throws {
        let store = try FileDeviceStore(directory: directory)
        let key = Data(repeating: 7, count: 32)
        let pairing = ReceiverPairing(label: "home", receiverKey: key, kinds: [.pin], lastHost: "192.0.2.10", lastPort: 49000)
        try store.save(pairing)

        let again = try FileDeviceStore(directory: directory)
        XCTAssertEqual(try again.pairings().first?.label, "home")
        XCTAssertEqual(try mode(of: directory.appendingPathComponent("pairings.json")), 0o600)

        let first = try again.nextConnectCounter(receiverFingerprint: pairing.fingerprint, nowMS: 1000)
        let second = try again.nextConnectCounter(receiverFingerprint: pairing.fingerprint, nowMS: 500)
        XCTAssertEqual(first, 1000)
        XCTAssertEqual(second, 1001, "the counter never goes back, even if the clock does")
        let third = try FileDeviceStore(directory: directory).nextConnectCounter(receiverFingerprint: pairing.fingerprint, nowMS: 0)
        XCTAssertEqual(third, 1002)

        try again.removePairing(fingerprint: pairing.fingerprint)
        XCTAssertEqual(try again.pairings(), [])
    }

    func testCountersAreSafeAcrossThreads() throws {
        let store = try FileDeviceStore(directory: directory)
        let lock = NSLock()
        var values: [UInt64] = []
        DispatchQueue.concurrentPerform(iterations: 32) { _ in
            let value = try! store.nextConnectCounter(receiverFingerprint: "x", nowMS: 1)
            lock.lock()
            values.append(value)
            lock.unlock()
        }
        XCTAssertEqual(Set(values).count, 32)
    }
}
