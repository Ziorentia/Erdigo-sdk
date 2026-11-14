<?php

declare(strict_types=1);

namespace Erdigo\Core\Noise;

/**
 * Encrypts and decrypts messages after a handshake has finished. A handshake yields one transport, because
 * {@see HandshakeState::split()} succeeds only once, so no key and nonce pair is ever used twice. The caller still has
 * to send frames in the order they were sealed, because the peer can only open them in that order.
 */
final class Transport
{
    /** @internal */
    public function __construct(
        private readonly CipherState $sendCipher,
        private readonly CipherState $receiveCipher,
        public readonly string $handshakeHash,
    ) {
    }

    public function seal(#[\SensitiveParameter] string $plaintext): string
    {
        if (strlen($plaintext) + Noise::TAG_LENGTH > Noise::MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException::MESSAGE_TOO_LARGE);
        }
        return $this->sendCipher->encrypt('', $plaintext);
    }

    public function open(string $ciphertext): string
    {
        if (strlen($ciphertext) > Noise::MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException::MESSAGE_TOO_LARGE);
        }
        return $this->receiveCipher->decrypt('', $ciphertext);
    }

    /** Clears both keys. Every later operation fails, rather than falling back to no encryption. */
    public function close(): void
    {
        $this->sendCipher->exhaust();
        $this->receiveCipher->exhaust();
    }
}
