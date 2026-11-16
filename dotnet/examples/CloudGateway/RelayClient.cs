using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Send;

namespace Erdigo.Examples.CloudGateway;

/// <summary>The relay could not be reached, or answered with a server error. Worth trying again.</summary>
internal sealed class RelayUnavailableException : Exception
{
}

/// <summary>The relay refused the token, or answered with something the relay API does not allow. Exit code 2.</summary>
internal sealed class RelayRefusedException : Exception
{
    public RelayRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>The relay's address, checked: HTTPS, or plain HTTP on this computer only.</summary>
internal sealed class RelayAddress
{
    private RelayAddress(string key) => Key = key;

    /// <summary>The address in one spelling, without a trailing slash. relay-state.json keeps the place for this address only.</summary>
    public string Key { get; }

    public static RelayAddress Parse(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new UsageException("--relay must be the relay's address, such as https://relay.example.com");
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !IsLoopback(uri))
        {
            throw new UsageException("--relay must start with https://. Plain http:// is accepted only for a loopback host, for testing on one computer.");
        }
        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new UsageException("--relay takes the relay's address alone, with no user name, query or fragment");
        }
        return new RelayAddress($"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}");
    }

    /// <summary>
    /// The name localhost in any case, or a loopback address. Uri.IsLoopback is not used, because it takes 'localhost'
    /// only in lower case, while host names are compared without regard to case.
    /// </summary>
    private static bool IsLoopback(Uri uri) => uri.HostNameType switch
    {
        UriHostNameType.Dns => string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase),
        UriHostNameType.IPv4 or UriHostNameType.IPv6 => IPAddress.TryParse(uri.IdnHost, out var address) && IPAddress.IsLoopback(address),
        _ => false,
    };

    public Uri Operations(string query) => new($"{Key}/v1/ops?{query}");
}

/// <summary>One answer to <c>GET /v1/ops</c>, checked against the relay API.</summary>
internal sealed record RelayAnswer(string Epoch, bool Reset, bool Missed, IReadOnlyList<(long Seq, JsonValue.Object Op)> Operations)
{
    public const int Limit = 100;

    private static readonly string[] Fields = { "epoch", "reset", "missed", "ops" };
    private static readonly string[] EntryFields = { "seq", "received_ms", "op" };
    private const long MaxSeq = (1L << 53) - 1;

    /// <summary>Checks the answer to a fetch sent with <paramref name="sentEpoch"/> and <paramref name="sentAfter"/>.</summary>
    public static RelayAnswer Parse(byte[] body, string? sentEpoch, long sentAfter)
    {
        JsonValue document;
        try
        {
            // Deep enough that an operation of the wrong shape is refused as an operation, not as the whole answer.
            document = StrictJson.Parse(body, maxDepth: 16);
        }
        catch (JsonParseException)
        {
            throw Broken("the answer is not one JSON object");
        }
        if (document is not JsonValue.Object root || root.Fields.Count != Fields.Length || !Fields.All(root.Fields.ContainsKey))
        {
            throw Broken("the answer must have epoch, reset, missed and ops, and nothing else");
        }
        if (root.Fields["epoch"] is not JsonValue.String { Value: var epoch } || !IsEpoch(epoch))
        {
            throw Broken("the epoch is not 16 to 64 letters, digits, '-' or '_'");
        }
        if (root.Fields["reset"] is not JsonValue.Bool { Value: var reset } || root.Fields["missed"] is not JsonValue.Bool { Value: var missed })
        {
            throw Broken("reset and missed must be true or false");
        }
        if (!reset && !string.Equals(epoch, sentEpoch, StringComparison.Ordinal))
        {
            throw Broken("the relay kept a numbering it was not asked about");
        }
        if (root.Fields["ops"] is not JsonValue.Array list || list.Values.Count > Limit)
        {
            throw Broken($"ops must be a list of at most {Limit} operations");
        }
        var operations = new List<(long, JsonValue.Object)>(list.Values.Count);
        var previous = reset ? 0 : sentAfter;
        foreach (var value in list.Values)
        {
            if (value is not JsonValue.Object entry || entry.Fields.Count != EntryFields.Length || !EntryFields.All(entry.Fields.ContainsKey))
            {
                throw Broken("an entry in ops must have seq, received_ms and op, and nothing else");
            }
            if (entry.Fields["seq"] is not JsonValue.Integer { Value: var seq } || seq <= previous || seq > MaxSeq)
            {
                throw Broken("the sequence numbers must go up, and start above the one acknowledged");
            }
            if (entry.Fields["received_ms"] is not JsonValue.Integer { Value: var received } || received < 0 || received > MaxSeq)
            {
                throw Broken("received_ms must be milliseconds since 1970");
            }
            if (entry.Fields["op"] is not JsonValue.Object operation)
            {
                throw Broken("an operation must be an object");
            }
            operations.Add((seq, operation));
            previous = seq;
        }
        return new RelayAnswer(epoch, reset, missed, operations);
    }

    public static bool IsEpoch(string text) =>
        text.Length is >= 16 and <= 64 && text.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static RelayRefusedException Broken(string reason) => new($"The relay's answer does not follow the relay API: {reason}.");
}

/// <summary>Fetches from the relay with outgoing HTTPS requests only.</summary>
internal sealed class RelayClient : IDisposable
{
    private const int MaxAnswerBytes = 8 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly RelayAddress relay;
    private readonly string token;
    private readonly TimeSpan timeout;

    public RelayClient(RelayAddress relay, string token, TimeSpan timeout)
    {
        this.relay = relay;
        this.token = token;
        this.timeout = timeout;
        http = new HttpClient(new SocketsHttpHandler
        {
            // A redirect would carry the request somewhere the person did not name.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(50),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public void Dispose() => http.Dispose();

    public async Task<RelayAnswer> FetchAsync(string? epoch, long after, int wait, CancellationToken stop)
    {
        var query = epoch is null
            ? string.Create(CultureInfo.InvariantCulture, $"limit={RelayAnswer.Limit}&wait={wait}")
            : string.Create(CultureInfo.InvariantCulture, $"epoch={epoch}&after={after}&limit={RelayAnswer.Limit}&wait={wait}");
        using var request = new HttpRequestMessage(HttpMethod.Get, relay.Operations(query));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status == 401)
            {
                throw new RelayRefusedException("The relay refused the token.");
            }
            // A proxy in front of the relay may limit requests, or time out.
            if (status is 408 or 429 or >= 500)
            {
                throw new RelayUnavailableException();
            }
            if (status != 200)
            {
                throw new RelayRefusedException(string.Create(CultureInfo.InvariantCulture, $"The relay's answer does not follow the relay API: status {status}."));
            }
            var type = response.Content.Headers.ContentType;
            if (type is null || !string.Equals(type.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
                || (type.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            {
                throw new RelayRefusedException("The relay's answer does not follow the relay API: it is not application/json.");
            }
            var body = await ReadAtMostAsync(response.Content, limit.Token).ConfigureAwait(false);
            return RelayAnswer.Parse(body, epoch, after);
        }
        catch (Exception error) when (error is HttpRequestException or IOException
            || (error is OperationCanceledException && !stop.IsCancellationRequested))
        {
            throw new RelayUnavailableException();
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const string tooLarge = "The relay's answer does not follow the relay API: it is larger than 8 MiB.";
        if (content.Headers.ContentLength > MaxAnswerBytes)
        {
            throw new RelayRefusedException(tooLarge);
        }
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaxAnswerBytes)
            {
                throw new RelayRefusedException(tooLarge);
            }
            output.Write(chunk, 0, read);
        }
        return output.ToArray();
    }
}
