<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * Constants of the integration-v1 protocol. See spec/integration-v1.md.
 */
final class IntegrationV1
{
    public const VERSION = 1;
    public const SERVICE_TYPE = '_erdigo-int._tcp';
    public const MAX_FRAME_LENGTH = 65535;
    public const MAX_PLAINTEXT_LENGTH = 65519;
    public const MAX_HANDSHAKE_FRAME_LENGTH = 1024;
    public const MAX_CHUNK_BYTES = 48000;
    public const MAX_POINTS_PER_MESSAGE = 100;
    /** 2^53-1, the largest sequence number and connection counter. */
    public const MAX_SEQUENCE = 9007199254740991;
    public const MAX_DEVICE_NAME_SCALARS = 40;
    public const CONNECT_FIRST_MESSAGE_LENGTH = 32 + 48 + 8 + 16;
    public const PAIR_FIRST_MESSAGE_LENGTH = 32 + 16;

    /** The fifth byte of the preamble: an ordinary connection. */
    public const MODE_CONNECT = 0x01;
    /** The fifth byte of the preamble: pairing. */
    public const MODE_PAIR = 0x02;

    private const FINGERPRINT_LABEL = 'erdigo-integration-v1/fingerprint';

    private function __construct()
    {
    }

    /**
     * Five bytes sent in the clear before the handshake. They are also the Noise prologue, so tampering with them
     * makes the handshake fail.
     */
    public static function preamble(int $mode): string
    {
        if ($mode !== self::MODE_CONNECT && $mode !== self::MODE_PAIR) {
            throw new \InvalidArgumentException('unknown preamble mode');
        }
        return 'EIN1' . chr($mode);
    }

    /** The mode of a preamble, or null for anything that is not one. */
    public static function parsePreamble(string $data): ?int
    {
        if (strlen($data) !== 5 || substr($data, 0, 4) !== 'EIN1') {
            return null;
        }
        $mode = ord($data[4]);
        return $mode === self::MODE_CONNECT || $mode === self::MODE_PAIR ? $mode : null;
    }

    /** A receiver's or a device's key fingerprint, as 64 lowercase hexadecimal digits. */
    public static function fingerprint(string $publicKey): string
    {
        return hash('sha256', self::FINGERPRINT_LABEL . $publicKey);
    }

    /** The first 16 hexadecimal digits in groups of four, for showing to people. */
    public static function shortFingerprint(string $fingerprint): string
    {
        $head = substr($fingerprint, 0, 16);
        return implode('-', str_split($head, 4));
    }
}
