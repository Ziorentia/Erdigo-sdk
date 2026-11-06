import Foundation
import Network

/// Where to look for a receiver.
public enum ReceiverTarget: Equatable, Sendable {
    /// Try this address only.
    case address(host: String, port: UInt16)
    /// Try the remembered address, then up to eight receivers advertised on the local network.
    /// A receiver this device is not paired with stays silent. An advertisement proves nothing about
    /// who is behind it, so use `.address` where that matters. See the threat model.
    case discover
}

enum Discovery {
    /// Listens for receivers for a short time. Requires Local Network permission on macOS.
    static func browse(duration: TimeInterval = 2, limit: Int = 8) async -> [NWEndpoint] {
        final class Collector: @unchecked Sendable {
            private let lock = NSLock()
            private var finished = false
            private var endpoints: [NWEndpoint] = []
            private let continuation: CheckedContinuation<[NWEndpoint], Never>
            init(_ continuation: CheckedContinuation<[NWEndpoint], Never>) { self.continuation = continuation }
            func update(_ found: [NWEndpoint]) {
                lock.lock()
                endpoints = found
                lock.unlock()
            }
            func finish() {
                lock.lock()
                let alreadyFinished = finished
                finished = true
                let result = endpoints
                lock.unlock()
                if !alreadyFinished { continuation.resume(returning: result) }
            }
        }

        let queue = DispatchQueue(label: "erdigo.discovery")
        let browser = NWBrowser(for: .bonjour(type: IntegrationV1.serviceType, domain: nil), using: .tcp)
        return await withCheckedContinuation { continuation in
            let collector = Collector(continuation)
            browser.browseResultsChangedHandler = { results, _ in
                collector.update(Array(results.map(\.endpoint).prefix(limit)))
            }
            browser.stateUpdateHandler = { state in
                if case .failed = state { collector.finish() }
            }
            browser.start(queue: queue)
            queue.asyncAfter(deadline: .now() + duration) {
                browser.cancel()
                collector.finish()
            }
        }
    }

    /// The address given or remembered, which can be tried without looking around the network.
    static func knownEndpoints(target: ReceiverTarget, pairing: ReceiverPairing?) throws -> [NWEndpoint] {
        switch target {
        case .address(let host, let port):
            return [try endpoint(host: host, port: port)]
        case .discover:
            guard let pairing, let host = pairing.lastHost, let port = pairing.lastPort else { return [] }
            return [try endpoint(host: host, port: port)]
        }
    }

    /// Every endpoint to try when pairing, where no address is remembered.
    static func candidates(target: ReceiverTarget) async throws -> [NWEndpoint] {
        let known = try knownEndpoints(target: target, pairing: nil)
        return target == .discover ? await browse() : known
    }

    static func endpoint(host: String, port: UInt16) throws -> NWEndpoint {
        guard let nwPort = NWEndpoint.Port(rawValue: port), port != 0 else {
            throw ErdigoError.invalidArgument("the port must be between 1 and 65535")
        }
        return .hostPort(host: NWEndpoint.Host(host), port: nwPort)
    }
}
