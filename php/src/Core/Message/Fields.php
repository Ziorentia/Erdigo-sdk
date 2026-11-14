<?php

declare(strict_types=1);

namespace Erdigo\Core\Message;

use Erdigo\Core\Coordinate;
use Erdigo\Core\ErrorCode;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemId;
use Erdigo\Core\ItemKind;
use Erdigo\Core\JsonObject;
use Erdigo\Core\Limits;
use Erdigo\Core\TextRules;
use Erdigo\Core\WireException;

/**
 * Reads a JSON object against an explicit list of fields.
 *
 * @internal
 */
final class Fields
{
    /** @var array<string|int, mixed> */
    public readonly array $map;

    public function __construct(mixed $value)
    {
        if (!$value instanceof JsonObject) {
            throw new WireException(ErrorCode::InvalidMessage, 'a message is a JSON object');
        }
        $this->map = $value->fields;
    }

    public function has(string $key): bool
    {
        return array_key_exists($key, $this->map);
    }

    public function only(string ...$allowed): void
    {
        foreach (array_keys($this->map) as $key) {
            if (!in_array((string) $key, $allowed, true)) {
                throw new WireException(ErrorCode::InvalidMessage, 'unknown field');
            }
        }
    }

    public function string(string $key): string
    {
        $value = $this->map[$key] ?? null;
        if (!is_string($value)) {
            throw new WireException(ErrorCode::InvalidMessage, "field '{$key}' must be a string");
        }
        return $value;
    }

    public function optionalString(string $key): ?string
    {
        return $this->has($key) ? $this->string($key) : null;
    }

    public function integer(string $key): int
    {
        $value = $this->map[$key] ?? null;
        if (!is_int($value)) {
            throw new WireException(ErrorCode::InvalidMessage, "field '{$key}' must be an integer");
        }
        return $value;
    }

    public function optionalInteger(string $key): ?int
    {
        return $this->has($key) ? $this->integer($key) : null;
    }

    public function number(string $key): float
    {
        $value = $this->map[$key] ?? null;
        if (!is_int($value) && !is_float($value)) {
            throw new WireException(ErrorCode::InvalidMessage, "field '{$key}' must be a number");
        }
        return (float) $value;
    }

    public function bool(string $key): bool
    {
        $value = $this->map[$key] ?? null;
        if (!is_bool($value)) {
            throw new WireException(ErrorCode::InvalidMessage, "field '{$key}' must be true or false");
        }
        return $value;
    }

    /** @return list<mixed> */
    public function array(string $key): array
    {
        $value = $this->map[$key] ?? null;
        if (!is_array($value)) {
            throw new WireException(ErrorCode::InvalidMessage, "field '{$key}' must be an array");
        }
        return $value;
    }

    public function seq(string $key = 'seq'): int
    {
        $value = $this->integer($key);
        if ($value < 1 || $value > IntegrationV1::MAX_SEQUENCE) {
            throw new WireException(ErrorCode::InvalidMessage, 'a sequence number is within 1...2^53-1');
        }
        return $value;
    }

    public function nonNegative(string $key): int
    {
        $value = $this->integer($key);
        if ($value < 0 || $value > IntegrationV1::MAX_SEQUENCE) {
            throw new WireException(ErrorCode::InvalidMessage, "field '{$key}' is out of range");
        }
        return $value;
    }

    public function id(): string
    {
        return ItemId::validate($this->string('id'));
    }

    public function coordinate(): Coordinate
    {
        $latitude = $this->number('lat');
        $longitude = $this->number('lon');
        return new Coordinate($latitude, $longitude);
    }

    public function title(Limits $limits): ?string
    {
        $title = $this->optionalString('title');
        if ($title === null) {
            return null;
        }
        TextRules::validateTitle($title, $limits);
        return $title;
    }

    /** @return list<int> */
    public function versions(): array
    {
        $values = $this->array('versions');
        if (count($values) < 1 || count($values) > 16) {
            throw new WireException(ErrorCode::InvalidMessage, "field 'versions' must have 1 to 16 entries");
        }
        return array_map([Wire::class, 'version'], $values);
    }

    /** @return list<ItemKind> */
    public function kinds(): array
    {
        $kinds = [];
        foreach ($this->array('kinds') as $value) {
            $kind = is_string($value) ? ItemKind::tryFrom($value) : null;
            if ($kind === null || in_array($kind, $kinds, true)) {
                throw new WireException(ErrorCode::InvalidMessage, "field 'kinds' lists item kinds without repeats");
            }
            $kinds[] = $kind;
        }
        return $kinds;
    }

    public function sdkName(): string
    {
        $name = $this->string('sdk');
        if (preg_match('#^[A-Za-z0-9._/-]{1,64}$#D', $name) !== 1) {
            throw new WireException(ErrorCode::InvalidMessage, "field 'sdk' is 1 to 64 ASCII letters, digits or . _ / -");
        }
        return $name;
    }
}
