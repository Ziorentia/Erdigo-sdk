<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\Cli\UsageException;

/**
 * `--name value` pairs and bare flags, like erdigo-send's. Errors never repeat what was typed, which might be a
 * pairing code or a token.
 */
final class GatewayOptions
{
    /** @var array<string, string> */
    private array $values = [];
    /** @var array<string, true> */
    private array $flags = [];

    /**
     * @param list<string> $arguments
     * @param list<string> $valued The options of this command that take a value.
     * @param list<string> $flagNames The options of this command that take none.
     */
    public function __construct(array $arguments, array $valued, array $flagNames)
    {
        $count = count($arguments);
        for ($index = 0; $index < $count; $index++) {
            $argument = $arguments[$index];
            if (!str_starts_with($argument, '--')) {
                throw new UsageException('unexpected argument. Options start with --, and a pairing code or a token is never given as an argument.');
            }
            $name = substr($argument, 2);
            if ($name === 'code' || str_starts_with($name, 'code=')) {
                throw new UsageException('the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. Type it when asked, or use --code-file.');
            }
            if ($name === 'token' || str_starts_with($name, 'token=')) {
                throw new UsageException('the token is not accepted as an argument, because arguments appear in the process list and shell history. Set ERDIGO_RELAY_TOKEN instead.');
            }
            $equals = strpos($name, '=');
            if ($equals !== false) {
                $given = substr($name, 0, $equals);
                $known = in_array($given, $valued, true) || in_array($given, $flagNames, true);
                throw new UsageException('write ' . ($known ? "--{$given}" : 'an option') . ' and its value as two separate arguments');
            }
            if (in_array($name, $flagNames, true)) {
                $this->flags[$name] = true;
                continue;
            }
            if (!in_array($name, $valued, true)) {
                throw new UsageException("an unknown option was given. See 'gateway help'.");
            }
            if ($index + 1 >= $count) {
                throw new UsageException("--{$name} needs a value");
            }
            if (isset($this->values[$name])) {
                throw new UsageException("--{$name} was given twice");
            }
            $this->values[$name] = $arguments[++$index];
        }
    }

    public function flag(string $name): bool
    {
        return isset($this->flags[$name]);
    }

    public function optional(string $name): ?string
    {
        return $this->values[$name] ?? null;
    }

    public function required(string $name): string
    {
        return $this->values[$name] ?? throw new UsageException("--{$name} is required");
    }
}
