using System;
using System.IO;
using System.Threading;
using Erdigo.Core;
using Erdigo.Sdk;
using Erdigo.Send;

namespace Erdigo.Examples.CloudGateway;

/// <summary>Where the gateway is in one relay's numbering: the epoch, and the last operation it finished with.</summary>
internal sealed record RelayPlace(string Epoch, long Seq);

/// <summary>
/// <c>relay-state.json</c> in the profile's folder: the relay's address, its epoch and the last finished sequence
/// number. It is replaced whole through a temporary file and a rename, so it is never half written, and it is
/// readable by the current user alone, like the SDK's own files.
/// </summary>
internal static class RelayState
{
    public const string FileName = "relay-state.json";

    /// <summary>The place kept for <paramref name="relay"/>, or null if there is none, or it is for another relay.</summary>
    public static RelayPlace? Load(string directory, RelayAddress relay)
    {
        byte[] data;
        try
        {
            data = PrivateFile.ReadAllBytes(Path.Combine(directory, FileName), 64 * 1024);
        }
        catch (PrivateFileException error) when (error.Problem == PrivateFileProblem.Missing)
        {
            return null;
        }
        catch (PrivateFileException error) when (error.Problem == PrivateFileProblem.Insecure)
        {
            throw new UsageException(OperatingSystem.IsWindows()
                ? $"{FileName} must be a regular file that only you can read. Nothing was changed."
                : $"{FileName} must be a regular file you own, with mode 0600. Nothing was changed.");
        }
        catch (PrivateFileException)
        {
            throw new UsageException($"{FileName} could not be read. Nothing was changed.");
        }

        try
        {
            if (StrictJson.Parse(data) is JsonValue.Object { Fields: var fields } && fields.Count == 3
                && fields.TryGetValue("relay", out var address) && address is JsonValue.String { Value: var saved }
                && fields.TryGetValue("epoch", out var epochValue) && epochValue is JsonValue.String { Value: var epoch } && RelayAnswer.IsEpoch(epoch)
                && fields.TryGetValue("seq", out var seqValue) && seqValue is JsonValue.Integer { Value: var seq } && seq >= 0)
            {
                return string.Equals(saved, relay.Key, StringComparison.Ordinal) ? new RelayPlace(epoch, seq) : null;
            }
        }
        catch (JsonParseException)
        {
        }
        throw new UsageException($"{FileName} is damaged. Move it away to start again from the relay's oldest operation.");
    }

    public static void Save(string directory, RelayAddress relay, RelayPlace place)
    {
        var data = JsonWriter.Encode(JsonWriter.Value.Object(new[]
        {
            ("relay", JsonWriter.Value.Of(relay.Key)),
            ("epoch", JsonWriter.Value.Of(place.Epoch)),
            ("seq", JsonWriter.Value.Of(place.Seq)),
        }));
        var path = Path.Combine(directory, FileName);
        var temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}");
        var succeeded = false;
        try
        {
            // In the SDK's private folder a new file takes the folder's access list on Windows. Elsewhere it is made 0600.
            using (var stream = OperatingSystem.IsWindows()
                ? new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)
                : new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (IOException) when (OperatingSystem.IsWindows() && attempt < 20)
                {
                    // A virus scanner or an indexer can hold a file open for a moment.
                    Thread.Sleep(50);
                }
            }
            succeeded = true;
        }
        finally
        {
            if (!succeeded)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
