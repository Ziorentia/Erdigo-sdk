<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\PairingCode;
use Erdigo\Core\PairingTicket;
use Erdigo\ErrorKind;
use Erdigo\InMemoryDeviceStore;
use Erdigo\Pairing;
use Erdigo\ReceiverTarget;
use Erdigo\Tests\Support\EndToEnd;
use Erdigo\Tests\Support\ScriptedReceiver;
use PHPUnit\Framework\TestCase;

final class PairingTest extends TestCase
{
    /** @param array<string, mixed> $pairing */
    private static function pairingReceiver(array $pairing = []): ScriptedReceiver
    {
        return new ScriptedReceiver(['pairing' => $pairing + ['decision' => 'approve']]);
    }

    private static function addressOf(ScriptedReceiver $receiver): ReceiverTarget
    {
        return ReceiverTarget::address('127.0.0.1', $receiver->port);
    }

    public function testTheQrTextPairsWithoutAskingAnyone(): void
    {
        $receiver = self::pairingReceiver(['kinds' => ['pin'], 'counter' => 1234]);
        $identity = (new InMemoryDeviceStore())->loadOrCreateIdentity();
        $asked = false;
        $pairing = Pairing::pair(
            self::addressOf($receiver),
            PairingTicket::parse($receiver->qrText()),
            $identity,
            "  Greenhouse\u{202E} sensor  ",
            [ItemKind::Track, ItemKind::Pin, ItemKind::Pin],
            'home',
            confirmKey: static function () use (&$asked): bool {
                $asked = true;
                return true;
            },
        );
        $this->assertFalse($asked);
        $this->assertSame('home', $pairing->label);
        $this->assertSame($receiver->publicKey, $pairing->receiverKey);
        $this->assertSame([ItemKind::Pin], $pairing->kinds);
        $this->assertSame(1234, $pairing->counterFloor);
        $this->assertSame('127.0.0.1', $pairing->lastHost);
        $this->assertSame($receiver->port, $pairing->lastPort);
        $request = $receiver->events('pair_request')[0];
        $this->assertSame('Greenhouse sensor', $request['name']);
        $this->assertSame(['pin', 'track'], $request['kinds']);
        $this->assertSame(Pairing::SDK_NAME, $request['sdk']);
        $this->assertSame(bin2hex($identity->publicKey), $request['device']);
        $this->assertTrue($request['canonical']);
    }

    public function testADefaultLabelComesFromTheFingerprint(): void
    {
        $receiver = self::pairingReceiver();
        $pairing = Pairing::pair(self::addressOf($receiver), PairingTicket::parse($receiver->qrText()), (new InMemoryDeviceStore())->loadOrCreateIdentity(), 'Sensor', [ItemKind::Pin]);
        $this->assertSame('receiver-' . substr($receiver->fingerprint, 0, 8), $pairing->label);
    }

    public function testAWrongFingerprintStopsBeforeTheReceiverHearsAboutTheDevice(): void
    {
        $receiver = self::pairingReceiver();
        $ticket = new PairingTicket(PairingTicket::parse($receiver->qrText())->code, str_repeat('0', 64));
        EndToEnd::expect($this, ErrorKind::FingerprintMismatch, static fn () => Pairing::pair(
            self::addressOf($receiver),
            $ticket,
            (new InMemoryDeviceStore())->loadOrCreateIdentity(),
            'Sensor',
            [ItemKind::Pin],
        ));
        $this->assertCount(1, $receiver->events('message2_sent'));
        $this->assertSame([], $receiver->events('pair_request'));
    }

    public function testATypedCodeNeedsSomeoneToCompareTheKey(): void
    {
        $receiver = self::pairingReceiver();
        $ticket = new PairingTicket(PairingTicket::parse($receiver->qrText())->code);
        EndToEnd::expect($this, ErrorKind::InvalidArgument, static fn () => Pairing::pair(
            self::addressOf($receiver),
            $ticket,
            (new InMemoryDeviceStore())->loadOrCreateIdentity(),
            'Sensor',
            [ItemKind::Pin],
        ));
        // Refused before anything reached the receiver.
        $this->assertSame([], $receiver->events());
    }

    public function testATypedCodeThatIsNotConfirmedTellsTheReceiverNothing(): void
    {
        $receiver = self::pairingReceiver();
        $ticket = new PairingTicket(PairingTicket::parse($receiver->qrText())->code);
        $shown = null;
        $confirm = static function (string $key) use (&$shown): bool {
            $shown = $key;
            return false;
        };
        EndToEnd::expect($this, ErrorKind::KeyNotConfirmed, static fn () => Pairing::pair(
            self::addressOf($receiver),
            $ticket,
            (new InMemoryDeviceStore())->loadOrCreateIdentity(),
            'Sensor',
            [ItemKind::Pin],
            confirmKey: $confirm,
        ));
        $this->assertSame(IntegrationV1::shortFingerprint($receiver->fingerprint), $shown);
        $this->assertCount(1, $receiver->events('message2_sent'));
        $this->assertSame([], $receiver->events('pair_request'));
    }

    public function testATypedCodeThatIsConfirmedPairs(): void
    {
        $receiver = self::pairingReceiver();
        $ticket = new PairingTicket(PairingTicket::parse($receiver->qrText())->code);
        $pairing = Pairing::pair(
            self::addressOf($receiver),
            $ticket,
            (new InMemoryDeviceStore())->loadOrCreateIdentity(),
            'Sensor',
            [ItemKind::Pin],
            confirmKey: static fn (string $key): bool => true,
        );
        $this->assertSame($receiver->publicKey, $pairing->receiverKey);
        // Once to find the key, and once to pair.
        $this->assertCount(2, $receiver->events('message2_sent'));
        $this->assertCount(1, $receiver->events('pair_request'));
    }

    public function testAnswersOtherThanApproval(): void
    {
        foreach (['deny' => ErrorKind::PairingDenied, 'code_reused' => ErrorKind::CodeReused] as $decision => $kind) {
            $receiver = self::pairingReceiver(['decision' => $decision]);
            EndToEnd::expect($this, $kind, static fn () => Pairing::pair(
                self::addressOf($receiver),
                PairingTicket::parse($receiver->qrText()),
                (new InMemoryDeviceStore())->loadOrCreateIdentity(),
                'Sensor',
                [ItemKind::Pin],
            ));
        }
    }

    public function testNobodyAnsweringIsATimeout(): void
    {
        $receiver = self::pairingReceiver(['decision' => 'silent']);
        EndToEnd::expect($this, ErrorKind::PairingTimedOut, static fn () => Pairing::pair(
            self::addressOf($receiver),
            PairingTicket::parse($receiver->qrText()),
            (new InMemoryDeviceStore())->loadOrCreateIdentity(),
            'Sensor',
            [ItemKind::Pin],
            approvalTimeout: 1.0,
        ));
    }

    public function testAWrongCodeGetsNoAnswer(): void
    {
        $receiver = self::pairingReceiver();
        $ticket = new PairingTicket(PairingCode::generate(), $receiver->fingerprint);
        EndToEnd::expect($this, ErrorKind::NoAnswer, static fn () => Pairing::pair(
            self::addressOf($receiver),
            $ticket,
            (new InMemoryDeviceStore())->loadOrCreateIdentity(),
            'Sensor',
            [ItemKind::Pin],
            handshakeTimeout: 2.0,
        ));
        $this->assertSame([], $receiver->events('message2_sent'));
    }

    public function testArgumentsAreCheckedFirst(): void
    {
        $identity = (new InMemoryDeviceStore())->loadOrCreateIdentity();
        $ticket = new PairingTicket(PairingCode::generate(), str_repeat('a', 64));
        $target = ReceiverTarget::address('127.0.0.1', 9);
        EndToEnd::expect($this, ErrorKind::InvalidArgument, static fn () => Pairing::pair($target, $ticket, $identity, " \u{202E} ", [ItemKind::Pin]));
        EndToEnd::expect($this, ErrorKind::InvalidArgument, static fn () => Pairing::pair($target, $ticket, $identity, 'Sensor', []));
        EndToEnd::expect($this, ErrorKind::InvalidArgument, static fn () => Pairing::pair($target, $ticket, $identity, 'Sensor', [ItemKind::Pin], "bad\nlabel"));
        EndToEnd::expect($this, ErrorKind::InvalidArgument, static fn () => ReceiverTarget::address('127.0.0.1', 0));
    }
}
