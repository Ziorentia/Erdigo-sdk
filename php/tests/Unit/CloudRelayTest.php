<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\Cli\UsageException;
use Erdigo\Examples\CloudRelay\Operations;
use Erdigo\Examples\CloudRelay\Refusal;
use Erdigo\Examples\CloudRelay\Relay;
use Erdigo\Examples\CloudRelay\RelayConfig;
use Erdigo\Examples\CloudRelay\RelayUrl;
use PHPUnit\Framework\TestCase;

require_once __DIR__ . '/../../examples/cloud-relay/src/autoload.php';

/**
 * The rules of the cloud relay example, in this process, on every system CI runs. tests/cloud-relay-check.php checks
 * the relay over HTTP, and tests/cloud-relay-smoke.sh the relay and the gateway with a mock receiver.
 */
final class CloudRelayTest extends TestCase
{
    private static function cases(): object
    {
        $text = (string) file_get_contents(__DIR__ . '/../../../docs/cloud-relay-cases.json');
        return json_decode($text, false, 512, JSON_THROW_ON_ERROR);
    }

    private static function batch(array $ops): string
    {
        return json_encode(['ops' => $ops], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_PRESERVE_ZERO_FRACTION | JSON_THROW_ON_ERROR);
    }

    /** @return array{0: int, 1: array<string, mixed>}|null */
    private static function refusal(string $body): ?array
    {
        try {
            Operations::parseBatch($body);
            return null;
        } catch (Refusal $refusal) {
            return [$refusal->status, $refusal->body];
        }
    }

    public function testEveryValidOperationInTheSharedCasesIsTaken(): void
    {
        foreach (self::cases()->valid_ops as $case) {
            $this->assertNull(self::refusal(self::batch([$case->op])), $case->name);
        }
    }

    public function testEveryInvalidOperationInTheSharedCasesIsRefusedAtItsIndex(): void
    {
        foreach (self::cases()->invalid_ops as $case) {
            $refusal = self::refusal(self::batch([$case->op]));
            $this->assertNotNull($refusal, $case->name);
            $this->assertSame(400, $refusal[0], $case->name);
            $this->assertSame('invalid_op', $refusal[1]['error'], $case->name);
            $this->assertSame(0, $refusal[1]['index'], $case->name);
        }
    }

    public function testEveryRawBodyInTheSharedCasesIsRefusedWithAnErrorItLists(): void
    {
        foreach (self::cases()->raw_bodies as $case) {
            $body = isset($case->body_base64) ? base64_decode($case->body_base64, true) : $case->body;
            $refusal = self::refusal($body);
            $this->assertNotNull($refusal, $case->name);
            $this->assertSame(400, $refusal[0], $case->name);
            $this->assertContains($refusal[1]['error'], $case->errors, $case->name);
        }
    }

    public function testTheLimitsOnABatchAndAPhoto(): void
    {
        $pin = ['op' => 'pin', 'id' => 'p1', 'lat' => 37.5663, 'lon' => 126.9779];
        $this->assertNull(self::refusal(self::batch(array_fill(0, 100, $pin))));
        $tooMany = self::refusal(self::batch(array_fill(0, 101, $pin)));
        $this->assertSame(400, $tooMany[0] ?? null);
        $this->assertSame('invalid_body', $tooMany[1]['error'] ?? null);
        $photo = ['op' => 'photo', 'id' => 'ph', 'lat' => 0, 'lon' => 0, 'type' => 'image/jpeg'];
        $this->assertNull(self::refusal(self::batch([$photo + ['data' => base64_encode("\xFF\xD8\xFF" . str_repeat("\0", 1048573))]])));
        $over = self::refusal(self::batch([$photo + ['data' => base64_encode("\xFF\xD8\xFF" . str_repeat("\0", 1048574))]]));
        $this->assertSame('invalid_op', $over[1]['error'] ?? null);
        $third = self::refusal(self::batch([$pin, $pin, ['op' => 'pin', 'id' => 'p1', 'lat' => 0, 'lon' => 181], $pin]));
        $this->assertSame(2, $third[1]['index'] ?? null);
    }

    public function testTheQueryIsReadStrictly(): void
    {
        $this->assertSame(['epoch' => 'AAAAAAAAAAAAAAAA', 'after' => 5, 'limit' => 100, 'wait' => 25], Relay::parseQuery('epoch=AAAAAAAAAAAAAAAA&after=5&limit=100&wait=25'));
        $this->assertSame([], Relay::parseQuery(''));
        foreach (['after=1&after=2', 'since=1', 'limit=0', 'limit=101', 'wait=31', 'after=-1', 'after=01', 'after=1.0', 'epoch=short', 'limit', 'after=9007199254740992'] as $query) {
            try {
                Relay::parseQuery($query);
                $this->fail("accepted {$query}");
            } catch (Refusal $refusal) {
                $this->assertSame(['error' => 'invalid_query'], $refusal->body);
            }
        }
    }

    public function testOnlyJsonIsTaken(): void
    {
        foreach (['application/json', 'application/json; charset=utf-8', 'APPLICATION/JSON;charset=UTF-8'] as $type) {
            $this->assertTrue(Relay::isJson($type), $type);
        }
        foreach (['', 'text/plain', 'application/json; charset=latin1', 'application/json; x=y', 'application/jsonx'] as $type) {
            $this->assertFalse(Relay::isJson($type), $type);
        }
    }

    public function testThePhpSettingsTheRelayChecksAreReadAsPhpReadsThem(): void
    {
        foreach (['', '0', 'Off', 'off'] as $setting) {
            $this->assertFalse(Relay::displaysErrors($setting), $setting);
        }
        foreach (['1', 'On', 'yes', 'stdout', 'STDERR', '2'] as $setting) {
            $this->assertTrue(Relay::displaysErrors($setting), $setting);
        }
        $this->assertSame(2097152, RelayConfig::bytesSetting('2M'));
        $this->assertSame(8388608, RelayConfig::bytesSetting(' 8m '));
        $this->assertSame(1048576, RelayConfig::bytesSetting('1024K'));
        $this->assertSame(2097151, RelayConfig::bytesSetting('2097151'));
        $this->assertSame(0, RelayConfig::bytesSetting('0'));
        $this->assertSame(0, RelayConfig::bytesSetting('lots'));
    }

    public function testTheGatewayTakesHttpsOrARelayOnThisComputer(): void
    {
        $this->assertSame('https://relay.example.com', RelayUrl::parse('https://relay.example.com/')->url);
        $this->assertSame('https://relay.example.com:8443/erdigo', RelayUrl::parse('HTTPS://Relay.Example.com:8443/erdigo/')->url);
        $this->assertSame('http://127.0.0.1:8080', RelayUrl::parse('http://127.0.0.1:8080')->url);
        $this->assertSame('http://[::1]:8080', RelayUrl::parse('http://[::1]:8080')->url);
        $this->assertSame('http://localhost:8080', RelayUrl::parse('http://localhost:8080')->url);
        foreach (['http://relay.example.com', 'http://192.168.1.20:8080', 'ftp://relay.example.com', 'https://user:pass@relay.example.com',
            'https://relay.example.com/?a=1', 'https://relay.example.com/#a', 'relay.example.com', 'https://[zz]:1', 'https://relay.example.com:0'] as $url) {
            try {
                RelayUrl::parse($url);
                $this->fail("accepted {$url}");
            } catch (UsageException) {
                $this->assertTrue(true);
            }
        }
    }
}
