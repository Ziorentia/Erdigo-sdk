// Generates spec/vectors/integration-v1 from keys that exist only for this purpose.
//
//     swift run erdigo-vectors spec/vectors/integration-v1
//
// Every private key below is derived from a public label, so anyone can see it is not a real key.
// Do not generate vectors from real device or receiver keys.

import CryptoKit
@_spi(TestVectors) @_spi(ErdigoInternal) import ErdigoCore
import Foundation

func testKey(_ label: String) -> Curve25519.KeyAgreement.PrivateKey {
    let raw = Data(SHA256.hash(data: Data("erdigo-integration-v1 test-only key: \(label)".utf8)))
    return try! Curve25519.KeyAgreement.PrivateKey(rawRepresentation: raw)
}

struct VectorCheckFailed: Error {}

func require(_ condition: Bool) throws {
    guard condition else { throw VectorCheckFailed() }
}

func hex(_ data: Data) -> String { data.hexString }

func hex(_ key: Curve25519.KeyAgreement.PrivateKey) -> String { key.rawRepresentation.hexString }

func hexPublic(_ key: Curve25519.KeyAgreement.PrivateKey) -> String { key.publicKey.rawRepresentation.hexString }

func framed(_ data: Data) -> Data { try! ByteStream.frame(data) }

struct Transcript {
    var frames: [[String: Any]] = []

    mutating func addPreamble(_ bytes: Data) {
        frames.append(["stage": "preamble", "direction": "device_to_receiver", "wire": hex(bytes)])
    }

    mutating func add(stage: String, direction: String, payload: Data, noise: Data) {
        frames.append([
            "stage": stage,
            "direction": direction,
            "payload": hex(payload),
            "payload_text": String(data: payload, encoding: .utf8) ?? "",
            "noise_message": hex(noise),
            "wire": hex(framed(noise)),
        ])
    }
}

func write(_ object: Any, to path: URL) throws {
    let data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
    try (data + Data("\n".utf8)).write(to: path)
}

let deviceStatic = testKey("device static")
let receiverStatic = testKey("receiver static")

func connectVector() throws -> [String: Any] {
    let deviceEphemeral = testKey("connect device ephemeral")
    let receiverEphemeral = testKey("connect receiver ephemeral")
    let preamble = IntegrationV1.preamble(.connect)
    let counter: UInt64 = 1_700_000_000_000

    let initiator = try NoiseHandshake(
        pattern: .ik, isInitiator: true, prologue: preamble, staticKey: deviceStatic,
        remoteStaticKey: receiverStatic.publicKey.rawRepresentation, psks: [], fixedEphemeral: deviceEphemeral
    )
    let responder = try NoiseHandshake(
        pattern: .ik, isInitiator: false, prologue: preamble, staticKey: receiverStatic,
        remoteStaticKey: nil, psks: [], fixedEphemeral: receiverEphemeral
    )

    var transcript = Transcript()
    transcript.addPreamble(preamble)

    var counterBytes = Data()
    withUnsafeBytes(of: counter.bigEndian) { counterBytes.append(contentsOf: $0) }
    let first = try initiator.writeMessage(payload: counterBytes)
    try require(responder.readMessage(first) == counterBytes)
    transcript.add(stage: "handshake_1", direction: "device_to_receiver", payload: counterBytes, noise: first)

    let second = try responder.writeMessage()
    try require(initiator.readMessage(second).isEmpty)
    transcript.add(stage: "handshake_2", direction: "receiver_to_device", payload: Data(), noise: second)

    let device = try initiator.split()
    let receiver = try responder.split()
    precondition(device.handshakeHash == receiver.handshakeHash)

    func toReceiver(_ stage: String, _ payload: Data) throws {
        let sealed = try device.seal(payload)
        try require(receiver.open(sealed) == payload)
        transcript.add(stage: stage, direction: "device_to_receiver", payload: payload, noise: sealed)
    }
    func toDevice(_ stage: String, _ payload: Data) throws {
        let sealed = try receiver.seal(payload)
        try require(device.open(sealed) == payload)
        transcript.add(stage: stage, direction: "receiver_to_device", payload: payload, noise: sealed)
    }

    try toReceiver("hello", Wire.encode(Hello(versions: [1], sdk: "erdigo-test/1.0")))
    try toDevice("welcome", Wire.encode(.welcome(Welcome(version: 1, lastSeq: 41, kinds: [.pin, .track], limits: .v1))))
    let coordinate = try Coordinate(latitude: 37.5796, longitude: 126.977)
    try toReceiver("request", Wire.encode(RequestEnvelope(seq: 42, request: .putPin(id: try ItemID("gate"), at: coordinate, title: "Palace gate"))))
    try toDevice("ack", Wire.encode(.ack(seq: 42, duplicate: false)))

    return [
        "description": "An ordinary connection. All keys here are test-only and derived from public labels.",
        "protocol_name": "Noise_IK_25519_ChaChaPoly_SHA256",
        "preamble_and_prologue": hex(preamble),
        "device_static_private": hex(deviceStatic),
        "device_static_public": hexPublic(deviceStatic),
        "device_fingerprint": IntegrationV1.fingerprint(of: deviceStatic.publicKey.rawRepresentation),
        "device_ephemeral_private": hex(deviceEphemeral),
        "receiver_static_private": hex(receiverStatic),
        "receiver_static_public": hexPublic(receiverStatic),
        "receiver_fingerprint": IntegrationV1.fingerprint(of: receiverStatic.publicKey.rawRepresentation),
        "receiver_ephemeral_private": hex(receiverEphemeral),
        "connect_counter": counter,
        "handshake_hash": hex(device.handshakeHash),
        "frames": transcript.frames,
    ]
}

func pairVector() throws -> [String: Any] {
    let deviceEphemeral = testKey("pair device ephemeral")
    let receiverEphemeral = testKey("pair receiver ephemeral")
    let preamble = IntegrationV1.preamble(.pair)
    let code = try PairingCode(parsing: "TEST0-0NLYC-0DE00-00000")
    let psk = code.presharedKey()
    let fingerprint = IntegrationV1.fingerprint(of: receiverStatic.publicKey.rawRepresentation)

    let initiator = try NoiseHandshake(
        pattern: .xxPSK0, isInitiator: true, prologue: preamble, staticKey: deviceStatic,
        remoteStaticKey: nil, psks: [psk], fixedEphemeral: deviceEphemeral
    )
    let responder = try NoiseHandshake(
        pattern: .xxPSK0, isInitiator: false, prologue: preamble, staticKey: receiverStatic,
        remoteStaticKey: nil, psks: [psk], fixedEphemeral: receiverEphemeral
    )

    var transcript = Transcript()
    transcript.addPreamble(preamble)

    let first = try initiator.writeMessage()
    try require(responder.readMessage(first).isEmpty)
    transcript.add(stage: "handshake_1", direction: "device_to_receiver", payload: Data(), noise: first)

    let second = try responder.writeMessage()
    try require(initiator.readMessage(second).isEmpty)
    transcript.add(stage: "handshake_2", direction: "receiver_to_device", payload: Data(), noise: second)

    let request = PairRequest(versions: [1], sdk: "erdigo-test/1.0", name: "Test device", kinds: [.pin, .track])
    let payload = Wire.encode(request)
    let third = try initiator.writeMessage(payload: payload)
    try require(responder.readMessage(third) == payload)
    transcript.add(stage: "handshake_3", direction: "device_to_receiver", payload: payload, noise: third)

    let device = try initiator.split()
    let receiver = try responder.split()
    precondition(device.handshakeHash == receiver.handshakeHash)

    for (stage, status) in [("pair_status_pending", PairStatus.pending), ("pair_status_approved", .approved(kinds: [.pin], counter: 0))] {
        let plain = Wire.encode(.pairStatus(status))
        transcript.add(stage: stage, direction: "receiver_to_device", payload: plain, noise: try receiver.seal(plain))
    }

    return [
        "description": "Pairing. All keys and the code here are test-only and derived from public labels.",
        "protocol_name": "Noise_XXpsk0_25519_ChaChaPoly_SHA256",
        "preamble_and_prologue": hex(preamble),
        "pairing_code_as_typed": "TEST0-0NLYC-0DE00-00000",
        "pairing_code_canonical": code.canonical,
        "psk": hex(psk),
        "qr_text": PairingTicket(code: code, fingerprint: fingerprint).uri!,
        "device_static_private": hex(deviceStatic),
        "device_static_public": hexPublic(deviceStatic),
        "device_ephemeral_private": hex(deviceEphemeral),
        "receiver_static_private": hex(receiverStatic),
        "receiver_static_public": hexPublic(receiverStatic),
        "receiver_ephemeral_private": hex(receiverEphemeral),
        "handshake_hash": hex(device.handshakeHash),
        "frames": transcript.frames,
    ]
}

/// Messages a device may send, which a receiver must accept.
func validRequests() -> [[String: String]] {
    let list: [(String, String)] = [
        ("ping", #"{"t":"ping","seq":1}"#),
        ("pin", #"{"t":"put","seq":2,"kind":"pin","id":"gate-1","lat":37.5796,"lon":126.977,"title":"Palace gate"}"#),
        ("pin without title", #"{"t":"put","seq":2,"kind":"pin","id":"gate_1","lat":-33.8568,"lon":151.2153}"#),
        ("pin at the extremes", #"{"t":"put","seq":2,"kind":"pin","id":"a","lat":-90,"lon":180}"#),
        ("fields in another order", #"{"lon":2.2945,"lat":48.8584,"id":"tower","kind":"pin","seq":3,"t":"put"}"#),
        ("marker", #"{"t":"put","seq":4,"kind":"marker","id":"m1","lat":40.7484,"lon":-73.9857,"title":"Tower","color":"blue"}"#),
        ("note with line break", #"{"t":"put","seq":5,"kind":"note","id":"n1","lat":37.5663,"lon":126.9779,"text":"First line\nSecond line"}"#),
        ("non-ASCII title", #"{"t":"put","seq":6,"kind":"pin","id":"p6","lat":47.3779,"lon":8.5403,"title":"Zürich Hauptbahnhof €"}"#),
        ("move", #"{"t":"move","seq":7,"id":"gate-1","lat":37.58,"lon":126.98}"#),
        ("delete", #"{"t":"delete","seq":8,"id":"gate-1"}"#),
        ("delete all", #"{"t":"delete_all","seq":9}"#),
        ("photo begin", #"{"t":"photo_begin","seq":10,"id":"ph1","lat":37.5663,"lon":126.9779,"type":"image/jpeg","size":1000,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","chunks":1}"#),
        ("photo chunk", #"{"t":"photo_chunk","seq":11,"id":"ph1","index":0,"data":"AAEC"}"#),
        ("photo end", #"{"t":"photo_end","seq":12,"id":"ph1"}"#),
        ("track append", #"{"t":"track_append","seq":13,"id":"walk","title":"Morning walk","points":[{"lat":37.5663,"lon":126.9779,"ts":1700000000000,"alt":38.5},{"lat":37.5665,"lon":126.978}]}"#),
        ("track finish", #"{"t":"track_finish","seq":14,"id":"walk"}"#),
        ("largest safe sequence number", #"{"t":"ping","seq":9007199254740991}"#),
    ]
    return list.map { ["name": $0.0, "json": $0.1] }
}

/// Messages a receiver must reject, with the error code it must use.
func invalidRequests() -> [[String: String]] {
    let list: [(String, String, ErrorCode)] = [
        ("unknown type", #"{"t":"steal_everything","seq":1}"#, .invalidMessage),
        ("unknown field", #"{"t":"ping","seq":1,"extra":true}"#, .invalidMessage),
        ("identity field", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1,"uid":"someone-else"}"#, .invalidMessage),
        ("name field", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1,"name":"someone-else"}"#, .invalidMessage),
        ("duplicate key", #"{"t":"ping","seq":1,"seq":2}"#, .invalidMessage),
        ("trailing data", #"{"t":"ping","seq":1} x"#, .invalidMessage),
        ("not an object", #"[1,2,3]"#, .invalidMessage),
        ("sequence number zero", #"{"t":"ping","seq":0}"#, .invalidMessage),
        ("sequence number too large", #"{"t":"ping","seq":9007199254740992}"#, .invalidMessage),
        ("sequence number as a fraction", #"{"t":"ping","seq":1.5}"#, .invalidMessage),
        ("sequence number as text", #"{"t":"ping","seq":"1"}"#, .invalidMessage),
        ("NaN literal", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":NaN,"lon":1}"#, .invalidMessage),
        ("number too large to represent", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1e999,"lon":1}"#, .invalidMessage),
        ("latitude out of range", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":90.01,"lon":1}"#, .invalidCoordinate),
        ("longitude out of range", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":-180.5}"#, .invalidCoordinate),
        ("empty id", #"{"t":"delete","seq":1,"id":""}"#, .invalidID),
        ("id too long", #"{"t":"delete","seq":1,"id":"\#(String(repeating: "a", count: 65))"}"#, .invalidID),
        ("id with a slash", #"{"t":"delete","seq":1,"id":"a/b"}"#, .invalidID),
        ("non-ASCII id", #"{"t":"delete","seq":1,"id":"pïn"}"#, .invalidID),
        ("empty title", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1,"title":""}"#, .invalidText),
        ("title with a control character", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1,"title":"a\u0007b"}"#, .invalidText),
        ("title with a direction override", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1,"title":"a‮b"}"#, .invalidText),
        ("title too long", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1,"title":"\#(String(repeating: "a", count: 201))"}"#, .tooLarge),
        ("note too long", #"{"t":"put","seq":1,"kind":"note","id":"a","lat":1,"lon":1,"text":"\#(String(repeating: "a", count: 4097))"}"#, .tooLarge),
        ("put of a photo", #"{"t":"put","seq":1,"kind":"photo","id":"a","lat":1,"lon":1}"#, .invalidMessage),
        ("pin with note text", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":1,"lon":1,"text":"x"}"#, .invalidMessage),
        ("unknown colour", ##"{"t":"put","seq":1,"kind":"marker","id":"a","lat":1,"lon":1,"color":"#ff0000"}"##, .invalidMessage),
        ("photo in a forbidden format", #"{"t":"photo_begin","seq":1,"id":"a","lat":1,"lon":1,"type":"image/heic","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","chunks":1}"#, .photoInvalid),
        ("hash in uppercase", #"{"t":"photo_begin","seq":1,"id":"a","lat":1,"lon":1,"type":"image/png","size":1,"sha256":"AA00000000000000000000000000000000000000000000000000000000000000","chunks":1}"#, .invalidMessage),
        ("base64 that is not canonical", #"{"t":"photo_chunk","seq":1,"id":"a","index":0,"data":"AAF="}"#, .invalidMessage),
        ("base64 with a line break", #"{"t":"photo_chunk","seq":1,"id":"a","index":0,"data":"AA\nEC"}"#, .invalidMessage),
        ("photo larger than the limit", #"{"t":"photo_begin","seq":1,"id":"a","lat":1,"lon":1,"type":"image/png","size":1048577,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","chunks":22}"#, .tooLarge),
        ("enormous photo size", #"{"t":"photo_begin","seq":1,"id":"a","lat":1,"lon":1,"type":"image/png","size":9007199254740991,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","chunks":1}"#, .tooLarge),
        ("chunk count that does not match the size", #"{"t":"photo_begin","seq":1,"id":"a","lat":1,"lon":1,"type":"image/png","size":48001,"sha256":"0000000000000000000000000000000000000000000000000000000000000000","chunks":1}"#, .invalidMessage),
        ("empty photo chunk", #"{"t":"photo_chunk","seq":1,"id":"a","index":0,"data":""}"#, .invalidMessage),
        ("coordinate as a very long integer", #"{"t":"put","seq":1,"kind":"pin","id":"a","lat":99999999999999999999,"lon":1}"#, .invalidCoordinate),
        ("sequence number as a very long integer", #"{"t":"ping","seq":99999999999999999999}"#, .invalidMessage),
        ("empty track append", #"{"t":"track_append","seq":1,"id":"a","points":[]}"#, .invalidMessage),
        ("unknown field in a point", #"{"t":"track_append","seq":1,"id":"a","points":[{"lat":1,"lon":1,"speed":3}]}"#, .invalidMessage),
        ("nesting too deep", #"{"t":"ping","seq":1,"a":[[[[[[[[[[1]]]]]]]]]]}"#, .invalidMessage),
    ]
    return list.map { ["name": $0.0, "json": $0.1, "code": $0.2.rawValue] }
}

/// Byte sequences that are not valid UTF-8, as hexadecimal.
func invalidBytes() -> [[String: String]] {
    let list: [(String, Data)] = [
        ("byte order mark", Data([0xEF, 0xBB, 0xBF]) + Data(#"{"t":"ping","seq":1}"#.utf8)),
        ("overlong encoding", Data(#"{"t":"ping","seq":1,"a":""#.utf8) + Data([0xC0, 0xAF]) + Data(#""}"#.utf8)),
        ("encoded surrogate", Data(#"{"t":"ping","seq":1,"a":""#.utf8) + Data([0xED, 0xA0, 0x80]) + Data(#""}"#.utf8)),
        ("above U+10FFFF", Data(#"{"t":"ping","seq":1,"a":""#.utf8) + Data([0xF4, 0x90, 0x80, 0x80]) + Data(#""}"#.utf8)),
        ("unpaired surrogate escape", Data(#"{"t":"ping","seq":1,"a":"\ud800"}"#.utf8)),
    ]
    return list.map { ["name": $0.0, "hex": $0.1.hexString, "code": ErrorCode.invalidMessage.rawValue] }
}

func checkMessages() throws {
    for entry in validRequests() {
        _ = try Wire.decodeRequest(Data(entry["json"]!.utf8))
    }
    for entry in invalidRequests() {
        do {
            _ = try Wire.decodeRequest(Data(entry["json"]!.utf8))
            fatalError("accepted: \(entry["name"]!)")
        } catch let error as WireError {
            precondition(error.code.rawValue == entry["code"]!, "wrong code for \(entry["name"]!): \(error.code.rawValue)")
        }
    }
    for entry in invalidBytes() {
        do {
            _ = try Wire.decodeRequest(Data(hexString: entry["hex"]!)!)
            fatalError("accepted: \(entry["name"]!)")
        } catch let error as WireError {
            precondition(error.code.rawValue == entry["code"]!, "wrong code for \(entry["name"]!)")
        }
    }
}

guard CommandLine.arguments.count == 2 else {
    print("usage: erdigo-vectors <output folder>")
    exit(2)
}
let folder = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try checkMessages()
try write(try connectVector(), to: folder.appendingPathComponent("handshake-connect.json"))
try write(try pairVector(), to: folder.appendingPathComponent("handshake-pair.json"))
try write([
    "description": "Requests a receiver must accept and must reject. 'code' is the error it must report.",
    "valid": validRequests(),
    "invalid": invalidRequests(),
    "invalid_bytes": invalidBytes(),
] as [String: Any], to: folder.appendingPathComponent("messages.json"))
print("wrote vectors to \(folder.lastPathComponent)")
