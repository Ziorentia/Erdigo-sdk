using System;
using System.IO;
using System.Text;
using Erdigo.Core;

namespace Erdigo.Send;

/// <summary>
/// Reads the note and photo files named on the command line. Their size is not trusted: nothing past the limit is
/// read, so a huge file, or one that keeps growing while it is read, costs no more memory than the largest file that
/// is allowed, and is refused before any connection is made.
/// </summary>
internal static class CommandFiles
{
    public static string NoteTooLarge => $"a note is at most {Limits.V1.NoteBytes} bytes of UTF-8";

    public static string PhotoTooLarge => $"a photo is at most {Limits.V1.PhotoBytes} bytes";

    /// <summary>
    /// A note file holds the note as UTF-8. Two things editors add are taken out, so that a note written in Notepad
    /// can be sent as it is: a byte order mark at the start, and the carriage return of each Windows line ending
    /// (CR LF becomes LF). Nothing else is changed: a trailing newline stays, and a carriage return on its own is
    /// left for the note rules to refuse. The limit applies to the note that is sent, not to the file.
    /// </summary>
    public static string ReadNote(string path)
    {
        // Every line feed may come with a carriage return, and a byte order mark is three bytes.
        var data = ReadAtMost(path, (Limits.V1.NoteBytes * 2) + 3, "note", NoteTooLarge);
        string text;
        try
        {
            text = Text.StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new UsageException("the note file is not UTF-8 text");
        }
        text = NoteFromFile(text);
        if (Encoding.UTF8.GetByteCount(text) > Limits.V1.NoteBytes)
        {
            throw new UsageException(NoteTooLarge);
        }
        return text;
    }

    /// <summary>The note in a file's text: without a leading byte order mark, and with CR LF turned into LF.</summary>
    internal static string NoteFromFile(string text)
    {
        if (text.StartsWith('﻿'))
        {
            text = text[1..];
        }
        return text.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>The format is checked by the SDK when the photo is sent, before it connects.</summary>
    public static byte[] ReadPhoto(string path) => ReadAtMost(path, Limits.V1.PhotoBytes, "photo", PhotoTooLarge);

    internal static byte[] ReadAtMost(string path, int maxBytes, string what, string tooLarge)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new UsageException($"the {what} file does not exist");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new UsageException($"could not open the {what} file");
        }
        using (stream)
        {
            try
            {
                return ReadAtMost(stream, maxBytes, tooLarge);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new UsageException($"could not read the {what} file");
            }
        }
    }

    /// <summary>
    /// Reads what is left of <paramref name="stream"/>, refusing it with <paramref name="tooLarge"/> if that is more
    /// than <paramref name="maxBytes"/>. A stream that gives its length is refused by that alone. Otherwise, or if it
    /// grows past what it said, the first byte over the limit settles it, so at most one byte more is ever read and
    /// the buffer never holds more than the limit.
    /// </summary>
    internal static byte[] ReadAtMost(Stream stream, int maxBytes, string tooLarge)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        long? remaining = null;
        if (stream.CanSeek)
        {
            remaining = Math.Max(0, stream.Length - stream.Position);
            if (remaining > maxBytes)
            {
                throw new UsageException(tooLarge);
            }
        }

        // Room for what the stream says it holds, or a modest start when it does not say. The buffer grows only while
        // data keeps coming, and never past the limit.
        var buffer = new byte[remaining is { } known ? (int)known : Math.Min(maxBytes, 64 * 1024)];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                // Full: see whether there is more before making room for it.
                var next = stream.ReadByte();
                if (next == -1)
                {
                    return buffer;
                }
                if (filled == maxBytes)
                {
                    throw new UsageException(tooLarge);
                }
                Array.Resize(ref buffer, (int)Math.Min(maxBytes, Math.Max(16L, (long)buffer.Length * 2)));
                buffer[filled++] = (byte)next;
                continue;
            }
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
            {
                return buffer.AsSpan(0, filled).ToArray();
            }
            filled += read;
        }
    }
}
