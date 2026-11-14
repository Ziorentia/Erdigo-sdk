<?php

declare(strict_types=1);

namespace Erdigo\Tests\Unit;

use Erdigo\DiscoveredServices;
use Erdigo\LocalNetwork;
use Erdigo\MdnsWire;
use PHPUnit\Framework\TestCase;

final class DiscoveryTest extends TestCase
{
    private static function name(string $name): string
    {
        $output = '';
        foreach (explode('.', $name) as $label) {
            $output .= chr(strlen($label)) . $label;
        }
        return $output . "\0";
    }

    private static function record(string $name, int $type, string $data, int $ttl = 120): string
    {
        return self::name($name) . pack('nnNn', $type, 0x8001, $ttl, strlen($data)) . $data;
    }

    /** @param list<string> $records */
    private static function response(int $id, array $records): string
    {
        return pack('nnnnnn', $id, 0x8400, 0, count($records), 0, 0) . implode('', $records);
    }

    public function testTheQueryAsksForTheService(): void
    {
        $query = MdnsWire::encodeQuery(0x1234, [[MdnsWire::serviceName(), MdnsWire::TYPE_PTR]]);
        $this->assertSame(pack('nnnnnn', 0x1234, 0, 1, 0, 0, 0) . self::name('_erdigo-int._tcp.local') . pack('nn', 12, 1), $query);
    }

    public function testAnAnswerLeadsToAnEndpoint(): void
    {
        $instance = 'a1b2c3._erdigo-int._tcp.local';
        $message = MdnsWire::decode(self::response(7, [
            self::record('_erdigo-int._tcp.local', MdnsWire::TYPE_PTR, self::name($instance)),
            self::record($instance, MdnsWire::TYPE_SRV, pack('nnn', 0, 0, 52345) . self::name('host-x.local')),
            self::record('host-x.local', MdnsWire::TYPE_A, (string) inet_pton('192.168.1.20')),
            self::record('other.local', MdnsWire::TYPE_A, (string) inet_pton('192.168.1.99')),
        ]));
        $this->assertNotNull($message);
        $this->assertSame(7, $message['id']);
        $this->assertTrue($message['response']);
        $found = new DiscoveredServices(8);
        $found->add($message['records']);
        $this->assertSame([['192.168.1.20', 52345]], $found->endpoints());
        $this->assertSame([], $found->instancesWithoutService());
        $this->assertSame([], $found->hostsWithoutAddress());
    }

    public function testCompressedNamesAreFollowedOnlyBackwards(): void
    {
        $header = pack('nnnnnn', 1, 0x8400, 0, 1, 0, 0);
        // A PTR record whose target points back at its own name.
        $good = $header . self::name('_erdigo-int._tcp.local') . pack('nnNn', 12, 1, 120, 9) . "\x01a" . "\x01b\xC0\x0C" . "\0";
        $this->assertNull(MdnsWire::decode($good), 'a target that runs past its record');
        $pointer = $header . self::name('_erdigo-int._tcp.local') . pack('nnNn', 12, 1, 120, 4) . "\x01a\xC0\x0C";
        $decoded = MdnsWire::decode($pointer);
        $this->assertNotNull($decoded);
        $this->assertSame('a._erdigo-int._tcp.local', $decoded['records'][0]['target']);
        // A pointer to itself, or forwards, would loop.
        $loop = $header . "\xC0\x0C" . pack('nnNn', 12, 1, 120, 2) . "\xC0\x0C";
        $this->assertNull(MdnsWire::decode($loop));
    }

    public function testMalformedMessagesAreRefused(): void
    {
        $this->assertNull(MdnsWire::decode('short'));
        $this->assertNull(MdnsWire::decode(str_repeat("\0", 9001)));
        $this->assertNull(MdnsWire::decode(pack('nnnnnn', 1, 0x8400, 0, 1, 0, 0) . "\x05ab"));
        $this->assertNull(MdnsWire::decode(pack('nnnnnn', 1, 0x8403, 0, 0, 0, 0)), 'an error response');
        $long = pack('nnnnnn', 1, 0x8400, 0, 1, 0, 0) . str_repeat("\x3F" . str_repeat('a', 63), 5) . "\0" . pack('nnNn', 1, 1, 1, 4) . "\1\2\3\4";
        $this->assertNull(MdnsWire::decode($long), 'a name over 255 bytes');
    }

    public function testWhatIsKeptStaysBounded(): void
    {
        $found = new DiscoveredServices(8);
        for ($round = 0; $round < 50; $round++) {
            $records = [];
            for ($index = 0; $index < 20; $index++) {
                $instance = "i{$round}x{$index}._erdigo-int._tcp.local";
                $records[] = ['name' => '_erdigo-int._tcp.local', 'type' => MdnsWire::TYPE_PTR, 'ttl' => 120, 'target' => $instance];
                $records[] = ['name' => $instance, 'type' => MdnsWire::TYPE_SRV, 'ttl' => 120, 'target' => "h{$index}.local", 'port' => 1000 + $index];
                $records[] = ['name' => "h{$index}.local", 'type' => MdnsWire::TYPE_A, 'ttl' => 120, 'address' => "10.0.{$round}.{$index}"];
            }
            $found->add($records);
        }
        $this->assertCount(8, $found->instances());
        $this->assertLessThan(8 + 8 + 8 * DiscoveredServices::ADDRESSES_PER_HOST + 1, $found->keptRecords());
        // A goodbye (time to live 0) adds nothing.
        $other = new DiscoveredServices(8);
        $other->add([['name' => '_erdigo-int._tcp.local', 'type' => MdnsWire::TYPE_PTR, 'ttl' => 0, 'target' => 'x._erdigo-int._tcp.local']]);
        $this->assertSame([], $other->instances());
    }

    public function testLoopbackAndLinkLocalAreLocal(): void
    {
        $this->assertTrue(LocalNetwork::isLocal('127.0.0.1'));
        $this->assertTrue(LocalNetwork::isLocal('169.254.10.20'));
        $this->assertFalse(LocalNetwork::isLocal('8.8.8.8'));
        $this->assertFalse(LocalNetwork::isLocal('not an address'));
    }
}
