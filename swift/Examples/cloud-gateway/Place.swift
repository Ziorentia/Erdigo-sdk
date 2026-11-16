import Darwin
import ErdigoSDK
import Foundation

/// The gateway's place in a relay's queue, kept as relay-state.json next to the pairing.
struct Place: Equatable {
    var relay: String
    var epoch: String?
    var after: UInt64

    static let fileName = "relay-state.json"

    struct Unreadable: Error, LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    /// nil when there is no file yet. Like the SDK, it refuses a file that other users could read or replace.
    static func load(from directory: URL) throws -> Place? {
        let path = directory.appendingPathComponent(fileName).path
        let descriptor = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC)
        if descriptor < 0 {
            if errno == ENOENT { return nil }
            throw Unreadable(message: "\(fileName) could not be opened")
        }
        defer { close(descriptor) }
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & S_IFMT == S_IFREG, info.st_uid == getuid(), info.st_mode & 0o077 == 0 else {
            throw Unreadable(message: "\(fileName) must be a regular file owned by you with mode 0600")
        }
        guard info.st_size <= 64 * 1024 else { throw Unreadable(message: "\(fileName) is unexpectedly large") }
        let data = FileHandle(fileDescriptor: descriptor, closeOnDealloc: false).readDataToEndOfFile()
        let broken = Unreadable(message: "\(fileName) could not be read. Remove it to start again from the relay's oldest operation.")
        guard case .object(let object)? = try? StrictJSON.parse(data), Set(object.keys).isSubset(of: ["relay", "epoch", "after"]),
              case .string(let relay)? = object["relay"], case .integer(let after)? = object["after"], after >= 0
        else { throw broken }
        var epoch: String?
        if let value = object["epoch"] {
            guard case .string(let text) = value, RelayClient.isValidEpoch(text) else { throw broken }
            epoch = text
        }
        return Place(relay: relay, epoch: epoch, after: UInt64(after))
    }

    /// Writes a temporary file and renames it over the old one, so the file is never half written.
    func save(in directory: URL) throws {
        var fields: [(String, JSONWriter.Value)] = [("relay", .string(relay))]
        if let epoch { fields.append(("epoch", .string(epoch))) }
        fields.append(("after", .integer(Int64(after))))
        let data = JSONWriter.encode(.object(fields))

        let target = directory.appendingPathComponent(Self.fileName).path
        let temporary = directory.appendingPathComponent(".\(Self.fileName).\(UUID().uuidString)").path
        let descriptor = open(temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else { throw Unreadable(message: "\(Self.fileName) could not be written") }
        var succeeded = false
        defer {
            close(descriptor)
            if !succeeded { unlink(temporary) }
        }
        let written = data.withUnsafeBytes { write(descriptor, $0.baseAddress, $0.count) }
        guard written == data.count, fsync(descriptor) == 0, rename(temporary, target) == 0 else {
            throw Unreadable(message: "\(Self.fileName) could not be written")
        }
        succeeded = true
    }
}
