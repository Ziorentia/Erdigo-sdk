using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Erdigo.Core;
using Erdigo.Sdk;
using Mock = Erdigo.MockReceiver.MockReceiver;

namespace Erdigo.Send;

internal static class Program
{
    private const string Usage = """
erdigo-send: write pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network.

Pairing
  pair --name NAME --kinds pin,marker,note,photo,track [--label LABEL]
       [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
      The code is read from the keyboard, or from a file only you can read.
      It is never accepted as a command-line argument.
      A bare code means you compare the receiver's key and type yes before the receiver is asked to pair.
      That needs standard input and output to be a terminal, so a bare code is refused without one.
      The QR text or --fingerprint compares the key for you, so nothing is asked.
  list
  forget --to LABEL
  identity
      Prints this device's fingerprint.

Writing
  ping
  pin          --id ID --lat N --lon N [--title TEXT]
  marker       --id ID --lat N --lon N [--title TEXT] [--color COLOR]
  note         --id ID --lat N --lon N (--text TEXT | --text-file FILE)
  photo        --id ID --lat N --lon N --file FILE [--title TEXT]
  track-add    --id ID --lat N --lon N [--ts MILLISECONDS] [--alt METRES] [--title TEXT]
  track-finish --id ID
  move         --id ID --lat N --lon N
  delete       --id ID
  delete-all   --yes
      Options: --to LABEL (needed with several receivers), --host HOST --port PORT, --retry-for SECONDS

Testing without the Erdigo app
  serve --mock [--port PORT] [--lan] [--bonjour] [--code-file FILE] [--ask | --auto-approve] [--redact]
      Runs an in-memory receiver. By default only this computer can reach it, and it allows every
      pairing request. Use --ask to answer each request yourself, as a person would in the app.
      With --lan anyone on the network can reach it, so it asks you about each request unless
      --auto-approve is given, for a test that runs unattended.
      --bonjour advertises it on the local network with multicast DNS.
      Where its output is saved or shared, use --code-file FILE, which only you can read, instead of
      showing the pairing code, and --redact to hide codes, keys, device names and what items hold.
      Pairing questions go to standard error and always show the device's name and address.
      Type 'help' while it runs to see its commands.

For pairing and writing commands
  --profile NAME    Use a separate identity and pairings (default: "default").
  --state-dir DIR   Keep state in DIR instead of the default folder.
  --redact          Hide labels, keys and other identifying output, for pasting into a bug report.
                    With pair, it needs the QR text or --fingerprint, because a hidden key cannot be compared.

On Windows the device key is encrypted with DPAPI for the current Windows user, in %LOCALAPPDATA%\ErdigoSDK.
Elsewhere it is kept in a file of mode 0600.

""";

    /// <summary>
    /// Every option the tool has. Errors name an option only if it is on this list, because anything else that was
    /// typed might be a pairing code.
    /// </summary>
    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "alt", "ask", "auto-approve", "bonjour", "code-file", "color", "file", "fingerprint", "host", "id", "keychain", "kinds", "label", "lan",
        "lat", "lon", "mock", "name", "port", "profile", "redact", "retry-for", "state-dir", "text", "text-file", "title", "to", "ts", "yes",
    };

    private static string OptionDescription(string name) => KnownOptions.Contains(name) ? $"--{name}" : "an unknown option";

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
                // Errors here never repeat what was typed, because it might be a pairing code.
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new UsageException("unexpected argument. Options start with --, and a pairing code is never given as an argument.");
                }
                var name = argument[2..];
                if (name == "code" || name.StartsWith("code=", StringComparison.Ordinal))
                {
                    throw new UsageException("the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. Type it when asked, or use --code-file.");
                }
                var equals = name.IndexOf('=', StringComparison.Ordinal);
                if (equals >= 0)
                {
                    throw new UsageException($"write {OptionDescription(name[..equals])} and its value as two separate arguments");
                }
                if (allowed.Contains(name))
                {
                    flags.Add(name);
                    index += 1;
                }
                else
                {
                    if (!KnownOptions.Contains(name))
                    {
                        throw new UsageException("an unknown option was given. See 'erdigo-send help'.");
                    }
                    if (index + 1 >= arguments.Count)
                    {
                        throw new UsageException($"--{name} needs a value");
                    }
                    if (values.ContainsKey(name))
                    {
                        throw new UsageException($"--{name} was given twice");
                    }
                    values[name] = arguments[index + 1];
                    index += 2;
                }
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

        public double RequiredDouble(string name)
        {
            var text = Required(name);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            {
                throw new UsageException($"--{name} must be a number");
            }
            return value;
        }

        public ushort? OptionalPort()
        {
            var text = Optional("port");
            if (text is null)
            {
                return null;
            }
            if (!ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                throw new UsageException("--port must be between 0 and 65535");
            }
            return value;
        }

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

    /// <summary>Reads a file only if nobody else can read it, as ssh does for its keys.</summary>
    private static string ReadCodeFile(string path)
    {
        byte[] data;
        try
        {
            data = PrivateFile.ReadAllBytes(path, 1024);
        }
        catch (PrivateFileException error)
        {
            throw error.Problem switch
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

    private sealed class Context
    {
        public Context(FileDeviceStore store, bool redact)
        {
            Store = store;
            Redact = redact;
        }

        public FileDeviceStore Store { get; }

        public bool Redact { get; }

        public string Shown(string text) => Redact ? "<redacted>" : text;
    }

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

    private static Context MakeContext(Options options)
    {
        var profile = options.Optional("profile") ?? "default";
        if (profile.Length == 0 || !profile.EnumerateRunes().All(IsProfileCharacter))
        {
            throw new UsageException("--profile may contain only letters, digits, '-' and '_'");
        }
        string directory;
        if (options.Optional("state-dir") is { } path)
        {
            if (options.Optional("profile") is not null)
            {
                throw new UsageException("--state-dir and --profile cannot be used together, because a state folder holds one identity");
            }
            directory = path;
        }
        else
        {
            directory = FileDeviceStore.DefaultDirectory(profile);
        }
        if (options.Flag("keychain"))
        {
            throw new UsageException("--keychain belongs to the macOS SDK. This edition keeps the device key with DPAPI on Windows, and in a file of mode 0600 elsewhere.");
        }
        return new Context(new FileDeviceStore(directory), options.Flag("redact"));
    }

    private static IReadOnlyCollection<ItemKind> ParseKinds(string text)
    {
        var kinds = new HashSet<ItemKind>();
        foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kind = ItemKinds.FromWire(Text.TrimSpaces(piece))
                ?? throw new UsageException("--kinds takes pin, marker, note, photo and track, separated by commas.");
            kinds.Add(kind);
        }
        if (kinds.Count == 0)
        {
            throw new UsageException("--kinds needs at least one item kind");
        }
        return kinds;
    }

    private static ReceiverTarget Target(Options options)
    {
        var host = options.Optional("host");
        var port = options.OptionalPort();
        return (host, port) switch
        {
            (null, null) => ReceiverTarget.Discover,
            ({ } givenHost, { } givenPort) => ReceiverTarget.Address(givenHost, givenPort),
            _ => throw new UsageException("--host and --port go together"),
        };
    }

    private static ReceiverPairing SelectPairing(Context context, string? label)
    {
        var pairings = context.Store.Pairings();
        if (label is not null)
        {
            var found = pairings.Where(pairing => pairing.Label == label).ToList();
            if (found.Count != 1)
            {
                throw new UsageException(found.Count == 0
                    ? "no receiver is paired under that label. See 'erdigo-send list'."
                    : "several receivers have that label. Forget one of them first.");
            }
            return found[0];
        }
        return pairings.Count switch
        {
            0 => throw new UsageException("no receiver is paired yet. Run 'erdigo-send pair' first."),
            1 => pairings[0],
            _ => throw new UsageException("several receivers are paired. Choose one with --to LABEL."),
        };
    }

    private static Coordinate ParseCoordinate(Options options)
    {
        try
        {
            var latitude = options.RequiredDouble("lat");
            var longitude = options.RequiredDouble("lon");
            return new Coordinate(latitude, longitude);
        }
        catch (WireException error)
        {
            throw new UsageException(error.Detail);
        }
    }

    private static async Task RunPairAsync(IReadOnlyList<string> arguments)
    {
        var options = new Options(arguments, new[] { "keychain", "redact" });
        var context = MakeContext(options);
        var name = options.Required("name");
        var kinds = ParseKinds(options.Required("kinds"));
        var label = options.Optional("label");
        var codeFile = options.Optional("code-file");
        var fingerprint = options.Optional("fingerprint")?.ToLowerInvariant();
        var where = Target(options);
        options.Finish();

        if (fingerprint is not null && !Hex.IsFingerprint(fingerprint))
        {
            throw new UsageException("--fingerprint must be 64 hexadecimal digits");
        }
        if (label is not null && context.Store.Pairings().Any(pairing => pairing.Label == label))
        {
            throw new UsageException("a receiver is already paired under that label. Choose another, or use 'erdigo-send forget --to LABEL' first.");
        }
        var text = codeFile is not null ? ReadCodeFile(codeFile) : ReadCodeFromKeyboard();
        PairingTicket ticket;
        try
        {
            ticket = PairingTicket.Parse(text);
        }
        catch (WireException error)
        {
            throw new UsageException(error.Detail);
        }
        if (fingerprint is not null)
        {
            if (ticket.Fingerprint is { } fromQr && fromQr != fingerprint)
            {
                throw new ErdigoException(ErdigoErrorKind.FingerprintMismatch);
            }
            ticket = new PairingTicket(ticket.Code, fingerprint);
        }
        if (ticket.Fingerprint is null && context.Redact)
        {
            throw new UsageException("--redact hides the receiver's key, so it cannot be compared for a bare code. Use the QR text, or give the full 64-digit key with --fingerprint.");
        }
        // Only a person can compare the key. Whatever a pipe or a file holds was written before the key existed, so
        // it is no answer, and a question sent to a file would be answered unseen. The receiver is not contacted
        // when no answer is possible.
        if (ticket.Fingerprint is null && !TerminalInput.PersonIsThere)
        {
            throw new UsageException("a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.");
        }

        const string waiting = "Waiting for the request to be confirmed on the receiver...";
        var personCompares = ticket.Fingerprint is null;
        Console.WriteLine(personCompares ? "Checking the code with the receiver..." : waiting);
        var identity = context.Store.LoadOrCreateIdentity();
        var pairing = await Pairing.PairAsync(
            where,
            ticket,
            identity,
            name,
            kinds,
            label,
            // A bare code proves only that the other side knows the code, and only a person can tell whether this is
            // the right receiver. This runs before the receiver is asked to pair.
            confirmKey: (key, _) =>
            {
                // Only an answer given after seeing the key counts, so anything typed before it is discarded.
                // Standard input is a terminal here, because a bare code is refused otherwise.
                TerminalInput.DiscardTypedAhead();
                Console.WriteLine($"Receiver key: {key}");
                Console.Write("If this matches the key shown on the receiver, type yes: ");
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
        context.Store.Save(pairing);
        Console.WriteLine($"Paired as '{context.Shown(pairing.Label)}'. Allowed: {string.Join(", ", pairing.Kinds.Select(kind => kind.ToWire()))}.");
        if (!personCompares)
        {
            Console.WriteLine($"Receiver key: {context.Shown(IntegrationV1.ShortFingerprint(pairing.Fingerprint))}");
        }
    }

    private static void RunList(IReadOnlyList<string> arguments)
    {
        var options = new Options(arguments, new[] { "keychain", "redact" });
        var context = MakeContext(options);
        options.Finish();
        var pairings = context.Store.Pairings();
        if (pairings.Count == 0)
        {
            Console.WriteLine("No receivers are paired.");
        }
        foreach (var pairing in pairings)
        {
            var kinds = string.Join(",", pairing.Kinds.Select(kind => kind.ToWire()));
            Console.WriteLine($"{context.Shown(pairing.Label)}  key {context.Shown(IntegrationV1.ShortFingerprint(pairing.Fingerprint))}  allowed {kinds}");
        }
    }

    private static void RunForget(IReadOnlyList<string> arguments)
    {
        var options = new Options(arguments, new[] { "keychain", "redact" });
        var context = MakeContext(options);
        var label = options.Required("to");
        options.Finish();
        var pairing = SelectPairing(context, label);
        context.Store.RemovePairing(pairing.Fingerprint);
        Console.WriteLine("Forgotten here. To remove this device on the receiver, remove it from the list of connected devices there.");
    }

    private static void RunIdentity(IReadOnlyList<string> arguments)
    {
        var options = new Options(arguments, new[] { "keychain", "redact" });
        var context = MakeContext(options);
        options.Finish();
        using var identity = context.Store.LoadOrCreateIdentity();
        Console.WriteLine(context.Shown(identity.Fingerprint));
    }

    /// <summary>Everything common to the writing commands. <paramref name="prepare"/> reads its own options and returns the work to do.</summary>
    private static async Task RunWriteAsync(IReadOnlyList<string> arguments, Func<Options, Func<ErdigoClient, Task>> prepare, params string[] extraFlags)
    {
        var options = new Options(arguments, extraFlags.Concat(new[] { "keychain", "redact" }));
        var context = MakeContext(options);
        var label = options.Optional("to");
        var where = Target(options);
        var retry = RetryPolicy.UpTo(TimeSpan.FromSeconds(30));
        if (options.Optional("retry-for") is { } text)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || !double.IsFinite(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds / 2)
            {
                throw new UsageException("--retry-for must be a number of seconds");
            }
            retry = seconds == 0 ? RetryPolicy.None : RetryPolicy.UpTo(TimeSpan.FromSeconds(seconds));
        }
        var body = prepare(options);
        options.Finish();

        var pairing = SelectPairing(context, label);
        using var identity = context.Store.LoadOrCreateIdentity();
        using var client = new ErdigoClient(identity, pairing, context.Store, where, retry);
        await body(client).ConfigureAwait(false);
        client.Close();
        Console.WriteLine("OK");
    }

    /// <summary>
    /// Whether the mock asks a person about each pairing request. Anyone who can reach a mock on the network and has a
    /// code could pair, so there a person decides unless the test says it runs unattended. On this computer alone,
    /// allowing every request stays the default.
    /// </summary>
    internal static bool AsksAboutPairing(bool localNetwork, bool ask, bool autoApprove)
    {
        if (ask && autoApprove)
        {
            throw new UsageException("--ask and --auto-approve cannot be used together");
        }
        return ask || (localNetwork && !autoApprove);
    }

    private static async Task RunServeAsync(IReadOnlyList<string> arguments)
    {
        var options = new Options(arguments, new[] { "mock", "lan", "bonjour", "ask", "auto-approve", "redact" });
        if (!options.Flag("mock"))
        {
            throw new UsageException("'serve' only runs a mock receiver for testing, so --mock is required");
        }
        var settings = new Mock.Options
        {
            Port = options.OptionalPort() ?? 0,
            AcceptsLocalNetwork = options.Flag("lan"),
            Advertises = options.Flag("bonjour"),
        };
        if (settings.Advertises && !settings.AcceptsLocalNetwork)
        {
            throw new UsageException("--bonjour needs --lan, because a loopback-only receiver cannot be reached from the network");
        }
        var askGiven = options.Flag("ask");
        var autoApprove = options.Flag("auto-approve");
        var redact = options.Flag("redact");
        var codeFile = options.Optional("code-file");
        options.Finish();
        var ask = AsksAboutPairing(settings.AcceptsLocalNetwork, askGiven, autoApprove);
        if (redact && codeFile is null)
        {
            throw new UsageException("--redact hides the pairing code, so it needs --code-file to give you the code another way");
        }

        string Shown(string text) => redact ? "<redacted>" : text;
        static string Short(string device) => device.Length > 16 ? device[..16] : device;

        using var receiver = new Mock(settings);
        receiver.SetEventHandler(happened =>
        {
            switch (happened)
            {
                case Mock.Event.PairingRequested requested:
                    Console.WriteLine($"[mock] pairing request from {Shown(Short(requested.Device))}");
                    break;
                case Mock.Event.Paired paired:
                    Console.WriteLine($"[mock] paired {Shown(Short(paired.Device))}: {string.Join(",", paired.Kinds.Select(kind => kind.ToWire()))}");
                    break;
                case Mock.Event.PairingDenied:
                    Console.WriteLine("[mock] pairing denied");
                    break;
                case Mock.Event.CodeReusedAlarm:
                    Console.WriteLine("[mock] WARNING: this code was used by another device. The pairing made with it was removed.");
                    break;
                case Mock.Event.SilentlyDropped dropped:
                    Console.WriteLine($"[mock] dropped a connection without answering: {dropped.Reason}");
                    break;
                case Mock.Event.Connected connected:
                    Console.WriteLine($"[mock] connected {Shown(Short(connected.Device))}");
                    break;
                case Mock.Event.Disconnected disconnected:
                    Console.WriteLine($"[mock] disconnected {Shown(Short(disconnected.Device))}");
                    break;
                case Mock.Event.Applied applied:
                    Console.WriteLine($"[mock] #{applied.Seq} {applied.Summary}");
                    break;
                case Mock.Event.Rejected rejected:
                    Console.WriteLine($"[mock] rejected #{(rejected.Seq is { } seq ? seq.ToString(CultureInfo.InvariantCulture) : "-")}: {rejected.Code}");
                    break;
                case Mock.Event.Revoked revoked:
                    Console.WriteLine($"[mock] removed {Shown(Short(revoked.Device))}");
                    break;
            }
        });

        var console = new MockConsole(Console.In);
        if (ask)
        {
            receiver.SetApprovalHandler(async (request, cancellationToken) =>
            {
                var kinds = string.Join(", ", request.RequestedKinds.Select(kind => kind.ToWire()));
                var address = request.Address ?? "an unknown address";
                var note = request.WasRemovedBefore ? " You removed this device earlier." : string.Empty;
                // The name and address are what a person decides on, so --redact does not hide them here. The question
                // goes to standard error, so that standard output saved to a file does not hold them.
                Console.Error.WriteLine($"[mock] '{request.DeviceName}' at {address} wants to put {kinds} on your map.{note} Allow? Type 'y' or 'n':");
                var answer = await console.NextDecisionAsync(cancellationToken).ConfigureAwait(false);
                return answer ? Mock.ApprovalDecision.Allow(request.RequestedKinds) : Mock.ApprovalDecision.Deny;
            });
        }

        void Publish(PairingTicket ticket)
        {
            var uri = ticket.Uri ?? throw new UsageException("the mock's key could not be put in the pairing text");
            if (codeFile is not null)
            {
                try
                {
                    PrivateFile.WriteAllBytes(codeFile, Encoding.UTF8.GetBytes(uri));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    throw new UsageException("could not write the code file");
                }
                Console.WriteLine("Pairing text written to the code file. It is valid for 10 minutes.");
            }
            else
            {
                Console.WriteLine($"Pairing code (valid for 10 minutes): {ticket.Code.Display}");
                Console.WriteLine($"QR text: {uri}");
            }
        }

        ushort port;
        try
        {
            port = await receiver.StartAsync().ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException error)
        {
            throw new UsageException($"the mock receiver could not start listening: {error.SocketErrorCode}");
        }
        Console.WriteLine("This is a mock receiver for testing. It stores nothing on disk and draws no map.");
        Console.WriteLine($"Listening on {(settings.AcceptsLocalNetwork ? "the local network" : "127.0.0.1")}, port {port}");
        Console.WriteLine($"Receiver key: {Shown(IntegrationV1.ShortFingerprint(receiver.Fingerprint))}");
        Console.WriteLine(ask
            ? "Each pairing request is asked about here."
            : settings.AcceptsLocalNetwork
                ? "Every pairing request with a valid code is allowed without asking, from anywhere on the network."
                : "Every pairing request with a valid code is allowed without asking.");
        Publish(receiver.OpenPairingWindow());
        Console.WriteLine("Type 'help' for commands.");

        await foreach (var line in console.ReadAllAsync().ConfigureAwait(false))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (words.FirstOrDefault())
            {
                case null:
                    break;
                case "help":
                    Console.WriteLine("code | devices | items N | remove N | pause | resume | quit");
                    break;
                case "code":
                    Publish(receiver.OpenPairingWindow());
                    break;
                case "devices":
                {
                    var list = receiver.PairedDevices();
                    if (list.Count == 0)
                    {
                        Console.WriteLine("No devices.");
                    }
                    for (var index = 0; index < list.Count; index++)
                    {
                        var device = list[index];
                        Console.WriteLine($"{index + 1}. '{Shown(device.Name)}' {Shown(Short(device.Fingerprint))} {string.Join(",", device.Kinds.Select(kind => kind.ToWire()))}");
                    }
                    break;
                }
                case "items":
                {
                    if (ChooseDevice(receiver, words) is not { } device)
                    {
                        break;
                    }
                    foreach (var item in receiver.Items(device))
                    {
                        var position = redact
                            ? "<redacted>"
                            : $"{(item.Latitude is { } latitude ? JsonWriter.FormatNumber(latitude) : "-")},{(item.Longitude is { } longitude ? JsonWriter.FormatNumber(longitude) : "-")}";
                        Console.WriteLine($"{item.Kind.ToWire()} {Shown(item.Id)} at {position} title={Shown(item.Title ?? "-")} points={item.Points.Count}");
                    }
                    break;
                }
                case "remove":
                    if (ChooseDevice(receiver, words) is { } removed)
                    {
                        receiver.Revoke(removed);
                    }
                    break;
                case "pause":
                    receiver.SetPaused(true);
                    Console.WriteLine("Paused. All connections were closed.");
                    break;
                case "resume":
                    receiver.SetPaused(false);
                    Console.WriteLine("Resumed.");
                    break;
                case "quit":
                    receiver.Stop();
                    return;
                default:
                    Console.WriteLine("Unknown command. Type 'help'.");
                    break;
            }
        }

        // Standard input ended, for example when started from a script. Keep serving until the process is stopped.
        await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
    }

    private static string? ChooseDevice(Mock receiver, string[] words)
    {
        var list = receiver.PairedDevices();
        if (words.Length != 2 || !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1 || number > list.Count)
        {
            Console.WriteLine("Give a device number from 'devices'.");
            return null;
        }
        return list[number - 1].Fingerprint;
    }

    private static Task RunCommandAsync(string command, IReadOnlyList<string> arguments)
    {
        switch (command)
        {
            case "pair":
                return RunPairAsync(arguments);
            case "list":
                RunList(arguments);
                return Task.CompletedTask;
            case "forget":
                RunForget(arguments);
                return Task.CompletedTask;
            case "identity":
                RunIdentity(arguments);
                return Task.CompletedTask;
            case "serve":
                return RunServeAsync(arguments);
            case "ping":
                return RunWriteAsync(arguments, _ => client => client.PingAsync());
            case "pin":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    var at = ParseCoordinate(options);
                    var title = options.Optional("title");
                    return client => client.PutPinAsync(id, at, title);
                });
            case "marker":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    var at = ParseCoordinate(options);
                    var title = options.Optional("title");
                    MarkerColor? color = null;
                    if (options.Optional("color") is { } text)
                    {
                        color = MarkerColors.FromWire(text)
                            ?? throw new UsageException($"--color must be one of: {string.Join(", ", MarkerColors.All.Select(each => each.ToWire()))}");
                    }
                    return client => client.PutMarkerAsync(id, at, title, color);
                });
            case "note":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    var at = ParseCoordinate(options);
                    var inline = options.Optional("text");
                    var file = options.Optional("text-file");
                    var text = (inline, file) switch
                    {
                        ({ } value, null) => value,
                        (null, { } path) => CommandFiles.ReadNote(path),
                        _ => throw new UsageException("give either --text or --text-file"),
                    };
                    return client => client.PutNoteAsync(id, at, text);
                });
            case "photo":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    var at = ParseCoordinate(options);
                    var title = options.Optional("title");
                    var data = CommandFiles.ReadPhoto(options.Required("file"));
                    return client => client.PutPhotoAsync(id, at, data, title);
                });
            case "track-add":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    var at = ParseCoordinate(options);
                    var title = options.Optional("title");
                    long? timestamp = null;
                    if (options.Optional("ts") is { } tsText)
                    {
                        if (!long.TryParse(tsText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                        {
                            throw new UsageException("--ts must be whole milliseconds");
                        }
                        timestamp = value;
                    }
                    double? altitude = null;
                    if (options.Optional("alt") is { } altText)
                    {
                        if (!double.TryParse(altText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        {
                            throw new UsageException("--alt must be a number");
                        }
                        altitude = value;
                    }
                    TrackPoint point;
                    try
                    {
                        point = new TrackPoint(at, timestamp, altitude);
                    }
                    catch (WireException error)
                    {
                        throw new UsageException(error.Detail);
                    }
                    return client => client.AppendTrackAsync(id, new[] { point }, title);
                });
            case "track-finish":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    return client => client.FinishTrackAsync(id);
                });
            case "move":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    var at = ParseCoordinate(options);
                    return client => client.MoveAsync(id, at);
                });
            case "delete":
                return RunWriteAsync(arguments, options =>
                {
                    var id = options.Required("id");
                    return client => client.DeleteAsync(id);
                });
            case "delete-all":
                return RunWriteAsync(
                    arguments,
                    options =>
                    {
                        if (!options.Flag("yes"))
                        {
                            throw new UsageException("this deletes every item this device put on the receiver. Add --yes to confirm.");
                        }
                        return client => client.DeleteAllAsync();
                    },
                    "yes");
            default:
                Console.WriteLine(Usage);
                throw new UsageException("unknown command");
        }
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        try
        {
            await RunCommandAsync(args[0], args.Skip(1).ToArray()).ConfigureAwait(false);
            return 0;
        }
        catch (Exception error) when (error is UsageException or ErdigoException or WireException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            Console.Error.WriteLine($"erdigo-send: {error.Message}");
            return 1;
        }
    }
}

/// <summary>Standard input for the mock's commands, shared with the <c>--ask</c> prompt.</summary>
internal sealed class MockConsole
{
    private const string NobodyCanAnswer = "[mock] nothing more can be typed here, so the request was denied.";

    private readonly object sync = new();
    private readonly LinkedList<TaskCompletionSource<bool>> pendingDecisions = new();
    private readonly System.Threading.Channels.Channel<string> lines = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private bool inputEnded;

    public MockConsole(TextReader input)
    {
        var reader = new Thread(() =>
        {
            while (input.ReadLine() is { } line)
            {
                if (!AnswerPending(line))
                {
                    lines.Writer.TryWrite(line);
                }
            }
            EndInput();
        })
        {
            IsBackground = true,
            Name = "mock console",
        };
        reader.Start();
    }

    public IAsyncEnumerable<string> ReadAllAsync() => lines.Reader.ReadAllAsync();

    /// <summary>
    /// Waits for 'y' or 'n'. If the request expires first, the answer is no, and the next line typed is an ordinary
    /// command rather than an answer to a prompt that has gone. Once nothing more can be typed, as when the mock was
    /// started from a script, every answer is no at once.
    /// </summary>
    public async Task<bool> NextDecisionAsync(CancellationToken cancellationToken)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkedListNode<TaskCompletionSource<bool>> node;
        lock (sync)
        {
            if (inputEnded)
            {
                Console.WriteLine(NobodyCanAnswer);
                return false;
            }
            node = pendingDecisions.AddLast(decision);
        }
        using (cancellationToken.Register(() =>
        {
            bool removed;
            lock (sync)
            {
                removed = node.List is not null;
                if (removed)
                {
                    pendingDecisions.Remove(node);
                }
            }
            if (removed && decision.TrySetResult(false))
            {
                Console.WriteLine("[mock] nobody answered in time, so the request was denied.");
            }
        }))
        {
            return await decision.Task.ConfigureAwait(false);
        }
    }

    private void EndInput()
    {
        List<TaskCompletionSource<bool>> waiting;
        lock (sync)
        {
            inputEnded = true;
            waiting = pendingDecisions.ToList();
            pendingDecisions.Clear();
        }
        foreach (var decision in waiting)
        {
            if (decision.TrySetResult(false))
            {
                Console.WriteLine(NobodyCanAnswer);
            }
        }
        lines.Writer.TryComplete();
    }

    private bool AnswerPending(string line)
    {
        TaskCompletionSource<bool>? first;
        lock (sync)
        {
            first = pendingDecisions.First?.Value;
            if (first is null)
            {
                return false;
            }
            pendingDecisions.RemoveFirst();
        }
        var answer = Text.TrimSpaces(line).ToLowerInvariant();
        first.TrySetResult(answer is "y" or "yes");
        return true;
    }
}
