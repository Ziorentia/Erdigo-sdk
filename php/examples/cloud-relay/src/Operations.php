<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemId;
use Erdigo\Core\JsonObject;
use Erdigo\Core\JsonParseException;
use Erdigo\Core\Limits;
use Erdigo\Core\MarkerColor;
use Erdigo\Core\PhotoType;
use Erdigo\Core\StrictJson;
use Erdigo\Core\TextRules;
use Erdigo\Core\WireException;

/**
 * The operations of the relay API (docs/cloud-relay.md), checked with the SDK's own rules for ids and text. The relay
 * checks what it takes with these, and the gateway checks again what it fetches.
 */
final class Operations
{
    public const MAX_PER_POST = 100;
    public const MAX_POINTS = 100;
    /** Deep enough for any valid body. Deeper nesting is refused as not JSON, so that parsing cannot run out of stack. */
    public const MAX_DEPTH = 64;

    /** Each operation's required and optional fields, besides `op`. */
    private const FIELDS = [
        'pin' => [['id', 'lat', 'lon'], ['title']],
        'marker' => [['id', 'lat', 'lon'], ['title', 'color']],
        'note' => [['id', 'lat', 'lon', 'text'], []],
        'photo' => [['id', 'lat', 'lon', 'type', 'data'], ['title']],
        'track' => [['id', 'points'], ['title']],
        'finish_track' => [['id'], []],
        'move' => [['id', 'lat', 'lon'], []],
        'delete' => [['id'], []],
    ];

    private function __construct()
    {
    }

    /** The operation's name if it is one the API has, for messages that must not repeat what a relay sent. */
    public static function knownName(mixed $op): ?string
    {
        if (!$op instanceof JsonObject) {
            return null;
        }
        $name = $op->fields['op'] ?? null;
        return is_string($name) && isset(self::FIELDS[$name]) ? $name : null;
    }

    /**
     * The operations of a `POST /v1/ops` body.
     *
     * @return list<JsonObject>
     * @throws Refusal
     */
    public static function parseBatch(string $body): array
    {
        try {
            $root = StrictJson::parse($body, self::MAX_DEPTH);
        } catch (JsonParseException) {
            throw new Refusal(400, ['error' => 'invalid_json']);
        }
        if (!$root instanceof JsonObject) {
            throw new Refusal(400, ['error' => 'invalid_json']);
        }
        $fields = $root->fields;
        if (array_keys($fields) !== ['ops']) {
            throw new Refusal(400, ['error' => 'invalid_body', 'reason' => 'the body is {"ops":[...]} and nothing else']);
        }
        $ops = $fields['ops'];
        if (!is_array($ops) || $ops === [] || count($ops) > self::MAX_PER_POST) {
            throw new Refusal(400, ['error' => 'invalid_body', 'reason' => 'ops is a list of 1 to 100 operations']);
        }
        foreach ($ops as $index => $op) {
            $problem = self::problem($op);
            if ($problem !== null) {
                throw new Refusal(400, ['error' => 'invalid_op', 'index' => $index, 'reason' => $problem]);
            }
        }
        return $ops;
    }

    /** Why an operation breaks the rules, or null if it follows them. */
    public static function problem(mixed $op): ?string
    {
        if (!$op instanceof JsonObject) {
            return 'an operation is a JSON object';
        }
        $fields = $op->fields;
        $name = self::knownName($op);
        if ($name === null) {
            return 'op is pin, marker, note, photo, track, finish_track, move or delete';
        }
        [$required, $optional] = self::FIELDS[$name];
        foreach (array_keys($fields) as $key) {
            $key = (string) $key;
            if ($key !== 'op' && !in_array($key, $required, true) && !in_array($key, $optional, true)) {
                return "{$name} has a field it does not take";
            }
        }
        foreach ($required as $key) {
            if (!array_key_exists($key, $fields)) {
                return "{$name} needs {$key}";
            }
        }
        foreach ($fields as $key => $value) {
            $problem = match ((string) $key) {
                'id' => is_string($value) && self::isId($value) ? null : "id is 1 to 64 ASCII letters, digits, '-' or '_'",
                'lat' => self::inRange($value, -90.0, 90.0) ? null : 'lat is a number from -90 to 90',
                'lon' => self::inRange($value, -180.0, 180.0) ? null : 'lon is a number from -180 to 180',
                'title' => self::textProblem($value, false),
                'text' => self::textProblem($value, true),
                'color' => is_string($value) && MarkerColor::tryFrom($value) !== null
                    ? null : 'color is red, orange, yellow, green, blue, purple or gray',
                'type' => is_string($value) && PhotoType::tryFrom($value) !== null ? null : 'type is image/jpeg or image/png',
                'points' => self::pointsProblem($value),
                default => null,
            };
            if ($problem !== null) {
                return $problem;
            }
        }
        if ($name === 'photo') {
            $bytes = is_string($fields['data']) ? self::decodePhoto($fields['data']) : null;
            if ($bytes === null) {
                return 'data is 1 to ' . Limits::v1()->photoBytes . ' bytes in standard base64 with padding';
            }
            if (PhotoType::detect($bytes)?->value !== $fields['type']) {
                return 'data does not start like the type says';
            }
        }
        return null;
    }

    /**
     * The bytes of a photo's `data`, or null unless it is standard base64 with padding, exactly as an encoder writes
     * it, with no line breaks, and decodes to 1 to 1048576 bytes.
     */
    public static function decodePhoto(string $data): ?string
    {
        $maxBytes = Limits::v1()->photoBytes;
        if ($data === '' || strlen($data) % 4 !== 0 || strlen($data) > intdiv($maxBytes + 2, 3) * 4) {
            return null;
        }
        if (preg_match('#^[A-Za-z0-9+/]*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})$#D', $data) !== 1) {
            return null;
        }
        $bytes = base64_decode($data, true);
        // Re-encoding catches leftover bits that a decoder ignores, which an encoder never writes.
        if ($bytes === false || $bytes === '' || strlen($bytes) > $maxBytes || base64_encode($bytes) !== $data) {
            return null;
        }
        return $bytes;
    }

    private static function isId(string $id): bool
    {
        try {
            ItemId::validate($id);
            return true;
        } catch (WireException) {
            return false;
        }
    }

    /** A JSON number, which the strict reader gives as an int or a finite float, never a boolean. */
    public static function inRange(mixed $value, float $low, float $high): bool
    {
        return (is_int($value) || is_float($value)) && $value >= $low && $value <= $high;
    }

    private static function textProblem(mixed $value, bool $isNote): ?string
    {
        $what = $isNote ? 'text' : 'title';
        if (!is_string($value)) {
            return "{$what} is a string";
        }
        try {
            $isNote ? TextRules::validateNote($value) : TextRules::validateTitle($value);
            return null;
        } catch (WireException $error) {
            return "{$what}: {$error->detail}";
        }
    }

    private static function pointsProblem(mixed $points): ?string
    {
        if (!is_array($points) || $points === [] || count($points) > self::MAX_POINTS) {
            return 'points is a list of 1 to 100 points';
        }
        foreach ($points as $point) {
            if (!$point instanceof JsonObject) {
                return 'a point is a JSON object';
            }
            $fields = $point->fields;
            foreach (array_keys($fields) as $key) {
                if (!in_array((string) $key, ['lat', 'lon', 'ts', 'alt'], true)) {
                    return 'a point has lat, lon, and optionally ts and alt, and nothing else';
                }
            }
            if (!array_key_exists('lat', $fields) || !self::inRange($fields['lat'], -90.0, 90.0)) {
                return 'a point needs lat, a number from -90 to 90';
            }
            if (!array_key_exists('lon', $fields) || !self::inRange($fields['lon'], -180.0, 180.0)) {
                return 'a point needs lon, a number from -180 to 180';
            }
            if (array_key_exists('ts', $fields)
                && (!is_int($fields['ts']) || $fields['ts'] < 0 || $fields['ts'] > IntegrationV1::MAX_SEQUENCE)) {
                return 'ts is a whole number of milliseconds from 0 to 2^53-1';
            }
            if (array_key_exists('alt', $fields) && !self::inRange($fields['alt'], -1000.0, 100000.0)) {
                return 'alt is a number of metres from -1000 to 100000';
            }
        }
        return null;
    }
}
