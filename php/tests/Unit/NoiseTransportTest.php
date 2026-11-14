<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\Noise\CipherState;
use Erdigo\Core\Noise\HandshakeState;
use Erdigo\Core\Noise\Noise;
use Erdigo\Core\Noise\NoiseException;
use Erdigo\Core\Noise\Pattern;
use Erdigo\Core\Noise\Transport;
use PHPUnit\Framework\TestCase;

final class NoiseTransportTest extends TestCase
{
    /** @return array{0: Transport, 1: Transport} */
    private static function connectedPair(): array
    {
        $device = Noise::newPrivateKey();
        $receiver = Noise::newPrivateKey();
        $initiator = HandshakeState::start(Pattern::ik(), true, 'EIN1' . "\x01", $device, Noise::publicKey($receiver));
        $responder = HandshakeState::start(Pattern::ik(), false, 'EIN1' . "\x01", $receiver);
        $responder->readMessage($initiator->writeMessage('counter!'));
        $initiator->readMessage($responder->writeMessage());
        return [$initiator->split(), $responder->split()];
    }

    private function expectNoise(string $error, callable $body): void
    {
        try {
            $body();
        } catch (NoiseException $caught) {
            $this->assertSame($error, $caught->error);
            return;
        }
        $this->fail("expected {$error}");
    }

    public function testMessagesTravelBothWaysInOrder(): void
    {
        [$device, $receiver] = self::connectedPair();
        for ($index = 0; $index < 5; $index++) {
            $this->assertSame("to receiver {$index}", $receiver->open($device->seal("to receiver {$index}")));
            $this->assertSame("to device {$index}", $device->open($receiver->seal("to device {$index}")));
        }
        $this->assertSame($device->handshakeHash, $receiver->handshakeHash);
    }

    public function testChangedReplayedOrReorderedMessagesFail(): void
    {
        [$device, $receiver] = self::connectedPair();
        $first = $device->seal('first');
        $second = $device->seal('second');
        $tampered = $first;
        $tampered[3] = chr(ord($tampered[3]) ^ 1);
        $this->expectNoise(NoiseException::DECRYPTION_FAILED, static fn () => $receiver->open($tampered));
        $this->expectNoise(NoiseException::DECRYPTION_FAILED, static fn () => $receiver->open($second));
        $this->assertSame('first', $receiver->open($first));
        $this->expectNoise(NoiseException::DECRYPTION_FAILED, static fn () => $receiver->open($first));
        $this->expectNoise(NoiseException::TRUNCATED_MESSAGE, static fn () => $receiver->open('short'));
    }

    public function testAClosedTransportRefusesInsteadOfSendingInTheClear(): void
    {
        [$device] = self::connectedPair();
        $device->close();
        $this->expectNoise(NoiseException::NONCE_EXHAUSTED, static fn () => $device->seal('secret'));
    }

    public function testCounterRunsOut(): void
    {
        $cipher = new CipherState();
        $cipher->initialize(str_repeat("\x07", 32), PHP_INT_MAX - 1);
        $cipher->encrypt('', 'last');
        $this->expectNoise(NoiseException::NONCE_EXHAUSTED, static fn () => $cipher->encrypt('', 'one too many'));
    }

    public function testOversizedMessagesAreRefused(): void
    {
        [$device, $receiver] = self::connectedPair();
        $this->expectNoise(NoiseException::MESSAGE_TOO_LARGE, static fn () => $device->seal(str_repeat('a', 65520)));
        $device->seal(str_repeat('a', 65519));
        $this->expectNoise(NoiseException::MESSAGE_TOO_LARGE, static fn () => $receiver->open(str_repeat('a', 65536)));
    }

    public function testAHandshakeSplitsOnlyOnce(): void
    {
        $device = Noise::newPrivateKey();
        $receiver = Noise::newPrivateKey();
        $initiator = HandshakeState::start(Pattern::ik(), true, '', $device, Noise::publicKey($receiver));
        $responder = HandshakeState::start(Pattern::ik(), false, '', $receiver);
        $this->expectNoise(NoiseException::WRONG_HANDSHAKE_STATE, static fn () => $initiator->split());
        $this->expectNoise(NoiseException::WRONG_HANDSHAKE_STATE, static fn () => $responder->writeMessage());
        $responder->readMessage($initiator->writeMessage());
        $initiator->readMessage($responder->writeMessage());
        $initiator->split();
        $this->expectNoise(NoiseException::ALREADY_SPLIT, static fn () => $initiator->split());
        $this->expectNoise(NoiseException::WRONG_HANDSHAKE_STATE, static fn () => $initiator->writeMessage());
    }

    /** @return list<string> Public keys of small order, which give an all-zero shared secret. */
    private static function lowOrderPoints(): array
    {
        return [
            str_repeat("\0", 32),
            "\x01" . str_repeat("\0", 31),
            (string) hex2bin('e0eb7a7c3b41b8ae1656e3faf19fc46ada098deb9c32b1fd866205165f49b800'),
            (string) hex2bin('5f9c95bca3508c24b1d0b1559c83ef5b04445cc4581c8e86d8224eddd09f1157'),
        ];
    }

    public function testLowOrderPublicKeysAreRefused(): void
    {
        foreach (self::lowOrderPoints() as $point) {
            $this->expectNoise(NoiseException::WEAK_SHARED_SECRET, static fn () => Noise::dh(Noise::newPrivateKey(), $point));
        }
        // A receiver that sends a low-order ephemeral key ends the handshake.
        $device = Noise::newPrivateKey();
        $receiver = Noise::newPrivateKey();
        $initiator = HandshakeState::start(Pattern::ik(), true, '', $device, Noise::publicKey($receiver));
        $initiator->writeMessage();
        $this->expectNoise(NoiseException::WEAK_SHARED_SECRET, static fn () => $initiator->readMessage(str_repeat("\0", 32) . str_repeat("\0", 16)));
    }

    public function testOnlyCanonicalPublicKeysAreAccepted(): void
    {
        $this->assertTrue(Noise::isCanonicalPublicKey(Noise::publicKey(Noise::newPrivateKey())));
        $this->assertTrue(Noise::isCanonicalPublicKey("\xEC" . str_repeat("\xFF", 30) . "\x7F"));
        $this->assertFalse(Noise::isCanonicalPublicKey("\xED" . str_repeat("\xFF", 30) . "\x7F"));
        $this->assertFalse(Noise::isCanonicalPublicKey(str_repeat("\xFF", 32)));
        $this->assertFalse(Noise::isCanonicalPublicKey(str_repeat("\0", 31) . "\x80"));
        $this->assertFalse(Noise::isCanonicalPublicKey(str_repeat("\0", 31)));
    }

    public function testKeysOfTheWrongLengthAreRefused(): void
    {
        $this->expectNoise(NoiseException::INVALID_KEY, static fn () => Noise::publicKey('short'));
        $this->expectNoise(NoiseException::INVALID_KEY, static fn () => Noise::dh(Noise::newPrivateKey(), 'short'));
        $this->expectNoise(NoiseException::MISSING_KEY, static fn () => HandshakeState::start(Pattern::xxPsk0(), true, '', Noise::newPrivateKey()));
        $this->expectNoise(NoiseException::INVALID_KEY, static fn () => HandshakeState::start(Pattern::xxPsk0(), true, '', Noise::newPrivateKey(), null, 'short'));
    }

    public function testAWrongPairingCodeGivesNoAnswerTheDeviceCanRead(): void
    {
        $prologue = "EIN1\x02";
        $device = HandshakeState::start(Pattern::xxPsk0(), true, $prologue, Noise::newPrivateKey(), null, str_repeat('a', 32));
        $receiver = HandshakeState::start(Pattern::xxPsk0(), false, $prologue, Noise::newPrivateKey(), null, str_repeat('b', 32));
        $this->expectNoise(NoiseException::DECRYPTION_FAILED, static fn () => $receiver->readMessage($device->writeMessage()));
    }
}
