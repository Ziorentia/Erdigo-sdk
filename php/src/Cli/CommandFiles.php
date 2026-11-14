<?php

declare(strict_types=1);

namespace Erdigo\Cli;

use Erdigo\Core\Limits;
use Erdigo\Core\Utf8;
use Erdigo\PrivateFile;
use Erdigo\PrivateFileException;

/**
 * Reads the files named on the command line. Their size is not trusted: nothing past the limit is read, so a huge file,
 * or one that keeps growing while it is read, costs no more memory than the largest file that is allowed, and is
 * refused before any connection is made.
 */
final class CommandFiles
{
    private function __construct()
    {
    }

    public static function noteTooLarge(): string
    {
        return 'a note is at most ' . Limits::v1()->noteBytes . ' bytes of UTF-8';
    }

    public static function photoTooLarge(): string
    {
        return 'a photo is at most ' . Limits::v1()->photoBytes . ' bytes';
    }

    /** A note file holds the note as UTF-8, with nothing added: no byte order mark, and no trailing newline unless it belongs to the note. */
    public static function readNote(string $path): string
    {
        $data = self::readAtMost($path, Limits::v1()->noteBytes, 'note', self::noteTooLarge());
        if (!Utf8::isValid($data)) {
            throw new UsageException('the note file is not UTF-8 text');
        }
        return $data;
    }

    /** The format is checked by the SDK when the photo is sent, before it connects. */
    public static function readPhoto(string $path): string
    {
        return self::readAtMost($path, Limits::v1()->photoBytes, 'photo', self::photoTooLarge());
    }

    /** Reads a pairing code file only if nobody else can read it, as ssh does for its keys. */
    public static function readCodeFile(string $path): string
    {
        try {
            $data = PrivateFile::readAllBytes($path, 1024);
        } catch (PrivateFileException $error) {
            throw new UsageException(match ($error->problem) {
                PrivateFileException::INSECURE => PrivateFile::isWindows()
                    ? 'the code file must be a regular file that only you can read'
                    : 'the code file must be a regular file you own, with mode 0600, so that nobody else can read it',
                PrivateFileException::TOO_LARGE => 'the code file is too large',
                default => 'could not open the code file',
            });
        }
        // A byte order mark is how some editors mark UTF-8. It is not part of the code.
        if (str_starts_with($data, "\xEF\xBB\xBF")) {
            $data = substr($data, 3);
        }
        if (!Utf8::isValid($data)) {
            throw new UsageException('the code file is not UTF-8 text');
        }
        return $data;
    }

    /**
     * Reads a whole file, refusing it with `$tooLarge` if it holds more than `$maxBytes`. A regular file is refused by
     * its size alone. Anything else is read until the first byte over the limit settles it.
     */
    public static function readAtMost(string $path, int $maxBytes, string $what, string $tooLarge): string
    {
        clearstatcache(true, $path);
        if (!file_exists($path)) {
            throw new UsageException("the {$what} file does not exist");
        }
        $handle = PrivateFile::quietly(static fn () => is_dir($path) ? false : fopen($path, 'rb'));
        if ($handle === false) {
            throw new UsageException("could not open the {$what} file");
        }
        try {
            $status = fstat($handle);
            if ($status !== false && ($status['mode'] & 0170000) === 0100000 && $status['size'] > $maxBytes) {
                throw new UsageException($tooLarge);
            }
            $data = '';
            while (!feof($handle)) {
                $chunk = fread($handle, min(65536, $maxBytes + 1 - strlen($data)));
                if ($chunk === false) {
                    throw new UsageException("could not read the {$what} file");
                }
                $data .= $chunk;
                if (strlen($data) > $maxBytes) {
                    throw new UsageException($tooLarge);
                }
            }
            return $data;
        } finally {
            fclose($handle);
        }
    }
}
