package com.ziorentia.erdigo.examples;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

import com.ziorentia.erdigo.core.JsonValue;
import com.ziorentia.erdigo.core.JsonWriter;
import com.ziorentia.erdigo.core.StrictJson;
import com.ziorentia.erdigo.sdk.ErdigoClient;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.io.PrintStream;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.Socket;
import java.net.URI;
import java.net.URL;
import java.net.URLClassLoader;
import java.net.http.HttpClient;
import java.net.http.HttpHeaders;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.security.SecureRandom;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Base64;
import java.util.Comparator;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import java.util.function.LongSupplier;
import java.util.stream.Stream;
import javax.tools.DiagnosticCollector;
import javax.tools.JavaCompiler;
import javax.tools.JavaFileObject;
import javax.tools.StandardJavaFileManager;
import javax.tools.ToolProvider;

/**
 * What the cloud relay example's tests share. The example is not part of the SDK's jar, so it is compiled here, with
 * the same warnings-as-errors as the SDK, into {@code target/cloud-relay-classes}. The relay runs in this process,
 * where the tests can give it a clock and limits of their own, or as a program; the gateway always runs as a program.
 */
final class CloudRelayExample {
    static final String PACKAGE = "com.ziorentia.erdigo.examples.cloudrelay.";
    static final String RELAY = PACKAGE + "Relay";
    static final String GATEWAY = PACKAGE + "Gateway";
    static final String PNG_1X1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";
    static final boolean WINDOWS = System.getProperty("os.name", "").toLowerCase(Locale.ROOT).startsWith("windows");

    private static final SecureRandom RANDOM = new SecureRandom();
    private static Path compiled;
    private static ClassLoader loader;

    private CloudRelayExample() {
    }

    // Finding and compiling the example

    /** The java folder of the repository. */
    static Path javaFolder() {
        for (Path candidate = Paths.get("").toAbsolutePath(); candidate != null; candidate = candidate.getParent()) {
            if (Files.isRegularFile(candidate.resolve("examples/cloud-relay/Relay.java"))) {
                return candidate;
            }
            if (Files.isRegularFile(candidate.resolve("java/examples/cloud-relay/Relay.java"))) {
                return candidate.resolve("java");
            }
        }
        throw new IllegalStateException("java/examples/cloud-relay was not found");
    }

    static Path casesFile() {
        return javaFolder().getParent().resolve("docs").resolve("cloud-relay-cases.json");
    }

    /** The SDK's compiled classes, which the example is compiled and run against. */
    static Path sdkClasses() {
        try {
            return Paths.get(ErdigoClient.class.getProtectionDomain().getCodeSource().getLocation().toURI());
        } catch (Exception error) {
            throw new IllegalStateException(error);
        }
    }

    static synchronized Path compiled() throws IOException {
        if (compiled != null) {
            return compiled;
        }
        Path folder = javaFolder();
        Path output = folder.resolve("target").resolve("cloud-relay-classes");
        if (Files.exists(output)) {
            try (Stream<Path> old = Files.walk(output)) {
                for (Path path : old.sorted(Comparator.reverseOrder()).toList()) {
                    Files.delete(path);
                }
            }
        }
        Files.createDirectories(output);
        JavaCompiler compiler = ToolProvider.getSystemJavaCompiler();
        assertNotNull(compiler, "the tests need a JDK, not a JRE");
        DiagnosticCollector<JavaFileObject> diagnostics = new DiagnosticCollector<>();
        try (StandardJavaFileManager files = compiler.getStandardFileManager(diagnostics, Locale.ROOT, StandardCharsets.UTF_8)) {
            Iterable<? extends JavaFileObject> sources = files.getJavaFileObjects(
                    folder.resolve("examples/cloud-relay/Relay.java").toFile(), folder.resolve("examples/cloud-relay/Gateway.java").toFile());
            List<String> options = List.of("-Xlint:all", "-Werror", "--release", "17", "-encoding", "UTF-8",
                    "-cp", sdkClasses().toString(), "-d", output.toString());
            boolean ok = compiler.getTask(null, files, diagnostics, options, null, sources).call();
            assertTrue(ok, "the example does not compile without warnings: " + diagnostics.getDiagnostics());
        }
        compiled = output;
        return output;
    }

    static synchronized ClassLoader loader() throws IOException {
        if (loader == null) {
            loader = new URLClassLoader(new URL[] {compiled().toUri().toURL()}, CloudRelayExample.class.getClassLoader());
        }
        return loader;
    }

    // Tokens

    /** A new token, as the guide makes one. */
    static String token() {
        byte[] bytes = new byte[32];
        RANDOM.nextBytes(bytes);
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes);
    }

    // The relay in this process

    /** A relay started through its package-private {@code start}, with a clock and limits the test chooses. */
    static final class InProcessRelay implements AutoCloseable {
        final AutoCloseable relay;
        final int port;
        final String url;

        private InProcessRelay(AutoCloseable relay, int port, String url) {
            this.relay = relay;
            this.port = port;
            this.url = url;
        }

        static InProcessRelay start(List<String> args, Map<String, String> env, LongSupplier clock, int maxOps, long maxBytes, PrintStream log)
                throws Exception {
            Class<?> type = loader().loadClass(RELAY);
            Method start = type.getDeclaredMethod("start", String[].class, Map.class, LongSupplier.class, int.class, long.class, PrintStream.class);
            start.setAccessible(true);
            Object relay;
            try {
                relay = start.invoke(null, args.toArray(new String[0]), env, clock, maxOps, maxBytes, log);
            } catch (InvocationTargetException error) {
                if (error.getCause() instanceof Exception cause) {
                    throw cause;
                }
                throw error;
            }
            Method port = type.getDeclaredMethod("port");
            Method url = type.getDeclaredMethod("url");
            port.setAccessible(true);
            url.setAccessible(true);
            return new InProcessRelay((AutoCloseable) relay, (Integer) port.invoke(relay), (String) url.invoke(relay));
        }

        static InProcessRelay start(Map<String, String> env, PrintStream log) throws Exception {
            return start(List.of("--listen", "127.0.0.1:0"), env, System::currentTimeMillis, 10_000, 64L * 1024 * 1024, log);
        }

        @Override
        public void close() {
            try {
                relay.close();
            } catch (Exception error) {
                throw new IllegalStateException(error);
            }
        }
    }

    static Map<String, String> relayEnvironment(String source, String gateway) {
        return Map.of("ERDIGO_RELAY_SOURCE_TOKENS", source, "ERDIGO_RELAY_GATEWAY_TOKEN", gateway);
    }

    static PrintStream discard() {
        return new PrintStream(OutputStream.nullOutputStream(), true, StandardCharsets.UTF_8);
    }

    // Programs

    record Result(int status, String output) {
    }

    static Path javaExecutable() {
        return Paths.get(System.getProperty("java.home"), "bin", WINDOWS ? "java.exe" : "java");
    }

    /** Starts a program of the example with only the environment given (and what the system needs), writing to a file. */
    static Process launch(String mainClass, List<String> args, Map<String, String> env, Path output) throws IOException {
        return launch(List.of(), mainClass, args, env, output);
    }

    /** The same, with options for the Java VM, such as a heap size. */
    static Process launch(List<String> vmOptions, String mainClass, List<String> args, Map<String, String> env, Path output)
            throws IOException {
        List<String> command = new ArrayList<>(List.of(javaExecutable().toString()));
        command.addAll(vmOptions);
        command.addAll(List.of("-cp", sdkClasses() + java.io.File.pathSeparator + compiled(), mainClass));
        command.addAll(args);
        ProcessBuilder builder = new ProcessBuilder(command).redirectErrorStream(true).redirectOutput(output.toFile());
        builder.environment().keySet().removeIf(name -> name.startsWith("ERDIGO_RELAY_"));
        builder.environment().putAll(env);
        Process process = builder.start();
        process.getOutputStream().close();
        return process;
    }

    /**
     * A file for the output of a program that the test stops. Windows can hold the file for a moment after the program
     * has ended, and a test folder with a held file in it cannot be removed, so the file is not put in one. It is
     * removed when the tests end.
     */
    static Path outputOfStoppedProgram() throws IOException {
        Path output = Files.createTempFile("erdigo-cloud-relay-test", ".txt");
        output.toFile().deleteOnExit();
        return output;
    }

    /** Runs a program of the example to its end, or fails the test after {@code limit}. */
    static Result run(Path work, Duration limit, String mainClass, List<String> args, Map<String, String> env) throws Exception {
        Path output = Files.createTempFile(work, "output", ".txt");
        Process process = launch(mainClass, args, env, output);
        if (!process.waitFor(limit.toMillis(), TimeUnit.MILLISECONDS)) {
            process.destroyForcibly();
            fail("the program did not end in time: " + Files.readString(output));
        }
        return new Result(process.exitValue(), Files.readString(output, StandardCharsets.UTF_8));
    }

    static Result gateway(Path work, Map<String, String> env, String... args) throws Exception {
        return run(work, Duration.ofSeconds(120), GATEWAY, List.of(args), env);
    }

    /** Waits for a line that a running program wrote to its output file. */
    static String waitForLine(Path output, String start, Duration limit) throws Exception {
        long deadline = System.nanoTime() + limit.toNanos();
        while (System.nanoTime() < deadline) {
            for (String line : Files.readAllLines(output, StandardCharsets.UTF_8)) {
                if (line.startsWith(start)) {
                    return line;
                }
            }
            Thread.sleep(50);
        }
        return null;
    }

    // HTTP

    static final HttpClient HTTP = HttpClient.newBuilder().version(HttpClient.Version.HTTP_1_1).build();

    record Reply(int status, HttpHeaders headers, byte[] body) {
        JsonValue.Object json() {
            assertEquals("application/json", headers.firstValue("Content-Type").orElse(""), "every answer is JSON");
            return (JsonValue.Object) StrictJson.parse(body, 16);
        }

        String error() {
            return ((JsonValue.Text) json().fields().get("error")).value();
        }

        List<JsonValue> ops() {
            return ((JsonValue.Array) json().fields().get("ops")).values();
        }

        String epoch() {
            return ((JsonValue.Text) json().fields().get("epoch")).value();
        }

        boolean flag(String name) {
            return ((JsonValue.Bool) json().fields().get(name)).value();
        }
    }

    static Reply send(HttpRequest request) throws IOException, InterruptedException {
        HttpResponse<byte[]> response = HTTP.send(request, HttpResponse.BodyHandlers.ofByteArray());
        return new Reply(response.statusCode(), response.headers(), response.body());
    }

    static HttpRequest.Builder request(String url, String token) {
        HttpRequest.Builder builder = HttpRequest.newBuilder(URI.create(url)).timeout(Duration.ofSeconds(60));
        return token == null ? builder : builder.header("Authorization", "Bearer " + token);
    }

    static Reply post(String relay, String token, String body) throws IOException, InterruptedException {
        return post(relay, token, body.getBytes(StandardCharsets.UTF_8));
    }

    static Reply post(String relay, String token, byte[] body) throws IOException, InterruptedException {
        return send(request(relay + "/v1/ops", token).header("Content-Type", "application/json")
                .POST(HttpRequest.BodyPublishers.ofByteArray(body)).build());
    }

    static Reply fetch(String relay, String token, String query) throws IOException, InterruptedException {
        return send(request(relay + "/v1/ops" + (query.isEmpty() ? "" : "?" + query), token).GET().build());
    }

    /** Sends raw bytes and reads the status line of the answer, for requests an HTTP client will not make. */
    static int rawStatus(int port, String head) throws IOException {
        try (Socket socket = new Socket("127.0.0.1", port)) {
            socket.setSoTimeout(10_000);
            socket.getOutputStream().write(head.getBytes(StandardCharsets.US_ASCII));
            socket.getOutputStream().flush();
            String line = new BufferedReader(new InputStreamReader(socket.getInputStream(), StandardCharsets.US_ASCII)).readLine();
            assertNotNull(line, "no answer");
            return Integer.parseInt(line.split(" ")[1]);
        }
    }

    // JSON

    /** Writes a value from the cases file as JSON text again, null and booleans included. */
    static String json(JsonValue value) {
        if (value instanceof JsonValue.Null) {
            return "null";
        }
        if (value instanceof JsonValue.Bool flag) {
            return String.valueOf(flag.value());
        }
        if (value instanceof JsonValue.Integer integer) {
            return String.valueOf(integer.value());
        }
        if (value instanceof JsonValue.Number number) {
            return JsonWriter.formatNumber(number.value());
        }
        if (value instanceof JsonValue.Text text) {
            return new String(JsonWriter.encode(JsonWriter.of(text.value())), StandardCharsets.UTF_8);
        }
        if (value instanceof JsonValue.Array array) {
            List<String> parts = new ArrayList<>();
            for (JsonValue element : array.values()) {
                parts.add(json(element));
            }
            return "[" + String.join(",", parts) + "]";
        }
        List<String> parts = new ArrayList<>();
        for (Map.Entry<String, JsonValue> field : ((JsonValue.Object) value).fields().entrySet()) {
            parts.add(json(new JsonValue.Text(field.getKey())) + ":" + json(field.getValue()));
        }
        return "{" + String.join(",", parts) + "}";
    }

    static JsonValue parse(String text) {
        return StrictJson.parse(text.getBytes(StandardCharsets.UTF_8), 64);
    }

    static String text(JsonValue.Object object, String key) {
        return ((JsonValue.Text) object.fields().get(key)).value();
    }

    // The .NET or Swift SDK's mock receiver

    /**
     * A mock receiver from another SDK, run as {@code MockInteropTest} runs it: {@code ERDIGO_MOCK} names its
     * executable. Returns null when it is not set, so that the caller can skip.
     */
    static final class Mock implements AutoCloseable {
        final Process process;
        final Path codeFile;
        final int port;
        private final OutputStream commands;
        private final List<String> output = new ArrayList<>();

        private Mock(Path folder) throws Exception {
            com.ziorentia.erdigo.sdk.PrivateFile.ensurePrivateDirectory(folder);
            codeFile = folder.resolve("code");
            process = new ProcessBuilder(System.getenv("ERDIGO_MOCK"), "serve", "--mock", "--code-file", codeFile.toString())
                    .redirectErrorStream(true).start();
            commands = process.getOutputStream();
            Thread reader = new Thread(() -> {
                try (BufferedReader lines = new BufferedReader(new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
                    String line;
                    while ((line = lines.readLine()) != null) {
                        synchronized (output) {
                            output.add(line);
                            output.notifyAll();
                        }
                    }
                } catch (IOException ended) {
                    // The mock was stopped.
                }
            });
            reader.setDaemon(true);
            reader.start();
            assertNotNull(waitFor("Type 'help'", Duration.ofSeconds(120), 0), "the mock did not start: " + lines());
            int found = -1;
            for (String line : lines()) {
                java.util.regex.Matcher matcher = java.util.regex.Pattern.compile("port (\\d+)").matcher(line);
                if (matcher.find()) {
                    found = Integer.parseInt(matcher.group(1));
                }
            }
            assertTrue(found > 0, "no port in " + lines());
            port = found;
        }

        static boolean available() {
            String executable = System.getenv("ERDIGO_MOCK");
            if (executable == null || executable.isEmpty()) {
                if ("1".equals(System.getenv("ERDIGO_REQUIRE_MOCK"))) {
                    fail("ERDIGO_REQUIRE_MOCK is set, but ERDIGO_MOCK does not name a mock receiver");
                }
                return false;
            }
            return true;
        }

        static Mock start(Path folder) throws Exception {
            return new Mock(folder);
        }

        List<String> lines() {
            synchronized (output) {
                return List.copyOf(output);
            }
        }

        String waitFor(String start, Duration limit, int skip) throws InterruptedException {
            long deadline = System.nanoTime() + limit.toNanos();
            synchronized (output) {
                while (true) {
                    for (int index = skip; index < output.size(); index++) {
                        if (output.get(index).startsWith(start)) {
                            return output.get(index);
                        }
                    }
                    long left = deadline - System.nanoTime();
                    if (left <= 0) {
                        return null;
                    }
                    output.wait(Math.max(1, left / 1_000_000));
                }
            }
        }

        void command(String line) throws IOException {
            commands.write((line + "\n").getBytes(StandardCharsets.UTF_8));
            commands.flush();
        }

        /** Opens a new pairing window, which writes a new code to the code file. */
        void newCode() throws Exception {
            int before = lines().size();
            command("code");
            assertNotNull(waitFor("Pairing text written", Duration.ofSeconds(10), before));
        }

        /** The items of the first device, as the mock prints them. */
        List<String> items() throws Exception {
            int before = lines().size();
            command("items 1");
            command("help");
            assertNotNull(waitFor("code | devices", Duration.ofSeconds(10), before));
            List<String> items = new ArrayList<>();
            for (String line : lines().subList(before, lines().size())) {
                if (line.matches("^(pin|marker|note|photo|track) .*")) {
                    items.add(line);
                }
            }
            return items;
        }

        int requests() {
            return (int) lines().stream().filter(line -> line.matches("^\\[mock\\] (#|rejected).*")).count();
        }

        @Override
        public void close() {
            process.destroy();
            try {
                if (!process.waitFor(10, TimeUnit.SECONDS)) {
                    process.destroyForcibly();
                }
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                process.destroyForcibly();
            }
        }
    }

}
