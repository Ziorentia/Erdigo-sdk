package com.ziorentia.erdigo.send;

import com.ziorentia.erdigo.core.Coordinate;
import com.ziorentia.erdigo.core.Hex;
import com.ziorentia.erdigo.core.IntegrationV1;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.MarkerColor;
import com.ziorentia.erdigo.core.PairingTicket;
import com.ziorentia.erdigo.core.TextRules;
import com.ziorentia.erdigo.core.TrackPoint;
import com.ziorentia.erdigo.core.WireException;
import com.ziorentia.erdigo.sdk.DeviceIdentity;
import com.ziorentia.erdigo.sdk.ErdigoClient;
import com.ziorentia.erdigo.sdk.ErdigoException;
import com.ziorentia.erdigo.sdk.FileDeviceStore;
import com.ziorentia.erdigo.sdk.Pairing;
import com.ziorentia.erdigo.sdk.PrivateFile;
import com.ziorentia.erdigo.sdk.PrivateFile.PrivateFileException;
import com.ziorentia.erdigo.sdk.ReceiverPairing;
import com.ziorentia.erdigo.sdk.ReceiverTarget;
import com.ziorentia.erdigo.sdk.RetryPolicy;
import java.io.PrintStream;
import java.io.UncheckedIOException;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.StandardCharsets;
import java.nio.file.InvalidPathException;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.EnumSet;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.StringJoiner;
import java.util.function.Function;

/** The {@code erdigo-send} command-line tool. */
public final class ErdigoSend {
    private static final String USAGE = String.join("\n",
            "erdigo-send: write pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network.",
            "",
            "Pairing",
            "  pair --name NAME --kinds pin,marker,note,photo,track [--label LABEL]",
            "       [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]",
            "      The code is read from the keyboard, or from a file only you can read.",
            "      It is never accepted as a command-line argument.",
            "      A bare code means you compare the receiver's key and type yes before the receiver is asked to pair.",
            "      That needs standard input and output to be a terminal, so a bare code is refused without one.",
            "      The QR text or --fingerprint compares the key for you, so nothing is asked.",
            "  list",
            "  forget --to LABEL",
            "  identity",
            "      Prints this device's fingerprint.",
            "",
            "Writing",
            "  ping",
            "  pin          --id ID --lat N --lon N [--title TEXT]",
            "  marker       --id ID --lat N --lon N [--title TEXT] [--color COLOR]",
            "  note         --id ID --lat N --lon N (--text TEXT | --text-file FILE)",
            "  photo        --id ID --lat N --lon N --file FILE [--title TEXT]",
            "  track-add    --id ID --lat N --lon N [--ts MILLISECONDS] [--alt METRES] [--title TEXT]",
            "  track-finish --id ID",
            "  move         --id ID --lat N --lon N",
            "  delete       --id ID",
            "  delete-all   --yes",
            "      Options: --to LABEL (needed with several receivers), --host HOST --port PORT, --retry-for SECONDS",
            "",
            "Testing without the Erdigo app",
            "  serve --mock",
            "      This edition has no mock receiver of its own. Run the one from the .NET or Swift SDK in this",
            "      repository, which this tool works with: see java/README.md.",
            "",
            "For pairing and writing commands",
            "  --profile NAME    Use a separate identity and pairings (default: \"default\").",
            "  --state-dir DIR   Keep state in DIR instead of the default folder.",
            "  --redact          Hide labels, keys and other identifying output, for pasting into a bug report.",
            "                    With pair, it needs the QR text or --fingerprint, because a hidden key cannot be compared.",
            "",
            "The device key is kept in a file only you can read: mode 0600 on macOS and Linux, and an access list for",
            "you alone on Windows, in a folder of this edition's own.",
            "");

    private static final String WAITING = "Waiting for the request to be confirmed on the receiver...";

    private final PrintStream out;

    private ErdigoSend(PrintStream out) {
        this.out = out;
    }

    public static void main(String[] args) {
        System.exit(run(args));
    }

    /** Runs the tool and returns its exit status. */
    static int run(String[] args) {
        PrintStream out = new PrintStream(System.out, true, StandardCharsets.UTF_8);
        PrintStream err = new PrintStream(System.err, true, StandardCharsets.UTF_8);
        if (args.length == 0 || args[0].equals("help") || args[0].equals("--help") || args[0].equals("-h")) {
            out.println(USAGE);
            return 0;
        }
        try {
            new ErdigoSend(out).command(args[0], Arrays.asList(args).subList(1, args.length));
            return 0;
        } catch (UsageException | ErdigoException | WireException | UncheckedIOException error) {
            err.println("erdigo-send: " + error.getMessage());
            return 1;
        }
    }

    // Reading the code

    /** Reads a file only if nobody else can read it, as ssh does for its keys. */
    private static String readCodeFile(String path) {
        byte[] data;
        try {
            data = PrivateFile.readAllBytes(Paths.get(path), 1024);
        } catch (InvalidPathException error) {
            throw new UsageException("could not open the code file");
        } catch (PrivateFileException error) {
            switch (error.problem()) {
                case INSECURE:
                    throw new UsageException(Terminal.isWindows()
                            ? "the code file must be a regular file that only you can read"
                            : "the code file must be a regular file you own, with mode 0600, so that nobody else can read it");
                case TOO_LARGE:
                    throw new UsageException("the code file is too large");
                default:
                    throw new UsageException("could not open the code file");
            }
        }
        try {
            // A byte order mark is how some editors mark UTF-8. It is not part of the code.
            int start = data.length >= 3 && data[0] == (byte) 0xEF && data[1] == (byte) 0xBB && data[2] == (byte) 0xBF ? 3 : 0;
            return CommandFiles.strictUtf8(Arrays.copyOfRange(data, start, data.length));
        } catch (CharacterCodingException error) {
            throw new UsageException("the code file is not UTF-8 text");
        } finally {
            Arrays.fill(data, (byte) 0);
        }
    }

    private static String readCodeFromKeyboard() {
        String code = Terminal.readSecretLine("Pairing code: ", 1024);
        if (code == null) {
            throw new UsageException(Terminal.isWindows()
                    ? "no terminal to read the code from. Use --code-file with a file only you can read."
                    : "no terminal to read the code from. Use --code-file with a file of mode 0600.");
        }
        return code;
    }

    // Shared parts of the commands

    private static final class Context {
        final FileDeviceStore store;
        final boolean redact;

        Context(FileDeviceStore store, boolean redact) {
            this.store = store;
            this.redact = redact;
        }

        String shown(String text) {
            return redact ? "<redacted>" : text;
        }
    }

    private static boolean isProfileCharacter(int scalar) {
        if (scalar == '-' || scalar == '_') {
            return true;
        }
        switch (Character.getType(scalar)) {
            case Character.UPPERCASE_LETTER:
            case Character.LOWERCASE_LETTER:
            case Character.TITLECASE_LETTER:
            case Character.MODIFIER_LETTER:
            case Character.OTHER_LETTER:
            case Character.NON_SPACING_MARK:
            case Character.COMBINING_SPACING_MARK:
            case Character.ENCLOSING_MARK:
            case Character.DECIMAL_DIGIT_NUMBER:
            case Character.LETTER_NUMBER:
            case Character.OTHER_NUMBER:
                return true;
            default:
                return false;
        }
    }

    private static Context makeContext(Options options) {
        String given = options.optional("profile");
        String profile = given == null ? "default" : given;
        List<Integer> scalars = TextRules.scalars(profile);
        if (profile.isEmpty() || scalars == null || !scalars.stream().allMatch(ErdigoSend::isProfileCharacter)) {
            throw new UsageException("--profile may contain only letters, digits, '-' and '_'");
        }
        Path directory;
        String stateDir = options.optional("state-dir");
        if (stateDir != null) {
            if (given != null) {
                throw new UsageException("--state-dir and --profile cannot be used together, because a state folder holds one identity");
            }
            try {
                directory = Paths.get(stateDir);
            } catch (InvalidPathException error) {
                throw new UsageException("--state-dir is not a usable folder name");
            }
        } else {
            directory = FileDeviceStore.defaultDirectory(profile);
        }
        if (options.flag("keychain")) {
            throw new UsageException("--keychain belongs to the Swift SDK. This edition keeps the device key in a file only you can read: mode 0600 on macOS and Linux, and an access list for you alone on Windows.");
        }
        return new Context(new FileDeviceStore(directory), options.flag("redact"));
    }

    private static Set<String> flags(String... names) {
        Set<String> flags = new HashSet<>(List.of("keychain", "redact"));
        flags.addAll(List.of(names));
        return flags;
    }

    /** Trims spaces and tabs, but not line breaks, from both ends. */
    private static String trimSpaces(String text) {
        int start = 0;
        int end = text.length();
        while (start < end && isSpace(text.charAt(start))) {
            start += 1;
        }
        while (end > start && isSpace(text.charAt(end - 1))) {
            end -= 1;
        }
        return text.substring(start, end);
    }

    private static boolean isSpace(char character) {
        return character == '\t' || Character.getType(character) == Character.SPACE_SEPARATOR;
    }

    private static Set<ItemKind> parseKinds(String text) {
        Set<ItemKind> kinds = EnumSet.noneOf(ItemKind.class);
        for (String piece : text.split(",")) {
            if (piece.isEmpty()) {
                continue;
            }
            ItemKind kind = ItemKind.fromWire(trimSpaces(piece));
            if (kind == null) {
                throw new UsageException("--kinds takes pin, marker, note, photo and track, separated by commas.");
            }
            kinds.add(kind);
        }
        if (kinds.isEmpty()) {
            throw new UsageException("--kinds needs at least one item kind");
        }
        return kinds;
    }

    private static String joinKinds(List<ItemKind> kinds, String separator) {
        StringJoiner joiner = new StringJoiner(separator);
        for (ItemKind kind : kinds) {
            joiner.add(kind.wire());
        }
        return joiner.toString();
    }

    private static ReceiverTarget target(Options options) {
        String host = options.optional("host");
        Integer port = options.optionalPort();
        if (host == null && port == null) {
            return ReceiverTarget.discover();
        }
        if (host != null && port != null) {
            if (port == 0) {
                throw new ErdigoException(ErdigoException.Kind.INVALID_ARGUMENT, "the port must be between 1 and 65535");
            }
            return ReceiverTarget.address(host, port);
        }
        throw new UsageException("--host and --port go together");
    }

    private static ReceiverPairing selectPairing(Context context, String label) {
        List<ReceiverPairing> pairings = context.store.pairings();
        if (label != null) {
            List<ReceiverPairing> found = new ArrayList<>();
            for (ReceiverPairing pairing : pairings) {
                if (pairing.label().equals(label)) {
                    found.add(pairing);
                }
            }
            if (found.size() != 1) {
                throw new UsageException(found.isEmpty()
                        ? "no receiver is paired under that label. See 'erdigo-send list'."
                        : "several receivers have that label. Forget one of them first.");
            }
            return found.get(0);
        }
        if (pairings.isEmpty()) {
            throw new UsageException("no receiver is paired yet. Run 'erdigo-send pair' first.");
        }
        if (pairings.size() > 1) {
            throw new UsageException("several receivers are paired. Choose one with --to LABEL.");
        }
        return pairings.get(0);
    }

    private static Coordinate parseCoordinate(Options options) {
        double latitude = options.requiredDouble("lat");
        double longitude = options.requiredDouble("lon");
        try {
            return new Coordinate(latitude, longitude);
        } catch (WireException error) {
            throw new UsageException(error.detail());
        }
    }

    // Commands

    private void pair(List<String> arguments) {
        Options options = new Options(arguments, flags());
        Context context = makeContext(options);
        String name = options.required("name");
        Set<ItemKind> kinds = parseKinds(options.required("kinds"));
        String label = options.optional("label");
        String codeFile = options.optional("code-file");
        String givenFingerprint = options.optional("fingerprint");
        String fingerprint = givenFingerprint == null ? null : givenFingerprint.toLowerCase(Locale.ROOT);
        ReceiverTarget where = target(options);
        options.finish();

        if (fingerprint != null && !Hex.isFingerprint(fingerprint)) {
            throw new UsageException("--fingerprint must be 64 hexadecimal digits");
        }
        if (label != null && context.store.pairings().stream().anyMatch(pairing -> pairing.label().equals(label))) {
            throw new UsageException("a receiver is already paired under that label. Choose another, or use 'erdigo-send forget --to LABEL' first.");
        }
        String text = codeFile != null ? readCodeFile(codeFile) : readCodeFromKeyboard();
        PairingTicket ticket;
        try {
            ticket = PairingTicket.parse(text);
        } catch (WireException error) {
            throw new UsageException(error.detail());
        }
        if (fingerprint != null) {
            if (ticket.fingerprint() != null && !ticket.fingerprint().equals(fingerprint)) {
                throw new ErdigoException(ErdigoException.Kind.FINGERPRINT_MISMATCH);
            }
            ticket = new PairingTicket(ticket.code(), fingerprint);
        }
        if (ticket.fingerprint() == null && context.redact) {
            throw new UsageException("--redact hides the receiver's key, so it cannot be compared for a bare code. Use the QR text, or give the full 64-digit key with --fingerprint.");
        }
        // Only a person can compare the key. Whatever a pipe or a file holds was written before the key existed, so it
        // is no answer, and a question sent to a file would be answered unseen. The receiver is not contacted when no
        // answer is possible.
        if (ticket.fingerprint() == null && !Terminal.personIsThere()) {
            throw new UsageException("a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.");
        }

        boolean personCompares = ticket.fingerprint() == null;
        out.println(personCompares ? "Checking the code with the receiver..." : WAITING);
        DeviceIdentity identity = context.store.loadOrCreateIdentity();
        ReceiverPairing pairing;
        try {
            Pairing.Options settings = new Pairing.Options()
                    .label(label)
                    // A bare code proves only that the other side knows the code, and only a person can tell whether
                    // this is the right receiver. This runs before the receiver is asked to pair.
                    .confirmKey(key -> {
                        // Only an answer given after seeing the key counts, so anything typed before it is discarded.
                        // Standard input is a terminal here, because a bare code is refused otherwise.
                        Terminal.discardTypedAhead();
                        out.println("Receiver key: " + key);
                        out.print("If this matches the key shown on the receiver, type yes: ");
                        out.flush();
                        String answer = Terminal.readLine(256);
                        if (answer == null || !answer.trim().toLowerCase(Locale.ROOT).equals("yes")) {
                            out.println();
                            return false;
                        }
                        out.println(WAITING);
                        return true;
                    });
            pairing = Pairing.pair(where, ticket, identity, name, kinds, settings);
        } finally {
            identity.close();
        }
        context.store.save(pairing);
        out.println("Paired as '" + context.shown(pairing.label()) + "'. Allowed: " + joinKinds(pairing.kinds(), ", ") + ".");
        if (!personCompares) {
            out.println("Receiver key: " + context.shown(IntegrationV1.shortFingerprint(pairing.fingerprint())));
        }
    }

    private void list(List<String> arguments) {
        Options options = new Options(arguments, flags());
        Context context = makeContext(options);
        options.finish();
        List<ReceiverPairing> pairings = context.store.pairings();
        if (pairings.isEmpty()) {
            out.println("No receivers are paired.");
        }
        for (ReceiverPairing pairing : pairings) {
            out.println(context.shown(pairing.label()) + "  key " + context.shown(IntegrationV1.shortFingerprint(pairing.fingerprint()))
                    + "  allowed " + joinKinds(pairing.kinds(), ","));
        }
    }

    private void forget(List<String> arguments) {
        Options options = new Options(arguments, flags());
        Context context = makeContext(options);
        String label = options.required("to");
        options.finish();
        ReceiverPairing pairing = selectPairing(context, label);
        context.store.removePairing(pairing.fingerprint());
        out.println("Forgotten here. To remove this device on the receiver, remove it from the list of connected devices there.");
    }

    private void identity(List<String> arguments) {
        Options options = new Options(arguments, flags());
        Context context = makeContext(options);
        options.finish();
        try (DeviceIdentity identity = context.store.loadOrCreateIdentity()) {
            out.println(context.shown(identity.fingerprint()));
        }
    }

    @FunctionalInterface
    private interface Work {
        void run(ErdigoClient client);
    }

    /** Everything common to the writing commands. {@code prepare} reads its own options and returns the work to do. */
    private void write(List<String> arguments, Function<Options, Work> prepare, String... extraFlags) {
        Options options = new Options(arguments, flags(extraFlags));
        Context context = makeContext(options);
        String label = options.optional("to");
        ReceiverTarget where = target(options);
        RetryPolicy retry = RetryPolicy.upTo(Duration.ofSeconds(30));
        String retryText = options.optional("retry-for");
        if (retryText != null) {
            if (!Options.isDecimal(retryText)) {
                throw new UsageException("--retry-for must be a number of seconds");
            }
            double seconds = Double.parseDouble(retryText.trim());
            if (seconds < 0 || seconds > 1e12) {
                throw new UsageException("--retry-for must be a number of seconds");
            }
            retry = seconds == 0 ? RetryPolicy.none() : RetryPolicy.upTo(Duration.ofNanos((long) (seconds * 1e9)));
        }
        Work body = prepare.apply(options);
        options.finish();

        ReceiverPairing pairing = selectPairing(context, label);
        try (DeviceIdentity identity = context.store.loadOrCreateIdentity();
                ErdigoClient client = new ErdigoClient(identity, pairing, context.store, where, retry)) {
            body.run(client);
        }
        out.println("OK");
    }

    private void command(String command, List<String> arguments) {
        switch (command) {
            case "pair":
                pair(arguments);
                return;
            case "list":
                list(arguments);
                return;
            case "forget":
                forget(arguments);
                return;
            case "identity":
                identity(arguments);
                return;
            case "serve":
                throw new UsageException("this edition has no mock receiver. Run the one from the .NET SDK (dotnet run --project dotnet/src/erdigo-send -- serve --mock) or the Swift SDK (swift run erdigo-send serve --mock); this tool works with both.");
            case "ping":
                write(arguments, options -> ErdigoClient::ping);
                return;
            case "pin":
                write(arguments, options -> {
                    String id = options.required("id");
                    Coordinate at = parseCoordinate(options);
                    String title = options.optional("title");
                    return client -> client.putPin(id, at, title);
                });
                return;
            case "marker":
                write(arguments, options -> {
                    String id = options.required("id");
                    Coordinate at = parseCoordinate(options);
                    String title = options.optional("title");
                    String colorText = options.optional("color");
                    MarkerColor color = null;
                    if (colorText != null) {
                        color = MarkerColor.fromWire(colorText);
                        if (color == null) {
                            StringJoiner all = new StringJoiner(", ");
                            for (MarkerColor each : MarkerColor.values()) {
                                all.add(each.wire());
                            }
                            throw new UsageException("--color must be one of: " + all);
                        }
                    }
                    MarkerColor chosen = color;
                    return client -> client.putMarker(id, at, title, chosen);
                });
                return;
            case "note":
                write(arguments, options -> {
                    String id = options.required("id");
                    Coordinate at = parseCoordinate(options);
                    String inline = options.optional("text");
                    String file = options.optional("text-file");
                    String text;
                    if (inline != null && file == null) {
                        text = inline;
                    } else if (inline == null && file != null) {
                        text = CommandFiles.readNote(file);
                    } else {
                        throw new UsageException("give either --text or --text-file");
                    }
                    return client -> client.putNote(id, at, text);
                });
                return;
            case "photo":
                write(arguments, options -> {
                    String id = options.required("id");
                    Coordinate at = parseCoordinate(options);
                    String title = options.optional("title");
                    byte[] data = CommandFiles.readPhoto(options.required("file"));
                    return client -> client.putPhoto(id, at, data, title);
                });
                return;
            case "track-add":
                write(arguments, options -> {
                    String id = options.required("id");
                    Coordinate at = parseCoordinate(options);
                    String title = options.optional("title");
                    Long timestamp = null;
                    String tsText = options.optional("ts");
                    if (tsText != null) {
                        try {
                            if (!tsText.matches("[+-]?[0-9]+")) {
                                throw new NumberFormatException();
                            }
                            timestamp = Long.parseLong(tsText);
                        } catch (NumberFormatException error) {
                            throw new UsageException("--ts must be whole milliseconds");
                        }
                    }
                    Double altitude = null;
                    String altText = options.optional("alt");
                    if (altText != null) {
                        if (!Options.isDecimal(altText)) {
                            throw new UsageException("--alt must be a number");
                        }
                        altitude = Double.parseDouble(altText.trim());
                    }
                    TrackPoint point;
                    try {
                        point = new TrackPoint(at, timestamp, altitude);
                    } catch (WireException error) {
                        throw new UsageException(error.detail());
                    }
                    return client -> client.appendTrack(id, List.of(point), title);
                });
                return;
            case "track-finish":
                write(arguments, options -> {
                    String id = options.required("id");
                    return client -> client.finishTrack(id);
                });
                return;
            case "move":
                write(arguments, options -> {
                    String id = options.required("id");
                    Coordinate at = parseCoordinate(options);
                    return client -> client.move(id, at);
                });
                return;
            case "delete":
                write(arguments, options -> {
                    String id = options.required("id");
                    return client -> client.delete(id);
                });
                return;
            case "delete-all":
                write(
                        arguments,
                        options -> {
                            if (!options.flag("yes")) {
                                throw new UsageException("this deletes every item this device put on the receiver. Add --yes to confirm.");
                            }
                            return ErdigoClient::deleteAll;
                        },
                        "yes");
                return;
            default:
                out.println(USAGE);
                throw new UsageException("unknown command");
        }
    }
}
