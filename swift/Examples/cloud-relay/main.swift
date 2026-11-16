// An example cloud relay for Erdigo, as docs/cloud-relay.md describes it. Devices send operations to it
// with a source token, and a gateway on the app's local network fetches them with the gateway token.
//
//     ERDIGO_RELAY_SOURCE_TOKENS=... ERDIGO_RELAY_GATEWAY_TOKEN=... swift run erdigo-example-relay --listen 127.0.0.1:8080
//
// It speaks plain HTTP and keeps operations in memory, so put an HTTPS reverse proxy in front of it.
// It uses Network.framework and runs on macOS only. A Linux server can run another edition's relay,
// and the Swift gateway works with any of them.

import Darwin
import Foundation

private let usage = """
usage: erdigo-example-relay [--listen HOST:PORT] [--keep-hours HOURS] [--behind-proxy]

  --listen HOST:PORT  Where to accept plain HTTP. 127.0.0.1:8080 by default. Port 0 picks a free port.
                      Only 127.0.0.1, [::1] or localhost, unless --behind-proxy is given.
  --keep-hours HOURS  How long an operation waits for the gateway: 1 to 168, 24 by default.
  --behind-proxy      HTTPS ends in a proxy in front of the relay, so it may listen on another address.

Tokens come from the environment, never from the command line:
  ERDIGO_RELAY_SOURCE_TOKENS  One or more source tokens, separated by commas.
  ERDIGO_RELAY_GATEWAY_TOKEN  The gateway token, different from every source token.
A token is 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.
"""

private func stop(_ message: String) -> Never {
    fputs("erdigo-example-relay: \(message)\n", stderr)
    exit(1)
}

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

/// Splits HOST:PORT. An IPv6 address goes in brackets, as in a URL.
private func parseListen(_ text: String) -> (host: String, port: UInt16)? {
    let host: Substring
    let port: Substring
    if text.hasPrefix("[") {
        guard let close = text.range(of: "]:") else { return nil }
        host = text[text.index(after: text.startIndex)..<close.lowerBound]
        port = text[close.upperBound...]
    } else {
        guard let colon = text.lastIndex(of: ":") else { return nil }
        host = text[..<colon]
        port = text[text.index(after: colon)...]
        guard !host.contains(":") else { return nil }
    }
    guard !host.isEmpty, !port.isEmpty, port.utf8.allSatisfy({ (UInt8(ascii: "0")...UInt8(ascii: "9")).contains($0) }),
          let number = UInt16(port)
    else { return nil }
    return (String(host), number)
}

setvbuf(stdout, nil, _IOLBF, 0)
var arguments = Array(CommandLine.arguments.dropFirst())
if let first = arguments.first, ["help", "--help", "-h"].contains(first) {
    print(usage)
    exit(0)
}

var listen = (host: "127.0.0.1", port: UInt16(8080))
var keepHours = 24
var behindProxy = false
var given = Set<String>()
while !arguments.isEmpty {
    let option = arguments.removeFirst()
    guard ["--listen", "--keep-hours", "--behind-proxy"].contains(option) else {
        stop("an unknown argument was given. See 'erdigo-example-relay help'.")
    }
    guard given.insert(option).inserted else { stop("\(option) was given twice") }
    if option == "--behind-proxy" {
        behindProxy = true
        continue
    }
    guard !arguments.isEmpty else { stop("\(option) needs a value") }
    let value = arguments.removeFirst()
    if option == "--listen" {
        guard let parsed = parseListen(value) else { stop("--listen takes HOST:PORT, with an IPv6 address in brackets") }
        listen = parsed
    } else {
        guard let hours = Int(value), (1...168).contains(hours) else { stop("--keep-hours must be a whole number from 1 to 168") }
        keepHours = hours
    }
}

let isLoopback = ["127.0.0.1", "::1"].contains(listen.host) || listen.host.lowercased() == "localhost"
if !isLoopback, !behindProxy {
    stop("--listen accepts only 127.0.0.1, [::1] or localhost, because the relay speaks plain HTTP. Put an HTTPS proxy in front of it and add --behind-proxy to listen elsewhere.")
}
// One address, so that the address printed is the one that works.
let bindHost = listen.host.lowercased() == "localhost" ? "127.0.0.1" : listen.host

let environment = ProcessInfo.processInfo.environment
// Never repeat a token in a message: they are secrets, and messages end up in logs.
guard let sourceList = environment["ERDIGO_RELAY_SOURCE_TOKENS"], !sourceList.isEmpty else {
    stop("ERDIGO_RELAY_SOURCE_TOKENS is not set. Give one or more source tokens, separated by commas.")
}
let sourceTokens = sourceList.split(separator: ",", omittingEmptySubsequences: false).map { $0.trimmingCharacters(in: .whitespaces) }
guard sourceTokens.allSatisfy(isValidToken) else {
    stop("a token in ERDIGO_RELAY_SOURCE_TOKENS is not 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'")
}
guard let gatewayToken = environment["ERDIGO_RELAY_GATEWAY_TOKEN"], !gatewayToken.isEmpty else {
    stop("ERDIGO_RELAY_GATEWAY_TOKEN is not set")
}
guard isValidToken(gatewayToken) else {
    stop("ERDIGO_RELAY_GATEWAY_TOKEN is not 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'")
}
guard !sourceTokens.contains(gatewayToken) else {
    stop("the gateway token must differ from every source token")
}

var limits = RelayQueue.Limits()
limits.keepSeconds = Double(keepHours) * 3600
// Only for this example's own tests, to reach the limits and the expiry quickly. Not a user option.
if let text = environment["ERDIGO_RELAY_TEST_MAX_OPS"], let value = Int(text), value > 0 { limits.maxOperations = value }
if let text = environment["ERDIGO_RELAY_TEST_MAX_BYTES"], let value = Int(text), value > 0 { limits.maxBytes = value }
if let text = environment["ERDIGO_RELAY_TEST_KEEP_SECONDS"], let value = Double(text), value > 0 { limits.keepSeconds = value }

let server = RelayServer(sourceTokens: sourceTokens, gatewayToken: gatewayToken, limits: limits)

signal(SIGINT, SIG_IGN)
signal(SIGTERM, SIG_IGN)
let stopSignals = [SIGINT, SIGTERM].map { number -> DispatchSourceSignal in
    let source = DispatchSource.makeSignalSource(signal: number, queue: server.queue)
    source.setEventHandler {
        server.stop()
        print("Relay stopped.")
        exit(0)
    }
    source.resume()
    return source
}

do {
    try server.start(host: bindHost, port: listen.port) { result in
        switch result {
        case .success(let port):
            let shown = bindHost.contains(":") ? "[\(bindHost)]" : bindHost
            print("Relay listening on http://\(shown):\(port)")
            fflush(stdout)
        case .failure:
            stop("could not listen on that address. Is the port in use?")
        }
    }
} catch {
    stop("could not listen on that address")
}
dispatchMain()
