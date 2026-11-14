<?php

declare(strict_types=1);

namespace Erdigo;

use Erdigo\Core\FrameStream;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\Message\PairRequest;
use Erdigo\Core\Message\PairStatus;
use Erdigo\Core\Message\ReceiverError;
use Erdigo\Core\Message\Wire;
use Erdigo\Core\Noise\HandshakeState;
use Erdigo\Core\Noise\Noise;
use Erdigo\Core\Noise\Pattern;
use Erdigo\Core\PairingCode;
use Erdigo\Core\PairingTicket;
use Erdigo\Core\TextRules;
use Erdigo\Core\WireException;
use Erdigo\Core\ErrorCode;
use Erdigo\Core\FrameStreamException;

/**
 * Pairs this device with a receiver, using the one-time code a person reads from the receiver's screen.
 */
final class Pairing
{
    public const SDK_NAME = 'erdigo-php/0.1.0';

    private function __construct()
    {
    }

    /**
     * Pairs with a receiver and returns what to store about it.
     *
     * A ticket with a fingerprint, from the QR text or given separately, is compared here, and pairing stops on a
     * mismatch before the receiver hears anything about this device. For a typed code, `$confirmKey` runs before the
     * receiver hears anything about this device, so the receiver still shows its key. If it returns false, this throws
     * {@see ErrorKind::KeyNotConfirmed}, the code stays valid, and the receiver has seen neither this device's key nor
     * its name. If it returns true, the key the person confirmed is compared as if it came from a QR code.
     *
     * @param ReceiverTarget $target Where to find the receiver.
     * @param PairingTicket $ticket The code, and the receiver's key fingerprint when it comes from the QR text.
     * @param DeviceIdentity $identity This device's key.
     * @param string $deviceName Shown to the person who confirms the request. Treated as untrusted by the receiver.
     * @param list<ItemKind> $kinds What this device wants to send. The person may allow fewer.
     * @param string|null $label A local name for the receiver. Defaults to the start of its fingerprint.
     * @param float $handshakeTimeout How long a handshake may take, in seconds.
     * @param float $approvalTimeout How long to wait for the person to confirm, in seconds.
     * @param (callable(string): bool)|null $confirmKey Shows a person the receiver's key, in the form the receiver shows
     *     it (the first 16 digits of its fingerprint), and returns whether it matches the key on the receiver's screen.
     *     Needed for a typed code, because a code proves only that the other side knows it, and not called for any
     *     other ticket. The program that supplies it is responsible for a person answering it.
     */
    public static function pair(
        ReceiverTarget $target,
        PairingTicket $ticket,
        DeviceIdentity $identity,
        string $deviceName,
        array $kinds,
        ?string $label = null,
        float $handshakeTimeout = 5.0,
        float $approvalTimeout = 120.0,
        ?callable $confirmKey = null,
    ): ReceiverPairing {
        $name = TextRules::sanitizeDeviceName($deviceName);
        if ($name === '') {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'the device name cannot be empty');
        }
        if ($kinds === []) {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'choose at least one item kind');
        }
        foreach ($kinds as $kind) {
            if (!$kind instanceof ItemKind) {
                throw new ErdigoException(ErrorKind::InvalidArgument, 'item kinds are ItemKind values');
            }
        }
        if ($label !== null) {
            self::validateLabel($label);
        }

        if ($ticket->fingerprint === null) {
            if ($confirmKey === null) {
                throw new ErdigoException(
                    ErrorKind::InvalidArgument,
                    "a typed code needs confirmKey, because a person has to compare the receiver's key",
                );
            }
            $found = self::receiverFingerprint($target, $ticket->code, $handshakeTimeout);
            if ($confirmKey(IntegrationV1::shortFingerprint($found)) !== true) {
                throw new ErdigoException(ErrorKind::KeyNotConfirmed);
            }
            $ticket = new PairingTicket($ticket->code, $found);
        }

        $request = new PairRequest([IntegrationV1::VERSION], self::SDK_NAME, $name, ItemKind::canonicalList($kinds));
        $lastError = new ErdigoException(ErrorKind::NoAnswer);
        foreach ($target->pairingCandidates() as [$host, $port]) {
            try {
                return self::attempt($host, $port, $ticket, $identity, $request, $label, $handshakeTimeout, $approvalTimeout);
            } catch (ErdigoException $error) {
                if ($error->kind === ErrorKind::NoAnswer) {
                    continue;
                }
                // A definite answer from a receiver holding this code ends the search.
                $lastError = $error;
                break;
            }
        }
        throw $lastError;
    }

    /** @internal */
    public static function validateLabel(string $label): void
    {
        try {
            TextRules::validateTitle($label);
        } catch (WireException) {
            throw new ErdigoException(
                ErrorKind::InvalidArgument,
                'the receiver label cannot be empty or contain control characters',
            );
        }
    }

    /**
     * Finds the key of the receiver that holds this code, and stops there, so that a person can compare it. This runs
     * the exchange up to message 2, as spec section 4.1 describes, and closes the connection.
     */
    private static function receiverFingerprint(ReceiverTarget $target, PairingCode $code, float $handshakeTimeout): string
    {
        // The handshake needs a key, but uses it only in message 3, which is never sent here.
        $unused = DeviceIdentity::generate();
        $lastError = new ErdigoException(ErrorKind::NoAnswer);
        try {
            foreach ($target->pairingCandidates() as [$host, $port]) {
                try {
                    [$stream, $handshake, $receiverKey] = self::readReceiverKey($host, $port, $code, $unused, $handshakeTimeout);
                    $handshake->clear();
                    $stream->close();
                    return IntegrationV1::fingerprint($receiverKey);
                } catch (ErdigoException $error) {
                    if ($error->kind === ErrorKind::NoAnswer) {
                        continue;
                    }
                    $lastError = $error;
                    break;
                }
            }
        } finally {
            $unused->clear();
        }
        throw $lastError;
    }

    /**
     * Runs the handshake up to the receiver's answer. This device's key and name have not been sent yet.
     *
     * @return array{0: FrameStream, 1: HandshakeState, 2: string}
     */
    private static function readReceiverKey(string $host, int $port, PairingCode $code, DeviceIdentity $identity, float $handshakeTimeout): array
    {
        $prologue = IntegrationV1::preamble(IntegrationV1::MODE_PAIR);
        $psk = $code->presharedKey();
        try {
            $stream = FrameStream::connect($host, $port, $handshakeTimeout);
            $handshake = HandshakeState::start(Pattern::xxPsk0(), true, $prologue, $identity->privateKey(), null, $psk);
            $first = $handshake->writeMessage();
            $stream->armDeadline($handshakeTimeout);
            $stream->send($prologue . FrameStream::frame($first));
        } catch (FrameStreamException $error) {
            throw new ErdigoException(ErrorKind::NoAnswer, previous: $error);
        } finally {
            sodium_memzero($psk);
        }
        try {
            $second = $stream->readFrame(IntegrationV1::MAX_HANDSHAKE_FRAME_LENGTH);
            // Reading this message proves the receiver knows the code too.
            $handshake->readMessage($second);
        } catch (\Throwable $error) {
            $handshake->clear();
            $stream->close();
            throw new ErdigoException(ErrorKind::NoAnswer, previous: $error);
        }
        $receiverKey = $handshake->remoteStaticKey();
        if ($receiverKey === null || !Noise::isCanonicalPublicKey($receiverKey)) {
            $handshake->clear();
            $stream->close();
            throw new ErdigoException(ErrorKind::ProtocolViolation, 'the receiver sent an unusable key');
        }
        return [$stream, $handshake, $receiverKey];
    }

    private static function attempt(
        string $host,
        int $port,
        PairingTicket $ticket,
        DeviceIdentity $identity,
        PairRequest $request,
        ?string $label,
        float $handshakeTimeout,
        float $approvalTimeout,
    ): ReceiverPairing {
        [$stream, $handshake, $receiverKey] = self::readReceiverKey($host, $port, $ticket->code, $identity, $handshakeTimeout);
        try {
            $fingerprint = IntegrationV1::fingerprint($receiverKey);
            if ($ticket->fingerprint !== null && !hash_equals($ticket->fingerprint, $fingerprint)) {
                throw new ErdigoException(ErrorKind::FingerprintMismatch);
            }
            try {
                $third = $handshake->writeMessage(Wire::encodePairRequest($request));
                $stream->sendFrame($third);
                $transport = $handshake->split();
            } catch (\Throwable $error) {
                throw new ErdigoException(ErrorKind::NoAnswer, previous: $error);
            }
            try {
                // The person now decides, which can take a while.
                $stream->armDeadline($approvalTimeout);
                while (true) {
                    try {
                        $message = Wire::decodeReceiverMessage($transport->open($stream->readFrame()));
                    } catch (FrameStreamException $error) {
                        if ($error->failure === FrameStreamException::TIMED_OUT) {
                            throw new ErdigoException(ErrorKind::PairingTimedOut, previous: $error);
                        }
                        throw new ErdigoException(ErrorKind::NoAnswer, previous: $error);
                    } catch (WireException $error) {
                        throw new ErdigoException(ErrorKind::ProtocolViolation, $error->detail, previous: $error);
                    } catch (ErdigoException $error) {
                        throw $error;
                    } catch (\Throwable $error) {
                        throw new ErdigoException(ErrorKind::NoAnswer, previous: $error);
                    }
                    if ($message instanceof PairStatus) {
                        if ($message->status === PairStatus::PENDING) {
                            continue;
                        }
                        if ($message->status === PairStatus::DENIED) {
                            throw new ErdigoException(ErrorKind::PairingDenied);
                        }
                        $address = $stream->remoteAddress();
                        return new ReceiverPairing(
                            $label ?? 'receiver-' . substr($fingerprint, 0, 8),
                            $receiverKey,
                            $message->kinds,
                            $address[0] ?? null,
                            $address[1] ?? null,
                            null,
                            $message->counter,
                        );
                    }
                    if ($message instanceof ReceiverError) {
                        if ($message->is(ErrorCode::CodeReused)) {
                            throw new ErdigoException(ErrorKind::CodeReused, receiverError: $message);
                        }
                        if ($message->is(ErrorCode::VersionUnsupported)) {
                            throw new ErdigoException(ErrorKind::VersionUnsupported, receiverError: $message, supported: $message->supported ?? []);
                        }
                        throw new ErdigoException(ErrorKind::Rejected, receiverError: $message);
                    }
                    throw new ErdigoException(ErrorKind::ProtocolViolation, 'unexpected message during pairing');
                }
            } finally {
                $transport->close();
            }
        } finally {
            $handshake->clear();
            $stream->close();
        }
    }
}
