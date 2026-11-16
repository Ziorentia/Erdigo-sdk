package com.ziorentia.erdigo.examples.cloudrelay;

import com.ziorentia.erdigo.core.Coordinate;
import com.ziorentia.erdigo.core.ItemId;
import com.ziorentia.erdigo.core.JsonParseException;
import com.ziorentia.erdigo.core.JsonValue;
import com.ziorentia.erdigo.core.JsonWriter;
import com.ziorentia.erdigo.core.Limits;
import com.ziorentia.erdigo.core.MarkerColor;
import com.ziorentia.erdigo.core.PhotoType;
import com.ziorentia.erdigo.core.StrictJson;
import com.ziorentia.erdigo.core.TextRules;
import com.ziorentia.erdigo.core.TrackPoint;
import com.ziorentia.erdigo.core.WireException;
import com.ziorentia.erdigo.sdk.DeviceIdentity;
import com.ziorentia.erdigo.sdk.ErdigoClient;
import com.ziorentia.erdigo.sdk.ErdigoException;
import com.ziorentia.erdigo.sdk.FileDeviceStore;
import com.ziorentia.erdigo.sdk.PrivateFile;
import com.ziorentia.erdigo.sdk.PrivateFile.PrivateFileException;
import com.ziorentia.erdigo.sdk.ReceiverPairing;
import com.ziorentia.erdigo.sdk.ReceiverTarget;
import com.ziorentia.erdigo.sdk.RetryPolicy;
import com.ziorentia.erdigo.send.ErdigoSend;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.PrintStream;
import java.net.URI;
import java.net.URISyntaxException;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.channels.OverlappingFileLockException;
import java.nio.charset.StandardCharsets;
import java.nio.file.AccessDeniedException;
import java.nio.file.AtomicMoveNotSupportedException;
import java.nio.file.Files;
import java.nio.file.InvalidPathException;
import java.nio.file.LinkOption;
import java.nio.file.OpenOption;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.nio.file.attribute.PosixFilePermissions;
import java.security.SecureRandom;
import java.time.Duration;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Flow;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.regex.Pattern;

/**
 * The gateway of the Erdigo cloud relay example: it fetches operations from a relay over HTTPS and writes them to the
 * Erdigo app through the SDK, as a paired device. See docs/cloud-relay.md.
 *
 * <p>Run it from the java folder, with the SDK on the class path:
 *
 * <pre>
 * java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java pair --name "Cloud gateway"
 * java -cp target/erdigo-send.jar examples/cloud-relay/Gateway.java run --relay https://relay.example.com
 * </pre>
 */
public final class Gateway {
    static final String DEFAULT_PROFILE = "cloud-gateway";
    static final String DEFAULT_KINDS = "pin,marker,note,photo,track";
    static final String STATE_FILE = "relay-state.json";
    /** Locked by a run for as long as it runs, so that only one run at a time uses a profile. */
    static final String RUN_LOCK_FILE = ".gateway.lock";
    static final int WAIT_SECONDS = 25;
    /** Added to the wait for a fetch's timeout: comfortably above it with wait=25, and short with --once's wait=0. */
    static final int FETCH_MARGIN_SECONDS = 15;
    /** An empty answer that comes back sooner than this is followed by a pause, so a relay that cannot wait is not flooded. */
    static final double POLL_SECONDS = 5;
    static final int MAX_ANSWER_BYTES = 8 * 1024 * 1024;
    static final int MAX_OPS_PER_ANSWER = 100;
    static final double ONCE_GIVES_UP_AFTER_SECONDS = 30;
    static final long MAX_SEQ = (1L << 53) - 1;

    static final int EXIT_USAGE = 1;
    static final int EXIT_RELAY = 2;
    static final int EXIT_UNREACHABLE = 3;

    private static final Pattern TOKEN = Pattern.compile("[A-Za-z0-9._~-]{32,256}");
    private static final Pattern EPOCH = Pattern.compile("[A-Za-z0-9_-]{16,64}");
    private static final Pattern CODE = Pattern.compile("[a-z][a-z0-9_]{0,63}");
    private static final Set<String> OP_NAMES = Set.of("pin", "marker", "note", "photo", "track", "finish_track", "move", "delete");
    /** Answers from the app that say nothing about the operation itself, so it is tried again. */
    private static final Set<String> TEMPORARY_CODES = Set.of("internal_error", "too_many_errors", "rate_limited", "paused", "replaced");
    private static final SecureRandom RANDOM = new SecureRandom();

    private static final String USAGE = String.join("\n",
            "gateway: fetches operations from an Erdigo cloud relay and writes them to the Erdigo app as a paired device.",
            "",
            "  gateway pair --name NAME [--kinds LIST] [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]",
            "               [--profile NAME] [--state-dir DIR]",
            "      Pairs once, exactly as 'erdigo-send pair' does: the code or the QR text is read from the keyboard, or",
            "      from a file only you can read. --kinds is pin,marker,note,photo,track by default.",
            "  gateway run  --relay URL [--host HOST --port PORT] [--once] [--profile NAME] [--state-dir DIR]",
            "      Fetches from the relay and writes to the app until stopped. --relay must start with https://; plain",
            "      http:// is accepted only for 127.0.0.1, ::1 or localhost. The gateway token is read from the",
            "      ERDIGO_RELAY_TOKEN environment variable. --once stops when the relay has nothing more.",
            "",
            "  --host and --port give the app's address. Without them the gateway finds the app on the local network.",
            "  --profile is cloud-gateway by default. The device key, the pairing and the place in the relay's queue are",
            "  kept in its folder, or in --state-dir. A profile used by the gateway holds exactly one pairing, and only",
            "  one run at a time may use it.",
            "",
            "run exits with 0 when stopped or, with --once, when nothing more is waiting; 1 for a mistake in the command",
            "line or the environment, no pairing, or a profile another run is using; 2 when the relay refuses the token",
            "or breaks its API; and 3 when, with --once, the app or the relay could not be reached for about 30 seconds.",
            "",
            "See docs/cloud-relay.md.",
            "");

    /** A mistake in the command line, the environment or the stored state. Its message never repeats what was typed. */
    static final class UsageError extends RuntimeException {
        private static final long serialVersionUID = 1L;

        UsageError(String message) {
            super(message);
        }
    }

    /** The relay refused the token, or answered with something that does not follow the relay API. */
    static final class RelayBroken extends Exception {
        private static final long serialVersionUID = 1L;

        RelayBroken(String message) {
            super(message, null, false, false);
        }
    }

    /** The relay could not be reached, or answered with a server error. */
    static final class RelayUnreachable extends Exception {
        private static final long serialVersionUID = 1L;

        RelayUnreachable() {
            super(null, null, false, false);
        }
    }

    /** An operation the gateway refuses for good, with the code it prints. */
    static final class Refused extends Exception {
        private static final long serialVersionUID = 1L;

        final String code;

        Refused(String code) {
            super(code, null, false, false);
            this.code = code;
        }
    }

    private static final class TooLarge extends IOException {
        private static final long serialVersionUID = 1L;
    }

    // Set by the shutdown hook. The main thread is interrupted only while it waits for the relay, the app or a pause,
    // never while it saves its place: an operation that was interrupted is neither saved nor acknowledged, so it stays
    // on the relay.
    private static final Object STOP = new Object();
    private static volatile boolean stopping;
    private static boolean interruptible; // Guarded by STOP.
    private static volatile boolean hookRunning;
    private static volatile Integer finalCode;

    private final PrintStream out;

    private Gateway(PrintStream out) {
        this.out = out;
    }

    public static void main(String[] args) {
        PrintStream out = new PrintStream(System.out, true, StandardCharsets.UTF_8);
        PrintStream err = new PrintStream(System.err, true, StandardCharsets.UTF_8);
        if (args.length == 0 || args[0].equals("help") || args[0].equals("--help") || args[0].equals("-h")) {
            out.print(USAGE);
            return;
        }
        int code;
        try {
            switch (args[0]) {
                case "pair":
                    pair(options(args, Set.of()));
                    return;
                case "run":
                    code = run(options(args, Set.of("once")), out, err);
                    if (hookRunning) {
                        // Ctrl+C or SIGTERM: the stop hook ends the process once this thread is done.
                        return;
                    }
                    break;
                default:
                    throw new UsageError("unknown command. See 'gateway help'.");
            }
        } catch (UsageError error) {
            err.println("gateway: " + error.getMessage());
            code = EXIT_USAGE;
        }
        finalCode = code;
        out.flush();
        System.exit(code);
    }

    // Options

    private static final Set<String> VALUED = Set.of("name", "kinds", "host", "port", "code-file", "fingerprint", "profile", "state-dir", "relay");

    /** {@code --name value} pairs and the given flags, nothing else. Errors never repeat what was typed. */
    private static Map<String, String> options(String[] args, Set<String> flags) {
        Map<String, String> options = new HashMap<>();
        for (int index = 1; index < args.length; index++) {
            String argument = args[index];
            if (!argument.startsWith("--")) {
                throw new UsageError("unexpected argument. Options start with --, and neither a pairing code nor a token is ever given as an argument.");
            }
            String name = argument.substring(2);
            if (name.equals("code") || name.startsWith("code=") || name.equals("token") || name.startsWith("token=")) {
                throw new UsageError("secrets are not accepted as arguments, because arguments appear in the process list and shell history. "
                        + "Use --code-file for the pairing code, and ERDIGO_RELAY_TOKEN for the token.");
            }
            if (name.contains("=")) {
                throw new UsageError("write each option and its value as two separate arguments");
            }
            if (options.containsKey(name)) {
                throw new UsageError("an option was given twice");
            }
            if (flags.contains(name)) {
                options.put(name, "");
            } else if (VALUED.contains(name)) {
                if (index + 1 >= args.length) {
                    throw new UsageError("--" + name + " needs a value");
                }
                options.put(name, args[++index]);
            } else {
                throw new UsageError("an unknown option was given. See 'gateway help'.");
            }
        }
        return options;
    }

    private static void allow(Map<String, String> options, String... names) {
        Set<String> allowed = Set.of(names);
        for (String name : options.keySet()) {
            if (!allowed.contains(name)) {
                throw new UsageError("--" + name + " does not apply to this command");
            }
        }
    }

    /** The folder of the profile or --state-dir, as erdigo-send chooses it. */
    private static Path stateDirectory(Map<String, String> options) {
        String profile = options.get("profile");
        String stateDir = options.get("state-dir");
        if (profile != null && stateDir != null) {
            throw new UsageError("--state-dir and --profile cannot be used together, because a state folder holds one identity");
        }
        if (stateDir != null) {
            try {
                return Paths.get(stateDir);
            } catch (InvalidPathException error) {
                throw new UsageError("--state-dir is not a usable folder name");
            }
        }
        String name = profile == null ? DEFAULT_PROFILE : profile;
        List<Integer> scalars = TextRules.scalars(name);
        if (name.isEmpty() || scalars == null || !scalars.stream().allMatch(Gateway::isProfileCharacter)) {
            throw new UsageError("--profile may contain only letters, digits, '-' and '_'");
        }
        return FileDeviceStore.defaultDirectory(name);
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

    private static FileDeviceStore openStore(Path directory) {
        try {
            return new FileDeviceStore(directory);
        } catch (ErdigoException error) {
            throw new UsageError(error.getMessage());
        }
    }

    // pair

    /**
     * Hands over to {@code erdigo-send pair}, which reads the code from the keyboard or a private file, compares the
     * receiver's key and saves the pairing. Its terminal handling is not public API, and doing exactly what it does is
     * the point. It ends the process.
     */
    private static void pair(Map<String, String> options) {
        allow(options, "name", "kinds", "host", "port", "code-file", "fingerprint", "profile", "state-dir");
        if (options.get("name") == null) {
            throw new UsageError("--name is required");
        }
        Path directory = stateDirectory(options);
        List<ReceiverPairing> pairings;
        try {
            pairings = openStore(directory).pairings();
        } catch (ErdigoException error) {
            throw new UsageError(error.getMessage());
        }
        if (!pairings.isEmpty()) {
            throw new UsageError("this profile already holds a pairing, and a gateway's profile holds exactly one. Use another "
                    + "--profile or --state-dir, or remove the pairing with 'erdigo-send forget' first.");
        }
        List<String> forwarded = new ArrayList<>(List.of("pair", "--name", options.get("name"),
                "--kinds", options.getOrDefault("kinds", DEFAULT_KINDS)));
        if (options.containsKey("state-dir")) {
            forwarded.addAll(List.of("--state-dir", options.get("state-dir")));
        } else {
            forwarded.addAll(List.of("--profile", options.getOrDefault("profile", DEFAULT_PROFILE)));
        }
        for (String name : List.of("host", "port", "code-file", "fingerprint")) {
            if (options.containsKey(name)) {
                forwarded.addAll(List.of("--" + name, options.get(name)));
            }
        }
        ErdigoSend.main(forwarded.toArray(new String[0]));
    }

    // run

    private static int run(Map<String, String> options, PrintStream out, PrintStream err) {
        allow(options, "relay", "host", "port", "once", "profile", "state-dir");
        String relayText = options.get("relay");
        if (relayText == null) {
            throw new UsageError("--relay is required");
        }
        String relay = relayAddress(relayText);
        String token = System.getenv("ERDIGO_RELAY_TOKEN");
        if (token == null || token.isEmpty()) {
            throw new UsageError("ERDIGO_RELAY_TOKEN is not set. It holds the gateway token, which is never taken as an argument.");
        }
        if (!TOKEN.matcher(token).matches()) {
            throw new UsageError("ERDIGO_RELAY_TOKEN must hold the gateway token: 32 to 256 characters from A-Z, a-z, 0-9, "
                    + "'-', '_', '.' and '~'");
        }
        ReceiverTarget target = target(options);
        boolean once = options.containsKey("once");
        FileDeviceStore store = openStore(stateDirectory(options));
        List<ReceiverPairing> pairings;
        try {
            pairings = store.pairings();
        } catch (ErdigoException error) {
            throw new UsageError(error.getMessage());
        }
        if (pairings.isEmpty()) {
            throw new UsageError("no pairing in this profile. Run 'gateway pair' first.");
        }
        if (pairings.size() > 1) {
            throw new UsageError("this profile holds more than one pairing, and a gateway's profile must hold exactly one");
        }
        // Kept open, and so locked, until the run ends. The system releases the lock however the process ends.
        FileChannel held = holdProfile(store.directory());
        try {
            return runLocked(store, pairings.get(0), target, relay, token, once, out, err);
        } finally {
            closeQuietly(held);
        }
    }

    /**
     * Locks the profile's folder for the whole run: two gateways with one key would take each other's connection to the
     * app and repeat each other's work. A second run on the profile ends at once.
     */
    private static FileChannel holdProfile(Path directory) {
        Path path = directory.resolve(RUN_LOCK_FILE);
        Set<OpenOption> options = Set.of(StandardOpenOption.CREATE, StandardOpenOption.WRITE, LinkOption.NOFOLLOW_LINKS);
        FileChannel channel;
        try {
            channel = directory.getFileSystem().supportedFileAttributeViews().contains("posix")
                    ? FileChannel.open(path, options, PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rw-------")))
                    : FileChannel.open(path, options);
        } catch (IOException | UnsupportedOperationException error) {
            throw new UsageError("could not lock the state folder");
        }
        boolean locked = false;
        try {
            locked = channel.tryLock() != null;
        } catch (OverlappingFileLockException error) {
            // Held in this process already, which is another run as well.
        } catch (IOException error) {
            closeQuietly(channel);
            throw new UsageError("could not lock the state folder");
        }
        if (!locked) {
            closeQuietly(channel);
            throw new UsageError("another gateway is running with this profile or state folder. Run one gateway per profile.");
        }
        return channel;
    }

    private static void closeQuietly(FileChannel channel) {
        try {
            channel.close();
        } catch (IOException ignored) {
            // Nothing was written to it, and the lock goes with the process.
        }
    }

    private static int runLocked(FileDeviceStore store, ReceiverPairing pairing, ReceiverTarget target, String relay,
            String token, boolean once, PrintStream out, PrintStream err) {
        Place place = Place.load(store.directory(), relay);

        Thread main = Thread.currentThread();
        Thread hook = new Thread(() -> {
            synchronized (STOP) {
                stopping = true;
                if (interruptible) {
                    main.interrupt();
                }
            }
            try {
                main.join(20_000);
            } catch (InterruptedException ignored) {
                // Ending anyway.
            }
            out.flush();
            Runtime.getRuntime().halt(finalCode == null ? 0 : finalCode);
        }, "gateway-stop");
        Runtime.getRuntime().addShutdownHook(hook);
        // While the app cannot be reached, the SDK sends the same request again with the same sequence number, so the app
        // applies it once even if only its answer was lost (spec sections 7 and 13). With --once it gives up after
        // about 30 seconds without a connection; otherwise it keeps trying until the gateway is stopped, which
        // interrupts it. The SDK starts that count again at each new connection, so with --once the gateway keeps a
        // count of its own for each operation as well (see Watch).
        RetryPolicy retry = once
                ? RetryPolicy.upTo(Duration.ofMillis((long) (ONCE_GIVES_UP_AFTER_SECONDS * 1000)))
                : RetryPolicy.forever();
        int code;
        try (DeviceIdentity identity = store.loadOrCreateIdentity();
                ErdigoClient client = new ErdigoClient(identity, pairing, store, target, retry);
                Watch watch = new Watch(client, out, once, main)) {
            code = new Gateway(out).loop(client, watch, relay, token, place, once, err);
        } catch (ErdigoException error) {
            err.println("gateway: " + error.getMessage());
            code = EXIT_USAGE;
        } catch (UsageError error) {
            err.println("gateway: " + error.getMessage());
            code = EXIT_USAGE;
        }
        finalCode = stopping ? Integer.valueOf(0) : Integer.valueOf(code);
        try {
            Runtime.getRuntime().removeShutdownHook(hook);
        } catch (IllegalStateException shuttingDown) {
            hookRunning = true;
        }
        return finalCode;
    }

    private static ReceiverTarget target(Map<String, String> options) {
        String host = options.get("host");
        String port = options.get("port");
        if (host == null && port == null) {
            return ReceiverTarget.discover();
        }
        if (host == null || port == null) {
            throw new UsageError("--host and --port go together");
        }
        if (!port.matches("[0-9]{1,5}") || Integer.parseInt(port) < 1 || Integer.parseInt(port) > 65535) {
            throw new UsageError("--port must be between 1 and 65535");
        }
        return ReceiverTarget.address(host, Integer.parseInt(port));
    }

    /** The relay's address without a trailing slash: https://, or http:// on this computer only. */
    static String relayAddress(String text) {
        URI uri;
        try {
            uri = new URI(text);
        } catch (URISyntaxException error) {
            throw new UsageError("--relay is not a valid address");
        }
        String scheme = uri.getScheme() == null ? "" : uri.getScheme().toLowerCase(Locale.ROOT);
        String host = uri.getHost() == null ? "" : uri.getHost().toLowerCase(Locale.ROOT);
        if (!scheme.equals("https") && !scheme.equals("http")) {
            throw new UsageError("--relay must start with https://");
        }
        if (host.isEmpty() || uri.getRawUserInfo() != null || uri.getRawQuery() != null || uri.getRawFragment() != null) {
            throw new UsageError("--relay must be an address such as https://relay.example.com, with no user name, query or fragment");
        }
        if (uri.getPort() == 0 || uri.getPort() > 65535) {
            throw new UsageError("--relay has a port outside 1 to 65535");
        }
        boolean loopback = host.equals("127.0.0.1") || host.equals("[::1]") || host.equals("localhost");
        if (scheme.equals("http") && !loopback) {
            throw new UsageError("--relay must start with https://. Plain http:// is accepted only for 127.0.0.1, ::1 or "
                    + "localhost, for testing on one computer.");
        }
        String path = uri.getRawPath() == null ? "" : uri.getRawPath();
        while (path.endsWith("/")) {
            path = path.substring(0, path.length() - 1);
        }
        return scheme + "://" + host + (uri.getPort() >= 0 ? ":" + uri.getPort() : "") + path;
    }

    // The place in the relay's queue

    /** relay-state.json: the relay's address, its epoch and the last operation finished, replaced whole each time. */
    record Place(Path directory, String relay, String epoch, long seq) {
        static Place load(Path directory, String relay) {
            byte[] data;
            try {
                data = PrivateFile.readAllBytes(directory.resolve(STATE_FILE), 64 * 1024);
            } catch (PrivateFileException error) {
                switch (error.problem()) {
                    case MISSING:
                        return new Place(directory, relay, null, 0);
                    case INSECURE:
                        throw new UsageError(STATE_FILE + " must be a regular file that only you can read, such as mode 0600");
                    default:
                        throw new UsageError(STATE_FILE + " could not be read");
                }
            }
            try {
                Map<String, JsonValue> fields = ((JsonValue.Object) StrictJson.parse(data)).fields();
                long version = ((JsonValue.Integer) fields.get("version")).value();
                String storedRelay = ((JsonValue.Text) fields.get("relay")).value();
                long seq = ((JsonValue.Integer) fields.get("seq")).value();
                String epoch = fields.containsKey("epoch") ? ((JsonValue.Text) fields.get("epoch")).value() : null;
                if (version != 1 || seq < 0 || seq > MAX_SEQ || (epoch != null && !EPOCH.matcher(epoch).matches())
                        || (epoch == null && seq != 0) || !Set.of("version", "relay", "epoch", "seq").containsAll(fields.keySet())) {
                    throw new IllegalArgumentException();
                }
                // A place with another relay means nothing to this one.
                return storedRelay.equals(relay) ? new Place(directory, relay, epoch, seq) : new Place(directory, relay, null, 0);
            } catch (JsonParseException | ClassCastException | NullPointerException | IllegalArgumentException error) {
                throw new UsageError(STATE_FILE + " is damaged. Move it away to start again from the relay's oldest operation.");
            }
        }

        Place at(String newEpoch, long newSeq) {
            return new Place(directory, relay, newEpoch, newSeq);
        }

        /** Written to a private temporary file and renamed over the old one, so it is never half written. */
        void save() {
            JsonWriter.ObjectBuilder fields = JsonWriter.object().put("version", 1L).put("relay", relay);
            if (epoch != null) {
                fields.put("epoch", epoch);
            }
            byte[] data = JsonWriter.encode(fields.put("seq", seq).build());
            Path target = directory.resolve(STATE_FILE);
            Path temporary = directory.resolve("." + STATE_FILE + "." + Long.toHexString(RANDOM.nextLong()));
            try {
                PrivateFile.writeAllBytes(temporary, data);
                for (int attempt = 0; ; attempt++) {
                    try {
                        Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
                        return;
                    } catch (AtomicMoveNotSupportedException error) {
                        Files.move(temporary, target, StandardCopyOption.REPLACE_EXISTING);
                        return;
                    } catch (AccessDeniedException error) {
                        // On Windows a virus scanner can hold the file for a moment.
                        if (attempt >= 20) {
                            throw error;
                        }
                        Thread.sleep(50);
                    }
                }
            } catch (IOException | InterruptedException error) {
                if (error instanceof InterruptedException) {
                    Thread.currentThread().interrupt();
                }
                try {
                    Files.deleteIfExists(temporary);
                } catch (IOException ignored) {
                    // Left behind in the private folder.
                }
                throw new UsageError("could not save " + STATE_FILE);
            }
        }
    }

    // Fetching

    /** One answer from GET /v1/ops, checked against the relay API. */
    record Answer(String epoch, boolean reset, boolean missed, List<Long> seqs, List<Map<String, JsonValue>> ops) {
    }

    private HttpClient http;

    private Answer fetch(String relay, String token, Place place, int waitSeconds) throws RelayUnreachable, RelayBroken, InterruptedException {
        if (http == null) {
            http = HttpClient.newBuilder()
                    .version(HttpClient.Version.HTTP_1_1)
                    .connectTimeout(Duration.ofSeconds(15))
                    .followRedirects(HttpClient.Redirect.NEVER)
                    .build();
        }
        String query = (place.epoch() != null ? "epoch=" + place.epoch() + "&after=" + place.seq() + "&" : "") + "wait=" + waitSeconds;
        Duration timeout = Duration.ofSeconds(waitSeconds + FETCH_MARGIN_SECONDS);
        HttpRequest request = HttpRequest.newBuilder(URI.create(relay + "/v1/ops?" + query))
                .GET()
                .header("Authorization", "Bearer " + token)
                .header("Accept", "application/json")
                .timeout(timeout)
                .build();
        CompletableFuture<HttpResponse<byte[]>> pending = http.sendAsync(request, Gateway::capped);
        HttpResponse<byte[]> response;
        try {
            response = pending.get(timeout.toSeconds() + 5, TimeUnit.SECONDS);
        } catch (TimeoutException error) {
            pending.cancel(true);
            throw new RelayUnreachable();
        } catch (InterruptedException error) {
            pending.cancel(true);
            throw error;
        } catch (ExecutionException error) {
            for (Throwable cause = error; cause != null; cause = cause.getCause()) {
                if (cause instanceof TooLarge) {
                    throw new RelayBroken("the relay's answer is larger than " + MAX_ANSWER_BYTES + " bytes");
                }
            }
            throw new RelayUnreachable();
        }

        int status = response.statusCode();
        if (status == 401) {
            throw new RelayBroken("the relay refused the token");
        }
        if (status >= 500 || status == 408 || status == 429) {
            throw new RelayUnreachable();
        }
        if (status != 200) {
            throw new RelayBroken("the relay answered with HTTP status " + status + errorCode(response.body()));
        }
        String type = response.headers().firstValue("Content-Type").orElse("");
        if (!type.split(";", 2)[0].trim().equalsIgnoreCase("application/json")) {
            throw new RelayBroken("the relay's answer is not JSON");
        }
        return answer(response.body(), place);
    }

    /** The error code in a relay's error answer, if it is a plain one, to help find the mistake. */
    private static String errorCode(byte[] body) {
        try {
            if (StrictJson.parse(body) instanceof JsonValue.Object root
                    && root.fields().get("error") instanceof JsonValue.Text code
                    && CODE.matcher(code.value()).matches()) {
                return " (" + code.value() + ")";
            }
        } catch (RuntimeException ignored) {
            // Not a relay's answer.
        }
        return "";
    }

    /** Checks an answer against the relay API: anything else ends the gateway with code 2. */
    static Answer answer(byte[] body, Place place) throws RelayBroken {
        Map<String, JsonValue> fields;
        try {
            if (!(StrictJson.parse(body, 16) instanceof JsonValue.Object root)) {
                throw new RelayBroken("the relay's answer is not a JSON object");
            }
            fields = root.fields();
        } catch (JsonParseException error) {
            throw new RelayBroken("the relay's answer is not valid JSON");
        }
        if (!fields.keySet().equals(Set.of("epoch", "reset", "missed", "ops"))
                || !(fields.get("epoch") instanceof JsonValue.Text epochText)
                || !(fields.get("reset") instanceof JsonValue.Bool reset)
                || !(fields.get("missed") instanceof JsonValue.Bool missed)
                || !(fields.get("ops") instanceof JsonValue.Array ops)) {
            throw new RelayBroken("the relay's answer must have exactly epoch, reset, missed and ops");
        }
        String epoch = epochText.value();
        if (!EPOCH.matcher(epoch).matches()) {
            throw new RelayBroken("the relay's epoch is not 16 to 64 letters, digits, '-' or '_'");
        }
        boolean sameEpoch = epoch.equals(place.epoch());
        if (reset.value() == sameEpoch) {
            throw new RelayBroken(reset.value()
                    ? "the relay started a new numbering under the epoch it had"
                    : "the relay kept a numbering the gateway did not give it");
        }
        if (ops.values().size() > MAX_OPS_PER_ANSWER) {
            throw new RelayBroken("the relay's answer holds more than " + MAX_OPS_PER_ANSWER + " operations");
        }
        long previous = reset.value() ? 0 : place.seq();
        List<Long> seqs = new ArrayList<>();
        List<Map<String, JsonValue>> list = new ArrayList<>();
        for (JsonValue element : ops.values()) {
            if (!(element instanceof JsonValue.Object entry)
                    || !entry.fields().keySet().equals(Set.of("seq", "received_ms", "op"))
                    || !(entry.fields().get("seq") instanceof JsonValue.Integer seq)
                    || !(entry.fields().get("received_ms") instanceof JsonValue.Integer received)
                    || !(entry.fields().get("op") instanceof JsonValue.Object op)) {
                throw new RelayBroken("each operation in the relay's answer must have exactly seq, received_ms and op");
            }
            if (seq.value() <= previous || seq.value() > MAX_SEQ || received.value() < 0) {
                throw new RelayBroken("the relay's sequence numbers must go up, starting after the gateway's place");
            }
            previous = seq.value();
            seqs.add(seq.value());
            list.add(op.fields());
        }
        return new Answer(epoch, reset.value(), missed.value(), seqs, list);
    }

    /** Reads at most {@link #MAX_ANSWER_BYTES} of an answer, refusing it as soon as it is longer. */
    private static HttpResponse.BodySubscriber<byte[]> capped(HttpResponse.ResponseInfo info) {
        long announced = info.headers().firstValueAsLong("Content-Length").orElse(-1);
        return new HttpResponse.BodySubscriber<>() {
            private final CompletableFuture<byte[]> result = new CompletableFuture<>();
            private final ByteArrayOutputStream data = new ByteArrayOutputStream();
            private Flow.Subscription subscription;

            @Override
            public CompletionStage<byte[]> getBody() {
                return result;
            }

            @Override
            public void onSubscribe(Flow.Subscription value) {
                subscription = value;
                if (announced > MAX_ANSWER_BYTES) {
                    value.cancel();
                    result.completeExceptionally(new TooLarge());
                } else {
                    value.request(1);
                }
            }

            @Override
            public void onNext(List<ByteBuffer> items) {
                for (ByteBuffer item : items) {
                    if (data.size() + item.remaining() > MAX_ANSWER_BYTES) {
                        subscription.cancel();
                        result.completeExceptionally(new TooLarge());
                        return;
                    }
                    byte[] chunk = new byte[item.remaining()];
                    item.get(chunk);
                    data.write(chunk, 0, chunk.length);
                }
                subscription.request(1);
            }

            @Override
            public void onError(Throwable error) {
                result.completeExceptionally(error);
            }

            @Override
            public void onComplete() {
                result.complete(data.toByteArray());
            }
        };
    }

    // Applying

    @FunctionalInterface
    interface Action {
        void apply(ErdigoClient client);
    }

    /**
     * What an operation does through the SDK. An operation the gateway cannot map is refused for good as
     * {@code invalid_op}. Ids, titles, note texts and photo sizes are checked here with the rules the SDK checks them
     * with before it sends anything, and refused as it would refuse them, because the gateway connects to the app before
     * it hands an operation to the SDK. The SDK checks them again.
     */
    static Action action(Map<String, JsonValue> op) throws Refused {
        if (!(op.get("op") instanceof JsonValue.Text nameText) || !OP_NAMES.contains(nameText.value())) {
            throw new Refused("invalid_op");
        }
        String name = nameText.value();
        try {
            switch (name) {
                case "pin": {
                    fields(op, Set.of("id", "lat", "lon"), Set.of("title"));
                    String id = id(op);
                    Coordinate at = at(op);
                    String title = title(op);
                    return client -> client.putPin(id, at, title);
                }
                case "marker": {
                    fields(op, Set.of("id", "lat", "lon"), Set.of("title", "color"));
                    String id = id(op);
                    Coordinate at = at(op);
                    String title = title(op);
                    String colorName = optionalText(op, "color");
                    MarkerColor color = colorName == null ? null : MarkerColor.fromWire(colorName);
                    if (colorName != null && color == null) {
                        throw new Refused("invalid_op");
                    }
                    return client -> client.putMarker(id, at, title, color);
                }
                case "note": {
                    fields(op, Set.of("id", "lat", "lon", "text"), Set.of());
                    String id = id(op);
                    Coordinate at = at(op);
                    String text = text(op, "text");
                    sdkRule(() -> TextRules.validateNote(text));
                    return client -> client.putNote(id, at, text);
                }
                case "photo": {
                    fields(op, Set.of("id", "lat", "lon", "type", "data"), Set.of("title"));
                    String id = id(op);
                    Coordinate at = at(op);
                    String title = title(op);
                    PhotoType type = PhotoType.fromWire(text(op, "type"));
                    byte[] data = canonicalBase64(text(op, "data"));
                    // The SDK finds the type from the bytes, so they must say what the operation says.
                    if (type == null || data == null || PhotoType.detect(data) != type) {
                        throw new Refused("invalid_op");
                    }
                    if (data.length > Limits.V1.photoBytes()) {
                        throw new Refused("invalid_argument");
                    }
                    return client -> client.putPhoto(id, at, data, title);
                }
                case "track": {
                    fields(op, Set.of("id", "points"), Set.of("title"));
                    String id = id(op);
                    String title = title(op);
                    List<TrackPoint> points = points(op.get("points"));
                    return client -> client.appendTrack(id, points, title);
                }
                case "finish_track": {
                    fields(op, Set.of("id"), Set.of());
                    String id = id(op);
                    return client -> client.finishTrack(id);
                }
                case "move": {
                    fields(op, Set.of("id", "lat", "lon"), Set.of());
                    String id = id(op);
                    Coordinate at = at(op);
                    return client -> client.move(id, at);
                }
                default: {
                    fields(op, Set.of("id"), Set.of());
                    String id = id(op);
                    return client -> client.delete(id);
                }
            }
        } catch (WireException error) {
            throw new Refused(error.code().wire());
        }
    }

    private static void fields(Map<String, JsonValue> op, Set<String> required, Set<String> optional) throws Refused {
        Set<String> allowed = new HashSet<>(required);
        allowed.addAll(optional);
        allowed.add("op");
        if (!op.keySet().containsAll(required) || !allowed.containsAll(op.keySet())) {
            throw new Refused("invalid_op");
        }
    }

    private static String text(Map<String, JsonValue> fields, String key) throws Refused {
        if (fields.get(key) instanceof JsonValue.Text text) {
            return text.value();
        }
        throw new Refused("invalid_op");
    }

    private static String optionalText(Map<String, JsonValue> fields, String key) throws Refused {
        return fields.containsKey(key) ? text(fields, key) : null;
    }

    /** A rule the SDK checks before it sends anything, broken as the SDK reports it: {@code invalid_argument}. */
    private static void sdkRule(Runnable rule) throws Refused {
        try {
            rule.run();
        } catch (WireException error) {
            throw new Refused("invalid_argument");
        }
    }

    private static String id(Map<String, JsonValue> op) throws Refused {
        String id = text(op, "id");
        sdkRule(() -> new ItemId(id));
        return id;
    }

    private static String title(Map<String, JsonValue> op) throws Refused {
        String title = optionalText(op, "title");
        if (title != null) {
            sdkRule(() -> TextRules.validateTitle(title));
        }
        return title;
    }

    private static double number(Map<String, JsonValue> fields, String key) throws Refused {
        JsonValue value = fields.get(key);
        if (value instanceof JsonValue.Integer integer) {
            return integer.value();
        }
        if (value instanceof JsonValue.Number number) {
            return number.value();
        }
        throw new Refused("invalid_op");
    }

    private static Coordinate at(Map<String, JsonValue> fields) throws Refused {
        return new Coordinate(number(fields, "lat"), number(fields, "lon"));
    }

    private static List<TrackPoint> points(JsonValue value) throws Refused {
        if (!(value instanceof JsonValue.Array array) || array.values().isEmpty() || array.values().size() > 100) {
            throw new Refused("invalid_op");
        }
        List<TrackPoint> points = new ArrayList<>();
        for (JsonValue element : array.values()) {
            if (!(element instanceof JsonValue.Object point)) {
                throw new Refused("invalid_op");
            }
            Map<String, JsonValue> fields = point.fields();
            fields(fields, Set.of("lat", "lon"), Set.of("ts", "alt"));
            Long timestamp = null;
            if (fields.containsKey("ts")) {
                if (!(fields.get("ts") instanceof JsonValue.Integer integer)) {
                    throw new Refused("invalid_op");
                }
                timestamp = integer.value();
            }
            Double altitude = fields.containsKey("alt") ? number(fields, "alt") : null;
            points.add(new TrackPoint(at(fields), timestamp, altitude));
        }
        return points;
    }

    /** The bytes, or null unless the text is exactly what a standard base64 encoder writes for them. */
    static byte[] canonicalBase64(String text) {
        if (text.length() % 4 != 0) {
            return null;
        }
        try {
            byte[] bytes = java.util.Base64.getDecoder().decode(text);
            return java.util.Base64.getEncoder().encodeToString(bytes).equals(text) ? bytes : null;
        } catch (IllegalArgumentException error) {
            return null;
        }
    }

    /**
     * The code a refusal is printed with, or null when the app could not take the operation now and it is tried again.
     * A code the app sends is printed only if it looks like one.
     */
    static String refusalCode(ErdigoException error) {
        switch (error.kind()) {
            case NOT_PERMITTED:
                return "not_permitted";
            case INVALID_ARGUMENT:
                return "invalid_argument";
            case REJECTED: {
                String code = error.receiverError() == null ? null : error.receiverError().code();
                if (code != null && TEMPORARY_CODES.contains(code)) {
                    return null;
                }
                return code != null && CODE.matcher(code).matches() ? code : "rejected";
            }
            default:
                return null;
        }
    }

    // The loop

    /**
     * Waits 1, 2, 4 ... up to 60 seconds with 20 percent jitter; with --once, gives up about 30 seconds after the trouble
     * began. For the relay, and for answers from the app that say "not now" about an operation. An app that cannot be
     * reached at all is waited for by the SDK.
     */
    private static final class Outage {
        private final boolean once;
        private long startedNanos;
        private boolean started;
        private int attempt;

        Outage(boolean once) {
            this.once = once;
        }

        void over() {
            started = false;
            attempt = 0;
        }

        /**
         * Seconds to wait before the next try, or a negative number to give up. The outage starts when the trouble
         * began: for the relay, when the first failed attempt started, so an attempt that took long to fail counts
         * towards --once's limit.
         */
        double next(long troubleBeganNanos) {
            long now = System.nanoTime();
            if (!started || troubleBeganNanos - startedNanos < 0) {
                startedNanos = troubleBeganNanos;
                started = true;
            }
            double delay = Math.min(60.0, Math.pow(2.0, Math.min(attempt, 6))) * (0.8 + 0.4 * RANDOM.nextDouble());
            attempt += 1;
            if (once) {
                double left = ONCE_GIVES_UP_AFTER_SECONDS - (now - startedNanos) / 1e9;
                if (left <= 0) {
                    return -1;
                }
                delay = Math.min(delay, left);
            }
            return delay;
        }
    }

    /**
     * Watches each try to hand an operation to the SDK from outside, because the SDK does not say how it is doing, and
     * keeps what the gateway knows about the operation's trouble reaching the app across all its tries.
     *
     * <p>The SDK counts its 30 seconds with --once from the last connection it made, so an app that takes every
     * connection but never answers, or whose answers are all lost, would keep it trying for ever. So the gateway counts
     * too, from the first sign of trouble with the operation, however many connections it takes: a try that failed, or
     * the connection the try was sending on going away, which the SDK does only when sending failed. With --once it
     * interrupts the SDK once the trouble has lasted {@link #ONCE_GIVES_UP_AFTER_SECONDS}. Time the SDK spends pacing
     * points before it sends them is no trouble: it keeps its connection meanwhile, so a long track is never given up.
     *
     * <p>It also says once per operation that the app does not answer, when the trouble, or a try without any
     * connection, has lasted 2 seconds. The SDK does not say when it tries next, so the line gives no time. A request
     * that waits for the app's rate limit keeps its connection, so that wait is not mistaken for this.
     */
    private static final class Watch implements AutoCloseable {
        private static final long NOTICE_AFTER_NANOS = 2_000_000_000L;
        private static final long GIVE_UP_AFTER_NANOS = (long) (ONCE_GIVES_UP_AFTER_SECONDS * 1e9);
        /** Often enough to see the second or so without a connection before the SDK connects again. */
        private static final long EVERY_MILLIS = 100;

        private final ErdigoClient client;
        private final PrintStream out;
        private final boolean once;
        private final Thread main;
        private final Thread thread;
        // Guarded by this.
        private boolean trying;
        private boolean sending;
        private boolean unconnected;
        private long unconnectedSince;
        private boolean troubled;
        private long troubleSince;
        private boolean said;
        private boolean gaveUp;

        Watch(ErdigoClient client, PrintStream out, boolean once, Thread main) {
            this.client = client;
            this.out = out;
            this.once = once;
            this.main = main;
            thread = new Thread(this::watch, "gateway-watch");
            thread.setDaemon(true);
            thread.start();
        }

        synchronized void nextOperation() {
            troubled = false;
            said = false;
            gaveUp = false;
        }

        /** A try starts: the gateway is about to connect. */
        synchronized void tryStarted() {
            trying = true;
            sending = false;
            unconnected = false;
        }

        /** The try has a connection and is sending the operation over it. */
        synchronized void sending() {
            sending = true;
        }

        synchronized void tryEnded() {
            trying = false;
            sending = false;
            unconnected = false;
        }

        /** Notes trouble that showed at {@code moment}, and returns when the trouble with this operation began. */
        synchronized long note(long moment) {
            if (!troubled || moment - troubleSince < 0) {
                troubleSince = moment;
                troubled = true;
            }
            return troubleSince;
        }

        /** Whether the gateway gave up on this operation, with --once, and interrupted the try. */
        synchronized boolean gaveUp() {
            return gaveUp;
        }

        /** A line that says when the gateway tries next was printed, so the one without a time is not needed. */
        synchronized void noticeGiven() {
            said = true;
        }

        private void watch() {
            while (true) {
                try {
                    Thread.sleep(EVERY_MILLIS);
                } catch (InterruptedException closed) {
                    return;
                }
                synchronized (this) {
                    if (!trying) {
                        continue;
                    }
                    if (gaveUp) {
                        // Until the try has ended.
                        interruptMain();
                        continue;
                    }
                    long now = System.nanoTime();
                    if (client.limits() != null) {
                        unconnected = false;
                    } else {
                        if (!unconnected) {
                            unconnected = true;
                            unconnectedSince = now;
                        }
                        if (sending) {
                            note(now);
                        }
                    }
                    long troubleFor = troubled ? now - troubleSince : 0;
                    long unconnectedFor = unconnected ? now - unconnectedSince : 0;
                    if (!said && Math.max(troubleFor, unconnectedFor) >= NOTICE_AFTER_NANOS) {
                        out.println("No answer from the Erdigo app; trying again.");
                        said = true;
                    }
                    if (once && troubled && troubleFor >= GIVE_UP_AFTER_NANOS) {
                        gaveUp = true;
                        interruptMain();
                    }
                }
            }
        }

        /** Interrupts the SDK call the main thread is in. The main thread clears the interrupt once the try ended. */
        private void interruptMain() {
            synchronized (STOP) {
                if (interruptible) {
                    main.interrupt();
                }
            }
        }

        @Override
        public void close() {
            thread.interrupt();
        }
    }

    private static String seconds(double delay) {
        long whole = Math.max(1, Math.round(delay));
        return whole == 1 ? "1 second" : whole + " seconds";
    }

    /** From now on the stop hook may interrupt this thread. Returns false if the gateway is already stopping. */
    private static boolean enterWait() {
        synchronized (STOP) {
            interruptible = true;
            return !stopping;
        }
    }

    /** From now on the stop hook leaves this thread alone, and no interrupt from it is left pending. */
    private static void leaveWait() {
        synchronized (STOP) {
            interruptible = false;
            Thread.interrupted();
        }
    }

    /** Sleeps unless the gateway is stopping. Returns false if it is. */
    private static boolean pause(double seconds) {
        if (!enterWait()) {
            leaveWait();
            return false;
        }
        boolean slept = false;
        try {
            Thread.sleep((long) (seconds * 1000));
            slept = true;
        } catch (InterruptedException error) {
            // Stopping.
        } finally {
            leaveWait();
        }
        return slept && !stopping;
    }

    private void summary(int done, long upTo, int refused) {
        if (done > 0) {
            out.println("Done with " + done + (done == 1 ? " operation" : " operations") + ", up to " + upTo
                    + (refused > 0 ? " (" + refused + " refused)" : ""));
        }
    }

    private int loop(ErdigoClient client, Watch watch, String relay, String token, Place start, boolean once, PrintStream err) {
        Place place = start;
        Outage relayOutage = new Outage(once);
        String missedReported = null;
        while (!stopping) {
            Answer answer = null;
            long fetchStarted = System.nanoTime();
            try {
                if (!enterWait()) {
                    return 0;
                }
                answer = fetch(relay, token, place, once ? 0 : WAIT_SECONDS);
            } catch (InterruptedException error) {
                return 0;
            } catch (RelayBroken error) {
                err.println("gateway: " + error.getMessage() + ".");
                return EXIT_RELAY;
            } catch (RelayUnreachable error) {
                // Waited for below, once the stop hook can no longer interrupt the fetch.
            } finally {
                leaveWait();
            }
            if (stopping) {
                return 0;
            }
            if (answer == null) {
                double delay = relayOutage.next(fetchStarted);
                if (delay < 0) {
                    out.println("The relay could not be reached.");
                    return EXIT_UNREACHABLE;
                }
                out.println("The relay could not be reached; trying again in " + seconds(delay) + ".");
                if (!pause(delay)) {
                    return 0;
                }
                continue;
            }
            relayOutage.over();

            if (answer.reset()) {
                if (place.epoch() != null) {
                    out.println("The relay started a new numbering; starting from its oldest operation.");
                }
                place = place.at(answer.epoch(), 0);
                place.save();
            }
            // The relay says so on every fetch until the gateway is past the operations that expired.
            String missedAt = place.epoch() + "/" + place.seq();
            if (answer.missed() && !missedAt.equals(missedReported)) {
                out.println("Some operations expired on the relay before they were fetched.");
                missedReported = missedAt;
            }

            int done = 0;
            int refused = 0;
            for (int index = 0; index < answer.ops().size(); index++) {
                long seq = answer.seqs().get(index);
                Map<String, JsonValue> op = answer.ops().get(index);
                String name = op.get("op") instanceof JsonValue.Text text && OP_NAMES.contains(text.value()) ? text.value() : "unknown";
                String refusal = null;
                Action action = null;
                try {
                    action = action(op);
                } catch (Refused error) {
                    refusal = error.code;
                }
                watch.nextOperation();
                // For this operation only: the waits after answers that say "not now".
                Outage appOutage = new Outage(once);
                while (action != null) {
                    long attemptStarted = System.nanoTime();
                    ErdigoException failure = null;
                    boolean connected = false;
                    try {
                        if (!enterWait()) {
                            summary(done, place.seq(), refused);
                            return 0;
                        }
                        watch.tryStarted();
                        // Connecting first keeps a failure of the connection, such as an error answer to hello, apart
                        // from an answer about this operation. Only such an answer may refuse it.
                        client.connect();
                        connected = true;
                        watch.sending();
                        action.apply(client);
                    } catch (ErdigoException error) {
                        failure = error;
                    } finally {
                        watch.tryEnded();
                        leaveWait();
                    }
                    if (failure == null) {
                        // Taken by the app, even if the gateway gave up at the same moment. If the gateway is
                        // stopping, it still saves its place before it ends.
                        break;
                    }
                    if (stopping) {
                        // Stopped while the SDK waited: neither saved nor acknowledged, so it comes again. Giving up,
                        // which may have come at the same moment, ends the run with 0 rather than 3 then.
                        summary(done, place.seq(), refused);
                        return 0;
                    }
                    if (watch.gaveUp() || failure.kind() == ErdigoException.Kind.NO_ANSWER) {
                        // Only with --once: the app did not take it for as long as the gateway, or the SDK without a
                        // connection, may wait. A request interrupted after it was sent may have been applied, which
                        // at-least-once delivery allows: it stays on the relay.
                        summary(done, place.seq(), refused);
                        out.println("The Erdigo app could not be reached. The operations stay on the relay.");
                        return EXIT_UNREACHABLE;
                    }
                    if (failure.kind() == ErdigoException.Kind.INTERRUPTED) {
                        summary(done, place.seq(), refused);
                        return 0;
                    }
                    if (failure.kind() == ErdigoException.Kind.STORAGE) {
                        summary(done, place.seq(), refused);
                        err.println("gateway: " + failure.getMessage());
                        return EXIT_USAGE;
                    }
                    refusal = connected ? refusalCode(failure) : null;
                    if (refusal != null) {
                        break;
                    }
                    // The app answered about the connection rather than the operation. internal_error, rate_limited
                    // and too_many_errors leave it unapplied (spec sections 7 and 10), so it is sent again as a new
                    // request. So it is after an answer the SDK cannot use, or with another program using this key,
                    // though the app may then have applied it already. The time counts from the first sign of
                    // trouble with this operation, which may be earlier than this try.
                    double delay = appOutage.next(watch.note(attemptStarted));
                    if (delay < 0) {
                        summary(done, place.seq(), refused);
                        out.println("The Erdigo app could not be reached. The operations stay on the relay.");
                        return EXIT_UNREACHABLE;
                    }
                    watch.noticeGiven();
                    out.println("No answer from the Erdigo app; trying again in " + seconds(delay) + ".");
                    if (!pause(delay)) {
                        summary(done, place.seq(), refused);
                        return 0;
                    }
                }
                if (refusal != null) {
                    out.println("Refused operation " + seq + " (" + name + "): " + refusal);
                    refused += 1;
                }
                // Finished, by the app or for good: the relay may forget it once the next fetch says so.
                place = place.at(answer.epoch(), seq);
                place.save();
                done += 1;
            }
            summary(done, place.seq(), refused);
            // The app closes a connection left idle, so each batch starts on a fresh one.
            client.close();
            if (answer.ops().isEmpty()) {
                if (once) {
                    return 0;
                }
                // A relay that cannot hold a fetch open answers at once, and asking again straight away would flood it.
                double left = POLL_SECONDS * (0.8 + 0.4 * RANDOM.nextDouble()) - (System.nanoTime() - fetchStarted) / 1e9;
                if (left > 0 && !pause(left)) {
                    return 0;
                }
            }
        }
        return 0;
    }
}
