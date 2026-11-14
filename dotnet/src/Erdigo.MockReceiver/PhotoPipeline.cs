using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Erdigo.Core;

namespace Erdigo.MockReceiver;

/// <summary>
/// The photo checks of spec section 8 that can be made without a platform image decoder, so that the mock behaves
/// the same on every operating system.
/// </summary>
/// <remarks>
/// <para>
/// Steps 1 to 5 are made by reading the file's structure: the declared format against the first bytes, the format
/// and dimensions from the file's own headers, exactly one image, at most 64 million pixels, and, for a JPEG, at most
/// 32 scans with nothing between segments and no second start-of-image marker. For a PNG every chunk checksum is
/// checked and the image data is inflated, without being kept, to check that it is exactly as large as the header
/// says.
/// </para>
/// <para>
/// Steps 6 and 7 need a decoder, which .NET does not have on every system. In their place the stored file is rebuilt
/// from the original's image data alone: every metadata segment or chunk is left out, so that EXIF location, an
/// embedded colour profile, comments and the like do not survive. Pixels are not decoded, rescaled or encoded again.
/// A real receiver must still decode and re-encode as the specification says.
/// </para>
/// </remarks>
internal static class PhotoPipeline
{
    public const int MaxDimension = 32768;

    /// <summary>Decoders expand a PNG in full before scaling it, so a tiny file can cost gigabytes without this.</summary>
    public const long MaxPixels = 64_000_000;

    /// <summary>A progressive JPEG can repeat its scans so that a small file takes half a minute to decode. Ordinary encoders use about ten.</summary>
    public const int MaxJpegScans = 32;

    private static WireException Invalid(string detail) => new(ErrorCode.PhotoInvalid, detail);

    public static byte[] Process(byte[] data, PhotoType declared)
    {
        // 1. The first bytes must match the declared format.
        if (PhotoTypes.Detect(data) != declared)
        {
            throw Invalid("the data is not the declared format");
        }
        return declared == PhotoType.Jpeg ? Jpeg(data) : Png(data);
    }

    private static void CheckDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > MaxDimension || height > MaxDimension || width * height > MaxPixels)
        {
            throw Invalid("the image dimensions are not allowed");
        }
    }

    // JPEG

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;

    /// <summary>
    /// Walks the structure of a JPEG, counting its scans, reading its dimensions and leaving out every application
    /// segment except Adobe's colour transform flag, and every comment.
    /// </summary>
    internal static byte[] Jpeg(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length) { 0xFF, 0xD8 };
        var index = 2;
        var scans = 0;
        var frames = 0;
        long width = 0;
        long height = 0;
        var sawEnd = false;
        while (index < bytes.Length)
        {
            if (bytes[index] != 0xFF)
            {
                throw Invalid("the image has something between its segments");
            }
            index += 1;
            // A marker may be preceded by any number of fill bytes.
            while (index < bytes.Length && bytes[index] == 0xFF)
            {
                index += 1;
            }
            if (index >= bytes.Length)
            {
                throw Invalid("the image is cut short");
            }
            var marker = bytes[index];
            index += 1;
            if (marker == 0xD9)
            {
                output.Add(0xFF);
                output.Add(0xD9);
                sawEnd = true;
                break;
            }
            if (marker == 0xD8)
            {
                // A second start of image. It has no length, and a decoder carries on after it.
                throw Invalid("the image has a second start-of-image marker");
            }
            if (marker is >= 0xD0 and <= 0xD7 or 0x01)
            {
                output.Add(0xFF);
                output.Add(marker);
                continue;
            }
            if (marker == 0x00)
            {
                throw Invalid("the image has a damaged structure");
            }
            if (index + 1 >= bytes.Length)
            {
                throw Invalid("the image is cut short");
            }
            var length = (bytes[index] << 8) | bytes[index + 1];
            if (length < 2 || index + length > bytes.Length)
            {
                throw Invalid("the image has a damaged segment");
            }
            var segmentStart = index - 2;
            if (IsStartOfFrame(marker))
            {
                frames += 1;
                if (frames > 1 || length < 8)
                {
                    throw Invalid("the image does not have exactly one frame");
                }
                height = (bytes[index + 3] << 8) | bytes[index + 4];
                width = (bytes[index + 5] << 8) | bytes[index + 6];
                // Read the dimensions before anything is decoded.
                CheckDimensions(width, height);
            }
            if (KeepJpegSegment(marker, bytes.AsSpan(index + 2, length - 2)))
            {
                for (var position = segmentStart; position < index + length; position++)
                {
                    output.Add(bytes[position]);
                }
            }
            index += length;
            if (marker != 0xDA)
            {
                continue;
            }
            scans += 1;
            if (frames == 0)
            {
                throw Invalid("the image has a scan before its frame");
            }
            if (scans > MaxJpegScans)
            {
                throw Invalid("the image has too many scans");
            }
            // The coded data runs up to the next marker. FF 00 and restart markers are part of it.
            var dataStart = index;
            while (index + 1 < bytes.Length)
            {
                if (bytes[index] == 0xFF)
                {
                    var next = index + 1;
                    while (next < bytes.Length && bytes[next] == 0xFF)
                    {
                        next += 1;
                    }
                    if (next >= bytes.Length)
                    {
                        throw Invalid("the image is cut short");
                    }
                    if (bytes[next] != 0x00 && bytes[next] is not (>= 0xD0 and <= 0xD7))
                    {
                        break;
                    }
                    index = next;
                }
                index += 1;
            }
            if (index + 1 >= bytes.Length)
            {
                throw Invalid("the image is cut short");
            }
            for (var position = dataStart; position < index; position++)
            {
                output.Add(bytes[position]);
            }
        }
        if (!sawEnd || frames != 1 || scans == 0)
        {
            throw Invalid("the image is not one complete JPEG");
        }
        // Exactly one image: nothing may follow it except padding.
        for (var position = index; position < bytes.Length; position++)
        {
            if (bytes[position] is not (0x00 or 0xFF))
            {
                throw Invalid("the image has data after its end");
            }
        }
        return output.ToArray();
    }

    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> payload)
    {
        if (marker == 0xFE)
        {
            return false;
        }
        if (marker is >= 0xE0 and <= 0xEF)
        {
            // Adobe's segment says how to read the colours, and carries nothing else.
            return marker == 0xEE && payload.Length >= 5 && payload[..5].SequenceEqual("Adobe"u8);
        }
        return true;
    }

    // PNG

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint value = 0; value < 256; value++)
        {
            var crc = value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            table[value] = crc;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint ReadUInt32(byte[] bytes, int index) =>
        (uint)((bytes[index] << 24) | (bytes[index + 1] << 16) | (bytes[index + 2] << 8) | bytes[index + 3]);

    /// <summary>
    /// Checks every chunk of a PNG and keeps only the image itself: its header, palette, transparency, data and end.
    /// </summary>
    internal static byte[] Png(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length);
        output.AddRange(bytes.AsSpan(0, 8).ToArray());
        var index = 8;
        var sawHeader = false;
        var sawPalette = false;
        var sawEnd = false;
        var dataState = 0; // 0: none yet, 1: in the data chunks, 2: after them
        long width = 0;
        long height = 0;
        var bitDepth = 0;
        var colourType = 0;
        var interlace = 0;
        var compressed = new MemoryStream();
        while (index < bytes.Length)
        {
            if (index + 8 > bytes.Length)
            {
                throw Invalid("the image is cut short");
            }
            var length = ReadUInt32(bytes, index);
            if (length > int.MaxValue || index + 12L + length > bytes.Length)
            {
                throw Invalid("the image has a damaged chunk");
            }
            var type = bytes.AsSpan(index + 4, 4);
            foreach (var letter in type)
            {
                if (letter is not ((>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')))
                {
                    throw Invalid("the image has a damaged chunk");
                }
            }
            var name = System.Text.Encoding.ASCII.GetString(type);
            var body = bytes.AsSpan(index + 8, (int)length);
            if (Crc32(bytes.AsSpan(index + 4, 4 + (int)length)) != ReadUInt32(bytes, index + 8 + (int)length))
            {
                throw Invalid("the image has a damaged chunk");
            }
            if (!sawHeader && name != "IHDR")
            {
                throw Invalid("the image does not start with its header");
            }
            if (dataState == 1 && name != "IDAT")
            {
                dataState = 2;
            }
            var keep = false;
            switch (name)
            {
                case "IHDR":
                    if (sawHeader || length != 13)
                    {
                        throw Invalid("the image has a damaged header");
                    }
                    sawHeader = true;
                    width = ReadUInt32(bytes, index + 8);
                    height = ReadUInt32(bytes, index + 12);
                    bitDepth = body[8];
                    colourType = body[9];
                    interlace = body[12];
                    // Read the dimensions before anything is decoded.
                    CheckDimensions(width, height);
                    var validDepth = colourType switch
                    {
                        0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                        2 or 4 or 6 => bitDepth is 8 or 16,
                        3 => bitDepth is 1 or 2 or 4 or 8,
                        _ => false,
                    };
                    if (!validDepth || body[10] != 0 || body[11] != 0 || interlace is not (0 or 1))
                    {
                        throw Invalid("the image has a damaged header");
                    }
                    keep = true;
                    break;
                case "PLTE":
                    if (sawPalette || dataState != 0 || length == 0 || length % 3 != 0 || length > 768 || colourType is 0 or 4)
                    {
                        throw Invalid("the image has a damaged palette");
                    }
                    sawPalette = true;
                    keep = true;
                    break;
                case "tRNS":
                    keep = dataState == 0;
                    break;
                case "IDAT":
                    if (dataState == 2)
                    {
                        throw Invalid("the image data is split up");
                    }
                    dataState = 1;
                    compressed.Write(body);
                    keep = true;
                    break;
                case "IEND":
                    if (length != 0)
                    {
                        throw Invalid("the image has a damaged end");
                    }
                    sawEnd = true;
                    keep = true;
                    break;
                case "acTL" or "fcTL" or "fdAT":
                    throw Invalid("the image holds more than one image");
                default:
                    // An unknown critical chunk would stop a decoder. Ancillary chunks are metadata, and are left out.
                    if (name[0] is >= 'A' and <= 'Z')
                    {
                        throw Invalid("the image has an unknown critical chunk");
                    }
                    break;
            }
            if (keep)
            {
                output.AddRange(bytes.AsSpan(index, 12 + (int)length).ToArray());
            }
            index += 12 + (int)length;
            if (sawEnd)
            {
                break;
            }
        }
        if (!sawEnd || index != bytes.Length || dataState == 0 || (colourType == 3 && !sawPalette))
        {
            throw Invalid("the image is not one complete PNG");
        }
        CheckPngData(compressed.ToArray(), width, height, bitDepth, colourType, interlace);
        return output.ToArray();
    }

    /// <summary>
    /// Inflates the image data without keeping it, and checks that it holds exactly one filter byte and one row of
    /// samples for every row of every pass, as a decoder would.
    /// </summary>
    private static void CheckPngData(byte[] compressed, long width, long height, int bitDepth, int colourType, int interlace)
    {
        var channels = colourType switch
        {
            0 or 3 => 1,
            2 => 3,
            4 => 2,
            _ => 4,
        };
        var bitsPerPixel = (long)channels * bitDepth;
        var rows = new List<long>();
        if (interlace == 0)
        {
            for (var row = 0L; row < height; row++)
            {
                rows.Add((width * bitsPerPixel + 7) / 8);
            }
        }
        else
        {
            int[] startX = { 0, 4, 0, 2, 0, 1, 0 };
            int[] startY = { 0, 0, 4, 0, 2, 0, 1 };
            int[] stepX = { 8, 8, 4, 4, 2, 2, 1 };
            int[] stepY = { 8, 8, 8, 4, 4, 2, 2 };
            for (var pass = 0; pass < 7; pass++)
            {
                var passWidth = width <= startX[pass] ? 0 : (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
                var passHeight = height <= startY[pass] ? 0 : (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
                if (passWidth == 0 || passHeight == 0)
                {
                    continue;
                }
                for (var row = 0L; row < passHeight; row++)
                {
                    rows.Add((passWidth * bitsPerPixel + 7) / 8);
                }
            }
        }

        try
        {
            using var inflater = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var buffer = new byte[64 * 1024];
            var rowIndex = 0;
            var remainingInRow = -1L; // -1: the next byte is a filter type
            while (true)
            {
                var read = inflater.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                var position = 0;
                while (position < read)
                {
                    if (rowIndex >= rows.Count)
                    {
                        throw Invalid("the image data is longer than its header says");
                    }
                    if (remainingInRow < 0)
                    {
                        if (buffer[position] > 4)
                        {
                            throw Invalid("the image data is damaged");
                        }
                        position += 1;
                        remainingInRow = rows[rowIndex];
                    }
                    var take = (int)Math.Min(remainingInRow, read - position);
                    position += take;
                    remainingInRow -= take;
                    if (remainingInRow == 0)
                    {
                        rowIndex += 1;
                        remainingInRow = -1;
                    }
                }
            }
            if (rowIndex != rows.Count || remainingInRow != -1)
            {
                throw Invalid("the image data is shorter than its header says");
            }
        }
        catch (InvalidDataException)
        {
            throw Invalid("the image data is damaged");
        }
    }
}
