<?php

declare(strict_types=1);

namespace Erdigo\Core\Noise;

/**
 * Failures raised by the Noise layer. They deliberately carry no key material.
 */
final class NoiseException extends \RuntimeException
{
    public const INVALID_KEY = 'invalid key';
    public const WEAK_SHARED_SECRET = 'weak shared secret';
    public const TRUNCATED_MESSAGE = 'truncated message';
    public const DECRYPTION_FAILED = 'decryption failed';
    public const NONCE_EXHAUSTED = 'nonce exhausted';
    public const MESSAGE_TOO_LARGE = 'message too large';
    public const WRONG_HANDSHAKE_STATE = 'wrong handshake state';
    public const MISSING_KEY = 'missing key';
    /** Split was called a second time. A second pair of cipher states would repeat the first one's keys and nonces. */
    public const ALREADY_SPLIT = 'already split';

    public function __construct(public readonly string $error)
    {
        parent::__construct("Noise: {$error}");
    }
}
