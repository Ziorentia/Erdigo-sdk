using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Examples.CloudRelay;
using Xunit;

namespace Erdigo.Tests;

/// <summary>The relay of examples/CloudRelay against docs/cloud-relay.md and docs/cloud-relay-cases.json.</summary>
public sealed class CloudRelayTests
{
    private const string Pin = """{"op":"pin","id":"p1","lat":37.5663,"lon":126.9779}""";

    private static string PinWithId(string id) => $$"""{"op":"pin","id":"{{id}}","lat":37.5663,"lon":126.9779}""";

    private static string Photo(int bytes)
    {
        var data = new byte[bytes];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);
        return $$"""{"op":"photo","id":"big","lat":0,"lon":0,"type":"image/png","data":"{{Convert.ToBase64String(data)}}"}""";
    }

    private static long[] Seqs(TestRelay.Reply reply) =>
        reply.Json.GetProperty("ops").EnumerateArray().Select(entry => entry.GetProperty("seq").GetInt64()).ToArray();

    // The shared cases

    [Fact]
    public async Task EveryValidCaseIsTakenAndComesBackTheSame()
    {
        await using var relay = await TestRelay.StartAsync();
        var cases = RelayCases.Ops("valid_ops").ToList();
        Assert.NotEmpty(cases);
        foreach (var (name, op) in cases)
        {
            var reply = await relay.PostAsync($"{{\"ops\":[{op}]}}");
            Assert.True(reply.Status == HttpStatusCode.OK, $"{name}: {reply.Status} {reply.Text}");
            Assert.Equal(1, reply.Json.GetProperty("accepted").GetInt32());

            var fetched = await relay.GetAsync();
            var last = fetched.Json.GetProperty("ops").EnumerateArray().Last();
            Assert.True(RelayCases.SameJson(JsonDocument.Parse(op).RootElement, last.GetProperty("op")), $"{name}: {last.GetProperty("op").GetRawText()}");
        }
    }

    [Fact]
    public async Task EveryInvalidCaseIsRefusedAndNothingIsTaken()
    {
        await using var relay = await TestRelay.StartAsync();
        var cases = RelayCases.Ops("invalid_ops").ToList();
        Assert.NotEmpty(cases);
        foreach (var (name, op) in cases)
        {
            var reply = await relay.PostAsync($"{{\"ops\":[{op}]}}");
            Assert.True(reply.Status == HttpStatusCode.BadRequest, $"{name}: {reply.Status} {reply.Text}");
            Assert.True(reply.Error == "invalid_op", $"{name}: {reply.Text}");
            Assert.Equal(0, reply.Json.GetProperty("index").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(reply.Json.GetProperty("reason").GetString()), name);
        }
        Assert.Equal(0, relay.Server.Queue.Size.Count);
        Assert.Empty(Seqs(await relay.GetAsync()));
    }

    [Fact]
    public async Task EveryRawBodyIsRefusedWithOneOfItsErrors()
    {
        await using var relay = await TestRelay.StartAsync();
        var cases = RelayCases.Load().GetProperty("raw_bodies").EnumerateArray().ToList();
        Assert.NotEmpty(cases);
        foreach (var entry in cases)
        {
            var name = entry.GetProperty("name").GetString();
            var body = entry.TryGetProperty("body_base64", out var encoded)
                ? Convert.FromBase64String(encoded.GetString()!)
                : Encoding.UTF8.GetBytes(entry.GetProperty("body").GetString()!);
            var allowed = entry.GetProperty("errors").EnumerateArray().Select(error => error.GetString()).ToList();
            var reply = await relay.PostAsync(body);
            Assert.True(reply.Status == HttpStatusCode.BadRequest, $"{name}: {reply.Status} {reply.Text}");
            Assert.True(allowed.Contains(reply.Error), $"{name}: {reply.Text}");
        }
        Assert.Equal(0, relay.Server.Queue.Size.Count);
    }

    // Order of checks, and tokens

    [Fact]
    public async Task EachTokenWorksOnlyForItsRole()
    {
        await using var relay = await TestRelay.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await relay.PostAsync($"{{\"ops\":[{Pin}]}}", relay.Sources[0])).Status);
        Assert.Equal(HttpStatusCode.OK, (await relay.PostAsync($"{{\"ops\":[{Pin}]}}", relay.Sources[1])).Status);
        Assert.Equal(HttpStatusCode.OK, (await relay.GetAsync(token: relay.Gateway)).Status);

        foreach (var reply in new[]
        {
            await relay.PostAsync($"{{\"ops\":[{Pin}]}}", relay.Gateway),
            await relay.PostAsync($"{{\"ops\":[{Pin}]}}", TestRelay.NewToken()),
            await relay.GetAsync(token: relay.Source),
            await relay.GetAsync(token: TestRelay.NewToken()),
            await relay.GetAsync(token: relay.Gateway + "x"),
            await relay.GetAsync(token: relay.Gateway[..^1]),
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
            Assert.Equal("""{"error":"unauthorized"}""", reply.Text);
            Assert.Equal("application/json", reply.MediaType);
        }

        using (var missing = new HttpRequestMessage(HttpMethod.Get, "/v1/ops"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync(missing)).Status);
        }
        using (var basic = new HttpRequestMessage(HttpMethod.Get, "/v1/ops"))
        {
            basic.Headers.TryAddWithoutValidation("Authorization", $"Basic {relay.Gateway}");
            Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync(basic)).Status);
        }
        using (var bare = new HttpRequestMessage(HttpMethod.Get, "/v1/ops"))
        {
            bare.Headers.TryAddWithoutValidation("Authorization", relay.Gateway);
            Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync(bare)).Status);
        }
        // The scheme is not case-sensitive (RFC 9110).
        using (var lower = new HttpRequestMessage(HttpMethod.Get, "/v1/ops"))
        {
            lower.Headers.TryAddWithoutValidation("Authorization", $"bearer {relay.Gateway}");
            Assert.Equal(HttpStatusCode.OK, (await relay.SendAsync(lower)).Status);
        }
        Assert.Equal(2, relay.Server.Queue.Size.Count);
    }

    [Fact]
    public async Task UnknownPathsAndWrongMethodsComeBeforeTokens()
    {
        await using var relay = await TestRelay.StartAsync();
        foreach (var path in new[] { "/", "/v1", "/v1/ops/", "/v1/OPS", "/v2/ops", "/v1/health/", "/v1/ops/x" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var reply = await relay.SendAsync(request);
            Assert.True(reply.Status == HttpStatusCode.NotFound, path);
            Assert.Equal("""{"error":"not_found"}""", reply.Text);
            Assert.Equal("application/json", reply.MediaType);
        }
        foreach (var (method, path) in new[] { ("PUT", "/v1/ops"), ("DELETE", "/v1/ops"), ("PATCH", "/v1/ops"), ("POST", "/v1/health"), ("DELETE", "/v1/health") })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            var reply = await relay.SendAsync(request);
            Assert.True(reply.Status == HttpStatusCode.MethodNotAllowed, $"{method} {path}");
            Assert.Equal("""{"error":"method_not_allowed"}""", reply.Text);
            Assert.Equal("application/json", reply.MediaType);
        }
        // With a valid token too, the path and the method are checked first.
        using (var put = new HttpRequestMessage(HttpMethod.Put, "/v1/ops"))
        {
            put.Headers.TryAddWithoutValidation("Authorization", $"Bearer {relay.Source}");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await relay.SendAsync(put)).Status);
        }
    }

    [Fact]
    public async Task HealthNeedsNoToken()
    {
        await using var relay = await TestRelay.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        var reply = await relay.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.Equal("""{"ok":true}""", reply.Text);
        Assert.Equal("application/json", reply.MediaType);
    }

    // Sending

    [Fact]
    public async Task OnlyJsonIsTaken()
    {
        await using var relay = await TestRelay.StartAsync();
        var body = $"{{\"ops\":[{Pin}]}}";
        foreach (var type in new[] { "application/json", "application/json; charset=utf-8", "Application/JSON; charset=\"UTF-8\"" })
        {
            Assert.True((await relay.PostAsync(body, contentType: type)).Status == HttpStatusCode.OK, type);
        }
        foreach (var type in new[] { null, "text/plain", "application/json; charset=iso-8859-1", "application/json; foo=bar", "application/json-seq", "application/x-www-form-urlencoded" })
        {
            var reply = await relay.PostAsync(body, contentType: type);
            Assert.True(reply.Status == HttpStatusCode.UnsupportedMediaType, type ?? "no type");
            Assert.Equal("""{"error":"unsupported_media_type"}""", reply.Text);
        }
        // The token is checked before the type.
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.PostAsync(body, TestRelay.NewToken(), "text/plain")).Status);
    }

    [Fact]
    public async Task ABodyAnnouncedAsTooLargeIsRefusedWithoutBeingRead()
    {
        await using var relay = await TestRelay.StartAsync();
        var request = Encoding.ASCII.GetBytes(
            $"POST /v1/ops HTTP/1.1\r\nHost: relay\r\nAuthorization: Bearer {relay.Source}\r\nContent-Type: application/json\r\nContent-Length: 2097153\r\n\r\n");
        // No body is sent at all: the answer comes anyway.
        var answer = await relay.RawAsync(request, TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 413", answer, StringComparison.Ordinal);
        Assert.Contains("""{"error":"too_large"}""", answer, StringComparison.Ordinal);

        // A client that waits for "100 Continue" is answered without it.
        var expecting = Encoding.ASCII.GetBytes(
            $"POST /v1/ops HTTP/1.1\r\nHost: relay\r\nAuthorization: Bearer {relay.Source}\r\nContent-Type: application/json\r\nContent-Length: 9000000\r\nExpect: 100-continue\r\n\r\n");
        var refused = await relay.RawAsync(expecting, TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 413", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBodyLimitIsTwoMebibytes()
    {
        await using var relay = await TestRelay.StartAsync();
        var start = Encoding.UTF8.GetBytes($"{{\"ops\":[{Pin}]}}");
        var exact = new byte[2 * 1024 * 1024];
        exact.AsSpan().Fill((byte)' ');
        start.CopyTo(exact, 0);
        Assert.Equal(HttpStatusCode.OK, (await relay.PostAsync(exact)).Status);

        var over = new byte[exact.Length + 1];
        exact.CopyTo(over, 0);
        over[^1] = (byte)' ';
        var reply = await relay.PostAsync(over);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.Status);
        Assert.Equal("""{"error":"too_large"}""", reply.Text);

        // Without a length, exactly the limit is taken too.
        var whole = await relay.RawAsync(Chunked(relay.Source, exact, finished: true), TimeSpan.FromSeconds(20));
        Assert.StartsWith("HTTP/1.1 200", whole, StringComparison.Ordinal);

        // One byte more is refused once it is read. The body sent here stops right there, unfinished, so the answer
        // cannot come from the relay giving up on a body that never ends.
        var answer = await relay.RawAsync(Chunked(relay.Source, over, finished: false), TimeSpan.FromSeconds(20));
        Assert.StartsWith("HTTP/1.1 413", answer, StringComparison.Ordinal);
        Assert.Contains("""{"error":"too_large"}""", answer, StringComparison.Ordinal);
        Assert.Equal(2, relay.Server.Queue.Size.Count);
    }

    /// <summary>A POST whose body is sent in chunks of 4 KiB, without a length.</summary>
    private static byte[] Chunked(string token, byte[] body, bool finished)
    {
        using var request = new MemoryStream();
        request.Write(Encoding.ASCII.GetBytes(
            $"POST /v1/ops HTTP/1.1\r\nHost: relay\r\nAuthorization: Bearer {token}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n"));
        for (var start = 0; start < body.Length; start += 4096)
        {
            var length = Math.Min(4096, body.Length - start);
            request.Write(Encoding.ASCII.GetBytes($"{length:x}\r\n"));
            request.Write(body, start, length);
            request.Write("\r\n"u8);
        }
        if (finished)
        {
            request.Write("0\r\n\r\n"u8);
        }
        return request.ToArray();
    }

    [Fact]
    public async Task OneHundredOperationsAreTakenAndNoMore()
    {
        await using var relay = await TestRelay.StartAsync();
        var hundred = await relay.PostOpsAsync(Enumerable.Range(0, 100).Select(index => PinWithId($"p{index}")).ToArray());
        Assert.Equal(HttpStatusCode.OK, hundred.Status);
        Assert.Equal("""{"accepted":100,"last_seq":100}""", hundred.Text);

        var more = await relay.PostOpsAsync(Enumerable.Range(0, 101).Select(index => PinWithId($"p{index}")).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, more.Status);
        Assert.Equal("invalid_body", more.Error);
        Assert.Equal(100, relay.Server.Queue.Size.Count);
    }

    [Fact]
    public async Task APhotoIsAtMostOneMebibyte()
    {
        await using var relay = await TestRelay.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await relay.PostOpsAsync(Photo(1_048_576))).Status);
        var over = await relay.PostOpsAsync(Photo(1_048_577));
        Assert.Equal(HttpStatusCode.BadRequest, over.Status);
        Assert.Equal("invalid_op", over.Error);
        Assert.Equal(0, over.Json.GetProperty("index").GetInt32());
        Assert.Equal(1, relay.Server.Queue.Size.Count);
    }

    [Fact]
    public async Task ABatchIsTakenWholeOrNotAtAll()
    {
        await using var relay = await TestRelay.StartAsync();
        var reply = await relay.PostOpsAsync(Pin, PinWithId("p2"), """{"op":"pin","id":"p3","lat":91,"lon":0}""", PinWithId("p4"));
        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal("invalid_op", reply.Error);
        Assert.Equal(2, reply.Json.GetProperty("index").GetInt32());
        Assert.Empty(Seqs(await relay.GetAsync()));

        var taken = await relay.PostOpsAsync(Pin, PinWithId("p2"));
        Assert.Equal("""{"accepted":2,"last_seq":2}""", taken.Text);
        Assert.Equal(new long[] { 1, 2 }, Seqs(await relay.GetAsync()));
    }

    [Fact]
    public async Task AFullQueueRefusesTheWholeBatch()
    {
        await using (var relay = await TestRelay.StartAsync(RelayDefaults with { MaxQueuedOps = 3 }))
        {
            Assert.Equal(HttpStatusCode.OK, (await relay.PostOpsAsync(Pin, Pin)).Status);
            var full = await relay.PostOpsAsync(Pin, Pin);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, full.Status);
            Assert.Equal("""{"error":"queue_full"}""", full.Text);
            Assert.Equal("30", full.Response.Headers.GetValues("Retry-After").Single());
            Assert.Equal("""{"accepted":1,"last_seq":3}""", (await relay.PostOpsAsync(Pin)).Text);

            // Once the gateway is done with them, there is room again.
            var epoch = (await relay.GetAsync()).Json.GetProperty("epoch").GetString();
            await relay.GetAsync($"epoch={epoch}&after=3");
            Assert.Equal(HttpStatusCode.OK, (await relay.PostOpsAsync(Pin, Pin, Pin)).Status);
        }

        var pinBytes = Encoding.UTF8.GetByteCount(Pin);
        await using (var relay = await TestRelay.StartAsync(RelayDefaults with { MaxQueuedBytes = (pinBytes * 2) + 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, (await relay.PostOpsAsync(Pin, Pin)).Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await relay.PostOpsAsync(Pin)).Status);
        }
    }

    private static RelayLimits RelayDefaults => RelayLimits.Default;

    // Fetching

    [Fact]
    public async Task TheEpochDecidesWhatIsForgotten()
    {
        await using var relay = await TestRelay.StartAsync();
        await relay.PostOpsAsync(Pin, Pin, Pin);

        var first = await relay.GetAsync();
        Assert.True(first.Json.GetProperty("reset").GetBoolean());
        Assert.False(first.Json.GetProperty("missed").GetBoolean());
        var epoch = first.Json.GetProperty("epoch").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{16,64}$", epoch);
        Assert.Equal(new long[] { 1, 2, 3 }, Seqs(first));
        var received = first.Json.GetProperty("ops")[0].GetProperty("received_ms").GetInt64();
        Assert.Equal(relay.NowMs, received);

        var matching = await relay.GetAsync($"epoch={epoch}&after=1");
        Assert.False(matching.Json.GetProperty("reset").GetBoolean());
        Assert.Equal(new long[] { 2, 3 }, Seqs(matching));

        // Operation 1 was forgotten when the gateway said it was done with it.
        var again = await relay.GetAsync();
        Assert.True(again.Json.GetProperty("reset").GetBoolean());
        Assert.Equal(new long[] { 2, 3 }, Seqs(again));

        // Another epoch forgets nothing, whatever 'after' says.
        var other = await relay.GetAsync("epoch=AAAAAAAAAAAAAAAAAAAAAA&after=3");
        Assert.True(other.Json.GetProperty("reset").GetBoolean());
        Assert.Equal(epoch, other.Json.GetProperty("epoch").GetString());
        Assert.Equal(new long[] { 2, 3 }, Seqs(other));

        var done = await relay.GetAsync($"epoch={epoch}&after=3");
        Assert.Empty(Seqs(done));
        Assert.Equal(0, relay.Server.Queue.Size.Count);
        Assert.Equal("""{"accepted":1,"last_seq":4}""", (await relay.PostOpsAsync(Pin)).Text);
        Assert.Equal(new long[] { 4 }, Seqs(await relay.GetAsync($"epoch={epoch}&after=3")));

        // Past the last number given is a bad parameter.
        var past = await relay.GetAsync($"epoch={epoch}&after=5");
        Assert.Equal(HttpStatusCode.BadRequest, past.Status);
        Assert.Equal("""{"error":"invalid_query"}""", past.Text);
        Assert.Equal(new long[] { 4 }, Seqs(await relay.GetAsync()));
    }

    [Fact]
    public async Task EachStartHasItsOwnEpoch()
    {
        string first;
        await using (var relay = await TestRelay.StartAsync())
        {
            first = (await relay.GetAsync()).Json.GetProperty("epoch").GetString()!;
        }
        await using (var relay = await TestRelay.StartAsync())
        {
            var second = await relay.GetAsync($"epoch={first}&after=7");
            Assert.True(second.Json.GetProperty("reset").GetBoolean());
            Assert.NotEqual(first, second.Json.GetProperty("epoch").GetString());
        }
    }

    [Fact]
    public async Task BadQueriesAreRefused()
    {
        await using var relay = await TestRelay.StartAsync();
        await relay.PostOpsAsync(Pin);
        var epoch = (await relay.GetAsync()).Json.GetProperty("epoch").GetString();
        var bad = new[]
        {
            "limit=1&limit=1", "after=0&after=0", "foo=1", "LIMIT=1", "limit", "limit=", "limit=0", "limit=101", "limit=-1",
            "limit=1.0", "limit=+5", "wait=31", "wait=-1", "after=-1", "after=1.5", "after=abc", "after=9007199254740992",
            "epoch=short", "epoch=" + new string('a', 65), "epoch=aaaaaaaaaaaaaaa.a", "epoch=", "&", "limit=1&", "limit=1&&wait=0",
        };
        foreach (var query in bad)
        {
            var reply = await relay.GetAsync(query);
            Assert.True(reply.Status == HttpStatusCode.BadRequest, query);
            Assert.Equal("""{"error":"invalid_query"}""", reply.Text);
        }
        foreach (var query in new[] { "limit=1", "limit=100", "wait=0", "wait=1", "after=0", $"epoch={epoch}&after=0&limit=100&wait=0", $"wait=0&limit=5&after=1&epoch={epoch}" })
        {
            Assert.True((await relay.GetAsync(query)).Status == HttpStatusCode.OK, query);
        }
    }

    [Fact]
    public async Task OperationsThatWaitTooLongAreDroppedAndReported()
    {
        await using var relay = await TestRelay.StartAsync(keepHours: 1);
        var hour = 3_600_000L;
        await relay.PostOpsAsync(PinWithId("one"));
        relay.NowMs += hour / 2;
        await relay.PostOpsAsync(PinWithId("two"));
        relay.NowMs += (hour / 2) + 1;

        var answer = await relay.GetAsync();
        Assert.True(answer.Json.GetProperty("missed").GetBoolean());
        Assert.Equal(new long[] { 2 }, Seqs(answer));
        var epoch = answer.Json.GetProperty("epoch").GetString();

        // Once the gateway has gone past the dropped one, it is no longer news.
        Assert.False((await relay.GetAsync($"epoch={epoch}&after=2")).Json.GetProperty("missed").GetBoolean());
        // A fetch that starts over hears about it again.
        Assert.True((await relay.GetAsync()).Json.GetProperty("missed").GetBoolean());

        // What the gateway is done with does not count, however old it gets.
        await relay.PostOpsAsync(PinWithId("three"));
        Assert.Equal(new long[] { 3 }, Seqs(await relay.GetAsync($"epoch={epoch}&after=2")));
        Assert.False((await relay.GetAsync($"epoch={epoch}&after=3")).Json.GetProperty("missed").GetBoolean());
        relay.NowMs += 5 * hour;
        var later = await relay.GetAsync($"epoch={epoch}&after=3");
        Assert.False(later.Json.GetProperty("missed").GetBoolean());
        Assert.Empty(Seqs(later));
    }

    [Fact]
    public async Task AnswersArePagedByCountAndBySize()
    {
        await using (var relay = await TestRelay.StartAsync())
        {
            await relay.PostOpsAsync(Pin, Pin, Pin, Pin, Pin);
            var first = await relay.GetAsync("limit=2");
            Assert.Equal(new long[] { 1, 2 }, Seqs(first));
            var epoch = first.Json.GetProperty("epoch").GetString();
            Assert.Equal(new long[] { 3, 4 }, Seqs(await relay.GetAsync($"epoch={epoch}&after=2&limit=2")));
            Assert.Equal(new long[] { 5 }, Seqs(await relay.GetAsync($"epoch={epoch}&after=4&limit=2")));

            // Four of the largest photos, 1398104 bytes of base64 each: two fit in 4 MiB, three do not.
            for (var count = 0; count < 4; count++)
            {
                Assert.Equal(HttpStatusCode.OK, (await relay.PostOpsAsync(Photo(1_048_576))).Status);
            }
            var photos = await relay.GetAsync($"epoch={epoch}&after=5");
            Assert.Equal(new long[] { 6, 7 }, Seqs(photos));
            Assert.True(Encoding.UTF8.GetByteCount(photos.Text) <= 4 * 1024 * 1024);
            Assert.Equal(new long[] { 8, 9 }, Seqs(await relay.GetAsync($"epoch={epoch}&after=7")));
        }

        // An operation larger than the answer's limit still comes, alone.
        await using (var relay = await TestRelay.StartAsync(RelayDefaults with { MaxAnswerBytes = 200 }))
        {
            await relay.PostOpsAsync(Photo(1000), Photo(1000));
            Assert.Equal(new long[] { 1 }, Seqs(await relay.GetAsync()));
        }
    }

    [Fact]
    public async Task AFetchCanWaitForSomethingToArrive()
    {
        await using var relay = await TestRelay.StartAsync();
        var clock = Stopwatch.StartNew();
        var empty = await relay.GetAsync("wait=1");
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.9), $"answered after {clock.Elapsed}");
        Assert.Empty(Seqs(empty));

        clock.Restart();
        var waiting = relay.GetAsync("wait=20");
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);
        await relay.PostOpsAsync(Pin);
        var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(new long[] { 1 }, Seqs(answer));

        // A waiting fetch does not hold up anything else.
        var epoch = answer.Json.GetProperty("epoch").GetString();
        var held = relay.GetAsync($"epoch={epoch}&after=1&wait=20");
        using (var health = new HttpRequestMessage(HttpMethod.Get, "/v1/health"))
        {
            Assert.Equal(HttpStatusCode.OK, (await relay.SendAsync(health)).Status);
        }
        Assert.Equal(HttpStatusCode.OK, (await relay.PostOpsAsync(Pin)).Status);
        Assert.Equal(new long[] { 2 }, Seqs(await held.WaitAsync(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task StoppingAnswersWaitingFetches()
    {
        var relay = await TestRelay.StartAsync();
        var waiting = relay.GetAsync("wait=30");
        await Task.Delay(300);
        var clock = Stopwatch.StartNew();
        await relay.Server.DisposeAsync();
        try
        {
            await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (HttpRequestException)
        {
            // Closing the connection is an answer too.
        }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        relay.Http.Dispose();
    }

    [Fact]
    public async Task TheLogHoldsNoTokensOrContents()
    {
        await using var relay = await TestRelay.StartAsync();
        await relay.PostOpsAsync("""{"op":"note","id":"secret-id","lat":12.3456,"lon":65.4321,"text":"secret text"}""");
        var epoch = (await relay.GetAsync()).Json.GetProperty("epoch").GetString()!;
        await relay.GetAsync($"epoch={epoch}&after=1");
        await relay.GetAsync(token: TestRelay.NewToken());
        using (var odd = new HttpRequestMessage(HttpMethod.Get, "/secret-path"))
        {
            await relay.SendAsync(odd);
        }
        var log = relay.Log.ToString();
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        foreach (var line in lines)
        {
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ (GET|POST) (/v1/ops|-) \d{3} \d+ ms\r?$", line);
        }
        foreach (var secret in relay.Sources.Append(relay.Gateway).Append(epoch).Append("secret").Append("12.3456"))
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
    }

    // Starting

    private static Func<string, string?> Environment(string? sources, string? gateway) => name => name switch
    {
        "ERDIGO_RELAY_SOURCE_TOKENS" => sources,
        "ERDIGO_RELAY_GATEWAY_TOKEN" => gateway,
        _ => null,
    };

    [Fact]
    public async Task BadTokensOrOptionsStopTheRelayFromStarting()
    {
        var source = TestRelay.NewToken();
        var gateway = TestRelay.NewToken();
        var cases = new (string[] Args, string? Sources, string? Gateway)[]
        {
            (new[] { "--listen", "127.0.0.1:0" }, null, gateway),
            (new[] { "--listen", "127.0.0.1:0" }, " , ", gateway),
            (new[] { "--listen", "127.0.0.1:0" }, source, null),
            (new[] { "--listen", "127.0.0.1:0" }, source, source),
            (new[] { "--listen", "127.0.0.1:0" }, $"{TestRelay.NewToken()},{gateway}", gateway),
            (new[] { "--listen", "127.0.0.1:0" }, new string('a', 31), gateway),
            (new[] { "--listen", "127.0.0.1:0" }, new string('a', 257), gateway),
            (new[] { "--listen", "127.0.0.1:0" }, source + "!", gateway),
            (new[] { "--listen", "127.0.0.1:0" }, source, gateway + "/"),
            (new[] { "--listen", "127.0.0.1:0" }, source, new string('b', 31)),
            (new[] { "--listen", "0.0.0.0:0" }, source, gateway),
            (new[] { "--listen", "[::]:0" }, source, gateway),
            (new[] { "--listen", "192.0.2.1:8080" }, source, gateway),
            (new[] { "--listen", "relay.example.com:8080" }, source, gateway),
            (new[] { "--listen", "127.0.0.1" }, source, gateway),
            (new[] { "--listen", "127.0.0.1:65536" }, source, gateway),
            (new[] { "--listen" }, source, gateway),
            (new[] { "--keep-hours", "0" }, source, gateway),
            (new[] { "--keep-hours", "169" }, source, gateway),
            (new[] { "--keep-hours", "1.5" }, source, gateway),
            (new[] { "--token", source }, source, gateway),
            (new[] { source }, source, gateway),
            (new[] { "--listen", "127.0.0.1:0", "--listen", "127.0.0.1:0" }, source, gateway),
        };
        foreach (var (args, sources, gatewayToken) in cases)
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var code = await RelayCommand.RunAsync(args, Environment(sources, gatewayToken), output, error, CancellationToken.None);
            var shown = string.Join(" ", args.Select(argument => argument.Length > 20 ? "<long>" : argument));
            Assert.True(code == 1, $"{shown}: exit {code}");
            Assert.Empty(output.ToString());
            Assert.StartsWith("erdigo-relay: ", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(source, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(gateway, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("0.0.0.0:8080", "0.0.0.0", 8080)]
    [InlineData("[::]:0", "::", 0)]
    public void AnyAddressNeedsAProxyInFront(string listen, string address, int port)
    {
        Assert.Throws<UsageException>(() => RelayCommand.ParseListen(listen, behindProxy: false));
        var parsed = RelayCommand.ParseListen(listen, behindProxy: true);
        Assert.Equal(IPAddress.Parse(address), parsed.Address);
        Assert.Equal(port, parsed.Port);
    }

    [Theory]
    [InlineData("127.0.0.1:0", @"^http://127\.0\.0\.1:\d+$")]
    [InlineData("localhost:0", @"^http://127\.0\.0\.1:\d+$")]
    [InlineData("[::1]:0", @"^http://\[::1\]:\d+$")]
    public async Task ItSaysWhereItListensAndStopsWhenAsked(string listen, string pattern)
    {
        if (listen.StartsWith('[') && !Socket.OSSupportsIPv6)
        {
            return;
        }
        var output = new StringWriter();
        var error = new StringWriter();
        using var stop = new CancellationTokenSource();
        var running = RelayCommand.RunAsync(
            new[] { "--listen", listen, "--keep-hours", "1" },
            Environment($"{TestRelay.NewToken()}, {TestRelay.NewToken()}", TestRelay.NewToken()),
            TextWriter.Synchronized(output),
            error,
            stop.Token);
        string? line = null;
        for (var attempt = 0; attempt < 100 && line is null; attempt++)
        {
            await Task.Delay(50);
            line = output.ToString().Split('\n').FirstOrDefault(text => text.StartsWith("Relay listening on ", StringComparison.Ordinal))?.TrimEnd('\r');
        }
        Assert.NotNull(line);
        var url = line!["Relay listening on ".Length..];
        Assert.Matches(pattern, url);

        using (var http = new HttpClient())
        {
            Assert.Equal("""{"ok":true}""", await http.GetStringAsync($"{url}/v1/health"));
        }
        stop.Cancel();
        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(error.ToString());
        Assert.StartsWith("Relay listening on ", output.ToString(), StringComparison.Ordinal);
    }
}
