<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\Noise\HandshakeState;
use Erdigo\Core\Noise\Pattern;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

/**
 * The official cacophony vectors in spec/vectors/noise, for the patterns that exercise what integration-v1 uses.
 */
final class NoiseVectorTest extends TestCase
{
    /** @return array<string, array{0: array<string, mixed>}> */
    public static function vectors(): array
    {
        $file = json_decode((string) file_get_contents(__DIR__ . '/../../../spec/vectors/noise/cacophony-subset.json'), true);
        $cases = [];
        foreach ($file['vectors'] as $vector) {
            $cases[$vector['protocol_name']] = [$vector];
        }
        return $cases;
    }

    private static function pattern(string $protocolName): Pattern
    {
        return match ($protocolName) {
            'Noise_IK_25519_ChaChaPoly_SHA256' => Pattern::ik(),
            'Noise_XXpsk3_25519_ChaChaPoly_SHA256' => new Pattern('XXpsk3', false, [
                [Pattern::E], [Pattern::E, Pattern::EE, Pattern::S, Pattern::ES], [Pattern::S, Pattern::SE, Pattern::PSK],
            ]),
            'Noise_NNpsk0_25519_ChaChaPoly_SHA256' => new Pattern('NNpsk0', false, [[Pattern::PSK, Pattern::E], [Pattern::E, Pattern::EE]]),
        };
    }

    private static function bytes(?string $hex): ?string
    {
        return $hex === null ? null : (string) hex2bin($hex);
    }

    /** @param array<string, mixed> $vector */
    #[DataProvider('vectors')]
    public function testVectorIsReproducedByteForByte(array $vector): void
    {
        $pattern = self::pattern($vector['protocol_name']);
        $this->assertSame($vector['protocol_name'], $pattern->protocolName());
        $initiator = new HandshakeState(
            $pattern,
            true,
            self::bytes($vector['init_prologue']),
            self::bytes($vector['init_static'] ?? null),
            self::bytes($vector['init_remote_static'] ?? null),
            array_map([self::class, 'bytes'], $vector['init_psks'] ?? []),
            self::bytes($vector['init_ephemeral']),
        );
        $responder = new HandshakeState(
            $pattern,
            false,
            self::bytes($vector['resp_prologue']),
            self::bytes($vector['resp_static'] ?? null),
            null,
            array_map([self::class, 'bytes'], $vector['resp_psks'] ?? []),
            self::bytes($vector['resp_ephemeral']),
        );
        $transports = null;
        foreach ($vector['messages'] as $index => $message) {
            $payload = self::bytes($message['payload']);
            $fromInitiator = $index % 2 === 0;
            if ($transports === null) {
                [$sender, $reader] = $fromInitiator ? [$initiator, $responder] : [$responder, $initiator];
                $written = $sender->writeMessage($payload);
                $this->assertSame($message['ciphertext'], bin2hex($written), "message {$index}");
                $this->assertSame($payload, $reader->readMessage($written));
                if ($initiator->isComplete() && $responder->isComplete()) {
                    $this->assertSame($vector['handshake_hash'], bin2hex($initiator->handshakeHash()));
                    $this->assertSame($vector['handshake_hash'], bin2hex($responder->handshakeHash()));
                    $transports = [$initiator->split(), $responder->split()];
                }
            } else {
                [$sender, $reader] = $fromInitiator ? $transports : array_reverse($transports);
                $sealed = $sender->seal($payload);
                $this->assertSame($message['ciphertext'], bin2hex($sealed), "message {$index}");
                $this->assertSame($payload, $reader->open($sealed));
            }
        }
        $this->assertNotNull($transports, 'the handshake finished');
    }
}
