using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Examples.CloudRelay;
using Xunit;

namespace Erdigo.Tests;

/// <summary>A relay from examples/CloudRelay, running in this process on a free loopback port.</summary>
internal sealed class TestRelay : IAsyncDisposable
{
    private long nowMs = 1_700_000_000_000;

    private TestRelay(RelayServer server, string[] sources, string gateway, StringWriter log)
    {
        Server = server;
        Sources = sources;
        Gateway = gateway;
        Log = log;
        Http = new HttpClient { BaseAddress = new Uri(server.Url), Timeout = TimeSpan.FromSeconds(60) };
    }

    public RelayServer Server { get; }

    public string[] Sources { get; }

    public string Source => Sources[0];

    public string Gateway { get; }

    public StringWriter Log { get; }

    public HttpClient Http { get; }

    public string Url => Server.Url;

    /// <summary>The relay's clock, which only a test moves.</summary>
    public long NowMs
    {
        get => Interlocked.Read(ref nowMs);
        set => Interlocked.Exchange(ref nowMs, value);
    }

    /// <summary>A token of the documented form, made fresh for each test.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<TestRelay> StartAsync(RelayLimits? limits = null, int keepHours = 24, int port = 0, string[]? sources = null, string? gateway = null, long? startMs = null)
    {
        var sourceTokens = sources ?? new[] { NewToken(), NewToken() };
        var gatewayToken = gateway ?? NewToken();
        var log = new StringWriter();
        TestRelay? relay = null;
        var settings = new RelaySettings(IPAddress.Loopback, port, keepHours, sourceTokens, gatewayToken)
        {
            Limits = limits ?? RelayLimits.Default,
            Clock = () => relay?.NowMs ?? startMs ?? 1_700_000_000_000,
        };
        var server = await RelayServer.StartAsync(settings, TextWriter.Synchronized(log));
        relay = new TestRelay(server, sourceTokens, gatewayToken, log);
        if (startMs is { } start)
        {
            relay.NowMs = start;
        }
        return relay;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Server.DisposeAsync();
    }

    public sealed record Reply(HttpStatusCode Status, string Text, HttpResponseMessage Response)
    {
        public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();

        public string? Error => Json.TryGetProperty("error", out var error) ? error.GetString() : null;

        public string? MediaType => Response.Content.Headers.ContentType?.MediaType;
    }

    public Task<Reply> PostAsync(string body, string? token = null, string? contentType = "application/json") =>
        PostAsync(Encoding.UTF8.GetBytes(body), token, contentType);

    public async Task<Reply> PostAsync(byte[] body, string? token = null, string? contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/ops") { Content = new ByteArrayContent(body) };
        if (contentType is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token ?? Source}");
        return await SendAsync(request);
    }

    public Task<Reply> PostOpsAsync(params string[] operations) => PostAsync($"{{\"ops\":[{string.Join(",", operations)}]}}");

    public async Task<Reply> GetAsync(string query = "", string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, query.Length == 0 ? "/v1/ops" : $"/v1/ops?{query}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token ?? Gateway}");
        return await SendAsync(request);
    }

    public async Task<Reply> SendAsync(HttpRequestMessage request)
    {
        var response = await Http.SendAsync(request);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(), response);
    }

    /// <summary>Sends raw bytes and reads until the relay closes the connection or the time is up.</summary>
    public async Task<string> RawAsync(byte[] request, TimeSpan timeout)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, Server.Port);
        var stream = socket.GetStream();
        await stream.WriteAsync(request);
        using var limit = new CancellationTokenSource(timeout);
        var received = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, limit.Token);
                if (read == 0)
                {
                    break;
                }
                received.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(received.ToArray());
                // One whole answer is enough.
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal) && text.Contains("}", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
        }
        return Encoding.ASCII.GetString(received.ToArray());
    }
}

/// <summary>docs/cloud-relay-cases.json, which every example relay is tested against.</summary>
internal static class RelayCases
{
    public static JsonElement Load() => VectorFiles.Load(Path.Combine("docs", "cloud-relay-cases.json"));

    public static IEnumerable<(string Name, string Op)> Ops(string group) =>
        Load().GetProperty(group).EnumerateArray().Select(entry => (entry.GetProperty("name").GetString()!, entry.GetProperty("op").GetRawText()));

    /// <summary>The 1x1 PNG of the cases file, as base64.</summary>
    public static string TinyPng() =>
        Load().GetProperty("valid_ops").EnumerateArray()
            .First(entry => entry.GetProperty("name").GetString() == "photo, a 1x1 PNG")
            .GetProperty("op").GetProperty("data").GetString()!;

    /// <summary>Whether two JSON values are the same, with numbers compared by value.</summary>
    public static bool SameJson(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }
        return left.ValueKind switch
        {
            JsonValueKind.Object => left.EnumerateObject().Count() == right.EnumerateObject().Count()
                && left.EnumerateObject().All(property => right.TryGetProperty(property.Name, out var other) && SameJson(property.Value, other)),
            JsonValueKind.Array => left.GetArrayLength() == right.GetArrayLength()
                && left.EnumerateArray().Zip(right.EnumerateArray()).All(pair => SameJson(pair.First, pair.Second)),
            JsonValueKind.Number => left.GetDouble() == right.GetDouble(),
            JsonValueKind.String => left.GetString() == right.GetString(),
            _ => true,
        };
    }
}
