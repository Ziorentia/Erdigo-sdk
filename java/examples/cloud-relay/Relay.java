package com.ziorentia.erdigo.examples.cloudrelay;

import com.sun.net.httpserver.Headers;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;
import com.ziorentia.erdigo.core.Coordinate;
import com.ziorentia.erdigo.core.ItemId;
import com.ziorentia.erdigo.core.JsonParseException;
import com.ziorentia.erdigo.core.JsonValue;
import com.ziorentia.erdigo.core.JsonWriter;
import com.ziorentia.erdigo.core.MarkerColor;
import com.ziorentia.erdigo.core.PhotoType;
import com.ziorentia.erdigo.core.StrictJson;
import com.ziorentia.erdigo.core.TextRules;
import com.ziorentia.erdigo.core.TrackPoint;
import com.ziorentia.erdigo.core.WireException;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.PrintStream;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.UnknownHostException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.security.SecureRandom;
import java.time.Instant;
import java.time.temporal.ChronoUnit;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Base64;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.Semaphore;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.locks.Condition;
import java.util.concurrent.locks.ReentrantLock;
import java.util.function.LongSupplier;
import java.util.regex.Pattern;

/**
 * The relay of the Erdigo cloud relay example: devices send operations to it, and one gateway fetches them. It speaks
 * the API in docs/cloud-relay.md, keeps its queue in memory, and so starts a new epoch each time it starts.
 *
 * <p>Run it from the java folder, with the SDK on the class path:
 *
 * <pre>
 * java -cp target/erdigo-send.jar examples/cloud-relay/Relay.java --listen 127.0.0.1:8080
 * </pre>
 */
public final class Relay implements AutoCloseable {
    static final int MAX_BODY_BYTES = 2 * 1024 * 1024;
    static final int MAX_OPS_PER_POST = 100;
    static final int MAX_QUEUED_OPS = 10_000;
    static final long MAX_QUEUED_BYTES = 64L * 1024 * 1024;
    static final int MAX_ANSWER_BYTES = 4 * 1024 * 1024;
    static final int MAX_LIMIT = 100;
    static final int MAX_WAIT_SECONDS = 30;
    static final int MAX_PHOTO_BYTES = 1_048_576;
    static final int MAX_PHOTO_BASE64 = 4 * ((MAX_PHOTO_BYTES + 2) / 3);
    static final int MAX_POINTS = 100;
    static final long MAX_SEQ = (1L << 53) - 1;
    /** Fetches held open at once. Each holds a worker, so more than a gateway needs would let them crowd out sources. */
    static final int MAX_WAITING_FETCHES = 4;
    /**
     * Connections the JDK's server lets in, and so workers: it reads a request's headers on a worker, so a slow client
     * holds one until the headers are in or maxReqTime ends it.
     */
    static final int MAX_CONNECTIONS = 256;
    /** How long a POST waits for room to read or check its body before it is answered with queue_full. */
    static final int BUSY_WAIT_SECONDS = 15;
    /** Checking a body of 2 MiB can build a tree of about 80 MB, so this much heap is set aside for each one at once. */
    static final long HEAP_PER_CHECK = 192L * 1024 * 1024;

    private static final Pattern TOKEN = Pattern.compile("[A-Za-z0-9._~-]{32,256}");
    private static final Pattern EPOCH = Pattern.compile("[A-Za-z0-9_-]{16,64}");
    private static final Pattern NUMBER = Pattern.compile("[0-9]{1,16}");
    private static final Set<String> QUERY_NAMES = Set.of("epoch", "after", "limit", "wait");
    private static final byte[] HEALTHY = "{\"ok\":true}".getBytes(StandardCharsets.UTF_8);

    private static final String USAGE = String.join("\n",
            "relay: the relay of the Erdigo cloud relay example. Devices send operations to it, and one gateway fetches them.",
            "",
            "Usage: relay [--listen HOST:PORT] [--keep-hours HOURS] [--behind-proxy]",
            "",
            "  --listen HOST:PORT  Where to accept plain HTTP, 127.0.0.1:8080 by default. Port 0 picks a free port.",
            "                      A host other than 127.0.0.1, ::1 or localhost needs --behind-proxy.",
            "  --keep-hours HOURS  How long an operation waits for the gateway: 1 to 168, 24 by default.",
            "  --behind-proxy      HTTPS ends in a proxy in front of the relay, so it may listen on another address.",
            "",
            "Tokens are read from the environment, never from the command line:",
            "  ERDIGO_RELAY_SOURCE_TOKENS  One or more source tokens, separated by commas.",
            "  ERDIGO_RELAY_GATEWAY_TOKEN  The gateway token, different from every source token.",
            "  A token is 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.",
            "",
            "See docs/cloud-relay.md.",
            "");

    /** Which fields each operation takes. */
    private record Shape(List<String> required, List<String> optional) {
    }

    private static final Map<String, Shape> SHAPES = Map.of(
            "pin", new Shape(List.of("id", "lat", "lon"), List.of("title")),
            "marker", new Shape(List.of("id", "lat", "lon"), List.of("title", "color")),
            "note", new Shape(List.of("id", "lat", "lon", "text"), List.of()),
            "photo", new Shape(List.of("id", "lat", "lon", "type", "data"), List.of("title")),
            "track", new Shape(List.of("id", "points"), List.of("title")),
            "finish_track", new Shape(List.of("id"), List.of()),
            "move", new Shape(List.of("id", "lat", "lon"), List.of()),
            "delete", new Shape(List.of("id"), List.of()));

    private static final String OP_NAMES = "op must be one of pin, marker, note, photo, track, finish_track, move and delete";

    /** A mistake in the command line or the environment. Its message never repeats what was given. */
    static final class UsageError extends RuntimeException {
        private static final long serialVersionUID = 1L;

        UsageError(String message) {
            super(message);
        }
    }

    record Settings(String host, InetAddress address, int port, int keepHours, List<byte[]> sourceDigests, byte[] gatewayDigest) {
    }

    private enum Role {
        NONE,
        SOURCE,
        GATEWAY,
    }

    /** An operation the relay holds. {@code json} is the operation as it was validated, written out again. */
    private record Entry(long seq, long receivedMs, byte[] json) {
    }

    private final Settings settings;
    private final LongSupplier clock;
    private final int maxQueuedOps;
    private final long maxQueuedBytes;
    private final PrintStream log;
    private final String epoch;
    private final HttpServer server;
    private final ExecutorService workers;
    private final ScheduledExecutorService sweeper;
    /** Bodies being read and checked, in KiB, and checks running at once, both sized to the heap. */
    private final Semaphore bodyKiB;
    private final Semaphore checks;

    private final ReentrantLock lock = new ReentrantLock();
    private final Condition arrived = lock.newCondition();
    // Guarded by lock.
    private final ArrayDeque<Entry> entries = new ArrayDeque<>();
    private long queuedBytes;
    private long lastSeq;
    private long expiredUpTo;
    private int waitingFetches;
    private boolean closing;

    private Relay(Settings settings, LongSupplier clock, int maxQueuedOps, long maxQueuedBytes, PrintStream log) throws IOException {
        this.settings = settings;
        this.clock = clock;
        this.maxQueuedOps = maxQueuedOps;
        this.maxQueuedBytes = maxQueuedBytes;
        this.log = log;
        byte[] random = new byte[16];
        new SecureRandom().nextBytes(random);
        this.epoch = Base64.getUrlEncoder().withoutPadding().encodeToString(random);
        long heap = Runtime.getRuntime().maxMemory();
        this.bodyKiB = new Semaphore((int) (Math.max(MAX_BODY_BYTES + 1024L, Math.min(heap / 8, 64L * 1024 * 1024)) / 1024));
        this.checks = new Semaphore((int) Math.max(1, Math.min(8, heap / HEAP_PER_CHECK)));
        limitTheServer();
        server = HttpServer.create(new InetSocketAddress(settings.address(), settings.port()), 0);
        AtomicInteger count = new AtomicInteger();
        ThreadPoolExecutor pool = new ThreadPoolExecutor(MAX_CONNECTIONS, MAX_CONNECTIONS, 60, TimeUnit.SECONDS,
                new LinkedBlockingQueue<>(), task -> daemon(task, "relay-worker-" + count.incrementAndGet()));
        pool.allowCoreThreadTimeOut(true);
        workers = pool;
        sweeper = Executors.newSingleThreadScheduledExecutor(task -> daemon(task, "relay-sweeper"));
        server.createContext("/", this::handle);
        server.setExecutor(workers);
        server.start();
        // Expired operations leave memory even while nobody asks.
        sweeper.scheduleWithFixedDelay(() -> {
            lock.lock();
            try {
                purge();
            } finally {
                lock.unlock();
            }
        }, 1, 1, TimeUnit.MINUTES);
    }

    private static Thread daemon(Runnable task, String name) {
        Thread thread = new Thread(task, name);
        thread.setDaemon(true);
        return thread;
    }

    /**
     * Bounds what one connection can cost, as far as the JDK's server allows. Each is read once, when the server is
     * first used, and a value set with -D on the command line wins.
     */
    private static void limitTheServer() {
        // Seconds to receive a whole request, and to send an answer after it; a fetch waits at most 30 seconds.
        setIfAbsent("sun.net.httpserver.maxReqTime", "60");
        setIfAbsent("sun.net.httpserver.maxRspTime", "120");
        setIfAbsent("sun.net.httpserver.maxReqHeaders", "100");
        setIfAbsent("sun.net.httpserver.maxReqHeaderSize", "32768");
        setIfAbsent("jdk.httpserver.maxConnections", String.valueOf(MAX_CONNECTIONS));
        // A body the relay did not read is not read at all when the exchange ends: the connection is closed instead,
        // so a client that refuses to finish its body cannot hold a worker.
        setIfAbsent("sun.net.httpserver.drainAmount", "0");
    }

    private static void setIfAbsent(String name, String value) {
        if (System.getProperty(name) == null) {
            System.setProperty(name, value);
        }
    }

    // Starting

    public static void main(String[] args) {
        PrintStream out = new PrintStream(System.out, true, StandardCharsets.UTF_8);
        PrintStream err = new PrintStream(System.err, true, StandardCharsets.UTF_8);
        if (args.length == 1 && (args[0].equals("--help") || args[0].equals("-h") || args[0].equals("help"))) {
            out.print(USAGE);
            return;
        }
        Relay relay;
        try {
            relay = start(args, System.getenv(), System::currentTimeMillis, MAX_QUEUED_OPS, MAX_QUEUED_BYTES, out);
        } catch (UsageError error) {
            err.println("relay: " + error.getMessage());
            System.exit(1);
            return;
        } catch (IOException error) {
            err.println("relay: could not listen on that address (" + error.getClass().getSimpleName() + ")");
            System.exit(1);
            return;
        }
        // Ctrl+C and SIGTERM end the process here, with status 0 as for any other clean stop.
        Runtime.getRuntime().addShutdownHook(new Thread(() -> {
            relay.close();
            out.flush();
            Runtime.getRuntime().halt(0);
        }, "relay-stop"));
        out.println("Relay listening on " + relay.url());
    }

    /**
     * Starts a relay. {@link #main} passes the real clock and the limits of the guide; the tests pass a clock of their
     * own and smaller limits. No option or variable reaches these.
     */
    static Relay start(String[] args, Map<String, String> env, LongSupplier clock, int maxQueuedOps, long maxQueuedBytes, PrintStream log)
            throws IOException {
        return new Relay(configure(args, env), clock, maxQueuedOps, maxQueuedBytes, log);
    }

    static Settings configure(String[] args, Map<String, String> env) {
        String listen = null;
        String keep = null;
        boolean behindProxy = false;
        for (int index = 0; index < args.length; index++) {
            switch (args[index]) {
                case "--listen":
                    if (listen != null) {
                        throw new UsageError("--listen was given twice");
                    }
                    listen = value(args, ++index, "--listen");
                    break;
                case "--keep-hours":
                    if (keep != null) {
                        throw new UsageError("--keep-hours was given twice");
                    }
                    keep = value(args, ++index, "--keep-hours");
                    break;
                case "--behind-proxy":
                    behindProxy = true;
                    break;
                default:
                    // Not repeated: it might be a token, which belongs in the environment.
                    throw new UsageError("unknown argument. See 'relay --help'. Tokens are read from the environment only.");
            }
        }
        int keepHours = 24;
        if (keep != null) {
            if (!keep.matches("[0-9]{1,3}") || Integer.parseInt(keep) < 1 || Integer.parseInt(keep) > 168) {
                throw new UsageError("--keep-hours must be a whole number of hours from 1 to 168");
            }
            keepHours = Integer.parseInt(keep);
        }

        String host;
        String portText;
        String where = listen == null ? "127.0.0.1:8080" : listen;
        if (where.startsWith("[")) {
            int close = where.indexOf(']');
            if (close < 0 || close + 1 >= where.length() || where.charAt(close + 1) != ':' || !where.substring(1, close).contains(":")) {
                throw new UsageError("--listen takes HOST:PORT, with an IPv6 address in brackets, such as [::1]:8080");
            }
            host = where.substring(1, close);
            portText = where.substring(close + 2);
        } else {
            int colon = where.lastIndexOf(':');
            if (colon <= 0 || where.indexOf(':') != colon) {
                throw new UsageError("--listen takes HOST:PORT, with an IPv6 address in brackets, such as [::1]:8080");
            }
            host = where.substring(0, colon);
            portText = where.substring(colon + 1);
        }
        if (!portText.matches("[0-9]{1,5}") || Integer.parseInt(portText) > 65535) {
            throw new UsageError("--listen needs a port from 0 to 65535");
        }
        boolean loopback = host.equals("127.0.0.1") || host.equals("::1") || host.equalsIgnoreCase("localhost");
        if (!loopback && !behindProxy) {
            throw new UsageError("the relay speaks plain HTTP, so it listens only on 127.0.0.1, ::1 or localhost unless "
                    + "--behind-proxy says that HTTPS ends in a proxy in front of it");
        }
        InetAddress address;
        try {
            address = InetAddress.getByName(host);
        } catch (UnknownHostException | SecurityException error) {
            throw new UsageError("the host in --listen could not be found");
        }

        List<byte[]> sources = new ArrayList<>();
        String sourceText = env.get("ERDIGO_RELAY_SOURCE_TOKENS");
        if (sourceText == null || sourceText.isEmpty()) {
            throw new UsageError("ERDIGO_RELAY_SOURCE_TOKENS must hold at least one source token");
        }
        List<String> sourceTokens = Arrays.asList(sourceText.split(",", -1));
        for (String token : sourceTokens) {
            if (!TOKEN.matcher(token).matches()) {
                throw new UsageError("ERDIGO_RELAY_SOURCE_TOKENS must hold tokens of 32 to 256 characters from A-Z, a-z, 0-9, "
                        + "'-', '_', '.' and '~', separated by commas without spaces");
            }
            sources.add(digest(token));
        }
        String gateway = env.get("ERDIGO_RELAY_GATEWAY_TOKEN");
        if (gateway == null || !TOKEN.matcher(gateway).matches()) {
            throw new UsageError("ERDIGO_RELAY_GATEWAY_TOKEN must hold a token of 32 to 256 characters from A-Z, a-z, 0-9, "
                    + "'-', '_', '.' and '~'");
        }
        if (sourceTokens.contains(gateway)) {
            throw new UsageError("ERDIGO_RELAY_GATEWAY_TOKEN must differ from every source token");
        }
        return new Settings(host, address, Integer.parseInt(portText), keepHours, List.copyOf(sources), digest(gateway));
    }

    private static String value(String[] args, int index, String name) {
        if (index >= args.length) {
            throw new UsageError(name + " needs a value");
        }
        return args[index];
    }

    private static byte[] digest(String token) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(token.getBytes(StandardCharsets.UTF_8));
        } catch (NoSuchAlgorithmException error) {
            throw new IllegalStateException("SHA-256 is missing", error);
        }
    }

    int port() {
        return server.getAddress().getPort();
    }

    String url() {
        String host = settings.host().contains(":") ? "[" + settings.host() + "]" : settings.host();
        return "http://" + host + ":" + port();
    }

    @Override
    public void close() {
        lock.lock();
        try {
            closing = true;
            arrived.signalAll();
        } finally {
            lock.unlock();
        }
        server.stop(1);
        workers.shutdownNow();
        sweeper.shutdownNow();
    }

    // Requests

    private void handle(HttpExchange exchange) {
        String method = exchange.getRequestMethod();
        String path = exchange.getRequestURI().getRawPath();
        int status = 0;
        boolean[] answered = {false};
        try {
            status = route(exchange, method, path == null ? "" : path, answered);
        } catch (IOException error) {
            // The client went away.
        } catch (RuntimeException error) {
            if (!answered[0]) {
                try {
                    status = send(exchange, 500, failure("internal_error"), answered);
                } catch (IOException ignored) {
                    // The client went away.
                }
            }
        } finally {
            exchange.close();
            // Never the query, a token or what an operation holds.
            String shownMethod = method.matches("[A-Z]{1,10}") ? method : "?";
            String shownPath = "/v1/ops".equals(path) || "/v1/health".equals(path) ? path : "(other)";
            log.println(Instant.now().truncatedTo(ChronoUnit.SECONDS) + " " + shownMethod + " " + shownPath + " "
                    + (status == 0 ? "aborted" : String.valueOf(status)));
        }
    }

    private int route(HttpExchange exchange, String method, String path, boolean[] answered) throws IOException {
        if (path.equals("/v1/health")) {
            if (!method.equals("GET")) {
                return refuse(exchange, 405, "method_not_allowed", answered);
            }
            finishEmptyBody(exchange);
            return send(exchange, 200, HEALTHY, answered);
        }
        if (!path.equals("/v1/ops")) {
            return refuse(exchange, 404, "not_found", answered);
        }
        boolean posting = method.equals("POST");
        if (!posting && !method.equals("GET")) {
            return refuse(exchange, 405, "method_not_allowed", answered);
        }
        Role role = role(exchange.getRequestHeaders());
        if (posting ? role != Role.SOURCE : role != Role.GATEWAY) {
            return refuse(exchange, 401, "unauthorized", answered);
        }
        return posting ? take(exchange, answered) : give(exchange, answered);
    }

    /** Every token is compared, each through its SHA-256, so the time taken says nothing about which one is close. */
    private Role role(Headers headers) {
        List<String> values = headers.get("Authorization");
        if (values == null || values.size() != 1) {
            return Role.NONE;
        }
        String value = values.get(0);
        if (value.length() <= 7 || !value.regionMatches(true, 0, "Bearer ", 0, 7)) {
            return Role.NONE;
        }
        byte[] given = digest(value.substring(7).strip());
        boolean source = false;
        for (byte[] known : settings.sourceDigests()) {
            source |= MessageDigest.isEqual(given, known);
        }
        boolean gateway = MessageDigest.isEqual(given, settings.gatewayDigest());
        return gateway ? Role.GATEWAY : source ? Role.SOURCE : Role.NONE;
    }

    // Sending: POST /v1/ops

    private int take(HttpExchange exchange, boolean[] answered) throws IOException {
        Headers headers = exchange.getRequestHeaders();
        if (!isJson(headers.get("Content-Type"))) {
            return refuse(exchange, 415, "unsupported_media_type", answered);
        }
        String transfer = headers.getFirst("Transfer-Encoding");
        boolean chunked = transfer != null && transfer.toLowerCase(Locale.ROOT).contains("chunked");
        String length = headers.getFirst("Content-Length");
        if (!chunked) {
            if (length == null || !length.trim().matches("[0-9]{1,18}")) {
                return refuse(exchange, 411, "length_required", answered);
            }
            if (Long.parseLong(length.trim()) > MAX_BODY_BYTES) {
                return refuse(exchange, 413, "too_large", answered);
            }
        }
        // What a body costs in memory is bounded before it is read, and again before it is parsed into a tree.
        int announced = chunked ? MAX_BODY_BYTES + 1 : (int) Long.parseLong(length.trim());
        int reservedKiB = announced / 1024 + 1;
        if (!acquire(bodyKiB, reservedKiB)) {
            return busy(exchange, answered);
        }
        List<byte[]> taken = new ArrayList<>();
        byte[] refused;
        try {
            byte[] body = readAtMost(exchange.getRequestBody(), MAX_BODY_BYTES, chunked ? 16 * 1024 : announced);
            if (body == null) {
                return refuse(exchange, 413, "too_large", answered);
            }
            if (!acquire(checks, 1)) {
                return busy(exchange, answered);
            }
            try {
                refused = check(body, taken);
            } finally {
                checks.release();
            }
        } finally {
            bodyKiB.release(reservedKiB);
        }
        if (refused != null) {
            return send(exchange, 400, refused, answered);
        }
        long takenBytes = 0;
        for (byte[] json : taken) {
            takenBytes += json.length;
        }

        long last;
        lock.lock();
        try {
            purge();
            if (entries.size() + taken.size() > maxQueuedOps || queuedBytes + takenBytes > maxQueuedBytes) {
                exchange.getResponseHeaders().set("Retry-After", "30");
                last = -1;
            } else {
                long now = clock.getAsLong();
                for (byte[] json : taken) {
                    lastSeq += 1;
                    entries.addLast(new Entry(lastSeq, now, json));
                }
                queuedBytes += takenBytes;
                last = lastSeq;
                arrived.signalAll();
            }
        } finally {
            lock.unlock();
        }
        if (last < 0) {
            return refuse(exchange, 503, "queue_full", answered);
        }
        byte[] answer = JsonWriter.encode(JsonWriter.object().put("accepted", (long) taken.size()).put("last_seq", last).build());
        return send(exchange, 200, answer, answered);
    }

    /**
     * Checks a body and adds its operations, written out again, to {@code taken}. Returns the 400 answer that refuses
     * it, or null if it is taken.
     */
    private static byte[] check(byte[] body, List<byte[]> taken) {
        JsonValue document;
        try {
            document = StrictJson.parse(body, 16);
        } catch (JsonParseException error) {
            return failure("invalid_json");
        }
        if (!(document instanceof JsonValue.Object root)) {
            return failure("invalid_json");
        }
        if (root.fields().size() != 1 || !(root.fields().get("ops") instanceof JsonValue.Array ops)) {
            return failure("invalid_body", "the body must be {\"ops\":[...]} and nothing else");
        }
        if (ops.values().isEmpty() || ops.values().size() > MAX_OPS_PER_POST) {
            return failure("invalid_body", "ops must hold 1 to " + MAX_OPS_PER_POST + " operations");
        }
        for (int index = 0; index < ops.values().size(); index++) {
            JsonValue op = ops.values().get(index);
            String reason = refusal(op);
            if (reason != null) {
                taken.clear();
                return JsonWriter.encode(JsonWriter.object()
                        .put("error", "invalid_op")
                        .put("index", (long) index)
                        .put("reason", reason)
                        .build());
            }
            taken.add(JsonWriter.encode(written(op)));
        }
        return null;
    }

    /** Waits a while for permits. False if there was no room in time, or the relay is stopping. */
    private static boolean acquire(Semaphore semaphore, int permits) {
        try {
            return semaphore.tryAcquire(permits, BUSY_WAIT_SECONDS, TimeUnit.SECONDS);
        } catch (InterruptedException stopping) {
            Thread.currentThread().interrupt();
            return false;
        }
    }

    /** Too many bodies at once for the memory set aside: as for a full queue, the source sends again later. */
    private static int busy(HttpExchange exchange, boolean[] answered) throws IOException {
        exchange.getResponseHeaders().set("Retry-After", "30");
        return refuse(exchange, 503, "queue_full", answered);
    }

    /** {@code application/json}, with nothing but an optional {@code charset=utf-8}. */
    private static boolean isJson(List<String> values) {
        if (values == null || values.size() != 1) {
            return false;
        }
        String[] parts = values.get(0).split(";", -1);
        if (!parts[0].trim().equalsIgnoreCase("application/json") || parts.length > 2) {
            return false;
        }
        if (parts.length == 2) {
            String parameter = parts[1].trim().toLowerCase(Locale.ROOT).replace(" ", "");
            return parameter.equals("charset=utf-8") || parameter.equals("charset=\"utf-8\"");
        }
        return true;
    }

    /** The whole body, or null if it is longer than {@code limit}. At most one byte past the limit is read. */
    private static byte[] readAtMost(InputStream input, int limit, int expected) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream(Math.max(1, Math.min(expected, limit)));
        byte[] buffer = new byte[16 * 1024];
        while (true) {
            int room = limit - output.size();
            int read = input.read(buffer, 0, Math.min(buffer.length, room + 1));
            if (read < 0) {
                return output.toByteArray();
            }
            if (read > room) {
                return null;
            }
            output.write(buffer, 0, read);
        }
    }

    /**
     * Why an operation is refused, or null if it follows docs/cloud-relay.md. The checks of the values are the SDK's
     * own, so the relay refuses what the SDKs would refuse before sending.
     */
    static String refusal(JsonValue value) {
        if (!(value instanceof JsonValue.Object object)) {
            return "an operation must be a JSON object";
        }
        Map<String, JsonValue> fields = object.fields();
        if (!(fields.get("op") instanceof JsonValue.Text name) || !SHAPES.containsKey(name.value())) {
            return OP_NAMES;
        }
        Shape shape = SHAPES.get(name.value());
        for (String key : fields.keySet()) {
            if (!key.equals("op") && !shape.required().contains(key) && !shape.optional().contains(key)) {
                return "the operation has a field that " + name.value() + " does not take";
            }
        }
        for (String key : shape.required()) {
            if (!fields.containsKey(key)) {
                return key + " is required for " + name.value();
            }
        }
        try {
            new ItemId(text(fields, "id"));
            if (fields.containsKey("lat")) {
                new Coordinate(number(fields, "lat"), number(fields, "lon"));
            }
            if (fields.containsKey("title")) {
                TextRules.validateTitle(text(fields, "title"));
            }
            if (fields.containsKey("text")) {
                TextRules.validateNote(text(fields, "text"));
            }
            if (fields.containsKey("color") && MarkerColor.fromWire(text(fields, "color")) == null) {
                return "color must be one of red, orange, yellow, green, blue, purple and gray";
            }
            if (fields.containsKey("type")) {
                String reason = photoRefusal(text(fields, "type"), text(fields, "data"));
                if (reason != null) {
                    return reason;
                }
            }
            if (fields.containsKey("points")) {
                return pointsRefusal(fields.get("points"));
            }
            return null;
        } catch (WireException error) {
            return error.detail();
        } catch (WrongType error) {
            return error.getMessage();
        }
    }

    private static final class WrongType extends Exception {
        private static final long serialVersionUID = 1L;

        WrongType(String message) {
            super(message, null, false, false);
        }
    }

    private static String text(Map<String, JsonValue> fields, String key) throws WrongType {
        if (fields.get(key) instanceof JsonValue.Text text) {
            return text.value();
        }
        throw new WrongType(key + " must be a string");
    }

    private static double number(Map<String, JsonValue> fields, String key) throws WrongType {
        JsonValue value = fields.get(key);
        if (value instanceof JsonValue.Integer integer) {
            return integer.value();
        }
        if (value instanceof JsonValue.Number number) {
            return number.value();
        }
        throw new WrongType(key + " must be a number");
    }

    private static String photoRefusal(String typeName, String data) {
        PhotoType type = PhotoType.fromWire(typeName);
        if (type == null) {
            return "type must be image/jpeg or image/png";
        }
        if (data.length() > MAX_PHOTO_BASE64) {
            return "data must decode to 1 to " + MAX_PHOTO_BYTES + " bytes";
        }
        byte[] bytes = canonicalBase64(data);
        if (bytes == null) {
            return "data must be standard base64 with padding, as an encoder writes it, with no line breaks";
        }
        if (bytes.length == 0 || bytes.length > MAX_PHOTO_BYTES) {
            return "data must decode to 1 to " + MAX_PHOTO_BYTES + " bytes";
        }
        if (PhotoType.detect(bytes) != type) {
            return "data does not start with the bytes of " + (type == PhotoType.JPEG ? "a JPEG" : "a PNG");
        }
        return null;
    }

    /** The bytes, or null unless the text is exactly what a standard encoder writes for them. */
    static byte[] canonicalBase64(String text) {
        if (text.length() % 4 != 0) {
            return null;
        }
        byte[] bytes;
        try {
            bytes = Base64.getDecoder().decode(text);
        } catch (IllegalArgumentException error) {
            return null;
        }
        return Base64.getEncoder().encodeToString(bytes).equals(text) ? bytes : null;
    }

    private static String pointsRefusal(JsonValue value) throws WrongType {
        if (!(value instanceof JsonValue.Array points) || points.values().isEmpty() || points.values().size() > MAX_POINTS) {
            return "points must be a list of 1 to " + MAX_POINTS + " points";
        }
        for (JsonValue element : points.values()) {
            if (!(element instanceof JsonValue.Object point)) {
                return "a point must be a JSON object";
            }
            Map<String, JsonValue> fields = point.fields();
            for (String key : fields.keySet()) {
                if (!key.equals("lat") && !key.equals("lon") && !key.equals("ts") && !key.equals("alt")) {
                    return "a point has lat, lon and optionally ts and alt, and nothing else";
                }
            }
            if (!fields.containsKey("lat") || !fields.containsKey("lon")) {
                return "a point needs lat and lon";
            }
            Long timestamp = null;
            if (fields.containsKey("ts")) {
                if (!(fields.get("ts") instanceof JsonValue.Integer integer)) {
                    return "ts must be a whole number of milliseconds from 0 to 2^53-1";
                }
                timestamp = integer.value();
            }
            Double altitude = fields.containsKey("alt") ? number(fields, "alt") : null;
            new TrackPoint(new Coordinate(number(fields, "lat"), number(fields, "lon")), timestamp, altitude);
        }
        return null;
    }

    /** Writes a validated operation out again. It holds only strings, numbers, lists and objects. */
    private static JsonWriter.Value written(JsonValue value) {
        if (value instanceof JsonValue.Text text) {
            return JsonWriter.of(text.value());
        }
        if (value instanceof JsonValue.Integer integer) {
            return JsonWriter.of(integer.value());
        }
        if (value instanceof JsonValue.Number number) {
            return JsonWriter.of(number.value());
        }
        if (value instanceof JsonValue.Array array) {
            List<JsonWriter.Value> values = new ArrayList<>();
            for (JsonValue element : array.values()) {
                values.add(written(element));
            }
            return JsonWriter.array(values);
        }
        if (value instanceof JsonValue.Object object) {
            JsonWriter.ObjectBuilder builder = JsonWriter.object();
            for (Map.Entry<String, JsonValue> field : object.fields().entrySet()) {
                builder.put(field.getKey(), written(field.getValue()));
            }
            return builder.build();
        }
        throw new IllegalStateException("a validated operation holds no such value");
    }

    // Fetching: GET /v1/ops

    private record Query(String epoch, long after, int limit, int waitSeconds) {
    }

    /** The query, or null if it breaks a rule: a parameter twice, one not of the four, or a value out of range. */
    static Query query(String raw) {
        Map<String, String> given = new HashMap<>();
        if (raw != null && !raw.isEmpty()) {
            for (String piece : raw.split("&", -1)) {
                int equals = piece.indexOf('=');
                if (equals < 0) {
                    return null;
                }
                String name = piece.substring(0, equals);
                if (!QUERY_NAMES.contains(name) || given.put(name, piece.substring(equals + 1)) != null) {
                    return null;
                }
            }
        }
        String epoch = given.get("epoch");
        if (epoch != null && !EPOCH.matcher(epoch).matches()) {
            return null;
        }
        long after = whole(given.get("after"), 0, 0, MAX_SEQ);
        long limit = whole(given.get("limit"), MAX_LIMIT, 1, MAX_LIMIT);
        long wait = whole(given.get("wait"), 0, 0, MAX_WAIT_SECONDS);
        if (after < 0 || limit < 0 || wait < 0) {
            return null;
        }
        return new Query(epoch, after, (int) limit, (int) wait);
    }

    /** The number, its default when absent, or -1 when it is not a whole number in its range. */
    private static long whole(String text, long absent, long minimum, long maximum) {
        if (text == null) {
            return absent;
        }
        if (!NUMBER.matcher(text).matches()) {
            return -1;
        }
        long value = Long.parseLong(text);
        return value < minimum || value > maximum ? -1 : value;
    }

    private int give(HttpExchange exchange, boolean[] answered) throws IOException {
        Query query = query(exchange.getRequestURI().getRawQuery());
        if (query == null) {
            return refuse(exchange, 400, "invalid_query", answered);
        }
        finishEmptyBody(exchange);
        ByteArrayOutputStream answer = new ByteArrayOutputStream();
        lock.lock();
        try {
            purge();
            // With the epoch it gave, the gateway is done with everything up to 'after'. Without it, nothing is forgotten.
            boolean reset = !epoch.equals(query.epoch());
            long from = 0;
            if (!reset) {
                if (query.after() > lastSeq) {
                    answer = null;
                } else {
                    while (!entries.isEmpty() && entries.peekFirst().seq() <= query.after()) {
                        queuedBytes -= entries.pollFirst().json().length;
                    }
                    from = query.after();
                }
            }
            if (answer != null) {
                if (query.waitSeconds() > 0 && !holdsAfter(from) && waitingFetches < MAX_WAITING_FETCHES && !closing) {
                    waitingFetches += 1;
                    try {
                        long left = TimeUnit.SECONDS.toNanos(query.waitSeconds());
                        while (left > 0 && !holdsAfter(from) && !closing) {
                            left = arrived.awaitNanos(left);
                        }
                    } catch (InterruptedException stopping) {
                        Thread.currentThread().interrupt();
                    } finally {
                        waitingFetches -= 1;
                    }
                    purge();
                }
                answer(answer, from, reset, query.limit());
            }
        } finally {
            lock.unlock();
        }
        if (answer == null) {
            return refuse(exchange, 400, "invalid_query", answered);
        }
        return send(exchange, 200, answer.toByteArray(), answered);
    }

    private boolean holdsAfter(long seq) {
        return !entries.isEmpty() && entries.peekLast().seq() > seq;
    }

    /** Writes the answer for operations after {@code from}. Called holding the lock. */
    private void answer(ByteArrayOutputStream output, long from, boolean reset, int limit) {
        boolean missed = expiredUpTo > from;
        output.writeBytes(("{\"epoch\":\"" + epoch + "\",\"reset\":" + reset + ",\"missed\":" + missed + ",\"ops\":[")
                .getBytes(StandardCharsets.US_ASCII));
        int count = 0;
        for (Entry entry : entries) {
            if (entry.seq() <= from) {
                continue;
            }
            if (count == limit) {
                break;
            }
            byte[] head = ((count > 0 ? "," : "") + "{\"seq\":" + entry.seq() + ",\"received_ms\":" + entry.receivedMs() + ",\"op\":")
                    .getBytes(StandardCharsets.US_ASCII);
            // Two closing brackets follow the last operation.
            if (count > 0 && output.size() + head.length + entry.json().length + 1 + 2 > MAX_ANSWER_BYTES) {
                break;
            }
            output.writeBytes(head);
            output.writeBytes(entry.json());
            output.write('}');
            count += 1;
        }
        output.writeBytes("]}".getBytes(StandardCharsets.US_ASCII));
    }

    /** Drops operations that waited longer than the relay keeps them. Called holding the lock. */
    private void purge() {
        long now = clock.getAsLong();
        long keep = TimeUnit.HOURS.toMillis(settings.keepHours());
        while (!entries.isEmpty() && now - entries.peekFirst().receivedMs() >= keep) {
            Entry gone = entries.pollFirst();
            queuedBytes -= gone.json().length;
            expiredUpTo = gone.seq();
        }
    }

    // Answers

    /** Reads a request body that should be empty, so that the connection can be used again. A real body is left unread. */
    private static void finishEmptyBody(HttpExchange exchange) throws IOException {
        Headers headers = exchange.getRequestHeaders();
        String length = headers.getFirst("Content-Length");
        if (headers.getFirst("Transfer-Encoding") == null && (length == null || length.trim().equals("0"))) {
            exchange.getRequestBody().read();
        }
    }

    private static byte[] failure(String error) {
        return JsonWriter.encode(JsonWriter.object().put("error", error).build());
    }

    private static byte[] failure(String error, String reason) {
        return JsonWriter.encode(JsonWriter.object().put("error", error).put("reason", reason).build());
    }

    private static int refuse(HttpExchange exchange, int status, String error, boolean[] answered) throws IOException {
        return send(exchange, status, failure(error), answered);
    }

    private static int send(HttpExchange exchange, int status, byte[] body, boolean[] answered) throws IOException {
        Headers headers = exchange.getResponseHeaders();
        headers.set("Content-Type", "application/json");
        headers.set("Cache-Control", "no-store");
        answered[0] = true;
        boolean head = exchange.getRequestMethod().equals("HEAD");
        exchange.sendResponseHeaders(status, head ? -1 : body.length);
        if (!head) {
            try (OutputStream output = exchange.getResponseBody()) {
                output.write(body);
            }
        }
        return status;
    }
}
