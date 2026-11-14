<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * A small DNS message reader and writer, for multicast DNS service discovery on the local network (RFC 6762 and
 * RFC 6763). Everything it reads comes from the network, so every length and every name pointer is checked.
 *
 * @internal
 */
final class MdnsWire
{
    public const TYPE_A = 1;
    public const TYPE_PTR = 12;
    public const TYPE_SRV = 33;
    public const PORT = 5353;
    public const MULTICAST_V4 = '224.0.0.251';
    private const MAX_NAME_LENGTH = 255;
    private const MAX_MESSAGE_LENGTH = 9000;

    private function __construct()
    {
    }

    public static function serviceName(): string
    {
        return Core\IntegrationV1::SERVICE_TYPE . '.local';
    }

    public static function namesEqual(string $left, string $right): bool
    {
        return Core\Hex::lowercase(rtrim($left, '.')) === Core\Hex::lowercase(rtrim($right, '.'));
    }

    /**
     * A query with these questions, asking for answers sent back to the port it came from (RFC 6762 section 6.7).
     *
     * @param list<array{0: string, 1: int}> $questions Names and record types.
     */
    public static function encodeQuery(int $id, array $questions): string
    {
        $output = pack('nnnnnn', $id, 0, count($questions), 0, 0, 0);
        foreach ($questions as [$name, $type]) {
            $output .= self::encodeName($name) . pack('nn', $type, 0x0001);
        }
        return $output;
    }

    private static function encodeName(string $name): string
    {
        $output = '';
        foreach (explode('.', rtrim($name, '.')) as $label) {
            if ($label === '' || strlen($label) > 63) {
                throw new \InvalidArgumentException('a DNS label is 1 to 63 bytes');
            }
            $output .= chr(strlen($label)) . $label;
        }
        return $output . "\0";
    }

    /**
     * Reads a response, or returns null for anything malformed. Records of types other than PTR, SRV and A are skipped.
     *
     * @return array{id: int, response: bool, records: list<array{name: string, type: int, ttl: int, target?: string, port?: int, address?: string}>}|null
     */
    public static function decode(string $data): ?array
    {
        $length = strlen($data);
        if ($length < 12 || $length > self::MAX_MESSAGE_LENGTH) {
            return null;
        }
        try {
            $header = unpack('nid/nflags/nquestions/nanswers/nauthorities/nadditionals', $data);
            if ($header === false) {
                return null;
            }
            $flags = $header['flags'];
            // Only a standard query or response with no error is of any use.
            if ((($flags >> 11) & 0xF) !== 0 || ($flags & 0xF) !== 0) {
                return null;
            }
            $offset = 12;
            for ($index = 0; $index < $header['questions']; $index++) {
                self::readName($data, $offset);
                self::require($data, $offset, 4);
                $offset += 4;
            }
            $records = [];
            $total = $header['answers'] + $header['authorities'] + $header['additionals'];
            for ($index = 0; $index < $total; $index++) {
                $record = self::readRecord($data, $offset);
                if ($record !== null && ($index < $header['answers'] || $index >= $header['answers'] + $header['authorities'])) {
                    $records[] = $record;
                }
            }
            return ['id' => $header['id'], 'response' => ($flags & 0x8000) !== 0, 'records' => $records];
        } catch (\UnexpectedValueException) {
            return null;
        }
    }

    private static function require(string $data, int $offset, int $count): void
    {
        if ($offset < 0 || $count < 0 || $offset + $count > strlen($data)) {
            throw new \UnexpectedValueException('truncated DNS message');
        }
    }

    /** @return array{name: string, type: int, ttl: int, target?: string, port?: int, address?: string}|null */
    private static function readRecord(string $data, int &$offset): ?array
    {
        $name = self::readName($data, $offset);
        self::require($data, $offset, 10);
        $fields = unpack('ntype/nclass/Nttl/nlength', $data, $offset);
        if ($fields === false) {
            throw new \UnexpectedValueException('truncated DNS message');
        }
        $offset += 10;
        $start = $offset;
        $length = $fields['length'];
        self::require($data, $start, $length);
        $end = $start + $length;
        $offset = $end;
        if (($fields['class'] & 0x7FFF) !== 0x0001) {
            return null;
        }
        $record = ['name' => $name, 'type' => $fields['type'], 'ttl' => $fields['ttl']];
        switch ($fields['type']) {
            case self::TYPE_PTR:
                $position = $start;
                $record['target'] = self::readName($data, $position, $end);
                return $record;
            case self::TYPE_SRV:
                if ($length < 7) {
                    throw new \UnexpectedValueException('short SRV record');
                }
                $record['port'] = (ord($data[$start + 4]) << 8) | ord($data[$start + 5]);
                $position = $start + 6;
                $record['target'] = self::readName($data, $position, $end);
                return $record;
            case self::TYPE_A:
                if ($length !== 4) {
                    return null;
                }
                $record['address'] = (string) inet_ntop(substr($data, $start, 4));
                return $record;
            default:
                return null;
        }
    }

    /**
     * Reads a possibly compressed name. A pointer must point backwards, to before where this name started, so a loop
     * of pointers cannot exist.
     */
    private static function readName(string $data, int &$offset, int $limit = PHP_INT_MAX): string
    {
        $labels = [];
        $total = 0;
        $position = $offset;
        $lowestStart = $offset;
        $jumped = false;
        while (true) {
            self::require($data, $position, 1);
            if (!$jumped && $position >= $limit) {
                throw new \UnexpectedValueException('name runs past its record');
            }
            $size = ord($data[$position]);
            if (($size & 0xC0) === 0xC0) {
                self::require($data, $position, 2);
                $target = (($size & 0x3F) << 8) | ord($data[$position + 1]);
                if (!$jumped) {
                    $offset = $position + 2;
                    $jumped = true;
                }
                if ($target >= $lowestStart) {
                    throw new \UnexpectedValueException('a name pointer must point backwards');
                }
                $lowestStart = $target;
                $position = $target;
                continue;
            }
            if (($size & 0xC0) !== 0) {
                throw new \UnexpectedValueException('unknown label type');
            }
            $position += 1;
            if ($size === 0) {
                if (!$jumped) {
                    $offset = $position;
                }
                break;
            }
            self::require($data, $position, $size);
            $total += $size + 1;
            if ($total > self::MAX_NAME_LENGTH) {
                throw new \UnexpectedValueException('name too long');
            }
            $labels[] = substr($data, $position, $size);
            $position += $size;
        }
        if (!$jumped && $offset > $limit) {
            throw new \UnexpectedValueException('name runs past its record');
        }
        return implode('.', $labels);
    }
}
