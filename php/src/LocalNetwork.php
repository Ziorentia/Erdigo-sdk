<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * Which IPv4 addresses count as the local network.
 *
 * @internal
 */
final class LocalNetwork
{
    private function __construct()
    {
    }

    /**
     * The interfaces that are up, with each of their IPv4 addresses and its prefix length.
     *
     * @return list<array{0: string, 1: string, 2: int}>
     */
    public static function interfaceAddresses(): array
    {
        $interfaces = function_exists('net_get_interfaces') ? @net_get_interfaces() : false;
        if (!is_array($interfaces)) {
            return [];
        }
        $addresses = [];
        foreach ($interfaces as $name => $details) {
            if (!is_array($details) || !($details['up'] ?? true)) {
                continue;
            }
            foreach ($details['unicast'] ?? [] as $unicast) {
                $address = $unicast['address'] ?? null;
                if (!is_string($address) || filter_var($address, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4) === false) {
                    continue;
                }
                $mask = $unicast['netmask'] ?? null;
                $prefix = is_string($mask) ? self::prefixLength($mask) : 32;
                $addresses[] = [(string) $name, $address, $prefix];
            }
        }
        return $addresses;
    }

    private static function prefixLength(string $mask): int
    {
        $packed = @inet_pton($mask);
        if ($packed === false || strlen($packed) !== 4) {
            return 32;
        }
        $bits = unpack('N', $packed)[1];
        $length = 0;
        while ($length < 32 && ($bits & (1 << (31 - $length))) !== 0) {
            $length += 1;
        }
        return $length;
    }

    /** True for this computer, link-local addresses and addresses on a subnet one of this computer's interfaces is directly on. */
    public static function isLocal(string $address): bool
    {
        $packed = @inet_pton($address);
        if ($packed === false || strlen($packed) !== 4) {
            return false;
        }
        $value = unpack('N', $packed)[1];
        if (($value >> 24) === 127 || ($value >> 16) === ((169 << 8) | 254)) {
            return true;
        }
        foreach (self::interfaceAddresses() as [, $own, $prefix]) {
            if ($prefix <= 0) {
                continue;
            }
            $ownValue = unpack('N', (string) inet_pton($own))[1];
            $mask = $prefix >= 32 ? 0xFFFFFFFF : (0xFFFFFFFF << (32 - $prefix)) & 0xFFFFFFFF;
            if (($ownValue & $mask) === ($value & $mask)) {
                return true;
            }
        }
        return false;
    }
}
