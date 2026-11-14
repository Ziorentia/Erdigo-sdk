<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * What a person gives the SDK to pair: the code, and the receiver's key fingerprint when it comes from the QR code.
 * A typed code has no fingerprint, and the person then has to compare the key.
 */
final class PairingTicket
{
    private const PREFIX = 'erdigo-pair:1?';

    /** Much longer than any valid text, which is 110 bytes without white space around it. */
    private const MAX_TEXT_LENGTH = 1024;

    public function __construct(public readonly PairingCode $code, public readonly ?string $fingerprint = null)
    {
    }

    /**
     * Accepts either a bare code or the QR form `erdigo-pair:1?code=...&fp=...`.
     *
     * The QR form needs exactly one code and exactly one valid fingerprint, because the key comparison is what makes
     * it stronger than a typed code. Nothing else may appear in it. Text over 1024 bytes is refused before it is
     * looked at, because it may come from anywhere, such as a web form.
     */
    public static function parse(#[\SensitiveParameter] string $text): self
    {
        if (strlen($text) > self::MAX_TEXT_LENGTH) {
            throw new WireException(ErrorCode::InvalidMessage, 'the pairing text is too long');
        }
        $trimmed = Utf8::trim($text);
        if (!str_starts_with($trimmed, self::PREFIX)) {
            return new self(new PairingCode($trimmed));
        }
        $code = null;
        $fingerprint = null;
        // Empty pieces are kept, so that '&&', a trailing '&' and a field starting with '=' are refused below.
        foreach (explode('&', substr($trimmed, strlen(self::PREFIX))) as $part) {
            $pieces = explode('=', $part, 2);
            if (count($pieces) !== 2) {
                throw new WireException(ErrorCode::InvalidMessage, 'the pairing text is malformed');
            }
            if ($pieces[0] === 'code' && $code === null) {
                $code = new PairingCode($pieces[1]);
            } elseif ($pieces[0] === 'fp' && $fingerprint === null) {
                $lowered = Hex::lowercase($pieces[1]);
                if (!Hex::isFingerprint($lowered)) {
                    throw new WireException(ErrorCode::InvalidMessage, 'the fingerprint in the pairing text is malformed');
                }
                $fingerprint = $lowered;
            } else {
                throw new WireException(ErrorCode::InvalidMessage, 'the pairing text is malformed');
            }
        }
        if ($code === null) {
            throw new WireException(ErrorCode::InvalidMessage, 'the pairing text has no code');
        }
        if ($fingerprint === null) {
            throw new WireException(ErrorCode::InvalidMessage, 'the pairing text has no fingerprint');
        }
        return new self($code, $fingerprint);
    }

    /**
     * The text of the QR code, or null without a valid fingerprint, because QR text without one would look stronger
     * than it is.
     */
    public function uri(): ?string
    {
        if (!Hex::isFingerprint($this->fingerprint)) {
            return null;
        }
        return self::PREFIX . 'code=' . $this->code->display() . '&fp=' . $this->fingerprint;
    }

    public function __toString(): string
    {
        return '<pairing ticket>';
    }

    /** @return array<string, string|null> */
    public function __debugInfo(): array
    {
        return ['code' => '<pairing code>', 'fingerprint' => $this->fingerprint];
    }
}
