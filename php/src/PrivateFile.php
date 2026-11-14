<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * Files and folders that only the current user can read, the way `ssh` treats its keys.
 *
 * On macOS, Linux and other Unix systems a private folder has mode 0700 and a private file mode 0600, and both must
 * belong to the user PHP runs as. On Windows, where PHP cannot read file modes, a private folder has a protected access
 * list that grants the current user alone, set and checked with the system's `icacls` and `whoami` tools, and an
 * existing file or folder is accepted only if nobody but the current user, SYSTEM and the Administrators group is
 * granted anything on it.
 */
final class PrivateFile
{
    private static ?int $currentUserId = null;

    private function __construct()
    {
    }

    public static function isWindows(): bool
    {
        return PHP_OS_FAMILY === 'Windows';
    }

    /**
     * Reads a whole file that only the current user can read, refusing links, other kinds of file and files over
     * `$maxBytes`.
     */
    public static function readAllBytes(string $path, int $maxBytes): string
    {
        clearstatcache(true, $path);
        $name = basename($path);
        if (is_link($path)) {
            throw new PrivateFileException(PrivateFileException::INSECURE, "{$name} must be a regular file, not a link");
        }
        if (!file_exists($path)) {
            throw new PrivateFileException(PrivateFileException::MISSING, "{$name} does not exist");
        }
        if (!is_file($path)) {
            throw new PrivateFileException(PrivateFileException::INSECURE, "{$name} must be a regular file");
        }
        $handle = self::quietly(static fn () => fopen($path, 'rb'));
        if ($handle === false) {
            throw new PrivateFileException(PrivateFileException::UNREADABLE, "{$name} could not be opened");
        }
        try {
            $status = fstat($handle);
            if ($status === false) {
                throw new PrivateFileException(PrivateFileException::UNREADABLE, "{$name} could not be read");
            }
            if (($status['mode'] & 0170000) !== 0100000) {
                throw new PrivateFileException(PrivateFileException::INSECURE, "{$name} must be a regular file");
            }
            if (self::isWindows()) {
                if (!WindowsAcl::isPrivate($path)) {
                    throw new PrivateFileException(PrivateFileException::INSECURE, "{$name} must be readable by you alone");
                }
            } elseif (($status['mode'] & 0077) !== 0 || $status['uid'] !== self::currentUserId()) {
                throw new PrivateFileException(
                    PrivateFileException::INSECURE,
                    "{$name} must be a regular file owned by you with mode 0600",
                );
            }
            $size = $status['size'];
            if ($size > $maxBytes) {
                throw new PrivateFileException(PrivateFileException::TOO_LARGE, "{$name} is unexpectedly large");
            }
            $data = '';
            while (strlen($data) < $size) {
                $chunk = fread($handle, $size - strlen($data));
                if ($chunk === false || $chunk === '') {
                    break;
                }
                $data .= $chunk;
            }
            if (strlen($data) !== $size) {
                throw new PrivateFileException(PrivateFileException::UNREADABLE, "{$name} could not be read");
            }
            if (fread($handle, 1) !== '') {
                throw new PrivateFileException(PrivateFileException::TOO_LARGE, "{$name} changed while it was read");
            }
            return $data;
        } finally {
            fclose($handle);
        }
    }

    /**
     * Writes a whole file through a temporary file and a rename, so that a crash never leaves a partial file and a
     * reader never sees one, and makes it durable before returning. Used inside a private folder.
     *
     * Without `$overwrite` it fails if anything at all is at the path, so a new device key can never take the place of
     * one that exists. The caller holds the folder's lock, so nothing of this SDK writes there meanwhile.
     */
    public static function writeAtomically(string $path, #[\SensitiveParameter] string $data, bool $overwrite = true): void
    {
        $temporary = dirname($path) . DIRECTORY_SEPARATOR . '.' . basename($path) . '.' . bin2hex(random_bytes(8));
        $handle = self::quietly(static fn () => fopen($temporary, 'xb'));
        if ($handle === false) {
            throw new \RuntimeException('could not create ' . basename($path));
        }
        $succeeded = false;
        try {
            if (!self::isWindows() && !chmod($temporary, 0600)) {
                throw new \RuntimeException('could not protect ' . basename($path));
            }
            $written = 0;
            while ($written < strlen($data)) {
                $count = fwrite($handle, substr($data, $written));
                if ($count === false || $count === 0) {
                    throw new \RuntimeException('could not write ' . basename($path));
                }
                $written += $count;
            }
            if (!fflush($handle) || !fsync($handle)) {
                throw new \RuntimeException('could not write ' . basename($path));
            }
            fclose($handle);
            $handle = null;
            clearstatcache(true, $path);
            if (!$overwrite && (file_exists($path) || is_link($path))) {
                throw new PrivateFileException(PrivateFileException::UNREADABLE, basename($path) . ' already exists');
            }
            self::replace($temporary, $path);
            $succeeded = true;
        } finally {
            if ($handle !== null) {
                fclose($handle);
            }
            if (!$succeeded) {
                @unlink($temporary);
            }
        }
    }

    private static function replace(string $source, string $destination): void
    {
        // A virus scanner or an indexer can hold a file open for a moment on Windows.
        for ($attempt = 0; ; $attempt++) {
            if (self::quietly(static fn () => rename($source, $destination))) {
                return;
            }
            if (!self::isWindows() || $attempt >= 20) {
                throw new \RuntimeException('could not replace ' . basename($destination));
            }
            usleep(50000);
        }
    }

    /** Creates a private folder, or checks that an existing one is private. Its parent folders get default permissions. */
    public static function ensurePrivateDirectory(string $path): void
    {
        $parent = dirname($path);
        if (!is_dir($parent)) {
            self::quietly(static fn () => mkdir($parent, 0777, true));
        }
        clearstatcache(true, $path);
        if (!file_exists($path) && !is_link($path)) {
            $created = self::quietly(static fn () => mkdir($path, 0700));
            if (!$created && !is_dir($path)) {
                throw new \RuntimeException('could not create the state folder');
            }
            if ($created && self::isWindows()) {
                WindowsAcl::makePrivate($path);
            }
        }
        self::checkPrivateDirectory($path);
    }

    private static function checkPrivateDirectory(string $path): void
    {
        clearstatcache(true, $path);
        if (is_link($path) || !is_dir($path)) {
            throw new PrivateFileException(PrivateFileException::INSECURE, 'the state folder must be a real folder');
        }
        if (self::isWindows()) {
            if (!WindowsAcl::isPrivate($path)) {
                throw new PrivateFileException(
                    PrivateFileException::INSECURE,
                    'the state folder must be readable by you alone. Remove the other entries from its permissions, or choose another folder',
                );
            }
            return;
        }
        $status = stat($path);
        if ($status === false || ($status['mode'] & 0077) !== 0 || $status['uid'] !== self::currentUserId()) {
            throw new PrivateFileException(
                PrivateFileException::INSECURE,
                'the state folder must be a real folder owned by you with mode 0700',
            );
        }
    }

    /** The user id PHP runs as, on Unix systems. */
    public static function currentUserId(): int
    {
        if (self::$currentUserId !== null) {
            return self::$currentUserId;
        }
        if (function_exists('posix_geteuid')) {
            return self::$currentUserId = posix_geteuid();
        }
        // Without the posix extension, a new file shows who this process is.
        $probe = tempnam(sys_get_temp_dir(), 'erdigo');
        if ($probe === false) {
            throw new PrivateFileException(PrivateFileException::UNREADABLE, 'the current user could not be determined');
        }
        $owner = fileowner($probe);
        @unlink($probe);
        if ($owner === false) {
            throw new PrivateFileException(PrivateFileException::UNREADABLE, 'the current user could not be determined');
        }
        return self::$currentUserId = $owner;
    }

    /**
     * Runs `$body` without letting PHP print a warning, which could put a path on a web page.
     *
     * @template T
     * @param callable(): T $body
     * @return T
     */
    public static function quietly(callable $body): mixed
    {
        set_error_handler(static fn (): bool => true);
        try {
            return $body();
        } finally {
            restore_error_handler();
        }
    }
}
