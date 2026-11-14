<?php

declare(strict_types=1);

namespace Erdigo\Core\Noise;

/**
 * The Noise handshake state machine, for either side.
 */
final class HandshakeState
{
    private readonly SymmetricState $symmetric;
    /** @var list<string> */
    private array $psks;
    private ?string $ephemeralKey = null;
    private ?string $remoteEphemeralKey = null;
    private ?string $remoteStaticKey;
    private int $messageIndex = 0;
    private bool $hasSplit = false;

    /**
     * Starts a handshake. `$staticKey` is this side's X25519 private key.
     *
     * @param list<string> $psks Pre-shared keys, used in order.
     * @param string|null $fixedEphemeral Exists only so that the published test vectors can be reproduced. Reusing an
     *     ephemeral key in real traffic breaks the protocol, so the SDK never passes one, and nothing a user can set
     *     reaches it. Use {@see self::start()}.
     * @internal
     */
    public function __construct(
        private readonly Pattern $pattern,
        private readonly bool $isInitiator,
        string $prologue,
        #[\SensitiveParameter] private readonly ?string $staticKey,
        ?string $remoteStaticKey,
        #[\SensitiveParameter] array $psks,
        #[\SensitiveParameter] private readonly ?string $fixedEphemeral,
    ) {
        foreach ($psks as $psk) {
            if (strlen($psk) !== 32) {
                throw new NoiseException(NoiseException::INVALID_KEY);
            }
        }
        if ($pattern->usesPsk() && $psks === []) {
            throw new NoiseException(NoiseException::MISSING_KEY);
        }
        if (($remoteStaticKey !== null && strlen($remoteStaticKey) !== Noise::DH_LENGTH)
            || ($staticKey !== null && strlen($staticKey) !== Noise::DH_LENGTH)
            || ($fixedEphemeral !== null && strlen($fixedEphemeral) !== Noise::DH_LENGTH)) {
            throw new NoiseException(NoiseException::INVALID_KEY);
        }
        $this->remoteStaticKey = $remoteStaticKey;
        $this->psks = array_values($psks);
        $this->symmetric = new SymmetricState($pattern->protocolName());
        $this->symmetric->mixHash($prologue);
        if ($pattern->responderStaticIsPreMessage) {
            if ($isInitiator) {
                $this->symmetric->mixHash($remoteStaticKey ?? throw new NoiseException(NoiseException::MISSING_KEY));
            } else {
                $this->symmetric->mixHash(Noise::publicKey($staticKey ?? throw new NoiseException(NoiseException::MISSING_KEY)));
            }
        }
    }

    /** Starts a handshake with a fresh ephemeral key for each message that needs one. */
    public static function start(
        Pattern $pattern,
        bool $isInitiator,
        string $prologue,
        #[\SensitiveParameter] ?string $staticKey,
        ?string $remoteStaticKey = null,
        #[\SensitiveParameter] ?string $psk = null,
    ): self {
        return new self($pattern, $isInitiator, $prologue, $staticKey, $remoteStaticKey, $psk === null ? [] : [$psk], null);
    }

    /** The other side's static public key, once known. */
    public function remoteStaticKey(): ?string
    {
        return $this->remoteStaticKey;
    }

    public function isComplete(): bool
    {
        return $this->messageIndex === count($this->pattern->messages);
    }

    public function handshakeHash(): string
    {
        return $this->symmetric->hash();
    }

    private function isMyTurn(): bool
    {
        return ($this->messageIndex % 2 === 0) === $this->isInitiator;
    }

    private function mixDh(string $token): void
    {
        $require = static fn (?string $key): string => $key ?? throw new NoiseException(NoiseException::MISSING_KEY);
        $secret = match ($token) {
            Pattern::EE => Noise::dh($require($this->ephemeralKey), $require($this->remoteEphemeralKey)),
            Pattern::ES => $this->isInitiator
                ? Noise::dh($require($this->ephemeralKey), $require($this->remoteStaticKey))
                : Noise::dh($require($this->staticKey), $require($this->remoteEphemeralKey)),
            Pattern::SE => $this->isInitiator
                ? Noise::dh($require($this->staticKey), $require($this->remoteEphemeralKey))
                : Noise::dh($require($this->ephemeralKey), $require($this->remoteStaticKey)),
            Pattern::SS => Noise::dh($require($this->staticKey), $require($this->remoteStaticKey)),
            default => throw new NoiseException(NoiseException::WRONG_HANDSHAKE_STATE),
        };
        $this->symmetric->mixKey($secret);
        sodium_memzero($secret);
    }

    private function mixPsk(): void
    {
        if ($this->psks === []) {
            throw new NoiseException(NoiseException::MISSING_KEY);
        }
        $psk = array_shift($this->psks);
        $this->symmetric->mixKeyAndHash($psk);
    }

    public function writeMessage(#[\SensitiveParameter] string $payload = ''): string
    {
        if ($this->isComplete() || !$this->isMyTurn()) {
            throw new NoiseException(NoiseException::WRONG_HANDSHAKE_STATE);
        }
        $output = '';
        foreach ($this->pattern->messages[$this->messageIndex] as $token) {
            switch ($token) {
                case Pattern::E:
                    if ($this->ephemeralKey !== null) {
                        sodium_memzero($this->ephemeralKey);
                    }
                    $this->ephemeralKey = $this->fixedEphemeral ?? Noise::newPrivateKey();
                    $publicKey = Noise::publicKey($this->ephemeralKey);
                    $output .= $publicKey;
                    $this->symmetric->mixHash($publicKey);
                    if ($this->pattern->usesPsk()) {
                        $this->symmetric->mixKey($publicKey);
                    }
                    break;
                case Pattern::S:
                    $output .= $this->symmetric->encryptAndHash(
                        Noise::publicKey($this->staticKey ?? throw new NoiseException(NoiseException::MISSING_KEY)),
                    );
                    break;
                case Pattern::PSK:
                    $this->mixPsk();
                    break;
                default:
                    $this->mixDh($token);
            }
        }
        $output .= $this->symmetric->encryptAndHash($payload);
        if (strlen($output) > Noise::MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException::MESSAGE_TOO_LARGE);
        }
        $this->messageIndex += 1;
        return $output;
    }

    public function readMessage(string $message): string
    {
        if ($this->isComplete() || $this->isMyTurn()) {
            throw new NoiseException(NoiseException::WRONG_HANDSHAKE_STATE);
        }
        if (strlen($message) > Noise::MAX_MESSAGE_LENGTH) {
            throw new NoiseException(NoiseException::MESSAGE_TOO_LARGE);
        }
        $offset = 0;
        $take = static function (int $count) use ($message, &$offset): string {
            if (strlen($message) - $offset < $count) {
                throw new NoiseException(NoiseException::TRUNCATED_MESSAGE);
            }
            $head = substr($message, $offset, $count);
            $offset += $count;
            return $head;
        };
        foreach ($this->pattern->messages[$this->messageIndex] as $token) {
            switch ($token) {
                case Pattern::E:
                    $publicKey = $take(Noise::DH_LENGTH);
                    $this->remoteEphemeralKey = $publicKey;
                    $this->symmetric->mixHash($publicKey);
                    if ($this->pattern->usesPsk()) {
                        $this->symmetric->mixKey($publicKey);
                    }
                    break;
                case Pattern::S:
                    $length = $this->symmetric->hasKey() ? Noise::DH_LENGTH + Noise::TAG_LENGTH : Noise::DH_LENGTH;
                    $this->remoteStaticKey = $this->symmetric->decryptAndHash($take($length));
                    break;
                case Pattern::PSK:
                    $this->mixPsk();
                    break;
                default:
                    $this->mixDh($token);
            }
        }
        $payload = $this->symmetric->decryptAndHash(substr($message, $offset));
        $this->messageIndex += 1;
        return $payload;
    }

    /**
     * Call once {@see self::isComplete()} is true. This succeeds only once, because a second transport would have the
     * same keys and start from the same nonces.
     */
    public function split(): Transport
    {
        if (!$this->isComplete()) {
            throw new NoiseException(NoiseException::WRONG_HANDSHAKE_STATE);
        }
        if ($this->hasSplit) {
            throw new NoiseException(NoiseException::ALREADY_SPLIT);
        }
        $this->hasSplit = true;
        [$first, $second] = $this->symmetric->split();
        $hash = $this->symmetric->hash();
        $this->clear();
        return $this->isInitiator ? new Transport($first, $second, $hash) : new Transport($second, $first, $hash);
    }

    /** Clears the ephemeral key and the handshake's own key material. The static key belongs to the caller. */
    public function clear(): void
    {
        if ($this->ephemeralKey !== null) {
            sodium_memzero($this->ephemeralKey);
            $this->ephemeralKey = null;
        }
        $this->psks = [];
        $this->symmetric->clear();
    }

    /** @return array<string, mixed> */
    public function __debugInfo(): array
    {
        return ['pattern' => $this->pattern->name, 'initiator' => $this->isInitiator, 'message' => $this->messageIndex];
    }
}
