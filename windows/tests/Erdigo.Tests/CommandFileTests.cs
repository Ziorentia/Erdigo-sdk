using System;
using System.IO;
using System.Linq;
using Erdigo.Core;
using Xunit;
using CommandFiles = Erdigo.Send.CommandFiles;
using UsageException = Erdigo.Send.UsageException;

namespace Erdigo.Tests;

/// <summary>How the command-line tool reads the note and photo files it is given.</summary>
public sealed class CommandFileTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"erdigo-files-{Guid.NewGuid():N}");

    public CommandFileTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string File(string name, byte[] data)
    {
        var path = Path.Combine(directory, name);
        System.IO.File.WriteAllBytes(path, data);
        return path;
    }

    /// <summary>A file this long, without writing its contents, so that a large one costs nothing to make.</summary>
    private string LongFile(string name, long length)
    {
        var path = Path.Combine(directory, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(length);
        return path;
    }

    private static void Refused(string expected, Action body)
    {
        var error = Assert.Throws<UsageException>(body);
        Assert.Equal(expected, error.Message);
    }

    // Notes

    [Fact]
    public void NoteAtTheLimitIsRead()
    {
        var text = new string('a', Limits.V1.NoteBytes - 3) + "é\n";
        Assert.Equal(Limits.V1.NoteBytes, System.Text.Encoding.UTF8.GetByteCount(text));
        Assert.Equal(text, CommandFiles.ReadNote(File("note.txt", System.Text.Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public void NoteOneByteOverTheLimitIsRefused()
    {
        var path = File("note.txt", Enumerable.Repeat((byte)'a', Limits.V1.NoteBytes + 1).ToArray());
        Refused(CommandFiles.NoteTooLarge, () => CommandFiles.ReadNote(path));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF })]
    [InlineData(new byte[] { 0x61, 0xC3 })]
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]
    [InlineData(new byte[] { 0xC0, 0xAF })]
    public void NoteThatIsNotUtf8IsRefused(byte[] data)
    {
        var path = File("note.txt", data);
        Refused("the note file is not UTF-8 text", () => CommandFiles.ReadNote(path));
    }

    [Fact]
    public void MissingFilesAreReportedWithoutTheirPath()
    {
        var path = Path.Combine(directory, "secret-name", "missing.txt");
        var note = Assert.Throws<UsageException>(() => CommandFiles.ReadNote(path));
        Assert.Equal("the note file does not exist", note.Message);
        Refused("the photo file does not exist", () => CommandFiles.ReadPhoto(Path.Combine(directory, "missing.jpg")));
    }

    [Fact]
    public void AFolderIsNotAFile()
    {
        Refused("could not open the note file", () => CommandFiles.ReadNote(directory));
    }

    // Photos

    [Fact]
    public void PhotoAtTheLimitIsRead()
    {
        var data = new byte[Limits.V1.PhotoBytes];
        new Random(7).NextBytes(data);
        Assert.Equal(data, CommandFiles.ReadPhoto(File("photo.jpg", data)));
    }

    [Fact]
    public void PhotoOneByteOverTheLimitIsRefused()
    {
        var path = File("photo.jpg", new byte[Limits.V1.PhotoBytes + 1]);
        Refused(CommandFiles.PhotoTooLarge, () => CommandFiles.ReadPhoto(path));
    }

    [Fact]
    public void LargeFilesAreRefusedWithoutReadingThem()
    {
        var note = LongFile("note.txt", 64L * 1024 * 1024);
        var photo = LongFile("photo.jpg", 64L * 1024 * 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Refused(CommandFiles.NoteTooLarge, () => CommandFiles.ReadNote(note));
        Refused(CommandFiles.PhotoTooLarge, () => CommandFiles.ReadPhoto(photo));
        // Opening the files and making the errors, but no buffer for their contents.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256 * 1024);
    }

    // Input that is longer than it says, or has no length

    /// <summary>A stream that claims a length, then keeps giving bytes past it, as a file being written to does.</summary>
    private sealed class GrowingStream : Stream
    {
        private readonly long claimed;

        public GrowingStream(long claimed, bool saysItsLength)
        {
            this.claimed = claimed;
            CanSeek = saysItsLength;
        }

        public long Given { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek { get; }

        public override bool CanWrite => false;

        public override long Length => CanSeek ? claimed : throw new NotSupportedException();

        public override long Position
        {
            get => CanSeek ? 0 : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            // A little at a time, as a pipe gives it.
            var given = Math.Min(count, 1000);
            buffer.AsSpan(offset, given).Fill((byte)'a');
            Given += given;
            return given;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(true, 10L)]
    [InlineData(true, 4096L)]
    [InlineData(false, 0L)]
    public void InputThatKeepsComingIsCutOffJustPastTheLimit(bool saysItsLength, long claimed)
    {
        var stream = new GrowingStream(claimed, saysItsLength);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Refused("too large", () => CommandFiles.ReadAtMost(stream, 4096, "too large"));
        // It read at most one chunk past the limit, and never held more than the limit allows.
        Assert.InRange(stream.Given, 4097, 4096 + 1000);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 128 * 1024);
    }

    [Fact]
    public void PhotoThatKeepsComingCostsNoMoreThanTheLimit()
    {
        var stream = new GrowingStream(0, saysItsLength: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Refused(CommandFiles.PhotoTooLarge, () => CommandFiles.ReadAtMost(stream, Limits.V1.PhotoBytes, CommandFiles.PhotoTooLarge));
        Assert.InRange(stream.Given, Limits.V1.PhotoBytes + 1, Limits.V1.PhotoBytes + 1000);
        // The buffer doubles on its way to the limit, so all of it together is under three times the limit.
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 3L * Limits.V1.PhotoBytes);
    }

    [Fact]
    public void InputShorterThanItSaysIsReadAsItIs()
    {
        var data = new byte[] { 1, 2, 3 };
        using var stream = new MemoryStream(data);
        Assert.Equal(data, CommandFiles.ReadAtMost(stream, 4096, "too large"));
    }

    /// <summary>A device that never ends, read as a file, as a file that keeps growing would be.</summary>
    [UnixFact]
    public void EndlessDeviceIsCutOffJustPastTheLimit()
    {
        Refused(CommandFiles.NoteTooLarge, () => CommandFiles.ReadNote("/dev/zero"));
        Refused(CommandFiles.PhotoTooLarge, () => CommandFiles.ReadPhoto("/dev/zero"));
    }
}
