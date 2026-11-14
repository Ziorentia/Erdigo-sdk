<?php

declare(strict_types=1);

namespace Erdigo\Core\Noise;

/**
 * A Noise handshake pattern, limited to the DH, cipher and hash functions used by integration-v1: `25519`,
 * `ChaChaPoly` and `SHA256`.
 */
final class Pattern
{
    public const E = 'e';
    public const S = 's';
    public const EE = 'ee';
    public const ES = 'es';
    public const SE = 'se';
    public const SS = 'ss';
    public const PSK = 'psk';

    /**
     * @param list<list<string>> $messages The tokens of each message, in order.
     */
    public function __construct(
        public readonly string $name,
        public readonly bool $responderStaticIsPreMessage,
        public readonly array $messages,
    ) {
    }

    public function protocolName(): string
    {
        return "Noise_{$this->name}_25519_ChaChaPoly_SHA256";
    }

    public function usesPsk(): bool
    {
        foreach ($this->messages as $tokens) {
            if (in_array(self::PSK, $tokens, true)) {
                return true;
            }
        }
        return false;
    }

    /** Used for ordinary connections: only a device that already knows the receiver's key can start one. */
    public static function ik(): self
    {
        return new self('IK', true, [[self::E, self::ES, self::S, self::SS], [self::E, self::EE, self::SE]]);
    }

    /**
     * Used for pairing: the pairing code is mixed in before anything else, so a party without the code cannot produce
     * a first message the receiver will answer.
     */
    public static function xxPsk0(): self
    {
        return new self('XXpsk0', false, [[self::PSK, self::E], [self::E, self::EE, self::S, self::ES], [self::S, self::SE]]);
    }
}
