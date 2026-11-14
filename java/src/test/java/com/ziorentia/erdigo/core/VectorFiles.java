package com.ziorentia.erdigo.core;

import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;

/** Finds and reads the shared files in {@code spec/}, from anywhere inside the repository. */
public final class VectorFiles {
    private VectorFiles() {
    }

    public static Path repositoryRoot() {
        Path here = Paths.get("").toAbsolutePath();
        for (Path candidate = here; candidate != null; candidate = candidate.getParent()) {
            if (Files.isDirectory(candidate.resolve("spec").resolve("vectors"))) {
                return candidate;
            }
        }
        throw new IllegalStateException("Run the tests from a full clone: spec/vectors was not found above " + here);
    }

    public static JsonValue.Object load(String relative) {
        try {
            byte[] data = Files.readAllBytes(repositoryRoot().resolve(relative));
            return (JsonValue.Object) StrictJson.parse(data, 64);
        } catch (IOException error) {
            throw new UncheckedIOException(error);
        }
    }

    public static String text(JsonValue.Object object, String key) {
        return ((JsonValue.Text) object.fields().get(key)).value();
    }

    public static byte[] bytes(JsonValue.Object object, String key) {
        return bytes(text(object, key));
    }

    public static byte[] optionalBytes(JsonValue.Object object, String key) {
        return object.fields().containsKey(key) ? bytes(object, key) : null;
    }

    public static byte[] bytes(String hex) {
        byte[] decoded = Hex.decode(hex);
        if (decoded == null) {
            throw new IllegalArgumentException("not hexadecimal");
        }
        return decoded;
    }

    public static java.util.List<JsonValue> array(JsonValue.Object object, String key) {
        return ((JsonValue.Array) object.fields().get(key)).values();
    }
}
