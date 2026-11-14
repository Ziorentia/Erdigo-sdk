using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Erdigo.Core;
using Xunit;

namespace Erdigo.Tests;

public sealed class ModelTests
{
    [Fact]
    public void PairingCodeParsing()
    {
        var code = new PairingCode("abcde-fghjk mnpqr\ttvwxy");
        Assert.Equal("ABCDEFGHJKMNPQRTVWXY", code.Canonical);
        Assert.Equal("ABCDE-FGHJK-MNPQR-TVWXY", code.Display);
        Assert.Equal("00111000000000000000", new PairingCode("O0IL1-00000-00000-00000").Canonical);
        Assert.Throws<WireException>(() => new PairingCode("ABCDE"));
        // U is not in the alphabet.
        Assert.Throws<WireException>(() => new PairingCode("UUUUU-UUUUU-UUUUU-UUUUU"));
        Assert.Throws<WireException>(() => new PairingCode("ÄÖÜÄÖ-00000-00000-00000"));
        // Spaces a document or an input method might use.
        Assert.Equal("ABCDEFGHJKMNPQRTVWXY", new PairingCode("ABCDE\u00A0FGHJK\u3000MNPQR\u000CTVWXY").Canonical);
        // A lone surrogate is not a character of the code.
        Assert.Throws<WireException>(() => new PairingCode("ABCDE-FGHJK-MNPQR-TVWX\uD800Y"));
    }

    [Fact]
    public void PairingCodeDoesNotPrintItself()
    {
        var code = PairingCode.Generate();
        Assert.DoesNotContain(code.Canonical, $"{code}", StringComparison.Ordinal);
        Assert.Equal(20, code.Canonical.Length);
        Assert.DoesNotContain(code.Canonical, $"{new PairingTicket(code)}", StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCodesDiffer()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => PairingCode.Generate().Canonical).ToHashSet();
        Assert.Equal(1000, codes.Count);
    }

    [Fact]
    public void PairingTicketParsing()
    {
        var fingerprint = string.Concat(Enumerable.Repeat("ab", 32));
        var ticket = PairingTicket.Parse($"erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&fp={fingerprint}");
        Assert.Equal(fingerprint, ticket.Fingerprint);
        Assert.Equal(ticket, PairingTicket.Parse(ticket.Uri!));
        Assert.Throws<WireException>(() => PairingTicket.Parse($"erdigo-pair:1?fp={fingerprint}"));
        Assert.Throws<WireException>(() => PairingTicket.Parse("erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&fp=zz"));
        Assert.Throws<WireException>(() => PairingTicket.Parse("erdigo-pair:1?code=TEST0-0NLYC-0DE00-00000&host=192.0.2.1"));
    }

    public static IEnumerable<object[]> MalformedQrText()
    {
        const string code = "TEST0-0NLYC-0DE00-00000";
        var fingerprint = string.Concat(Enumerable.Repeat("ab", 32));
        var other = string.Concat(Enumerable.Repeat("cd", 32));
        yield return new object[] { "nothing after the prefix", "erdigo-pair:1?" };
        yield return new object[] { "no fingerprint", $"erdigo-pair:1?code={code}" };
        yield return new object[] { "no code", $"erdigo-pair:1?fp={fingerprint}" };
        yield return new object[] { "empty fingerprint", $"erdigo-pair:1?code={code}&fp=" };
        yield return new object[] { "fingerprint without an equals sign", $"erdigo-pair:1?code={code}&fp" };
        yield return new object[] { "empty code", $"erdigo-pair:1?code=&fp={fingerprint}" };
        yield return new object[] { "code without an equals sign", $"erdigo-pair:1?code&fp={fingerprint}" };
        yield return new object[] { "fingerprint one digit short", $"erdigo-pair:1?code={code}&fp={fingerprint[..^1]}" };
        yield return new object[] { "fingerprint one digit long", $"erdigo-pair:1?code={code}&fp={fingerprint}a" };
        yield return new object[] { "fingerprint of half the length", $"erdigo-pair:1?code={code}&fp={fingerprint[..32]}" };
        yield return new object[] { "fingerprint that is not hexadecimal", $"erdigo-pair:1?code={code}&fp={string.Concat(Enumerable.Repeat("zz", 32))}" };
        yield return new object[] { "fingerprint with an equals sign", $"erdigo-pair:1?code={code}&fp={fingerprint[..^1]}=" };
        yield return new object[] { "fingerprint in other digits", $"erdigo-pair:1?code={code}&fp={new string('\uFF11', 64)}" };
        yield return new object[] { "fingerprint with a line break", $"erdigo-pair:1?code={code}&fp={fingerprint[..32]}\n{fingerprint[..31]}" };
        yield return new object[] { "repeated fingerprint", $"erdigo-pair:1?code={code}&fp={fingerprint}&fp={fingerprint}" };
        yield return new object[] { "two fingerprints", $"erdigo-pair:1?code={code}&fp={fingerprint}&fp={other}" };
        yield return new object[] { "repeated code", $"erdigo-pair:1?code={code}&code={code}&fp={fingerprint}" };
        yield return new object[] { "repeated code after the fingerprint", $"erdigo-pair:1?code={code}&fp={fingerprint}&code={code}" };
        yield return new object[] { "an address", $"erdigo-pair:1?code={code}&fp={fingerprint}&host=192.0.2.1" };
        yield return new object[] { "an unknown field in front", $"erdigo-pair:1?x=1&code={code}&fp={fingerprint}" };
        yield return new object[] { "an unknown field with no value", $"erdigo-pair:1?code={code}&fp={fingerprint}&x" };
        yield return new object[] { "a field with no name", $"erdigo-pair:1?code={code}&fp={fingerprint}&=1" };
        yield return new object[] { "an equals sign before the code", $"erdigo-pair:1?=code={code}&fp={fingerprint}" };
        yield return new object[] { "an equals sign before the fingerprint", $"erdigo-pair:1?code={code}&=fp={fingerprint}" };
        yield return new object[] { "an equals sign before both", $"erdigo-pair:1?=code={code}&=fp={fingerprint}" };
        yield return new object[] { "two equals signs before the fingerprint", $"erdigo-pair:1?code={code}&==fp={fingerprint}" };
        yield return new object[] { "several equals signs before both", $"erdigo-pair:1?===code={code}&===fp={fingerprint}" };
        yield return new object[] { "nothing but an equals sign", "erdigo-pair:1?=" };
        yield return new object[] { "an empty field in the middle", $"erdigo-pair:1?code={code}&&fp={fingerprint}" };
        yield return new object[] { "an empty field in front", $"erdigo-pair:1?&code={code}&fp={fingerprint}" };
        yield return new object[] { "a trailing separator", $"erdigo-pair:1?code={code}&fp={fingerprint}&" };
        yield return new object[] { "only separators", "erdigo-pair:1?&&" };
        yield return new object[] { "field names in capitals", $"erdigo-pair:1?CODE={code}&FP={fingerprint}" };
        yield return new object[] { "another version", $"erdigo-pair:2?code={code}&fp={fingerprint}" };
        yield return new object[] { "no question mark", $"erdigo-pair:1code={code}&fp={fingerprint}" };
        yield return new object[] { "a prefix in capitals", $"ERDIGO-PAIR:1?code={code}&fp={fingerprint}" };
    }

    [Theory]
    [MemberData(nameof(MalformedQrText))]
    public void QrTextNeedsExactlyOneCodeAndOneValidFingerprint(string name, string text)
    {
        var error = Assert.Throws<WireException>(() => PairingTicket.Parse(text));
        Assert.True(error.Code == ErrorCode.InvalidMessage, name);
    }

    [Fact]
    public void PairingTextOverTheLimitIsRefusedBeforeItIsRead()
    {
        const string code = "TEST0-0NLYC-0DE00-00000";
        var fingerprint = string.Concat(Enumerable.Repeat("ab", 32));
        const int limit = 1024;
        var qr = $"erdigo-pair:1?code={code}&fp={fingerprint}";
        Assert.Equal(110, Encoding.UTF8.GetByteCount(qr));

        // White space is the one thing a reader tolerates, so it shows exactly where the limit is.
        foreach (var text in new[] { qr, code })
        {
            var atTheLimit = text + new string(' ', limit - Encoding.UTF8.GetByteCount(text));
            PairingTicket.Parse(atTheLimit);
            Assert.Equal(ErrorCode.InvalidMessage, Assert.Throws<WireException>(() => PairingTicket.Parse(atTheLimit + " ")).Code);
        }

        // The limit counts bytes, not characters.
        var wide = new string('\u3000', limit / 3 + 1);
        Assert.True(wide.Length < limit);
        Assert.Throws<WireException>(() => PairingTicket.Parse(code + wide));

        // Valid text with something huge inside it is not valid.
        const int huge = 2_000_000;
        Assert.Throws<WireException>(() => PairingTicket.Parse($"erdigo-pair:1?code={new string('-', huge)}{code}&fp={fingerprint}"));
        Assert.Throws<WireException>(() => PairingTicket.Parse($"erdigo-pair:1?{new string('&', huge)}"));
    }

    [Fact]
    public void ValidQrTextWorksAndRoundTrips()
    {
        var code = new PairingCode("TEST0-0NLYC-0DE00-00000");
        var fingerprint = string.Concat(Enumerable.Repeat("ab", 32));

        var ticket = PairingTicket.Parse($"  erdigo-pair:1?code={code.Display}&fp={fingerprint}\n");
        Assert.Equal(new PairingTicket(code, fingerprint), ticket);
        Assert.Equal($"erdigo-pair:1?code={code.Display}&fp={fingerprint}", ticket.Uri);
        Assert.Equal(ticket, PairingTicket.Parse(ticket.Uri!));

        // The two fields may come in either order, and digits are read as lowercase.
        Assert.Equal(ticket, PairingTicket.Parse($"erdigo-pair:1?fp={fingerprint}&code={code.Display}"));
        var capitals = PairingTicket.Parse($"erdigo-pair:1?code={code.Display}&fp={fingerprint.ToUpperInvariant()}");
        Assert.Equal(ticket, capitals);
        Assert.Equal(ticket.Uri, capitals.Uri);
    }

    [Fact]
    public void ABareCodeStaysTheTypedPath()
    {
        var code = new PairingCode("TEST0-0NLYC-0DE00-00000");
        foreach (var typed in new[] { "TEST0-0NLYC-0DE00-00000", "test0 0nlyc 0de00 00000", " TEST00NLYC0DE0000000\n" })
        {
            var ticket = PairingTicket.Parse(typed);
            Assert.Equal(code, ticket.Code);
            Assert.Null(ticket.Fingerprint);
        }
        Assert.Throws<WireException>(() => PairingTicket.Parse(string.Empty));
        Assert.Throws<WireException>(() => PairingTicket.Parse("TEST0"));
    }

    [Fact]
    public void OnlyAValidFingerprintBecomesQrText()
    {
        var code = new PairingCode("TEST0-0NLYC-0DE00-00000");
        var fingerprint = string.Concat(Enumerable.Repeat("ab", 32));

        // A typed code can still be represented, but it does not become QR text.
        Assert.Null(new PairingTicket(code).Uri);
        Assert.Null(PairingTicket.Parse(code.Display).Uri);

        var malformed = new[]
        {
            string.Empty,
            fingerprint[..^1],
            fingerprint + "a",
            fingerprint.ToUpperInvariant(),
            string.Concat(Enumerable.Repeat("zz", 32)),
            fingerprint[..^10] + "&host=x",
            new string('\uFF11', 64),
        };
        foreach (var text in malformed)
        {
            Assert.Null(new PairingTicket(code, text).Uri);
        }
        Assert.NotNull(new PairingTicket(code, fingerprint).Uri);
    }

    [Fact]
    public void CoordinatesAreValidated()
    {
        _ = new Coordinate(-90, 180);
        Assert.Throws<WireException>(() => new Coordinate(double.NaN, 0));
        Assert.Throws<WireException>(() => new Coordinate(0, double.PositiveInfinity));
        Assert.Equal(ErrorCode.InvalidCoordinate, Assert.Throws<WireException>(() => new Coordinate(90.0001, 0)).Code);
    }

    [Fact]
    public void TextRules()
    {
        Assert.Equal(ErrorCode.InvalidText, Assert.Throws<WireException>(() => Core.TextRules.ValidateTitle(string.Empty)).Code);
        Assert.Equal(ErrorCode.InvalidText, Assert.Throws<WireException>(() => Core.TextRules.ValidateTitle("a\nb")).Code);
        Assert.Equal(ErrorCode.InvalidText, Assert.Throws<WireException>(() => Core.TextRules.ValidateTitle("a\u202Eb")).Code);
        Assert.Equal(ErrorCode.TooLarge, Assert.Throws<WireException>(() => Core.TextRules.ValidateTitle(new string('a', 201))).Code);
        // Characters, not UTF-16 units: 200 emoji are a valid title.
        Core.TextRules.ValidateTitle(string.Concat(Enumerable.Repeat("😀", 200)));
        Assert.Equal(ErrorCode.InvalidText, Assert.Throws<WireException>(() => Core.TextRules.ValidateTitle("a\uD800")).Code);
        Core.TextRules.ValidateNote("line\nline\ttab");
        Assert.Equal(ErrorCode.TooLarge, Assert.Throws<WireException>(() => Core.TextRules.ValidateNote(new string('é', 2049))).Code);
        Assert.Equal(ErrorCode.InvalidText, Assert.Throws<WireException>(() => Core.TextRules.ValidateNote("\uFEFFhello")).Code);
        var name = Core.TextRules.SanitizeDeviceName("  Bank\u202Ecod.exe\u0007\n" + new string('x', 60) + "\uD800");
        Assert.Equal(40, name.EnumerateRunes().Count());
        Assert.StartsWith("Bankcod.exe", name, StringComparison.Ordinal);
    }

    private static readonly Coordinate At = new(37.5796, 126.977);

    public static IEnumerable<object[]> Requests()
    {
        var id = new ItemId("a_b-1");
        yield return new object[] { Request.Ping.Instance };
        yield return new object[] { new Request.PutPin(id, At, "Zürich €") };
        yield return new object[] { new Request.PutMarker(id, At, null, MarkerColor.Green) };
        yield return new object[] { new Request.PutNote(id, At, "Line\nLine \"quoted\" \\ done") };
        yield return new object[] { new Request.Move(id, At) };
        yield return new object[] { new Request.Delete(id) };
        yield return new object[] { Request.DeleteAll.Instance };
        yield return new object[] { new Request.PhotoBegin(new PhotoHeader(id, At, null, PhotoType.Png, 5, new string('0', 64), 1)) };
        yield return new object[] { new Request.PhotoChunk(id, 3, new byte[] { 1, 2, 3 }) };
        yield return new object[] { new Request.PhotoEnd(id) };
        yield return new object[] { new Request.TrackAppend(id, "T", new[] { new TrackPoint(At, 5, -3.5) }) };
        yield return new object[] { new Request.TrackFinish(id) };
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void RequestsRoundTrip(Request request)
    {
        var envelope = new RequestEnvelope(7, request);
        var decoded = Wire.DecodeRequest(Wire.Encode(envelope));
        Assert.Equal(7UL, decoded.Seq);
        Assert.Equal(request, decoded.Request);
        Assert.Equal(request.GetType(), decoded.Request.GetType());
    }

    [Fact]
    public void ReceiverMessagesRoundTrip()
    {
        var messages = new ReceiverMessage[]
        {
            new ReceiverMessage.WelcomeMessage(new Welcome(1, 9, new[] { ItemKind.Pin }, Limits.V1)),
            new ReceiverMessage.Ack(1, true),
            new ReceiverMessage.Error(new ReceiverError("rate_limited", 3, 2, 100, new[] { 1 }, fatal: true)),
            new ReceiverMessage.PairStatusMessage(PairStatus.Pending.Instance),
            new ReceiverMessage.PairStatusMessage(PairStatus.Denied.Instance),
            new ReceiverMessage.PairStatusMessage(new PairStatus.Approved(new[] { ItemKind.Pin, ItemKind.Track }, 12)),
        };
        foreach (var message in messages)
        {
            var decoded = Wire.DecodeReceiverMessage(Wire.Encode(message));
            Assert.Equal(message, decoded);
            Assert.Equal(message.GetType(), decoded.GetType());
        }
        var welcome = (ReceiverMessage.WelcomeMessage)Wire.DecodeReceiverMessage(Wire.Encode(messages[0]));
        Assert.Equal(Limits.V1, welcome.Welcome.Limits);
    }

    [Theory]
    [InlineData("{\"t\":\"ack\",\"seq\":1,\"dup\":false,\"extra\":1}")]
    [InlineData("{\"t\":\"welcome\",\"version\":1,\"last_seq\":0,\"kinds\":[]}")]
    [InlineData("{\"t\":\"error\",\"code\":\"BAD CODE\"}")]
    [InlineData("{\"t\":\"pair_status\",\"status\":\"approved\"}")]
    [InlineData("{\"t\":\"pair_status\",\"status\":\"approved\",\"kinds\":[\"pin\"]}")]
    [InlineData("{\"t\":\"mystery\"}")]
    [InlineData("{\"t\":\"welcome\",\"version\":1,\"last_seq\":0,\"kinds\":[],\"limits\":{\"items\":500,\"photos\":50,\"photo_bytes\":1048576,\"note_bytes\":4096,\"title_chars\":200,\"track_points\":3000,\"open_tracks\":5,\"tracks\":200,\"msgs_per_s\":5,\"msg_burst\":20,\"points_per_s\":1,\"point_burst\":99,\"disk_bytes\":31457280}}")]
    public void SdkRejectsMalformedReceiverMessages(string text)
    {
        Assert.Throws<WireException>(() => Wire.DecodeReceiverMessage(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("{}{}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,]")]
    [InlineData("01")]
    [InlineData("1.")]
    [InlineData(".5")]
    [InlineData("+1")]
    [InlineData("1e")]
    [InlineData("tru")]
    [InlineData("\"\\x\"")]
    [InlineData("\"\u0001\"")]
    [InlineData("1e999")]
    [InlineData("[[[[[[[1]]]]]]]")]
    [InlineData("{\"é\":1,\"e\u0301\":2}")]
    public void StrictJsonRejections(string text)
    {
        Assert.Throws<JsonParseException>(() => StrictJson.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void StrictJsonAcceptances()
    {
        Assert.Equal(new JsonValue.String("😀"), StrictJson.Parse(Encoding.UTF8.GetBytes("\"😀\"")));
        Assert.Equal(new JsonValue.String("😀"), StrictJson.Parse(Encoding.UTF8.GetBytes("\"\\ud83d\\ude00\"")));
        Assert.Equal(new JsonValue.Integer(-1), StrictJson.Parse(Encoding.UTF8.GetBytes("-1")));
        Assert.Equal(new JsonValue.Number(150), StrictJson.Parse(Encoding.UTF8.GetBytes("1.5e2")));
        // Too long for an integer, but still a number, so a field wanting an integer refuses it later.
        Assert.Equal(new JsonValue.Number(9223372036854775808.0), StrictJson.Parse(Encoding.UTF8.GetBytes("9223372036854775808")));
        // Six levels are allowed, and seven are not.
        StrictJson.Parse(Encoding.UTF8.GetBytes("[[[[[[1]]]]]]"));
        // White space after the value is part of JSON. Anything else is not.
        StrictJson.Parse(Encoding.UTF8.GetBytes("{} \n"));
        Assert.Equal(JsonParseError.DuplicateKey, Assert.Throws<JsonParseException>(() => StrictJson.Parse(Encoding.UTF8.GetBytes("{\"a\":1,\"a\":1}"))).Error);
    }

    [Fact]
    public void MessageSizeLimit()
    {
        var tooLong = Enumerable.Repeat((byte)0x20, IntegrationV1.MaxPlaintextLength + 1).ToArray();
        Assert.Equal(ErrorCode.TooLarge, Assert.Throws<WireException>(() => Wire.DecodeRequest(tooLong)).Code);
    }

    [Fact]
    public void TokenBucket()
    {
        var bucket = new TokenBucket(2, 1, now: 100);
        Assert.Null(bucket.Take(100));
        Assert.Null(bucket.Take(100));
        Assert.Equal(1, bucket.Take(100) ?? 0, 3);
        Assert.Null(bucket.Take(101));
        // Asking for more than the capacity never succeeds, and must not promise otherwise.
        var small = new TokenBucket(2, 1, now: 0);
        Assert.NotNull(small.Take(0, count: 5));
    }

    /// <summary>Numbers are written as the Swift SDK writes them. These pairs were printed by Swift's Double.description.</summary>
    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(37.0, "37.0")]
    [InlineData(37.5796, "37.5796")]
    [InlineData(126.977, "126.977")]
    [InlineData(-33.8568, "-33.8568")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(0.000123, "0.000123")]
    [InlineData(1e-7, "1e-07")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(9007199254740992.0, "9007199254740992.0")]
    [InlineData(18014398509481984.0, "1.8014398509481984e+16")]
    [InlineData(1.5e16, "1.5e+16")]
    [InlineData(123456.789, "123456.789")]
    [InlineData(100000.0, "100000.0")]
    [InlineData(-1000.0, "-1000.0")]
    [InlineData(38.5, "38.5")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1e-300, "1e-300")]
    [InlineData(0.1, "0.1")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(1e21, "1e+21")]
    [InlineData(12345678901234567890.0, "1.2345678901234567e+19")]
    public void NumbersAreWrittenLikeSwift(double value, string expected)
    {
        Assert.Equal(expected, JsonWriter.FormatNumber(value));
        // And they read back as the same number.
        var parsed = StrictJson.Parse(Encoding.ASCII.GetBytes(expected));
        var number = parsed is JsonValue.Number fractional ? fractional.Value : ((JsonValue.Integer)parsed).Value;
        Assert.Equal(value, number);
    }

    [Fact]
    public void FingerprintsAndShortForms()
    {
        var fingerprint = IntegrationV1.Fingerprint(new byte[32]);
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal($"{fingerprint[..4]}-{fingerprint[4..8]}-{fingerprint[8..12]}-{fingerprint[12..16]}", IntegrationV1.ShortFingerprint(fingerprint));
        Assert.Equal(IntegrationV1.Mode.Connect, IntegrationV1.ParsePreamble(IntegrationV1.Preamble(IntegrationV1.Mode.Connect)));
        Assert.Null(IntegrationV1.ParsePreamble(Encoding.ASCII.GetBytes("EIN1\u0003")));
        Assert.Null(IntegrationV1.ParsePreamble(Encoding.ASCII.GetBytes("EIN2\u0001")));
    }
}
