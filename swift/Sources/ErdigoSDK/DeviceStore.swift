import Darwin
import Foundation
import Security

/// Persistent device state: its key, its pairings and the per-receiver connect counter.
public protocol DeviceStore: Sendable {
    func loadOrCreateIdentity() throws -> DeviceIdentity
    func pairings() throws -> [ReceiverPairing]
    /// Adds a pairing, or replaces the one for the same receiver. Its counter floor takes effect here.
    func save(_ pairing: ReceiverPairing) throws
    /// Remembers where a receiver was found. Does nothing if the pairing has since been forgotten.
    func updateAddress(receiverFingerprint: String, host: String, port: UInt16) throws
    func removePairing(fingerprint: String) throws
    /// Returns a value strictly greater than any previous result for this receiver. The value
    /// must be durable before it is returned, because a receiver ignores a counter it has seen.
    func nextConnectCounter(receiverFingerprint: String, nowMS: UInt64) throws -> UInt64
}

/// Keeps everything in memory. Intended for tests and short-lived programs.
public final class InMemoryDeviceStore: DeviceStore, @unchecked Sendable {
    private let lock = NSLock()
    private var identity: DeviceIdentity?
    private var stored: [ReceiverPairing] = []
    private var counters: [String: UInt64] = [:]

    public init(identity: DeviceIdentity? = nil) {
        self.identity = identity
    }

    public func loadOrCreateIdentity() throws -> DeviceIdentity {
        lock.lock()
        defer { lock.unlock() }
        if let identity { return identity }
        let created = DeviceIdentity.generate()
        identity = created
        return created
    }

    public func pairings() throws -> [ReceiverPairing] {
        lock.lock()
        defer { lock.unlock() }
        return stored
    }

    public func save(_ pairing: ReceiverPairing) throws {
        lock.lock()
        defer { lock.unlock() }
        stored.removeAll { $0.receiverKey == pairing.receiverKey }
        stored.append(pairing)
        counters[pairing.fingerprint] = max(counters[pairing.fingerprint] ?? 0, pairing.counterFloor)
    }

    public func updateAddress(receiverFingerprint: String, host: String, port: UInt16) throws {
        lock.lock()
        defer { lock.unlock() }
        guard let index = stored.firstIndex(where: { $0.fingerprint == receiverFingerprint }) else { return }
        stored[index].lastHost = host
        stored[index].lastPort = port
    }

    public func removePairing(fingerprint: String) throws {
        lock.lock()
        defer { lock.unlock() }
        stored.removeAll { $0.fingerprint == fingerprint }
        counters[fingerprint] = nil
    }

    public func nextConnectCounter(receiverFingerprint: String, nowMS: UInt64) throws -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        let next = max(nowMS, (counters[receiverFingerprint] ?? 0) + 1)
        counters[receiverFingerprint] = next
        return next
    }
}

/// Stores state in a private directory. Refuses to read files that other users could read or
/// replace, the same way `ssh` treats its key files.
public final class FileDeviceStore: DeviceStore, @unchecked Sendable {
    public enum KeyLocation: Sendable {
        /// A 0600 file in the store's directory. Works without a login session.
        case file
        /// The login keychain. The keychain may ask for confirmation after the program is rebuilt.
        case keychain(service: String, account: String)
    }

    public let directory: URL
    private let keyLocation: KeyLocation
    private let lock = NSLock()

    private struct State: Codable {
        var pairings: [ReceiverPairing] = []
        var counters: [String: UInt64] = [:]
    }

    private struct StoredKey: Codable {
        var version = 1
        var privateKey: Data
    }

    public static func defaultDirectory(profile: String = "default") throws -> URL {
        guard let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else {
            throw ErdigoError.storage("the Application Support folder could not be found")
        }
        return base.appendingPathComponent("ErdigoSDK", isDirectory: true).appendingPathComponent(profile, isDirectory: true)
    }

    public init(directory: URL, keyLocation: KeyLocation = .file) throws {
        self.directory = directory
        self.keyLocation = keyLocation
        try createPrivateDirectory()
    }

    private func createPrivateDirectory() throws {
        // The parent folders are created with default permissions; only the store's own folder must be private.
        try? FileManager.default.createDirectory(at: directory.deletingLastPathComponent(), withIntermediateDirectories: true)
        if mkdir(directory.path, 0o700) != 0, errno != EEXIST {
            throw ErdigoError.storage("could not create the state folder")
        }
        var info = stat()
        guard lstat(directory.path, &info) == 0 else { throw ErdigoError.storage("could not inspect the state folder") }
        guard info.st_mode & S_IFMT == S_IFDIR, info.st_uid == getuid(), info.st_mode & 0o077 == 0 else {
            throw ErdigoError.storage("the state folder must be a real folder owned by you with mode 0700")
        }
    }

    private func path(_ name: String) -> String {
        directory.appendingPathComponent(name).path
    }

    private func readPrivateFile(_ name: String) throws -> Data? {
        let descriptor = open(path(name), O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        if descriptor < 0 {
            if errno == ENOENT { return nil }
            throw ErdigoError.storage("could not open \(name)")
        }
        defer { close(descriptor) }
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_uid == getuid(), info.st_mode & 0o077 == 0 else {
            throw ErdigoError.storage("\(name) must be a regular file owned by you with mode 0600")
        }
        guard info.st_size <= 4 * 1024 * 1024 else { throw ErdigoError.storage("\(name) is unexpectedly large") }
        return FileHandle(fileDescriptor: descriptor, closeOnDealloc: false).readDataToEndOfFile()
    }

    /// Writes through a temporary file and renames it, so a crash never leaves a partial file.
    private func writePrivateFile(_ name: String, _ data: Data) throws {
        let temporary = path(".\(name).\(UUID().uuidString)")
        let descriptor = open(temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else { throw ErdigoError.storage("could not write \(name)") }
        var succeeded = false
        defer {
            close(descriptor)
            if !succeeded { unlink(temporary) }
        }
        var offset = 0
        try data.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) in
            while offset < buffer.count {
                let written = write(descriptor, buffer.baseAddress! + offset, buffer.count - offset)
                if written < 0 {
                    if errno == EINTR { continue }
                    throw ErdigoError.storage("could not write \(name)")
                }
                offset += written
            }
        }
        guard fsync(descriptor) == 0, rename(temporary, path(name)) == 0 else {
            throw ErdigoError.storage("could not write \(name)")
        }
        succeeded = true
    }

    /// Serialises access between threads and between processes.
    private func withLock<T>(_ body: () throws -> T) throws -> T {
        lock.lock()
        defer { lock.unlock() }
        let descriptor = open(path(".lock"), O_RDWR | O_CREAT | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else { throw ErdigoError.storage("could not lock the state folder") }
        defer { close(descriptor) }
        guard flock(descriptor, LOCK_EX) == 0 else { throw ErdigoError.storage("could not lock the state folder") }
        defer { flock(descriptor, LOCK_UN) }
        return try body()
    }

    private func loadState() throws -> State {
        guard let data = try readPrivateFile("pairings.json") else { return State() }
        do {
            return try JSONDecoder().decode(State.self, from: data)
        } catch {
            throw ErdigoError.storage("pairings.json could not be read")
        }
    }

    private func saveState(_ state: State) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .prettyPrinted]
        try writePrivateFile("pairings.json", try encoder.encode(state))
    }

    public func loadOrCreateIdentity() throws -> DeviceIdentity {
        try withLock {
            switch keyLocation {
            case .file:
                if let data = try readPrivateFile("identity.json") {
                    do {
                        let stored = try JSONDecoder().decode(StoredKey.self, from: data)
                        return try DeviceIdentity(rawPrivateKey: stored.privateKey)
                    } catch {
                        throw ErdigoError.storage("identity.json could not be read")
                    }
                }
                let created = DeviceIdentity.generate()
                let stored = StoredKey(privateKey: created.rawPrivateKeyForStorage())
                try writePrivateFile("identity.json", try JSONEncoder().encode(stored))
                return created
            case .keychain(let service, let account):
                if let raw = try KeychainKey.load(service: service, account: account) {
                    return try DeviceIdentity(rawPrivateKey: raw)
                }
                let created = DeviceIdentity.generate()
                try KeychainKey.save(created.rawPrivateKeyForStorage(), service: service, account: account)
                return created
            }
        }
    }

    public func pairings() throws -> [ReceiverPairing] {
        try withLock { try loadState().pairings }
    }

    public func save(_ pairing: ReceiverPairing) throws {
        try withLock {
            var state = try loadState()
            state.pairings.removeAll { $0.receiverKey == pairing.receiverKey }
            state.pairings.append(pairing)
            state.counters[pairing.fingerprint] = max(state.counters[pairing.fingerprint] ?? 0, pairing.counterFloor)
            try saveState(state)
        }
    }

    public func updateAddress(receiverFingerprint: String, host: String, port: UInt16) throws {
        try withLock {
            var state = try loadState()
            guard let index = state.pairings.firstIndex(where: { $0.fingerprint == receiverFingerprint }) else { return }
            state.pairings[index].lastHost = host
            state.pairings[index].lastPort = port
            try saveState(state)
        }
    }

    public func removePairing(fingerprint: String) throws {
        try withLock {
            var state = try loadState()
            state.pairings.removeAll { $0.fingerprint == fingerprint }
            state.counters[fingerprint] = nil
            try saveState(state)
        }
    }

    public func nextConnectCounter(receiverFingerprint: String, nowMS: UInt64) throws -> UInt64 {
        try withLock {
            var state = try loadState()
            let next = max(nowMS, (state.counters[receiverFingerprint] ?? 0) + 1)
            state.counters[receiverFingerprint] = next
            try saveState(state)
            return next
        }
    }
}

enum KeychainKey {
    private static func query(service: String, account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    static func load(service: String, account: String) throws -> Data? {
        var request = query(service: service, account: account)
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data else {
            throw ErdigoError.storage("the keychain could not be read (status \(status))")
        }
        return data
    }

    static func save(_ data: Data, service: String, account: String) throws {
        var item = query(service: service, account: account)
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        let status = SecItemAdd(item as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw ErdigoError.storage("the keychain could not be written (status \(status))")
        }
    }
}
