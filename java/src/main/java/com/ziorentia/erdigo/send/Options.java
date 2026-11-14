package com.ziorentia.erdigo.send;

import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeSet;
import java.util.regex.Pattern;

/** A deliberately small option parser: {@code --name value} pairs and bare flags, nothing else. */
final class Options {
    /**
     * Every option the tool has. Errors name an option only if it is on this list, because anything else that was typed
     * might be a pairing code.
     */
    static final Set<String> KNOWN = Set.of(
            "alt", "ask", "auto-approve", "bonjour", "code-file", "color", "file", "fingerprint", "host", "id", "keychain", "kinds",
            "label", "lan", "lat", "lon", "mock", "name", "port", "profile", "redact", "retry-for", "state-dir", "text", "text-file",
            "title", "to", "ts", "yes");

    /** A decimal number as people write one: no hexadecimal, no NaN or Infinity, no type suffix. */
    private static final Pattern DECIMAL = Pattern.compile("[ \\t]*[+-]?([0-9]+\\.?[0-9]*|\\.[0-9]+)([eE][+-]?[0-9]+)?[ \\t]*");

    private final Map<String, String> values = new HashMap<>();
    private final Set<String> flags = new HashSet<>();
    private final Set<String> used = new HashSet<>();

    static String describe(String name) {
        return KNOWN.contains(name) ? "--" + name : "an unknown option";
    }

    Options(List<String> arguments, Set<String> allowedFlags) {
        int index = 0;
        while (index < arguments.size()) {
            String argument = arguments.get(index);
            // Errors here never repeat what was typed, because it might be a pairing code.
            if (!argument.startsWith("--")) {
                throw new UsageException("unexpected argument. Options start with --, and a pairing code is never given as an argument.");
            }
            String name = argument.substring(2);
            if (name.equals("code") || name.startsWith("code=")) {
                throw new UsageException("the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. Type it when asked, or use --code-file.");
            }
            int equals = name.indexOf('=');
            if (equals >= 0) {
                throw new UsageException("write " + describe(name.substring(0, equals)) + " and its value as two separate arguments");
            }
            if (allowedFlags.contains(name)) {
                flags.add(name);
                index += 1;
            } else {
                if (!KNOWN.contains(name)) {
                    throw new UsageException("an unknown option was given. See 'erdigo-send help'.");
                }
                if (index + 1 >= arguments.size()) {
                    throw new UsageException("--" + name + " needs a value");
                }
                if (values.containsKey(name)) {
                    throw new UsageException("--" + name + " was given twice");
                }
                values.put(name, arguments.get(index + 1));
                index += 2;
            }
        }
    }

    boolean flag(String name) {
        used.add(name);
        return flags.contains(name);
    }

    String optional(String name) {
        used.add(name);
        return values.get(name);
    }

    String required(String name) {
        String value = optional(name);
        if (value == null) {
            throw new UsageException("--" + name + " is required");
        }
        return value;
    }

    /** Whether the text is a finite decimal number, written as the other SDKs' tools accept one. */
    static boolean isDecimal(String text) {
        if (!DECIMAL.matcher(text).matches()) {
            return false;
        }
        return Double.isFinite(Double.parseDouble(text.trim()));
    }

    double requiredDouble(String name) {
        String text = required(name);
        if (!isDecimal(text)) {
            throw new UsageException("--" + name + " must be a number");
        }
        return Double.parseDouble(text.trim());
    }

    Integer optionalPort() {
        String text = optional("port");
        if (text == null) {
            return null;
        }
        if (!text.matches("[0-9]{1,5}") || Integer.parseInt(text) > 65535) {
            throw new UsageException("--port must be between 0 and 65535");
        }
        return Integer.parseInt(text);
    }

    /** Call after reading every option the command understands. */
    void finish() {
        Set<String> given = new TreeSet<>(values.keySet());
        given.addAll(flags);
        for (String name : given) {
            if (!used.contains(name)) {
                throw new UsageException("--" + name + " does not apply to this command");
            }
        }
    }
}
