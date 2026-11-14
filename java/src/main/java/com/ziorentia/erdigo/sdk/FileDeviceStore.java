package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.Hex;
import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.JsonParseException;
import com.ziorentia.erdigo.core.JsonValue;
import com.ziorentia.erdigo.core.JsonWriter;
import com.ziorentia.erdigo.core.Noise;
import com.ziorentia.erdigo.core.StrictJson;
import com.ziorentia.erdigo.sdk.ErdigoException.Kind;
import com.ziorentia.erdigo.sdk.ErdigoException.StorageProblem;
import com.ziorentia.erdigo.sdk.PrivateFile.PrivateFileException;
import java.io.IOException;
import java.nio.channels.FileChannel;
import java.nio.channels.FileLock;
import java.nio.charset.StandardCharsets;
import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.nio.file.OpenOption;
import java.nio.file.StandardOpenOption;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Instant;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Objects;
import java.util.Set;
import java.util.TreeMap;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.locks.ReentrantLock;
import java.util.function.Supplier;

/**
 * Stores state in a private folder: the device key in {@code identity.key}, and pairings and connection counters in
 * {@code pairings.json}. Refuses to read files that other users could read or replace, the same way {@code ssh} treats
 * its key files.
 *
 * <p>The key is kept in the clear in a file only its owner can read: mode 0600 on macOS and Linux, or an access list
 * that grants the current user alone on Windows. A key file that exists but cannot be read is reported, and never
 * silently replaced by a new key.
 */
public final class FileDeviceStore implements DeviceStore {
    private static final String PAIRINGS_FILE = "pairings.json";
    private static final String KEY_FILE = "identity.key";
    private static final String LOCK_FILE = ".lock";
    private static final byte[] KEY_MAGIC = "EDK0".getBytes(StandardCharsets.US_ASCII);
    private static final Map<Path, ReentrantLock> THREAD_LOCKS = new ConcurrentHashMap<>();

    private final Path directory;

    public FileDeviceStore(Path directory) {
        Objects.requireNonNull(directory, "directory");
        this.directory = directory.toAbsolutePath().normalize();
        try {
            PrivateFile.ensurePrivateDirectory(this.directory);
        } catch (PrivateFileException error) {
            throw ErdigoException.builder(Kind.STORAGE)
                    .detail(error.getMessage())
                    .storageProblem(StorageProblem.INSECURE)
                    .cause(error)
                    .build();
        } catch (IOException | RuntimeException error) {
            throw ErdigoException.builder(Kind.STORAGE).detail("could not create the state folder").cause(error).build();
        }
    }

    public Path directory() {
        return directory;
    }

    /**
     * A folder of this SDK's own for a profile, so that it never shares a device key with the Swift or .NET SDK on the
     * same computer: {@code %LOCALAPPDATA%\ErdigoSDK.Java\<profile>} on Windows,
     * {@code ~/Library/Application Support/ErdigoSDK.Java/<profile>} on macOS, and
     * {@code $XDG_DATA_HOME/ErdigoSDK.Java/<profile>} or {@code ~/.local/share/ErdigoSDK.Java/<profile>} elsewhere.
     */
    public static Path defaultDirectory(String profile) {
        Objects.requireNonNull(profile, "profile");
        String os = System.getProperty("os.name", "").toLowerCase(Locale.ROOT);
        String home = System.getProperty("user.home");
        Path base;
        if (os.startsWith("windows")) {
            String local = System.getenv("LOCALAPPDATA");
            if (local == null || local.isEmpty()) {
                throw new ErdigoException(Kind.STORAGE, "the folder for application data could not be found");
            }
            base = Paths.get(local);
        } else if (os.startsWith("mac")) {
            base = Paths.get(home, "Library", "Application Support");
        } else {
            String data = System.getenv("XDG_DATA_HOME");
            base = data != null && !data.isEmpty() && Paths.get(data).isAbsolute() ? Paths.get(data) : Paths.get(home, ".local", "share");
        }
        return base.resolve("ErdigoSDK.Java").resolve(profile);
    }

    public static Path defaultDirectory() {
        return defaultDirectory("default");
    }

    private Path pathOf(String name) {
        return directory.resolve(name);
    }

    /** Serialises access between threads and between processes. */
    private <T> T withLock(Supplier<T> body) {
        ReentrantLock threadLock = THREAD_LOCKS.computeIfAbsent(directory, ignored -> new ReentrantLock());
        threadLock.lock();
        try (FileChannel channel = openLockFile()) {
            FileLock held = channel.lock();
            try {
                return body.get();
            } finally {
                held.release();
            }
        } catch (IOException error) {
            throw ErdigoException.builder(Kind.STORAGE).detail("could not lock the state folder").cause(error).build();
        } finally {
            threadLock.unlock();
        }
    }

    private FileChannel openLockFile() throws IOException {
        Set<OpenOption> options = Set.of(StandardOpenOption.CREATE, StandardOpenOption.WRITE, LinkOption.NOFOLLOW_LINKS);
        if (directory.getFileSystem().supportedFileAttributeViews().contains("posix")) {
            return FileChannel.open(pathOf(LOCK_FILE), options, PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rw-------")));
        }
        // Elsewhere it takes on the private folder's access list.
        return FileChannel.open(pathOf(LOCK_FILE), options);
    }

    private void withLock(Runnable body) {
        withLock(() -> {
            body.run();
            return null;
        });
    }

    private byte[] readPrivate(String name, StorageProblem unreadable, int maxBytes) {
        try {
            return PrivateFile.readAllBytes(pathOf(name), maxBytes);
        } catch (PrivateFileException error) {
            switch (error.problem()) {
                case MISSING:
                    return null;
                case INSECURE:
                    throw ErdigoException.builder(Kind.STORAGE)
                            .detail(error.getMessage())
                            .storageProblem(StorageProblem.INSECURE)
                            .cause(error)
                            .build();
                default:
                    throw ErdigoException.builder(Kind.STORAGE)
                            .detail(name + " exists but could not be read. Nothing was changed.")
                            .storageProblem(unreadable)
                            .cause(error)
                            .build();
            }
        }
    }

    private void writePrivate(String name, byte[] data, boolean overwrite) {
        try {
            PrivateFile.writeAllBytesAtomically(pathOf(name), data, overwrite);
        } catch (FileAlreadyExistsException error) {
            throw ErdigoException.builder(Kind.STORAGE)
                    .detail(name + " exists but could not be read. Nothing was changed, and no new key was created.")
                    .storageProblem(StorageProblem.IDENTITY_UNREADABLE)
                    .cause(error)
                    .build();
        } catch (IOException | RuntimeException error) {
            throw ErdigoException.builder(Kind.STORAGE).detail("could not write " + name).cause(error).build();
        }
    }

    @Override
    public DeviceIdentity loadOrCreateIdentity() {
        return withLock(() -> {
            byte[] stored = readPrivate(KEY_FILE, StorageProblem.IDENTITY_UNREADABLE, 64 * 1024);
            if (stored != null) {
                try {
                    return openKey(stored);
                } finally {
                    Arrays.fill(stored, (byte) 0);
                }
            }
            // Only a file that is not there at all leads to a new key.
            DeviceIdentity created = DeviceIdentity.generate();
            byte[] raw = created.exportPrivateKeyForStorage();
            byte[] sealed = new byte[KEY_MAGIC.length + raw.length];
            try {
                System.arraycopy(KEY_MAGIC, 0, sealed, 0, KEY_MAGIC.length);
                System.arraycopy(raw, 0, sealed, KEY_MAGIC.length, raw.length);
                // Never over an existing file, even one this process could not see.
                writePrivate(KEY_FILE, sealed, false);
            } catch (RuntimeException error) {
                created.close();
                throw error;
            } finally {
                Arrays.fill(raw, (byte) 0);
                Arrays.fill(sealed, (byte) 0);
            }
            return created;
        });
    }

    private static DeviceIdentity openKey(byte[] stored) {
        if (stored.length != KEY_MAGIC.length + Noise.DH_LENGTH
                || !Arrays.equals(Arrays.copyOf(stored, KEY_MAGIC.length), KEY_MAGIC)) {
            throw ErdigoException.builder(Kind.STORAGE)
                    .detail(KEY_FILE + " exists but does not hold a device key. It was left as it is. Move it away to create a new key, which means pairing again.")
                    .storageProblem(StorageProblem.IDENTITY_DAMAGED)
                    .build();
        }
        byte[] raw = Arrays.copyOfRange(stored, KEY_MAGIC.length, stored.length);
        try {
            return DeviceIdentity.fromPrivateKey(raw);
        } finally {
            Arrays.fill(raw, (byte) 0);
        }
    }

    // Pairings and counters

    private static final class State {
        final List<ReceiverPairing> pairings = new ArrayList<>();
        final Map<String, Long> counters = new TreeMap<>();
    }

    private State loadState() {
        byte[] data = readPrivate(PAIRINGS_FILE, StorageProblem.STATE_UNREADABLE, 4 * 1024 * 1024);
        if (data == null) {
            return new State();
        }
        try {
            return decodeState(data);
        } catch (JsonParseException | IllegalArgumentException | ClassCastException | NullPointerException | DateTimeParseException error) {
            throw ErdigoException.builder(Kind.STORAGE)
                    .detail(PAIRINGS_FILE + " could not be read")
                    .storageProblem(StorageProblem.STATE_DAMAGED)
                    .cause(error)
                    .build();
        }
    }

    private static Map<String, JsonValue> object(JsonValue value) {
        if (value instanceof JsonValue.Object object) {
            return object.fields();
        }
        throw new IllegalArgumentException("not an object");
    }

    private static String text(JsonValue value) {
        if (value instanceof JsonValue.Text text) {
            return text.value();
        }
        throw new IllegalArgumentException("not a string");
    }

    private static long integer(JsonValue value) {
        if (value instanceof JsonValue.Integer integer && integer.value() >= 0) {
            return integer.value();
        }
        throw new IllegalArgumentException("not a whole number");
    }

    private static List<JsonValue> array(JsonValue value) {
        if (value instanceof JsonValue.Array array) {
            return array.values();
        }
        throw new IllegalArgumentException("not an array");
    }

    private static State decodeState(byte[] data) {
        Map<String, JsonValue> root = object(StrictJson.parse(data));
        if (integer(root.get("version")) != 1) {
            throw new IllegalArgumentException("unknown version");
        }
        State state = new State();
        for (JsonValue element : array(root.get("pairings"))) {
            Map<String, JsonValue> fields = object(element);
            byte[] key = Hex.decode(text(fields.get("receiverKey")));
            if (key == null || !Noise.isCanonicalPublicKey(key)) {
                throw new IllegalArgumentException("a receiver key is not valid");
            }
            List<ItemKind> kinds = new ArrayList<>();
            for (JsonValue kind : array(fields.get("kinds"))) {
                ItemKind parsed = ItemKind.fromWire(text(kind));
                if (parsed == null) {
                    throw new IllegalArgumentException("unknown kind");
                }
                kinds.add(parsed);
            }
            String host = fields.containsKey("lastHost") && !(fields.get("lastHost") instanceof JsonValue.Null)
                    ? text(fields.get("lastHost"))
                    : null;
            Integer port = null;
            if (fields.containsKey("lastPort") && !(fields.get("lastPort") instanceof JsonValue.Null)) {
                long value = integer(fields.get("lastPort"));
                if (value > 65535) {
                    throw new IllegalArgumentException("a port is out of range");
                }
                port = (int) value;
            }
            state.pairings.add(new ReceiverPairing(
                    text(fields.get("label")),
                    key,
                    kinds,
                    host,
                    port,
                    Instant.parse(text(fields.get("pairedAt"))),
                    integer(fields.get("counterFloor"))));
        }
        for (Map.Entry<String, JsonValue> counter : object(root.get("counters")).entrySet()) {
            state.counters.put(counter.getKey(), integer(counter.getValue()));
        }
        return state;
    }

    private void saveState(State state) {
        List<JsonWriter.Value> pairings = new ArrayList<>();
        for (ReceiverPairing pairing : state.pairings) {
            List<JsonWriter.Value> kinds = new ArrayList<>();
            for (ItemKind kind : pairing.kinds()) {
                kinds.add(JsonWriter.of(kind.wire()));
            }
            JsonWriter.ObjectBuilder fields = JsonWriter.object()
                    .put("label", pairing.label())
                    .put("receiverKey", Hex.encode(pairing.receiverKey()))
                    .put("kinds", JsonWriter.array(kinds));
            if (pairing.lastHost() != null) {
                fields.put("lastHost", pairing.lastHost());
            }
            if (pairing.lastPort() != null) {
                fields.put("lastPort", (long) pairing.lastPort());
            }
            fields.put("pairedAt", pairing.pairedAt().toString());
            fields.put("counterFloor", pairing.counterFloor());
            pairings.add(fields.build());
        }
        JsonWriter.ObjectBuilder counters = JsonWriter.object();
        for (Map.Entry<String, Long> counter : state.counters.entrySet()) {
            counters.put(counter.getKey(), (long) counter.getValue());
        }
        byte[] data = JsonWriter.encode(JsonWriter.object()
                .put("version", 1L)
                .put("pairings", JsonWriter.array(pairings))
                .put("counters", counters.build())
                .build());
        writePrivate(PAIRINGS_FILE, data, true);
    }

    @Override
    public List<ReceiverPairing> pairings() {
        return withLock(() -> List.copyOf(loadState().pairings));
    }

    @Override
    public void save(ReceiverPairing pairing) {
        Objects.requireNonNull(pairing, "pairing");
        withLock(() -> {
            State state = loadState();
            state.pairings.removeIf(existing -> existing.hasKey(pairing.receiverKey()));
            state.pairings.add(pairing);
            state.counters.merge(pairing.fingerprint(), pairing.counterFloor(), Math::max);
            saveState(state);
        });
    }

    @Override
    public void updateAddress(String receiverFingerprint, String host, int port) {
        withLock(() -> {
            State state = loadState();
            for (int index = 0; index < state.pairings.size(); index++) {
                if (state.pairings.get(index).fingerprint().equals(receiverFingerprint)) {
                    state.pairings.set(index, state.pairings.get(index).withAddress(host, port));
                    saveState(state);
                    return;
                }
            }
        });
    }

    @Override
    public void removePairing(String fingerprint) {
        withLock(() -> {
            State state = loadState();
            state.pairings.removeIf(pairing -> pairing.fingerprint().equals(fingerprint));
            state.counters.remove(fingerprint);
            saveState(state);
        });
    }

    @Override
    public long nextConnectCounter(String receiverFingerprint, long nowMs) {
        return withLock(() -> {
            State state = loadState();
            long next = Math.max(nowMs, state.counters.getOrDefault(receiverFingerprint, 0L) + 1);
            state.counters.put(receiverFingerprint, next);
            saveState(state);
            return next;
        });
    }
}
