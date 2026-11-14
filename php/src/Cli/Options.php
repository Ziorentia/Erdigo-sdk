<?php

declare(strict_types=1);

namespace Erdigo\Cli;

/**
 * A deliberately small option parser: `--name value` pairs and bare flags, nothing else.
 */
final class Options
{
    /**
     * Every option the tool has, including those of the other editions. Errors name an option only if it is on this
     * list, because anything else that was typed might be a pairing code.
     */
    public const KNOWN = [
        'alt', 'ask', 'auto-approve', 'bonjour', 'code-file', 'color', 'file', 'fingerprint', 'host', 'id', 'keychain',
        'kinds', 'label', 'lan', 'lat', 'lon', 'mock', 'name', 'port', 'profile', 'redact', 'retry-for', 'state-dir',
        'text', 'text-file', 'title', 'to', 'ts', 'yes',
    ];

    /** @var array<string, string> */
    private array $values = [];
    /** @var array<string, true> */
    private array $flags = [];
    /** @var array<string, true> */
    private array $used = [];

    /**
     * @param list<string> $arguments
     * @param list<string> $allowedFlags
     */
    public function __construct(array $arguments, array $allowedFlags)
    {
        $index = 0;
        $count = count($arguments);
        while ($index < $count) {
            $argument = $arguments[$index];
            // Errors here never repeat what was typed, because it might be a pairing code.
            if (!str_starts_with($argument, '--')) {
                throw new UsageException('unexpected argument. Options start with --, and a pairing code is never given as an argument.');
            }
            $name = substr($argument, 2);
            if ($name === 'code' || str_starts_with($name, 'code=')) {
                throw new UsageException(
                    'the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. '
                    . 'Type it when asked, or use --code-file.',
                );
            }
            $equals = strpos($name, '=');
            if ($equals !== false) {
                throw new UsageException('write ' . self::describe(substr($name, 0, $equals)) . ' and its value as two separate arguments');
            }
            if (in_array($name, $allowedFlags, true)) {
                $this->flags[$name] = true;
                $index += 1;
                continue;
            }
            if (!in_array($name, self::KNOWN, true)) {
                throw new UsageException("an unknown option was given. See 'erdigo-send help'.");
            }
            if ($index + 1 >= $count) {
                throw new UsageException("--{$name} needs a value");
            }
            if (isset($this->values[$name])) {
                throw new UsageException("--{$name} was given twice");
            }
            $this->values[$name] = $arguments[$index + 1];
            $index += 2;
        }
    }

    private static function describe(string $name): string
    {
        return in_array($name, self::KNOWN, true) ? "--{$name}" : 'an unknown option';
    }

    public function flag(string $name): bool
    {
        $this->used[$name] = true;
        return isset($this->flags[$name]);
    }

    public function optional(string $name): ?string
    {
        $this->used[$name] = true;
        return $this->values[$name] ?? null;
    }

    public function required(string $name): string
    {
        return $this->optional($name) ?? throw new UsageException("--{$name} is required");
    }

    public function requiredNumber(string $name): float
    {
        $value = self::number($this->required($name));
        if ($value === null) {
            throw new UsageException("--{$name} must be a number");
        }
        return $value;
    }

    /** A finite decimal number, or null for anything else, such as `nan`. */
    public static function number(string $text): ?float
    {
        $text = trim($text, " \t");
        if (preg_match('/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/D', $text) !== 1) {
            return null;
        }
        $value = (float) $text;
        return is_finite($value) ? $value : null;
    }

    public function optionalPort(): ?int
    {
        $text = $this->optional('port');
        if ($text === null) {
            return null;
        }
        if (preg_match('/^\d{1,5}$/D', $text) !== 1 || (int) $text > 65535) {
            throw new UsageException('--port must be between 0 and 65535');
        }
        return (int) $text;
    }

    /** Call after reading every option the command understands. */
    public function finish(): void
    {
        $given = array_merge(array_keys($this->values), array_keys($this->flags));
        sort($given, SORT_STRING);
        foreach ($given as $name) {
            if (!isset($this->used[$name])) {
                throw new UsageException("--{$name} does not apply to this command");
            }
        }
    }
}
