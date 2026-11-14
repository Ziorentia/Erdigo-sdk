<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Core\Coordinate;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\Message\Hello;
use Erdigo\Core\Message\PairRequest;
use Erdigo\Core\Message\PutPin;
use Erdigo\Core\Message\RequestEnvelope;
use Erdigo\Core\Message\Wire;
use Erdigo\Core\Noise\HandshakeState;
use Erdigo\Core\Noise\Noise;
use Erdigo\Core\Noise\Pattern;
use Erdigo\Core\PairingCode;
use Erdigo\Core\PairingTicket;
use Erdigo\Core\WireException;
use PHPUnit\Framework\Attributes\DataProvider;
use PHPUnit\Framework\TestCase;

/**
 * Every vector in spec/vectors/integration-v1: both handshakes byte for byte, from either side, and every message a
 * receiver must accept or reject.
 */
final class IntegrationVectorTest extends TestCase
{
    private const DIRECTORY = __DIR__ . '/../../../spec/vectors/integration-v1/';

    /** @return array<string, mixed> */
    private static function load(string $name): array
    {
        return json_decode((string) file_get_contents(self::DIRECTORY . $name), true, 64, JSON_THROW_ON_ERROR);
    }

    /** @return array<string, array{0: string}> */
    public static function handshakes(): array
    {
        return ['pairing' => ['handshake-pair.json'], 'connection' => ['handshake-connect.json']];
    }

    #[DataProvider('handshakes')]
    public function testHandshakeIsReproducedFromBothSides(string $name): void
    {
        $vector = self::load($name);
        $prologue = (string) hex2bin($vector['preamble_and_prologue']);
        $isPairing = $vector['protocol_name'] === 'Noise_XXpsk0_25519_ChaChaPoly_SHA256';
        $pattern = $isPairing ? Pattern::xxPsk0() : Pattern::ik();
        $this->assertSame($vector['protocol_name'], $pattern->protocolName());
        $this->assertSame(
            $vector['preamble_and_prologue'],
            bin2hex(IntegrationV1::preamble($isPairing ? IntegrationV1::MODE_PAIR : IntegrationV1::MODE_CONNECT)),
        );
        $psks = $isPairing ? [(string) hex2bin($vector['psk'])] : [];
        $device = new HandshakeState(
            $pattern,
            true,
            $prologue,
            (string) hex2bin($vector['device_static_private']),
            $isPairing ? null : (string) hex2bin($vector['receiver_static_public']),
            $psks,
            (string) hex2bin($vector['device_ephemeral_private']),
        );
        $receiver = new HandshakeState(
            $pattern,
            false,
            $prologue,
            (string) hex2bin($vector['receiver_static_private']),
            null,
            $psks,
            (string) hex2bin($vector['receiver_ephemeral_private']),
        );
        $this->assertSame($vector['device_static_public'], bin2hex(Noise::publicKey((string) hex2bin($vector['device_static_private']))));
        $this->assertSame($vector['receiver_static_public'], bin2hex(Noise::publicKey((string) hex2bin($vector['receiver_static_private']))));

        $transports = null;
        foreach ($vector['frames'] as $frame) {
            if ($frame['stage'] === 'preamble') {
                $this->assertSame($vector['preamble_and_prologue'], $frame['wire']);
                continue;
            }
            $payload = (string) hex2bin($frame['payload']);
            $fromDevice = $frame['direction'] === 'device_to_receiver';
            if ($transports === null) {
                [$sender, $reader] = $fromDevice ? [$device, $receiver] : [$receiver, $device];
                $message = $sender->writeMessage($payload);
                $this->assertSame($payload, $reader->readMessage($message));
                if ($device->isComplete()) {
                    $this->assertSame($vector['handshake_hash'], bin2hex($device->handshakeHash()));
                    $this->assertSame($vector['handshake_hash'], bin2hex($receiver->handshakeHash()));
                    $transports = [$device->split(), $receiver->split()];
                }
            } else {
                [$sender, $reader] = $fromDevice ? $transports : array_reverse($transports);
                $message = $sender->seal($payload);
                $this->assertSame($payload, $reader->open($message));
            }
            $this->assertSame($frame['noise_message'], bin2hex($message), $frame['stage']);
            $this->assertSame($frame['wire'], bin2hex(\Erdigo\Core\FrameStream::frame($message)), $frame['stage']);
        }
        $this->assertNotNull($transports);
        $this->assertSame($vector['receiver_static_public'], bin2hex((string) $device->remoteStaticKey()));
        $this->assertSame($vector['device_static_public'], bin2hex((string) $receiver->remoteStaticKey()));
    }

    public function testPairingCodeKeyAndQrText(): void
    {
        $vector = self::load('handshake-pair.json');
        $code = new PairingCode($vector['pairing_code_as_typed']);
        $this->assertSame($vector['pairing_code_canonical'], $code->canonical);
        $this->assertSame($vector['psk'], bin2hex($code->presharedKey()));
        $fingerprint = IntegrationV1::fingerprint((string) hex2bin($vector['receiver_static_public']));
        $ticket = PairingTicket::parse($vector['qr_text']);
        $this->assertSame($fingerprint, $ticket->fingerprint);
        $this->assertSame($vector['pairing_code_canonical'], $ticket->code->canonical);
        $this->assertSame($vector['qr_text'], (new PairingTicket($code, $fingerprint))->uri());
    }

    public function testDeviceMessagesAreWrittenExactlyAsPublished(): void
    {
        $pair = self::load('handshake-pair.json');
        $request = new PairRequest([1], 'erdigo-test/1.0', 'Test device', [ItemKind::Pin, ItemKind::Track]);
        $this->assertSame(self::text($pair, 'handshake_3'), Wire::encodePairRequest($request));

        $connect = self::load('handshake-connect.json');
        $this->assertSame(self::text($connect, 'hello'), Wire::encodeHello(new Hello([1], 'erdigo-test/1.0')));
        $pin = new RequestEnvelope(42, new PutPin('gate', new Coordinate(37.5796, 126.977), 'Palace gate'));
        $this->assertSame(self::text($connect, 'request'), Wire::encodeRequest($pin));
        $this->assertSame($connect['frames'][1]['payload'], bin2hex(pack('J', $connect['connect_counter'])));
        $this->assertSame(IntegrationV1::CONNECT_FIRST_MESSAGE_LENGTH, strlen((string) hex2bin($connect['frames'][1]['noise_message'])));
        $this->assertSame($connect['device_fingerprint'], IntegrationV1::fingerprint((string) hex2bin($connect['device_static_public'])));
        $this->assertSame($connect['receiver_fingerprint'], IntegrationV1::fingerprint((string) hex2bin($connect['receiver_static_public'])));
    }

    public function testReceiverMessagesDecodeAndEncodeBackToTheSameBytes(): void
    {
        foreach (['handshake-pair.json', 'handshake-connect.json'] as $name) {
            foreach (self::load($name)['frames'] as $frame) {
                if ($frame['direction'] === 'receiver_to_device' && ($frame['payload_text'] ?? '') !== '') {
                    $message = Wire::decodeReceiverMessage($frame['payload_text']);
                    $this->assertSame($frame['payload_text'], Wire::encodeReceiverMessage($message), $frame['stage']);
                }
            }
        }
    }

    /** @param array<string, mixed> $vector */
    private static function text(array $vector, string $stage): string
    {
        foreach ($vector['frames'] as $frame) {
            if ($frame['stage'] === $stage) {
                return $frame['payload_text'];
            }
        }
        throw new \LogicException("no stage {$stage}");
    }

    /** @return array<string, array{0: string}> */
    public static function validMessages(): array
    {
        $cases = [];
        foreach (self::load('messages.json')['valid'] as $entry) {
            $cases[$entry['name']] = [$entry['json']];
        }
        return $cases;
    }

    #[DataProvider('validMessages')]
    public function testValidMessageIsAcceptedAndWrittenBackTheSameWay(string $json): void
    {
        $envelope = Wire::decodeRequest($json);
        // Written back in the SDK's own field order, it must read the same again.
        $again = Wire::decodeRequest(Wire::encodeRequest($envelope));
        $this->assertEquals($envelope, $again);
        if (str_starts_with($json, '{"t":')) {
            // The SDKs write a whole number of degrees as 37.0, where a vector may have 37.
            $written = preg_replace('/(\d)\.0(?=[,}\]])/', '$1', Wire::encodeRequest($envelope));
            $this->assertSame(preg_replace('/(\d)\.0(?=[,}\]])/', '$1', $json), $written);
        }
    }

    /** @return array<string, array{0: string, 1: string}> */
    public static function invalidMessages(): array
    {
        $file = self::load('messages.json');
        $cases = [];
        foreach ($file['invalid'] as $entry) {
            $cases[$entry['name']] = [$entry['json'], $entry['code']];
        }
        foreach ($file['invalid_bytes'] as $entry) {
            $cases[$entry['name']] = [(string) hex2bin($entry['hex']), $entry['code']];
        }
        return $cases;
    }

    #[DataProvider('invalidMessages')]
    public function testInvalidMessageIsRejectedWithTheRequiredCode(string $data, string $code): void
    {
        try {
            Wire::decodeRequest($data);
        } catch (WireException $error) {
            $this->assertSame($code, $error->errorCode->value);
            return;
        }
        $this->fail('the message was accepted');
    }
}
