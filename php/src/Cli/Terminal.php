<?php

declare(strict_types=1);

namespace Erdigo\Cli;

/**
 * Reads from the person at the keyboard, and only from a terminal. Echo and line handling are changed with the
 * system's `stty`, on the terminal device itself, and put back before each call returns.
 *
 * On Windows PHP can neither turn off echo nor discard what was typed ahead, so nothing is read from the keyboard
 * there: a pairing code comes from a code file, and the receiver's key from the QR text or `--fingerprint`.
 */
final class Terminal
{
    /** Settings to put back if the program is interrupted, keyed by an id of the terminal stream. @var array<int, array{0: resource, 1: string}> */
    private static array $pending = [];
    private static bool $handlersInstalled = false;

    private function __construct()
    {
    }

    public static function isWindows(): bool
    {
        return PHP_OS_FAMILY === 'Windows';
    }

    /** True if both standard input and standard output are a terminal, so that a person can see a question and answer it. */
    public static function personIsThere(): bool
    {
        return !self::isWindows() && defined('STDIN') && defined('STDOUT') && stream_isatty(STDIN) && stream_isatty(STDOUT);
    }

    /**
     * Shows `$prompt` on the terminal and reads one line from it, without showing what is typed, as `readpassphrase`
     * does, whatever standard input is. Anything typed before the prompt is discarded. Returns null if there is no
     * terminal, and an empty string at the end of input.
     */
    public static function readSecretLine(string $prompt, int $maxLength): ?string
    {
        if (self::isWindows()) {
            return null;
        }
        $terminal = @fopen('/dev/tty', 'r+');
        if ($terminal === false) {
            return null;
        }
        try {
            $saved = self::stty($terminal, ['-g']);
            if ($saved === null) {
                return null;
            }
            self::remember($terminal, $saved);
            try {
                self::discardTypedAhead($terminal, $saved);
                if (self::stty($terminal, ['-echo']) === null) {
                    return null;
                }
                fwrite($terminal, $prompt);
                fflush($terminal);
                $line = fgets($terminal, $maxLength + 2);
            } finally {
                self::stty($terminal, [$saved]);
                self::forget($terminal);
                fwrite($terminal, "\n");
            }
            if ($line === false) {
                return '';
            }
            if (strlen($line) > $maxLength && !str_ends_with($line, "\n")) {
                throw new UsageException('the text typed is too long');
            }
            return rtrim($line, "\r\n");
        } finally {
            fclose($terminal);
        }
    }

    /**
     * Asks a question on standard output and reads the answer from standard input, which must both be a terminal.
     * Only an answer typed after the question appeared counts, so anything typed before is discarded first. Returns
     * null at the end of input.
     */
    public static function ask(string $question, int $maxLength = 256): ?string
    {
        if (!self::personIsThere()) {
            return null;
        }
        $saved = self::stty(STDIN, ['-g']);
        if ($saved === null) {
            throw new UsageException('could not use the terminal. Use the QR text, or give the full 64-digit key with --fingerprint.');
        }
        self::remember(STDIN, $saved);
        try {
            self::discardTypedAhead(STDIN, $saved);
        } finally {
            self::stty(STDIN, [$saved]);
            self::forget(STDIN);
        }
        fwrite(STDOUT, $question);
        fflush(STDOUT);
        $line = fgets(STDIN, $maxLength + 2);
        if ($line === false) {
            return null;
        }
        if (strlen($line) > $maxLength && !str_ends_with($line, "\n")) {
            throw new UsageException('the text typed is too long');
        }
        return rtrim($line, "\r\n");
    }

    /**
     * Reads and drops everything the terminal holds, typed lines and a line not yet finished alike, by reading it for a
     * moment without line editing and without waiting. A stream of its own does the reading, so that the stream the
     * answer is read from has seen none of this.
     *
     * @param resource $terminal
     */
    private static function discardTypedAhead($terminal, string $saved): void
    {
        if (self::stty($terminal, ['-icanon', 'min', '0', 'time', '0']) === null) {
            throw new UsageException('could not discard what was typed before the question. Use the QR text, or give the full 64-digit key with --fingerprint.');
        }
        $reader = $terminal === STDIN ? @fopen('php://stdin', 'r') : @fopen('/dev/tty', 'r');
        if ($reader !== false) {
            for ($round = 0; $round < 10000; $round++) {
                $chunk = @fread($reader, 4096);
                if ($chunk === false || $chunk === '') {
                    break;
                }
            }
            fclose($reader);
        }
        self::stty($terminal, [$saved]);
    }

    /**
     * Runs `stty` on a terminal and returns what it printed, or null if it failed.
     *
     * @param resource $terminal
     * @param list<string> $arguments
     */
    private static function stty($terminal, array $arguments): ?string
    {
        if (!function_exists('proc_open')) {
            return null;
        }
        $process = @proc_open(array_merge(['stty'], $arguments), [0 => $terminal, 1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes);
        if (!is_resource($process)) {
            return null;
        }
        $output = (string) stream_get_contents($pipes[1]);
        stream_get_contents($pipes[2]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        return proc_close($process) === 0 ? trim($output) : null;
    }

    /** @param resource $terminal */
    private static function remember($terminal, string $saved): void
    {
        self::$pending[(int) $terminal] = [$terminal, $saved];
        if (self::$handlersInstalled) {
            return;
        }
        self::$handlersInstalled = true;
        register_shutdown_function([self::class, 'restoreAll']);
        if (function_exists('pcntl_async_signals') && function_exists('pcntl_signal')) {
            pcntl_async_signals(true);
            foreach ([SIGINT, SIGTERM] as $signal) {
                pcntl_signal($signal, static function (int $number): void {
                    self::restoreAll();
                    exit(128 + $number);
                });
            }
        }
    }

    /** @param resource $terminal */
    private static function forget($terminal): void
    {
        unset(self::$pending[(int) $terminal]);
    }

    /** @internal Puts back every terminal setting this class changed. */
    public static function restoreAll(): void
    {
        foreach (self::$pending as [$terminal, $saved]) {
            if (is_resource($terminal)) {
                self::stty($terminal, [$saved]);
                @fwrite($terminal, "\n");
            }
        }
        self::$pending = [];
    }
}
