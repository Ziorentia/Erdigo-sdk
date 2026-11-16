using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;

namespace Erdigo.Examples.CloudRelay;

/// <summary>Everything the relay is started with.</summary>
internal sealed record RelaySettings(IPAddress Address, int Port, int KeepHours, IReadOnlyList<string> SourceTokens, string GatewayToken)
{
    public RelayLimits Limits { get; init; } = RelayLimits.Default;

    /// <summary>Milliseconds since 1970. Tests move it on instead of waiting hours.</summary>
    public Func<long> Clock { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>The relay API of docs/cloud-relay.md, served by Kestrel over plain HTTP.</summary>
internal sealed class RelayServer : IAsyncDisposable
{
    private const string OpsPath = "/v1/ops";
    private const string HealthPath = "/v1/health";

    private static readonly byte[] Health = JsonWriter.Encode(JsonWriter.Value.Object(new[] { ("ok", JsonWriter.Value.Of(true)) }));

    private readonly WebApplication app;
    private readonly RelaySettings settings;
    private readonly TextWriter log;
    private readonly object logSync = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly byte[][] sourceDigests;
    private readonly byte[] gatewayDigest;
    private Timer? expiry;

    private RelayServer(RelaySettings settings, TextWriter log)
    {
        this.settings = settings;
        this.log = log;
        Queue = new RelayQueue(settings.Limits, settings.KeepHours, settings.Clock);
        // Comparing digests of equal length takes the same time whatever the token, and whatever its length.
        sourceDigests = settings.SourceTokens.Select(Digest).ToArray();
        gatewayDigest = Digest(settings.GatewayToken);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        // The program stops the relay itself on Ctrl+C and SIGTERM, and nothing is configured from files or the environment.
        builder.Services.AddSingleton<IHostLifetime, QuietLifetime>();
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            var limits = options.Limits;
            // The handler enforces the relay's limit exactly. Kestrel counts a chunked body by more than its length, so
            // its own limit is only a backstop well above that, which also bounds what it reads to discard a refused body.
            limits.MaxRequestBodySize = 4L * settings.Limits.MaxBodyBytes;
            limits.MaxRequestHeadersTotalSize = 16 * 1024;
            limits.MaxRequestHeaderCount = 50;
            limits.MaxRequestLineSize = 8 * 1024;
            limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            limits.KeepAliveTimeout = TimeSpan.FromSeconds(75);
            limits.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 1024, gracePeriod: TimeSpan.FromSeconds(10));
            limits.MaxConcurrentConnections = 1000;
            options.Listen(settings.Address, settings.Port, listen => listen.Protocols = HttpProtocols.Http1);
        });
        app = builder.Build();
        app.Run(HandleAsync);
    }

    public RelayQueue Queue { get; }

    /// <summary>Where it listens, with the port it actually got: <c>http://127.0.0.1:8080</c> or <c>http://[::1]:8080</c>.</summary>
    public string Url { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public static async Task<RelayServer> StartAsync(RelaySettings settings, TextWriter log, CancellationToken cancellationToken = default)
    {
        var server = new RelayServer(settings, log);
        try
        {
            await server.app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await server.app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        var bound = server.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("the server did not report its address");
        server.Port = new Uri(bound).Port;
        var host = settings.Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{settings.Address}]" : settings.Address.ToString();
        server.Url = string.Create(CultureInfo.InvariantCulture, $"http://{host}:{server.Port}");
        server.expiry = new Timer(_ => server.Queue.ExpireNow(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        // Waiting fetches answer at once, so stopping does not wait for them.
        stopping.Cancel();
        if (expiry is not null)
        {
            await expiry.DisposeAsync().ConfigureAwait(false);
        }
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] Digest(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private async Task HandleAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var path = context.Request.Path.Value;
        try
        {
            await DispatchAsync(context, path).ConfigureAwait(false);
        }
        catch (BadHttpRequestException error)
        {
            // Kestrel answers this itself: a body that ended early or came too slowly.
            context.Response.StatusCode = error.StatusCode;
            throw;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            // The client went away. There is nobody to answer.
        }
        finally
        {
            // Never the query, the headers or the body: they hold tokens, positions and text.
            var method = context.Request.Method is { Length: <= 10 } given && given.All(char.IsAsciiLetterUpper) ? context.Request.Method : "-";
            var shownPath = path is OpsPath or HealthPath ? path : "-";
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            lock (logSync)
            {
                log.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {method} {shownPath} {context.Response.StatusCode} {elapsed} ms"));
                log.Flush();
            }
        }
    }

    private Task DispatchAsync(HttpContext context, string? path)
    {
        var method = context.Request.Method;
        switch (path)
        {
            case HealthPath:
                return HttpMethods.IsGet(method)
                    ? AnswerAsync(context, StatusCodes.Status200OK, Health)
                    : ErrorAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed");
            case OpsPath when HttpMethods.IsPost(method):
                return IsAuthorized(context.Request, sourceDigests)
                    ? PostAsync(context)
                    : ErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized");
            case OpsPath when HttpMethods.IsGet(method):
                return IsAuthorized(context.Request, new[] { gatewayDigest })
                    ? FetchAsync(context)
                    : ErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized");
            case OpsPath:
                return ErrorAsync(context, StatusCodes.Status405MethodNotAllowed, "method_not_allowed");
            default:
                return ErrorAsync(context, StatusCodes.Status404NotFound, "not_found");
        }
    }

    private static bool IsAuthorized(HttpRequest request, IReadOnlyList<byte[]> allowed)
    {
        const string scheme = "Bearer ";
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } header || header.Length <= scheme.Length
            || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var presented = Digest(header[scheme.Length..].TrimStart(' '));
        var found = false;
        foreach (var digest in allowed)
        {
            // Every token is compared, so the time taken does not say which one matched.
            found |= CryptographicOperations.FixedTimeEquals(presented, digest);
        }
        return found;
    }

    // Sending

    private async Task PostAsync(HttpContext context)
    {
        var limits = settings.Limits;
        if (!IsJson(context.Request.ContentType))
        {
            await ErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type").ConfigureAwait(false);
            return;
        }
        // Refused without reading it. A client that waits for "100 Continue" never sends it.
        if (context.Request.ContentLength > limits.MaxBodyBytes)
        {
            await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "too_large").ConfigureAwait(false);
            return;
        }
        var body = await ReadBodyAsync(context.Request, limits.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
        if (body is null)
        {
            await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "too_large").ConfigureAwait(false);
            return;
        }

        IReadOnlyList<byte[]> operations;
        try
        {
            operations = Operations.ParseBatch(body, limits.MaxOpsPerPost);
        }
        catch (BatchRefusal refusal)
        {
            var fields = new List<(string, JsonWriter.Value)> { ("error", JsonWriter.Value.Of(refusal.Error)) };
            if (refusal.Index is { } index)
            {
                fields.Add(("index", JsonWriter.Value.Of(index)));
            }
            if (refusal.Reason is { } reason)
            {
                fields.Add(("reason", JsonWriter.Value.Of(reason)));
            }
            await AnswerAsync(context, StatusCodes.Status400BadRequest, JsonWriter.Encode(JsonWriter.Value.Object(fields))).ConfigureAwait(false);
            return;
        }

        if (Queue.TryAdd(operations) is not { } lastSeq)
        {
            context.Response.Headers.RetryAfter = "30";
            await ErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "queue_full").ConfigureAwait(false);
            return;
        }
        await AnswerAsync(context, StatusCodes.Status200OK, JsonWriter.Encode(JsonWriter.Value.Object(new[]
        {
            ("accepted", JsonWriter.Value.Of(operations.Count)),
            ("last_seq", JsonWriter.Value.Of(lastSeq)),
        }))).ConfigureAwait(false);
    }

    /// <summary><c>application/json</c>, with no parameter but an optional <c>charset=utf-8</c>.</summary>
    internal static bool IsJson(string? contentType)
    {
        if (contentType is null || !MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            || !parsed.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || parsed.Parameters.Count > 1)
        {
            return false;
        }
        return parsed.Parameters.All(parameter =>
            parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)
            && HeaderUtilities.RemoveQuotes(parameter.Value).Equals("utf-8", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads the body, or returns null once it passes <paramref name="limit"/>. A body without a length is read one
    /// byte past the limit at most, so a huge one costs no more than an allowed one.
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, int limit, CancellationToken cancellationToken)
    {
        var body = request.Body;
        if (request.ContentLength is { } announced)
        {
            var exact = new byte[announced];
            var read = 0;
            while (read < exact.Length)
            {
                var count = await body.ReadAsync(exact.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("the body ended early");
                }
                read += count;
            }
            return exact;
        }
        var buffer = new byte[Math.Min(limit + 1, 64 * 1024)];
        var filled = 0;
        while (true)
        {
            if (filled == buffer.Length)
            {
                if (filled > limit)
                {
                    return null;
                }
                Array.Resize(ref buffer, Math.Min(limit + 1, buffer.Length * 2));
            }
            var count = await body.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return buffer.AsSpan(0, filled).ToArray();
            }
            filled += count;
        }
    }

    // Fetching

    internal sealed record FetchQuery(string? Epoch, long After, int Limit, int Wait);

    private async Task FetchAsync(HttpContext context)
    {
        if (ParseQuery(context.Request.QueryString.Value, settings.Limits) is not { } query
            || Queue.Fetch(query.Epoch, query.After, query.Limit) is not { } batch)
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_query").ConfigureAwait(false);
            return;
        }
        if (batch.Operations.Count == 0 && query.Wait > 0)
        {
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping.Token);
            var timeUp = Task.Delay(TimeSpan.FromSeconds(query.Wait), waiting.Token);
            await Task.WhenAny(batch.Arrival, timeUp).ConfigureAwait(false);
            await waiting.CancelAsync().ConfigureAwait(false);
            if (context.RequestAborted.IsCancellationRequested)
            {
                return;
            }
            batch = Queue.Fetch(query.Epoch, query.After, query.Limit) ?? batch;
        }
        await AnswerAsync(context, StatusCodes.Status200OK, RelayQueue.Write(batch)).ConfigureAwait(false);
    }

    /// <summary>
    /// The query of <c>GET /v1/ops</c>, or null if a parameter appears twice, is unknown, or is out of its range.
    /// </summary>
    internal static FetchQuery? ParseQuery(string? raw, RelayLimits limits)
    {
        string? epoch = null;
        long after = 0;
        var limit = limits.MaxOpsPerAnswer;
        var wait = 0;
        var text = raw is { Length: > 0 } && raw[0] == '?' ? raw[1..] : raw ?? string.Empty;
        if (text.Length == 0)
        {
            return new FetchQuery(epoch, after, limit, wait);
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var piece in text.Split('&'))
        {
            var equals = piece.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return null;
            }
            var name = Uri.UnescapeDataString(piece[..equals]);
            var value = Uri.UnescapeDataString(piece[(equals + 1)..]);
            if (!seen.Add(name))
            {
                return null;
            }
            switch (name)
            {
                case "epoch" when IsEpoch(value):
                    epoch = value;
                    break;
                case "after" when WholeNumber(value, (1L << 53) - 1) is { } number:
                    after = number;
                    break;
                case "limit" when WholeNumber(value, limits.MaxOpsPerAnswer) is { } number && number >= 1:
                    limit = (int)number;
                    break;
                case "wait" when WholeNumber(value, limits.MaxWaitSeconds) is { } number:
                    wait = (int)number;
                    break;
                default:
                    return null;
            }
        }
        return new FetchQuery(epoch, after, limit, wait);
    }

    internal static bool IsEpoch(string text) =>
        text.Length is >= 16 and <= 64 && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static long? WholeNumber(string text, long max)
    {
        if (text.Length is 0 or > 18 || !text.All(char.IsAsciiDigit))
        {
            return null;
        }
        var value = long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        return value <= max ? value : null;
    }

    // Answers

    private static Task ErrorAsync(HttpContext context, int status, string error) =>
        AnswerAsync(context, status, JsonWriter.Encode(JsonWriter.Value.Object(new[] { ("error", JsonWriter.Value.Of(error)) })));

    private static async Task AnswerAsync(HttpContext context, int status, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Leaves Ctrl+C and SIGTERM to the program, which stops the relay itself.</summary>
    private sealed class QuietLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
