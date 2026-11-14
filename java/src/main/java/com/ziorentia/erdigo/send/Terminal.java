package com.ziorentia.erdigo.send;

import java.io.ByteArrayOutputStream;
import java.io.Console;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.lang.reflect.Method;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Locale;
import java.util.concurrent.TimeUnit;

/**
 * Reads from the person at the keyboard, and only from a terminal.
 *
 * <p>On macOS and Linux the code is read from the terminal itself ({@code /dev/tty}) with echo turned off, whatever
 * standard input is, as {@code readpassphrase} does. The terminal's settings are changed and put back with
 * {@code stty}, the standard tool for it, because Java has no API for them. On Windows the code is read from the
 * console with {@link Console#readPassword}, so standard input must be the console.
 */
final class Terminal {
    private static final boolean WINDOWS = System.getProperty("os.name", "").toLowerCase(Locale.ROOT).startsWith("windows");
    private static final File TTY = new File("/dev/tty");

    private Terminal() {
    }

    static boolean isWindows() {
        return WINDOWS;
    }

    /**
     * True if both standard input and standard output are a terminal, so that a person can see a question and answer
     * it. Before Java 22 that is exactly when {@link System#console()} exists. From Java 22 a console can exist without
     * a terminal, and {@code Console.isTerminal()} says which.
     */
    static boolean personIsThere() {
        Console console = System.console();
        if (console == null) {
            return false;
        }
        try {
            Method isTerminal = Console.class.getMethod("isTerminal");
            return (Boolean) isTerminal.invoke(console);
        } catch (NoSuchMethodException before22) {
            return true;
        } catch (ReflectiveOperationException | RuntimeException error) {
            return false;
        }
    }

    /**
     * Shows {@code prompt} on the terminal and reads one line from it without showing what is typed. Returns null if
     * there is no terminal to read from, and an empty string at the end of input.
     */
    static String readSecretLine(String prompt, int maxLength) {
        if (WINDOWS) {
            Console console = System.console();
            if (console == null || !personIsThere()) {
                return null;
            }
            char[] typed = console.readPassword("%s", prompt);
            if (typed == null) {
                return "";
            }
            try {
                if (typed.length > maxLength) {
                    throw new UsageException("the text typed is too long");
                }
                return new String(typed);
            } finally {
                Arrays.fill(typed, '\0');
            }
        }
        RandomAccessFile terminal;
        try {
            terminal = new RandomAccessFile(TTY, "rw");
        } catch (IOException | SecurityException error) {
            return null;
        }
        String saved = stty("-g");
        if (saved == null || saved.isBlank()) {
            closeQuietly(terminal);
            return null;
        }
        String settings = saved.trim();
        Thread restore = new Thread(() -> stty(settings));
        byte[] buffer = new byte[maxLength + 1];
        int length = 0;
        try {
            Runtime.getRuntime().addShutdownHook(restore);
            if (stty("-echo") == null) {
                return null;
            }
            terminal.write(prompt.getBytes(StandardCharsets.UTF_8));
            while (length < buffer.length) {
                int next = terminal.read();
                if (next < 0 || next == '\n') {
                    break;
                }
                buffer[length++] = (byte) next;
            }
            if (length > maxLength) {
                throw new UsageException("the text typed is too long");
            }
            String text = CommandFiles.strictUtf8(Arrays.copyOf(buffer, length));
            return text.endsWith("\r") ? text.substring(0, text.length() - 1) : text;
        } catch (CharacterCodingException error) {
            throw new UsageException("the pairing code has invalid characters");
        } catch (IOException error) {
            return null;
        } finally {
            stty(settings);
            try {
                Runtime.getRuntime().removeShutdownHook(restore);
            } catch (IllegalStateException shuttingDown) {
                // The hook puts the settings back itself.
            }
            try {
                terminal.write('\n');
            } catch (IOException ignored) {
                // The terminal has gone.
            }
            closeQuietly(terminal);
            Arrays.fill(buffer, (byte) 0);
        }
    }

    /**
     * Discards anything typed so far. Only an answer given after a question has appeared counts as an answer to it.
     * Standard input is a terminal here, because a question is asked only then.
     */
    static void discardTypedAhead() {
        if (WINDOWS) {
            // The console keeps keys typed ahead until a program reads them. A child process shares the console and can
            // empty it, which Java itself cannot do.
            run(new ProcessBuilder("powershell.exe", "-NoProfile", "-Command", "$Host.UI.RawUI.FlushInputBuffer()"));
            drainAvailable();
            return;
        }
        String saved = stty("-g");
        if (saved == null || saved.isBlank()) {
            return;
        }
        String settings = saved.trim();
        Thread restore = new Thread(() -> stty(settings));
        try {
            Runtime.getRuntime().addShutdownHook(restore);
            // Out of line mode, a line still being typed can be read too, and so discarded with the rest.
            if (stty("-icanon", "min", "0", "time", "0") != null) {
                drainAvailable();
            }
        } finally {
            stty(settings);
            try {
                Runtime.getRuntime().removeShutdownHook(restore);
            } catch (IllegalStateException shuttingDown) {
                // The hook puts the settings back itself.
            }
        }
    }

    private static void drainAvailable() {
        InputStream input = System.in;
        byte[] buffer = new byte[256];
        try {
            int available;
            while ((available = input.available()) > 0) {
                if (input.read(buffer, 0, Math.min(buffer.length, available)) <= 0) {
                    break;
                }
            }
        } catch (IOException ignored) {
            // Nothing more to discard.
        } finally {
            Arrays.fill(buffer, (byte) 0);
        }
    }

    /**
     * Reads one line typed at the terminal, which shows it as it is typed. Returns null at the end of input (Ctrl-D, or
     * Ctrl-Z on Windows).
     */
    static String readLine(int maxLength) {
        Console console = System.console();
        if (console == null) {
            return null;
        }
        String line = console.readLine();
        if (line != null && line.length() > maxLength) {
            throw new UsageException("the text typed is too long");
        }
        return line;
    }

    /** Runs {@code stty} on the terminal and returns what it printed, or null if it failed. */
    private static String stty(String... arguments) {
        String[] command = new String[arguments.length + 1];
        command[0] = "stty";
        System.arraycopy(arguments, 0, command, 1, arguments.length);
        return run(new ProcessBuilder(command).redirectInput(TTY));
    }

    private static String run(ProcessBuilder builder) {
        builder.redirectError(ProcessBuilder.Redirect.DISCARD);
        if (builder.redirectInput() == ProcessBuilder.Redirect.PIPE) {
            builder.redirectInput(ProcessBuilder.Redirect.INHERIT);
        }
        try {
            Process process = builder.start();
            ByteArrayOutputStream output = new ByteArrayOutputStream();
            try (InputStream stream = process.getInputStream()) {
                stream.transferTo(output);
            }
            if (!process.waitFor(10, TimeUnit.SECONDS)) {
                process.destroyForcibly();
                return null;
            }
            return process.exitValue() == 0 ? output.toString(StandardCharsets.UTF_8) : null;
        } catch (IOException | SecurityException error) {
            return null;
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            return null;
        }
    }

    private static void closeQuietly(RandomAccessFile file) {
        try {
            file.close();
        } catch (IOException ignored) {
            // Closing anyway.
        }
    }
}
