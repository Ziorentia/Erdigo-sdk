// The cloud relay of docs/cloud-relay.md: devices send operations to it, and a gateway on the Erdigo app's local
// network fetches them. It keeps them in memory, speaks plain HTTP, and is meant to sit behind an HTTPS reverse proxy.
//
//     ERDIGO_RELAY_SOURCE_TOKENS=... ERDIGO_RELAY_GATEWAY_TOKEN=... dotnet run --project examples/CloudRelay -- --listen 127.0.0.1:8080
//
// Tokens come only from the environment, because command-line arguments can be seen by other users of the computer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Erdigo.Examples.CloudRelay;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, press) =>
        {
            press.Cancel = true;
            stop.Cancel();
        };
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
        {
            signal.Cancel = true;
            stop.Cancel();
        });
        return await RelayCommand.RunAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, stop.Token).ConfigureAwait(false);
    }
}

/// <summary>A mistake in how the relay was started. Its message never repeats a token.</summary>
internal sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }
}

internal static class RelayCommand
{
    private const string Usage = """
usage: erdigo-relay [--listen HOST:PORT] [--keep-hours HOURS] [--behind-proxy]

  --listen HOST:PORT   Where to accept plain HTTP. 127.0.0.1:8080 by default. Port 0 picks a free port.
                       A host that is not a loopback address needs --behind-proxy.
  --keep-hours HOURS   How long an operation waits for the gateway, 1 to 168. 24 by default.
  --behind-proxy       HTTPS ends in a proxy in front of the relay, so it may listen on another address.

Environment
  ERDIGO_RELAY_SOURCE_TOKENS   One or more source tokens, separated by commas.
  ERDIGO_RELAY_GATEWAY_TOKEN   The gateway token. It must differ from every source token.
  A token is 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.
""";

    /// <summary>Runs until <paramref name="stop"/> is cancelled. Returns the exit code.</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        CancellationToken stop,
        RelayLimits? limits = null,
        Func<long>? clock = null)
    {
        if (args.Count == 1 && args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return 0;
        }
        RelaySettings settings;
        try
        {
            settings = Parse(args, environment);
        }
        catch (UsageException problem)
        {
            error.WriteLine($"erdigo-relay: {problem.Message}");
            return 1;
        }
        settings = settings with { Limits = limits ?? RelayLimits.Default };
        if (clock is not null)
        {
            settings = settings with { Clock = clock };
        }

        RelayServer server;
        try
        {
            server = await RelayServer.StartAsync(settings, output, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception problem) when (problem is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            error.WriteLine("erdigo-relay: could not listen there. The port may be in use, or the address not on this computer.");
            return 1;
        }
        await using (server.ConfigureAwait(false))
        {
            output.WriteLine($"Relay listening on {server.Url}");
            output.Flush();
            try
            {
                await Task.Delay(Timeout.Infinite, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        return 0;
    }

    internal static RelaySettings Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        string? listen = null;
        string? keepHours = null;
        var behindProxy = false;
        for (var index = 0; index < args.Count; index++)
        {
            // Errors never repeat what was typed, in case a token was put there by mistake.
            switch (args[index])
            {
                case "--behind-proxy" when !behindProxy:
                    behindProxy = true;
                    break;
                case "--listen" when listen is null && index + 1 < args.Count:
                    listen = args[++index];
                    break;
                case "--keep-hours" when keepHours is null && index + 1 < args.Count:
                    keepHours = args[++index];
                    break;
                default:
                    throw new UsageException("unexpected argument. Run 'erdigo-relay --help' for the options. Tokens are read from the environment only.");
            }
        }

        var (address, port) = ParseListen(listen ?? "127.0.0.1:8080", behindProxy);
        var hours = 24;
        if (keepHours is not null)
        {
            if (keepHours.Length is 0 or > 3 || !keepHours.All(char.IsAsciiDigit)
                || !int.TryParse(keepHours, NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours is < 1 or > 168)
            {
                throw new UsageException("--keep-hours must be a whole number from 1 to 168");
            }
        }

        var sources = (environment("ERDIGO_RELAY_SOURCE_TOKENS") ?? string.Empty)
            .Split(',')
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();
        if (sources.Length == 0)
        {
            throw new UsageException("set ERDIGO_RELAY_SOURCE_TOKENS to one or more source tokens, separated by commas");
        }
        if (!sources.All(IsToken))
        {
            throw new UsageException("every source token in ERDIGO_RELAY_SOURCE_TOKENS must be 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'");
        }
        var gateway = environment("ERDIGO_RELAY_GATEWAY_TOKEN")?.Trim() ?? string.Empty;
        if (gateway.Length == 0)
        {
            throw new UsageException("set ERDIGO_RELAY_GATEWAY_TOKEN to the gateway token");
        }
        if (!IsToken(gateway))
        {
            throw new UsageException("ERDIGO_RELAY_GATEWAY_TOKEN must be 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'");
        }
        if (sources.Contains(gateway, StringComparer.Ordinal))
        {
            throw new UsageException("the gateway token must differ from every source token");
        }
        return new RelaySettings(address, port, hours, sources, gateway);
    }

    internal static bool IsToken(string token) =>
        token.Length is >= 32 and <= 256 && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~');

    /// <summary><c>HOST:PORT</c>, with an IPv6 address in brackets: <c>[::1]:8080</c>.</summary>
    internal static (IPAddress Address, int Port) ParseListen(string text, bool behindProxy)
    {
        const string form = "--listen takes HOST:PORT, where HOST is an IP address or localhost, for example 127.0.0.1:8080 or [::1]:8080";
        string host;
        string portText;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf("]:", StringComparison.Ordinal);
            if (close < 0)
            {
                throw new UsageException(form);
            }
            host = text[1..close];
            portText = text[(close + 2)..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new UsageException(form);
            }
            host = text[..colon];
            portText = text[(colon + 1)..];
        }
        if (portText.Length is 0 or > 5 || !portText.All(char.IsAsciiDigit)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port > 65535)
        {
            throw new UsageException(form);
        }
        IPAddress? address;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
        }
        else if (!IPAddress.TryParse(host, out address))
        {
            throw new UsageException(form);
        }
        if (!IPAddress.IsLoopback(address) && !behindProxy)
        {
            throw new UsageException("the relay speaks plain HTTP, so it listens only on a loopback address unless --behind-proxy says that HTTPS ends in a proxy in front of it");
        }
        return (address, port);
    }
}
