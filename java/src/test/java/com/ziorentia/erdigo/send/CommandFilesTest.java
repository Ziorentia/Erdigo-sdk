package com.ziorentia.erdigo.send;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import com.ziorentia.erdigo.core.Limits;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** How the command-line tool reads a note file, as the .NET and Swift tools read it. */
class CommandFilesTest {
    private static final byte[] BOM = {(byte) 0xEF, (byte) 0xBB, (byte) 0xBF};

    @TempDir
    Path directory;

    private String file(String name, byte[]... parts) throws IOException {
        ByteArrayOutputStream data = new ByteArrayOutputStream();
        for (byte[] part : parts) {
            data.writeBytes(part);
        }
        Path path = directory.resolve(name);
        Files.write(path, data.toByteArray());
        return path.toString();
    }

    private static byte[] utf8(String text) {
        return text.getBytes(StandardCharsets.UTF_8);
    }

    @Test
    void aNoteSavedByNotepadIsReadWithoutItsByteOrderMarkOrCarriageReturns() throws IOException {
        String path = file("note.txt", BOM, utf8("First line\r\nSecond \u00e9\r\n\r\nend"));
        assertEquals("First line\nSecond \u00e9\n\nend", CommandFiles.readNote(path));
    }

    @Test
    void onlyOneByteOrderMarkAndOnlyCrLfPairsAreChanged() {
        assertEquals("\uFEFFa", CommandFiles.noteFromFile("\uFEFF\uFEFFa"));
        assertEquals("a\rb", CommandFiles.noteFromFile("a\rb"));
        assertEquals("a\r\nb", CommandFiles.noteFromFile("a\r\r\nb"));
        assertEquals("a\n\rb", CommandFiles.noteFromFile("a\n\rb"));
        assertEquals("a\r", CommandFiles.noteFromFile("a\r"));
    }

    @Test
    void theNoteLimitCountsTheNoteThatIsSentNotTheFile() throws IOException {
        int lines = Limits.V1.noteBytes() / 2;
        String atLimit = file("at-limit.txt", BOM, utf8("a\r\n".repeat(lines)));
        assertEquals("a\n".repeat(lines), CommandFiles.readNote(atLimit));
        String over = file("over.txt", utf8("a\r\n".repeat(lines) + "a"));
        UsageException error = assertThrows(UsageException.class, () -> CommandFiles.readNote(over));
        assertEquals(CommandFiles.NOTE_TOO_LARGE, error.getMessage());
        String plain = file("plain.txt", utf8("a".repeat(Limits.V1.noteBytes() + 1)));
        error = assertThrows(UsageException.class, () -> CommandFiles.readNote(plain));
        assertEquals(CommandFiles.NOTE_TOO_LARGE, error.getMessage());
    }
}
