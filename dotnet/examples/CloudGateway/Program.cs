// The gateway of docs/cloud-relay.md. It runs on the Erdigo app's local network, fetches operations from a cloud relay
// with outgoing HTTPS requests, and writes them to the app through the SDK, as a paired device.
//
//     dotnet run --project examples/CloudGateway -- pair --name "Cloud gateway"
//     ERDIGO_RELAY_TOKEN=... dotnet run --project examples/CloudGateway -- run --relay https://relay.example.com
//
// The token comes only from the environment, and the pairing code only from the keyboard or a private file, because
// command-line arguments can be seen by other users of the computer.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;
using Erdigo.Send;

namespace Erdigo.Examples.CloudGateway;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Ctrl+C and SIGTERM stop 'run' between steps. 'pair' keeps the usual behaviour, so Ctrl+C at the prompt ends it.
        var stop = new CancellationTokenSource();
        PosixSignalRegistration? terminate = null;
        if (args.Length > 0 && args[0] == "run")
        {
            Console.CancelKeyPress += (_, press) =>
            {
                press.Cancel = true;
                stop.Cancel();
            };
            terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                stop.Cancel();
            });
        }
        using (terminate)
        {
            return await Gateway.RunAsync(args, Environment.GetEnvironmentVariable, Console.Out, Console.Error, stop.Token).ConfigureAwait(false);
        }
    }
}

internal static class Gateway
{
    public const string DefaultProfile = "cloud-gateway";

    private const string Usage = """
erdigo-gateway: fetch operations from a cloud relay and write them to the Erdigo app on this local network.

  pair --name NAME [--kinds LIST] [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
       [--profile NAME] [--state-dir DIR]
      Pairs the gateway with the app, as a device. The code is read from the keyboard, or from a file only you
      can read. A bare code means you compare the app's key and type yes. --kinds is pin,marker,note,photo,track
      by default.
  run --relay URL [--host HOST --port PORT] [--once] [--profile NAME] [--state-dir DIR]
      Fetches from the relay and writes to the app until stopped. --relay must start with https://, or http://
      for a loopback host. --once stops when the relay has nothing more. The gateway token is read from
      ERDIGO_RELAY_TOKEN.

  --host and --port give the app's address. Without them the gateway finds the app on the local network.
  --profile is cloud-gateway by default. --state-dir keeps the gateway's key, pairing and place elsewhere.
  Only one run at a time may use a profile.

Exit codes of run: 0 stopped, or nothing more with --once; 1 a mistake in the command line or the environment,
or a profile another run is using; 2 the relay refused the token or broke the relay API; 3 with --once, the app
or the relay could not be reached.
""";

    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "code-file", "fingerprint", "host", "kinds", "name", "once", "port", "profile", "relay", "state-dir",
    };

    /// <summary>Runs one command and returns its exit code.</summary>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<string, string?> environment,
        TextWriter output,
        TextWriter error,
        CancellationToken stop,
        GatewayTimings? timings = null)
    {
        if (args.Count == 0 || args[0] is "help" or "--help" or "-h")
        {
            output.WriteLine(Usage);
            return 0;
        }
        try
        {
            switch (args[0])
            {
                case "pair":
                    await PairAsync(new Options(args.Skip(1).ToArray(), new[] { "once" }), output).ConfigureAwait(false);
                    return 0;
                case "run":
                    return await RunRelayAsync(new Options(args.Skip(1).ToArray(), new[] { "once" }), environment, output, stop, timings ?? new GatewayTimings()).ConfigureAwait(false);
                default:
                    output.WriteLine(Usage);
                    throw new UsageException("unknown command");
            }
        }
        catch (Exception problem) when (problem is UsageException or ErdigoException or WireException or GatewayStoppedException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"erdigo-gateway: {problem.Message}");
            return 1;
        }
        catch (RelayRefusedException problem)
        {
            error.WriteLine($"erdigo-gateway: {problem.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            output.WriteLine("Stopped.");
            return 0;
        }
    }

    // Pairing

    private static async Task PairAsync(Options options, TextWriter output)
    {
        var directory = StateDirectory(options);
        var name = options.Required("name");
        var kinds = ParseKinds(options.Optional("kinds") ?? "pin,marker,note,photo,track");
        var codeFile = options.Optional("code-file");
        var fingerprint = options.Optional("fingerprint")?.ToLowerInvariant();
        var target = Target(options);
        options.Finish();

        if (fingerprint is not null && !Hex.IsFingerprint(fingerprint))
        {
            throw new UsageException("--fingerprint must be 64 hexadecimal digits");
        }
        var store = new FileDeviceStore(directory);
        if (store.Pairings().Count > 0)
        {
            throw new UsageException("this profile is already paired, and a gateway's profile holds one pairing. Use another --profile or --state-dir.");
        }
        var text = codeFile is not null ? ReadCodeFile(codeFile) : ReadCodeFromKeyboard();
        PairingTicket ticket;
        try
        {
            ticket = PairingTicket.Parse(text);
        }
        catch (WireException problem)
        {
            throw new UsageException(problem.Detail);
        }
        if (fingerprint is not null)
        {
            if (ticket.Fingerprint is { } fromQr && fromQr != fingerprint)
            {
                throw new ErdigoException(ErdigoErrorKind.FingerprintMismatch);
            }
            ticket = new PairingTicket(ticket.Code, fingerprint);
        }
        // Only a person can compare the key, and nobody can answer through a pipe or a file. The app is not contacted
        // when no answer is possible.
        if (ticket.Fingerprint is null && !TerminalInput.PersonIsThere)
        {
            throw new UsageException("a bare code needs a person to compare the app's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.");
        }

        const string waiting = "Waiting for the request to be confirmed in the Erdigo app...";
        var personCompares = ticket.Fingerprint is null;
        output.WriteLine(personCompares ? "Checking the code with the Erdigo app..." : waiting);
        output.Flush();
        using var identity = store.LoadOrCreateIdentity();
        var pairing = await Pairing.PairAsync(
            target,
            ticket,
            identity,
            name,
            kinds,
            confirmKey: (key, _) =>
            {
                // Standard input is a terminal here, because a bare code is refused otherwise.
                TerminalInput.DiscardTypedAhead();
                Console.WriteLine($"Receiver key: {key}");
                Console.Write("If this matches the key shown in the app, type yes: ");
                Console.Out.Flush();
                var answer = TerminalInput.ReadLine(echo: true, maxLength: 256);
                if (answer is null || answer.Trim().ToLowerInvariant() != "yes")
                {
                    Console.WriteLine();
                    return Task.FromResult(false);
                }
                Console.WriteLine(waiting);
                return Task.FromResult(true);
            }).ConfigureAwait(false);
        store.Save(pairing);
        output.WriteLine($"Paired. Allowed: {string.Join(", ", pairing.Kinds.Select(kind => kind.ToWire()))}.");
        if (!personCompares)
        {
            output.WriteLine($"Receiver key: {IntegrationV1.ShortFingerprint(pairing.Fingerprint)}");
        }
        output.WriteLine("Next, set ERDIGO_RELAY_TOKEN and run: erdigo-gateway run --relay URL");
    }

    /// <summary>Reads a file only if nobody else can read it, as erdigo-send does.</summary>
    private static string ReadCodeFile(string path)
    {
        byte[] data;
        try
        {
            data = PrivateFile.ReadAllBytes(path, 1024);
        }
        catch (PrivateFileException problem)
        {
            throw problem.Problem switch
            {
                PrivateFileProblem.Insecure => new UsageException(OperatingSystem.IsWindows()
                    ? "the code file must be a regular file that only you can read"
                    : "the code file must be a regular file you own, with mode 0600, so that nobody else can read it"),
                PrivateFileProblem.TooLarge => new UsageException("the code file is too large"),
                _ => new UsageException("could not open the code file"),
            };
        }
        try
        {
            // A byte order mark is how some editors mark UTF-8. It is not part of the code.
            var start = data.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            return Text.StrictUtf8.GetString(data, start, data.Length - start);
        }
        catch (DecoderFallbackException)
        {
            throw new UsageException("the code file is not UTF-8 text");
        }
        finally
        {
            Array.Clear(data);
        }
    }

    private static string ReadCodeFromKeyboard()
    {
        if (!OperatingSystem.IsWindows())
        {
            // From the terminal itself, with echo off, whatever standard input is, as readpassphrase does.
            return UnixTerminal.ReadSecretLine("Pairing code: ", 1024)
                ?? throw new UsageException("no terminal to read the code from. Use --code-file with a file of mode 0600.");
        }
        if (Console.IsInputRedirected)
        {
            throw new UsageException("no terminal to read the code from. Use --code-file with a file only you can read.");
        }
        Console.Error.Write("Pairing code: ");
        Console.Error.Flush();
        var code = TerminalInput.ReadLine(echo: false, maxLength: 1024);
        Console.Error.WriteLine();
        return code ?? string.Empty;
    }

    private static IReadOnlyCollection<ItemKind> ParseKinds(string text)
    {
        var kinds = new HashSet<ItemKind>();
        foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            kinds.Add(ItemKinds.FromWire(Text.TrimSpaces(piece))
                ?? throw new UsageException("--kinds takes pin, marker, note, photo and track, separated by commas"));
        }
        if (kinds.Count == 0)
        {
            throw new UsageException("--kinds needs at least one item kind");
        }
        return kinds;
    }

    // Running

    private static async Task<int> RunRelayAsync(Options options, Func<string, string?> environment, TextWriter output, CancellationToken stop, GatewayTimings timings)
    {
        var directory = StateDirectory(options);
        var relayText = options.Required("relay");
        var once = options.Flag("once");
        var target = Target(options);
        options.Finish();

        var address = RelayAddress.Parse(relayText);
        var token = environment("ERDIGO_RELAY_TOKEN")?.Trim() ?? string.Empty;
        if (token.Length == 0)
        {
            throw new UsageException("set ERDIGO_RELAY_TOKEN to the relay's gateway token. It is never taken as an argument.");
        }
        if (token.Length is < 32 or > 256 || !token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '~'))
        {
            throw new UsageException("ERDIGO_RELAY_TOKEN must be 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'");
        }

        var store = new FileDeviceStore(directory);
        var pairings = store.Pairings();
        if (pairings.Count != 1)
        {
            throw new UsageException(pairings.Count == 0
                ? "this profile has no pairing. Run 'erdigo-gateway pair' first."
                : "this profile holds more than one pairing, and a gateway's profile must hold exactly one");
        }
        // Held until the run ends. The system lets it go if the process dies.
        using var running = HoldProfile(store.Directory);
        var place = RelayState.Load(store.Directory, address);

        using var identity = store.LoadOrCreateIdentity();
        // The SDK holds a request while the app is out of reach and resends it with the same number (spec section 13),
        // so an answer lost on the way does not make the app apply it twice. Only --once gives up. The SDK counts its
        // time from the last connection it made, so the gateway also keeps its own count for each operation.
        var retry = once ? RetryPolicy.UpTo(timings.GiveUpAfter) : RetryPolicy.Forever;
        using var client = new ErdigoClient(identity, pairings[0], store, target, retry, timings.SdkConnectTimeout, timings.SdkAckTimeout);
        var fetchTimeout = once && timings.OnceRequestTimeout < timings.RequestTimeout ? timings.OnceRequestTimeout : timings.RequestTimeout;
        using var relay = new RelayClient(address, token, fetchTimeout);
        if (!once)
        {
            output.WriteLine("Waiting for operations from the relay. Press Ctrl+C to stop.");
            output.Flush();
        }
        return await new GatewayRun(relay, address, client, store.Directory, once, timings, output, stop).RunAsync(place).ConfigureAwait(false);
    }

    /// <summary>The file in the state folder that a running gateway keeps locked.</summary>
    public const string RunLockFile = ".gateway.lock";

    /// <summary>
    /// Locks <see cref="RunLockFile"/> for as long as the returned stream is open, or exits with code 1 if another
    /// <c>run</c> holds it. Two gateways with one key would take each other's connection to the app and repeat each
    /// other's work. The SDK locks its own files only while it writes them, so that does not keep a second run out.
    /// FileShare.None is a sharing mode on Windows and an advisory lock (flock) elsewhere, held by the open file, so it
    /// also keeps out a second run in the same process.
    /// </summary>
    private static FileStream HoldProfile(string directory)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            return new FileStream(Path.Combine(directory, RunLockFile), options);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UsageException($"could not open {RunLockFile} in the state folder");
        }
        catch (IOException)
        {
            throw new UsageException("another 'run' is using this profile. Only one gateway at a time may use a profile or state folder.");
        }
    }

    // Options, as erdigo-send reads them

    private static bool IsProfileCharacter(Rune scalar)
    {
        if (scalar.Value is '-' or '_')
        {
            return true;
        }
        return Rune.GetUnicodeCategory(scalar) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
            _ => false,
        };
    }

    private static string StateDirectory(Options options)
    {
        var given = options.Optional("profile");
        var profile = given ?? DefaultProfile;
        if (profile.Length == 0 || !profile.EnumerateRunes().All(IsProfileCharacter))
        {
            throw new UsageException("--profile may contain only letters, digits, '-' and '_'");
        }
        if (options.Optional("state-dir") is { } path)
        {
            if (given is not null)
            {
                throw new UsageException("--state-dir and --profile cannot be used together, because a state folder holds one identity");
            }
            return path;
        }
        return FileDeviceStore.DefaultDirectory(profile);
    }

    private static ReceiverTarget Target(Options options)
    {
        var host = options.Optional("host");
        ushort? port = null;
        if (options.Optional("port") is { } text)
        {
            port = ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new UsageException("--port must be between 0 and 65535");
        }
        return (host, port) switch
        {
            (null, null) => ReceiverTarget.Discover,
            ({ } givenHost, { } givenPort) => ReceiverTarget.Address(givenHost, givenPort),
            _ => throw new UsageException("--host and --port go together"),
        };
    }

    /// <summary>A deliberately small option parser: <c>--name value</c> pairs and bare flags, nothing else.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        private readonly HashSet<string> flags = new(StringComparer.Ordinal);
        private readonly HashSet<string> used = new(StringComparer.Ordinal);

        public Options(IReadOnlyList<string> arguments, IEnumerable<string> allowedFlags)
        {
            var allowed = new HashSet<string>(allowedFlags, StringComparer.Ordinal);
            var index = 0;
            while (index < arguments.Count)
            {
                var argument = arguments[index];
                // Errors here never repeat what was typed, because it might be a pairing code or a token.
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException("unexpected argument. Options start with --, and neither a pairing code nor a token is given as an argument.");
                }
                var name = argument[2..];
                if (name is "code" or "token" || name.StartsWith("code=", StringComparison.Ordinal) || name.StartsWith("token=", StringComparison.Ordinal))
                {
                    throw new UsageException("pairing codes and tokens are not accepted as arguments, because arguments appear in the process list and shell history. Use --code-file, or ERDIGO_RELAY_TOKEN.");
                }
                var equals = name.IndexOf('=', StringComparison.Ordinal);
                if (equals >= 0)
                {
                    var option = name[..equals];
                    throw new UsageException($"write {(KnownOptions.Contains(option) ? $"--{option}" : "an unknown option")} and its value as two separate arguments");
                }
                if (allowed.Contains(name))
                {
                    flags.Add(name);
                    index += 1;
                    continue;
                }
                if (!KnownOptions.Contains(name))
                {
                    throw new UsageException("an unknown option was given. See 'erdigo-gateway help'.");
                }
                if (index + 1 >= arguments.Count)
                {
                    throw new UsageException($"--{name} needs a value");
                }
                if (!values.TryAdd(name, arguments[index + 1]))
                {
                    throw new UsageException($"--{name} was given twice");
                }
                index += 2;
            }
        }

        public bool Flag(string name)
        {
            used.Add(name);
            return flags.Contains(name);
        }

        public string? Optional(string name)
        {
            used.Add(name);
            return values.TryGetValue(name, out var value) ? value : null;
        }

        public string Required(string name) => Optional(name) ?? throw new UsageException($"--{name} is required");

        /// <summary>Call after reading every option the command understands.</summary>
        public void Finish()
        {
            var unknown = values.Keys.Concat(flags).Where(name => !used.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).FirstOrDefault();
            if (unknown is not null)
            {
                throw new UsageException($"--{unknown} does not apply to this command");
            }
        }
    }
}
