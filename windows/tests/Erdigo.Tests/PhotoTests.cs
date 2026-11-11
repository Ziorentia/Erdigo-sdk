using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.MockReceiver;
using Erdigo.Sdk;
using Xunit;

namespace Erdigo.Tests;

public sealed class PhotoTests : EndToEndBase
{
    private static bool HasSegment(byte[] jpeg, byte marker, string? payloadStart = null)
    {
        for (var index = 2; index + 4 < jpeg.Length; index++)
        {
            if (jpeg[index] == 0xFF && jpeg[index + 1] == marker
                && (payloadStart is null || jpeg.AsSpan(index + 4).StartsWith(Encoding.ASCII.GetBytes(payloadStart))))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasChunk(byte[] png, string type) => png.AsSpan().IndexOf(Encoding.ASCII.GetBytes(type)) >= 0;

    [Fact]
    public async Task ExifLocationIsRemoved()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var original = TestImages.JpegWithGps;
        // The test image must start with a location.
        Assert.True(HasSegment(original, 0xE1, "Exif"));

        await device.Client.PutPhotoAsync("p", Palace, original);
        var stored = receiver.Items(device.Identity.Fingerprint).First().Photo!;
        Assert.False(HasSegment(stored, 0xE1));
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(stored), StringComparison.Ordinal);
        Assert.NotEqual(original, stored);
        Assert.Equal(PhotoType.Jpeg, PhotoTypes.Detect(stored));
    }

    [Fact]
    public async Task ColourProfileFromTheSenderIsNotKept()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        Assert.True(HasSegment(TestImages.JpegWithProfile, 0xE2, "ICC_PROFILE"));
        await device.Client.PutPhotoAsync("p", Palace, TestImages.JpegWithProfile);
        var stored = receiver.Items(device.Identity.Fingerprint).First().Photo!;
        Assert.False(HasSegment(stored, 0xE2));
    }

    [Fact]
    public async Task PngIsAcceptedAndItsMetadataIsLeftOut()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var png = Png(32, 32);
        Assert.True(HasChunk(png, "tEXt"));
        await device.Client.PutPhotoAsync("p", Palace, png);
        var stored = receiver.Items(device.Identity.Fingerprint).First().Photo!;
        Assert.Equal(PhotoType.Png, PhotoTypes.Detect(stored));
        Assert.False(HasChunk(stored, "tEXt"));
        Assert.True(HasChunk(stored, "IDAT"));
    }

    [Fact]
    public async Task OrdinaryProgressiveJpegIsAccepted()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        await device.Client.PutPhotoAsync("p", Palace, TestImages.ProgressiveJpeg);
        Assert.Equal(ItemKind.Photo, receiver.Items(device.Identity.Fingerprint).First().Kind);
    }

    /// <summary>Sends a whole upload without the SDK's own checks, and returns the answer to photo_end.</summary>
    private static async Task<RawReply> RawUploadAsync(RawSession raw, byte[] data, string type, int? size = null, string? hash = null, int startSeq = 1)
    {
        var digest = hash ?? Hex.Encode(SHA256.HashData(data));
        var chunks = Math.Max(1, (data.Length + 47_999) / 48_000);
        var first = await raw.SendAsync($"{{\"t\":\"photo_begin\",\"seq\":{startSeq},\"id\":\"p\",\"lat\":1,\"lon\":1,\"type\":\"{type}\",\"size\":{size ?? data.Length},\"sha256\":\"{digest}\",\"chunks\":{chunks}}}");
        if (first.Ack is null)
        {
            return first;
        }
        var seq = startSeq + 1;
        for (var index = 0; index < chunks; index++)
        {
            var slice = data.AsSpan(index * 48_000, Math.Min(48_000, data.Length - index * 48_000)).ToArray();
            await raw.SendAsync($"{{\"t\":\"photo_chunk\",\"seq\":{seq},\"id\":\"p\",\"index\":{index},\"data\":\"{Convert.ToBase64String(slice)}\"}}");
            seq += 1;
        }
        return await raw.SendAsync($"{{\"t\":\"photo_end\",\"seq\":{seq},\"id\":\"p\"}}");
    }

    [Fact]
    public async Task ForbiddenPhotoFormatsAreRefused()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        var seq = 1;

        // A PDF claiming to be a JPEG.
        var reply = await RawUploadAsync(raw, Encoding.ASCII.GetBytes("%PDF-1.7 not an image"), "image/jpeg", startSeq: seq);
        Assert.Equal(ErrorCode.PhotoInvalid.ToWire(), reply.Code);
        seq += 3;

        // A real PNG claiming to be a JPEG.
        reply = await RawUploadAsync(raw, Png(32, 32), "image/jpeg", startSeq: seq);
        Assert.Equal(ErrorCode.PhotoInvalid.ToWire(), reply.Code);
        seq += 3;

        // A TIFF with its first bytes changed to look like a JPEG.
        var tiff = TestImages.Tiff;
        tiff[0] = 0xFF;
        tiff[1] = 0xD8;
        tiff[2] = 0xFF;
        reply = await RawUploadAsync(raw, tiff, "image/jpeg", startSeq: seq);
        Assert.Equal(ErrorCode.PhotoInvalid.ToWire(), reply.Code);
        seq += 3;

        // A forbidden format stated openly.
        reply = await raw.SendAsync($"{{\"t\":\"photo_begin\",\"seq\":{seq},\"id\":\"p\",\"lat\":1,\"lon\":1,\"type\":\"image/heic\",\"size\":10,\"sha256\":\"{new string('0', 64)}\",\"chunks\":1}}");
        Assert.Equal(ErrorCode.PhotoInvalid.ToWire(), reply.Code);

        Assert.Empty(receiver.Items(device.Identity.Fingerprint));
    }

    [Fact]
    public async Task OversizedAndMismatchedPhotosAreRefused()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        var zeros = new string('0', 64);
        Assert.Equal(ErrorCode.TooLarge.ToWire(), (await raw.SendAsync($"{{\"t\":\"photo_begin\",\"seq\":1,\"id\":\"p\",\"lat\":1,\"lon\":1,\"type\":\"image/jpeg\",\"size\":1153434,\"sha256\":\"{zeros}\",\"chunks\":25}}")).Code);

        var wrongHash = await RawUploadAsync(raw, TestImages.Jpeg, "image/jpeg", hash: new string('a', 64), startSeq: 2);
        Assert.Equal(ErrorCode.PhotoInvalid.ToWire(), wrongHash.Code);

        // Chunks out of order abandon the upload.
        await raw.SendAsync($"{{\"t\":\"photo_begin\",\"seq\":10,\"id\":\"q\",\"lat\":1,\"lon\":1,\"type\":\"image/jpeg\",\"size\":100,\"sha256\":\"{zeros}\",\"chunks\":1}}");
        Assert.Equal(ErrorCode.InvalidState.ToWire(), (await raw.SendAsync("{\"t\":\"photo_chunk\",\"seq\":11,\"id\":\"q\",\"index\":5,\"data\":\"AAEC\"}")).Code);

        // More data than declared.
        await raw.SendAsync($"{{\"t\":\"photo_begin\",\"seq\":12,\"id\":\"r\",\"lat\":1,\"lon\":1,\"type\":\"image/jpeg\",\"size\":2,\"sha256\":\"{zeros}\",\"chunks\":1}}");
        Assert.Equal(ErrorCode.PhotoInvalid.ToWire(), (await raw.SendAsync("{\"t\":\"photo_chunk\",\"seq\":13,\"id\":\"r\",\"index\":0,\"data\":\"AAEC\"}")).Code);
    }

    [Fact]
    public async Task SdkRefusesToSendANonImage()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        await ExpectAsync(ErdigoErrorKind.InvalidArgument, () => device.Client.PutPhotoAsync("p", Palace, Encoding.ASCII.GetBytes("%PDF-1.7")));
    }

    [Fact]
    public async Task PhotoWithTooManyPixelsIsRefusedBeforeDecoding()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        // 81 million pixels of one shade: a small file that would take far more memory to expand.
        var bomb = Png(9000, 9000, colourType: 0);
        Assert.True(bomb.Length < Limits.V1.PhotoBytes);
        await ExpectRejectedAsync(ErrorCode.PhotoInvalid, () => device.Client.PutPhotoAsync("p", Palace, bomb));
    }

    [Fact]
    public async Task JpegWithTooManyScansIsRefused()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var jpeg = TestImages.Jpeg;
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpeg[^2..]);
        var crafted = jpeg[..^2].Concat(Enumerable.Repeat(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 }, 40).SelectMany(bytes => bytes)).Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
        await ExpectRejectedAsync(ErrorCode.PhotoInvalid, () => device.Client.PutPhotoAsync("p", Palace, crafted));
    }

    /// <summary>A second start-of-image marker has no length, so a careless count would jump over what follows.</summary>
    [Fact]
    public async Task ScansHiddenBehindASecondStartOfImageAreRefused()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var crafted = TestImages.Jpeg[..^2].Concat(new byte[] { 0xFF, 0xD8 })
            .Concat(Enumerable.Repeat(new byte[] { 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00 }, 40).SelectMany(bytes => bytes))
            .Concat(new byte[] { 0xFF, 0xD9 }).ToArray();
        await ExpectRejectedAsync(ErrorCode.PhotoInvalid, () => device.Client.PutPhotoAsync("p", Palace, crafted));
    }

    [Fact]
    public async Task AJpegWithJunkBetweenItsSegmentsIsRefused()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var jpeg = TestImages.Jpeg;
        // After the first segment, so the file still starts like a JPEG.
        var afterFirstSegment = 4 + ((jpeg[4] << 8) | jpeg[5]);
        var crafted = jpeg[..afterFirstSegment].Concat(new byte[] { 0x00, 0x11, 0x22 }).Concat(jpeg[afterFirstSegment..]).ToArray();
        await ExpectRejectedAsync(ErrorCode.PhotoInvalid, () => device.Client.PutPhotoAsync("p", Palace, crafted));
    }

    [Fact]
    public async Task PhotoCannotReplaceAnotherKindOfItem()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        var raw = await AuthenticatedAsync(device, receiver, port);
        var photo = TestImages.Jpeg;
        var digest = Hex.Encode(SHA256.HashData(photo));
        Assert.Equal(1UL, (await raw.SendAsync($"{{\"t\":\"photo_begin\",\"seq\":1,\"id\":\"x\",\"lat\":1,\"lon\":1,\"type\":\"image/jpeg\",\"size\":{photo.Length},\"sha256\":\"{digest}\",\"chunks\":1}}")).Ack);
        // The id is taken while the upload is under way.
        Assert.Equal(2UL, (await raw.SendAsync("{\"t\":\"put\",\"seq\":2,\"kind\":\"pin\",\"id\":\"x\",\"lat\":1,\"lon\":1}")).Ack);
        await raw.SendAsync($"{{\"t\":\"photo_chunk\",\"seq\":3,\"id\":\"x\",\"index\":0,\"data\":\"{Convert.ToBase64String(photo)}\"}}");
        Assert.Equal(ErrorCode.KindConflict.ToWire(), (await raw.SendAsync("{\"t\":\"photo_end\",\"seq\":4,\"id\":\"x\"}")).Code);
        Assert.Equal(ItemKind.Pin, receiver.Items(device.Identity.Fingerprint).First().Kind);
    }

    [Fact]
    public async Task ALargePhotoInSeveralChunksArrivesWhole()
    {
        var (receiver, port) = await StartReceiverAsync();
        var device = await PairedDeviceAsync(receiver, port);
        // Noise does not compress, so this is several chunks.
        var noisy = Png(400, 400, colourType: 0);
        var random = RandomNumberGenerator.GetBytes(150_000);
        using (var stream = new MemoryStream())
        {
            stream.Write(noisy.AsSpan(0, noisy.Length - 12));
            WriteChunk(stream, "zTXt", random);
            stream.Write(noisy.AsSpan(noisy.Length - 12));
            noisy = stream.ToArray();
        }
        Assert.True(noisy.Length > 3 * IntegrationV1.MaxChunkBytes);
        await device.Client.PutPhotoAsync("big", Palace, noisy);
        var stored = receiver.Items(device.Identity.Fingerprint).First().Photo!;
        Assert.True(stored.Length < 20_000);
    }

    // The checks themselves

    [Fact]
    public void JpegStructureChecks()
    {
        var jpeg = TestImages.Jpeg;
        PhotoPipeline.Process(jpeg, PhotoType.Jpeg);
        // Cut short, with data after the end, with two frames, and with no frame at all.
        Assert.Throws<WireException>(() => PhotoPipeline.Process(jpeg[..^10], PhotoType.Jpeg));
        Assert.Throws<WireException>(() => PhotoPipeline.Process(jpeg.Concat(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }).ToArray(), PhotoType.Jpeg));
        var frameAt = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        var frameLength = (jpeg[frameAt + 2] << 8) | jpeg[frameAt + 3];
        var frame = jpeg[frameAt..(frameAt + 2 + frameLength)];
        Assert.Throws<WireException>(() => PhotoPipeline.Process(jpeg[..frameAt].Concat(frame).Concat(jpeg[frameAt..]).ToArray(), PhotoType.Jpeg));
        Assert.Throws<WireException>(() => PhotoPipeline.Process(jpeg[..frameAt].Concat(jpeg[(frameAt + 2 + frameLength)..]).ToArray(), PhotoType.Jpeg));
        // Padding after the end is fine.
        PhotoPipeline.Process(jpeg.Concat(new byte[] { 0x00, 0x00 }).ToArray(), PhotoType.Jpeg);
    }

    [Fact]
    public void JpegDimensionsAreReadFromTheFrame()
    {
        var jpeg = TestImages.Jpeg;
        var frameAt = jpeg.AsSpan().IndexOf(new byte[] { 0xFF, 0xC0 });
        // 9000 by 9000 is too many pixels, and 0 high is not an image.
        var large = jpeg.ToArray();
        large[frameAt + 5] = 0x23;
        large[frameAt + 6] = 0x28;
        large[frameAt + 7] = 0x23;
        large[frameAt + 8] = 0x28;
        Assert.Throws<WireException>(() => PhotoPipeline.Process(large, PhotoType.Jpeg));
        var flat = jpeg.ToArray();
        flat[frameAt + 5] = 0;
        flat[frameAt + 6] = 0;
        Assert.Throws<WireException>(() => PhotoPipeline.Process(flat, PhotoType.Jpeg));
    }

    [Fact]
    public void PngStructureChecks()
    {
        var png = Png(16, 16);
        PhotoPipeline.Process(png, PhotoType.Png);
        PhotoPipeline.Process(Png(17, 9, interlaced: true), PhotoType.Png);
        PhotoPipeline.Process(Png(5, 5, colourType: 6), PhotoType.Png);

        // A damaged checksum.
        var damaged = png.ToArray();
        damaged[^5] ^= 0x01;
        Assert.Throws<WireException>(() => PhotoPipeline.Process(damaged, PhotoType.Png));
        // Data after the end.
        Assert.Throws<WireException>(() => PhotoPipeline.Process(png.Concat(new byte[] { 0 }).ToArray(), PhotoType.Png));
        // An animated PNG holds more than one image.
        using (var stream = new MemoryStream())
        {
            stream.Write(png.AsSpan(0, 33));
            WriteChunk(stream, "acTL", new byte[8]);
            stream.Write(png.AsSpan(33));
            Assert.Throws<WireException>(() => PhotoPipeline.Process(stream.ToArray(), PhotoType.Png));
        }
        // An unknown critical chunk.
        using (var stream = new MemoryStream())
        {
            stream.Write(png.AsSpan(0, 33));
            WriteChunk(stream, "ABCD", new byte[2]);
            stream.Write(png.AsSpan(33));
            Assert.Throws<WireException>(() => PhotoPipeline.Process(stream.ToArray(), PhotoType.Png));
        }
        // Image data that is shorter than the header says.
        var header = Png(16, 16).ToArray();
        header[19] = 17;
        var crc = PhotoPipelineAccess.Crc32(header[12..29]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(29), crc);
        Assert.Throws<WireException>(() => PhotoPipeline.Process(header, PhotoType.Png));
    }
}
