package com.ziorentia.erdigo.send;

import com.ziorentia.erdigo.core.Limits;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.InvalidPathException;
import java.nio.file.NoSuchFileException;
import java.nio.file.Path;
import java.nio.file.Paths;

/**
 * Reads the note and photo files named on the command line. Their size is not trusted: nothing past the limit is read,
 * so a huge file, or one that keeps growing while it is read, costs no more memory than the largest file that is
 * allowed, and is refused before any connection is made.
 */
final class CommandFiles {
    static final String NOTE_TOO_LARGE = "a note is at most " + Limits.V1.noteBytes() + " bytes of UTF-8";
    static final String PHOTO_TOO_LARGE = "a photo is at most " + Limits.V1.photoBytes() + " bytes";

    private CommandFiles() {
    }

    /**
     * A note file holds the note as UTF-8. Two things editors add are taken out, so that a note written in Notepad can
     * be sent as it is: a byte order mark at the start, and the carriage return of each Windows line ending (CR LF
     * becomes LF). Nothing else is changed: a trailing newline stays, and a carriage return on its own is left for the
     * note rules to refuse. The limit applies to the note that is sent, not to the file.
     */
    static String readNote(String path) {
        // Every line feed may come with a carriage return, and a byte order mark is three bytes.
        byte[] data = readAtMost(path, Limits.V1.noteBytes() * 2 + 3, "note", NOTE_TOO_LARGE);
        String text;
        try {
            text = strictUtf8(data);
        } catch (CharacterCodingException error) {
            throw new UsageException("the note file is not UTF-8 text");
        }
        text = noteFromFile(text);
        if (text.getBytes(StandardCharsets.UTF_8).length > Limits.V1.noteBytes()) {
            throw new UsageException(NOTE_TOO_LARGE);
        }
        return text;
    }

    /** The note in a file's text: without a leading byte order mark, and with CR LF turned into LF. */
    static String noteFromFile(String text) {
        String rest = text.startsWith("\uFEFF") ? text.substring(1) : text;
        return rest.replace("\r\n", "\n");
    }

    /** The format is checked by the SDK when the photo is sent, before it connects. */
    static byte[] readPhoto(String path) {
        return readAtMost(path, Limits.V1.photoBytes(), "photo", PHOTO_TOO_LARGE);
    }

    static String strictUtf8(byte[] data) throws CharacterCodingException {
        return StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(data))
                .toString();
    }

    static byte[] readAtMost(String path, int maxBytes, String what, String tooLarge) {
        Path file;
        try {
            file = Paths.get(path);
        } catch (InvalidPathException error) {
            throw new UsageException("could not open the " + what + " file");
        }
        InputStream stream;
        try {
            if (Files.isDirectory(file)) {
                throw new UsageException("could not open the " + what + " file");
            }
            stream = Files.newInputStream(file);
        } catch (NoSuchFileException error) {
            throw new UsageException("the " + what + " file does not exist");
        } catch (IOException | SecurityException error) {
            throw new UsageException("could not open the " + what + " file");
        }
        try (InputStream input = stream) {
            long known = -1;
            try {
                known = Files.isRegularFile(file) ? Files.size(file) : -1;
            } catch (IOException ignored) {
                // Read below without knowing the size.
            }
            if (known > maxBytes) {
                throw new UsageException(tooLarge);
            }
            return readAtMost(input, maxBytes, tooLarge);
        } catch (IOException error) {
            throw new UsageException("could not read the " + what + " file");
        }
    }

    /**
     * Reads what is left of {@code input}, refusing it with {@code tooLarge} if that is more than {@code maxBytes}. The
     * first byte over the limit settles it, so at most one byte more is ever read and the buffer never holds more than
     * the limit.
     */
    static byte[] readAtMost(InputStream input, int maxBytes, String tooLarge) throws IOException {
        ByteArrayOutputStream output = new ByteArrayOutputStream(Math.min(maxBytes, 64 * 1024));
        byte[] buffer = new byte[8192];
        while (true) {
            int room = maxBytes - output.size();
            int read = input.read(buffer, 0, Math.min(buffer.length, room + 1));
            if (read < 0) {
                return output.toByteArray();
            }
            if (read > room) {
                throw new UsageException(tooLarge);
            }
            output.write(buffer, 0, read);
        }
    }
}
