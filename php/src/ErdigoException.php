<?php

declare(strict_types=1);

namespace Erdigo;

use Erdigo\Core\ItemKind;
use Erdigo\Core\Message\ReceiverError;

/**
 * Failures reported by the SDK. `$kind` says what happened.
 */
final class ErdigoException extends \RuntimeException
{
    public readonly StorageProblem $storageProblem;

    /** @param list<int>|null $supported */
    public function __construct(
        public readonly ErrorKind $kind,
        public readonly ?string $detail = null,
        public readonly ?ReceiverError $receiverError = null,
        public readonly ?ItemKind $itemKind = null,
        public readonly ?array $supported = null,
        public readonly ?int $lastSeq = null,
        StorageProblem $storageProblem = StorageProblem::None,
        ?\Throwable $previous = null,
    ) {
        $this->storageProblem = $kind === ErrorKind::Storage && $storageProblem === StorageProblem::None
            ? StorageProblem::Other
            : $storageProblem;
        parent::__construct(self::describe($kind, $detail, $receiverError, $itemKind, $supported), 0, $previous);
    }

    /** @param list<int>|null $supported */
    private static function describe(ErrorKind $kind, ?string $detail, ?ReceiverError $error, ?ItemKind $itemKind, ?array $supported): string
    {
        return match ($kind) {
            ErrorKind::NoAnswer => 'No receiver answered. It may be off, asleep or on another network, the pairing code may be '
                . 'wrong or expired, or this device may have been removed.',
            ErrorKind::FingerprintMismatch => "The receiver's key does not match the expected fingerprint. Pairing was stopped.",
            ErrorKind::KeyNotConfirmed => "The receiver's key was not confirmed. No pairing was saved, no items were sent, "
                . 'and the receiver was not asked to pair.',
            ErrorKind::PairingDenied => 'The request was declined on the receiver.',
            ErrorKind::PairingTimedOut => 'Nobody confirmed the request on the receiver in time.',
            ErrorKind::CodeReused => 'This pairing code was already used. The receiver removed the pairing made with it. Ask for a new code.',
            ErrorKind::Rejected => 'The receiver rejected the request: ' . ($error?->code ?? 'unknown') . '.',
            ErrorKind::NotPermitted => "This device is not allowed to send '" . ($itemKind?->value ?? 'unknown') . "' items.",
            ErrorKind::VersionUnsupported => 'The receiver supports protocol versions [' . implode(', ', $supported ?? [])
                . '], which this SDK does not speak. Update the SDK.',
            ErrorKind::Replaced => 'Another connection using this device key took over.',
            ErrorKind::SequenceConflict => 'Another program is writing with this device key at the same time.',
            ErrorKind::ProtocolViolation => "The receiver broke the protocol: {$detail}.",
            ErrorKind::TimedOut => 'The request was not confirmed in time. It may still have been applied.',
            default => $detail ?? $kind->value,
        };
    }
}
