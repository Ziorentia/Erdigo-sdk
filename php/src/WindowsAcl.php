<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * Windows access lists, read and set with the system's `icacls` and `whoami` tools, because PHP has no API for them.
 * Accounts are compared by their security identifiers, which do not depend on the system's language.
 *
 * @internal
 */
final class WindowsAcl
{
    private static ?string $currentUserSid = null;

    /** SYSTEM and the Administrators group control the computer anyway, as on the .NET SDK. */
    private const TRUSTED = ['SY', 'S-1-5-18', 'BA', 'S-1-5-32-544'];

    /** Every kind of access control entry that grants something. */
    private const ALLOW_TYPES = ['A', 'OA', 'XA', 'ZA'];

    private function __construct()
    {
    }

    public static function currentUserSid(): string
    {
        if (self::$currentUserSid !== null) {
            return self::$currentUserSid;
        }
        $output = self::run(['whoami', '/user', '/fo', 'csv', '/nh']);
        if (preg_match('/"(S-1-[0-9-]+)"\s*$/', trim($output), $match) !== 1) {
            throw new PrivateFileException(PrivateFileException::UNREADABLE, 'the current Windows user could not be determined');
        }
        return self::$currentUserSid = $match[1];
    }

    /**
     * True if nobody but the current user, SYSTEM and the Administrators group is granted anything on the file or
     * folder, including what it passes on to new files.
     */
    public static function isPrivate(string $path): bool
    {
        return self::grantsOnly(self::sddl($path), self::currentUserSid());
    }

    /** Whether an access list in SDDL grants nothing to anyone but `$userSid`, SYSTEM and Administrators. */
    public static function grantsOnly(string $sddl, string $userSid): bool
    {
        if (preg_match('/D:[A-Z]*((?:\([^()]*\))*)/', $sddl, $match) !== 1) {
            return false;
        }
        preg_match_all('/\(([^()]*)\)/', $match[1], $entries);
        foreach ($entries[1] as $entry) {
            $fields = explode(';', $entry);
            if (count($fields) < 6) {
                return false;
            }
            if (!in_array($fields[0], self::ALLOW_TYPES, true)) {
                continue;
            }
            $sid = $fields[5];
            if ($sid !== $userSid && !in_array($sid, self::TRUSTED, true)) {
                return false;
            }
        }
        return true;
    }

    /** Gives a folder a protected access list that grants the current user alone, passed on to everything inside. */
    public static function makePrivate(string $directory): void
    {
        $sid = self::currentUserSid();
        self::run(['icacls', self::native($directory), '/inheritance:r', '/grant:r', "*{$sid}:(OI)(CI)F", '/q']);
    }

    private static function sddl(string $path): string
    {
        $saved = tempnam(sys_get_temp_dir(), 'erdigo-acl');
        if ($saved === false) {
            throw new PrivateFileException(PrivateFileException::UNREADABLE, 'the permissions could not be checked');
        }
        try {
            self::run(['icacls', self::native($path), '/save', $saved, '/q']);
            $raw = (string) file_get_contents($saved);
        } finally {
            @unlink($saved);
        }
        // UTF-16 text: the file's name, then its access list. The access list is ASCII.
        if (str_starts_with($raw, "\xFF\xFE")) {
            $raw = substr($raw, 2);
        }
        $lines = preg_split('/\r?\n/', str_replace("\0", '', $raw)) ?: [];
        $lines = array_values(array_filter($lines, static fn (string $line): bool => trim($line) !== ''));
        return $lines === [] ? '' : trim($lines[count($lines) - 1]);
    }

    /** A path as Windows' own tools write it, with backslashes. */
    private static function native(string $path): string
    {
        return str_replace('/', '\\', $path);
    }

    /** @param list<string> $command */
    private static function run(array $command): string
    {
        if (!function_exists('proc_open')) {
            throw new PrivateFileException(
                PrivateFileException::UNREADABLE,
                'proc_open is disabled, so the permissions of the state folder cannot be checked on Windows',
            );
        }
        $pipes = [];
        $process = @proc_open(
            $command,
            [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']],
            $pipes,
            null,
            null,
            ['bypass_shell' => true],
        );
        if (!is_resource($process) || !isset($pipes[0], $pipes[1], $pipes[2])) {
            throw new PrivateFileException(PrivateFileException::UNREADABLE, "{$command[0]} could not be started");
        }
        fclose($pipes[0]);
        $output = (string) stream_get_contents($pipes[1]);
        stream_get_contents($pipes[2]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        if (proc_close($process) !== 0) {
            throw new PrivateFileException(PrivateFileException::UNREADABLE, "{$command[0]} could not check the permissions");
        }
        return $output;
    }
}
