<?php

declare(strict_types=1);

namespace Erdigo;

use Erdigo\Core\Clock;

/**
 * Finds receivers that advertise `_erdigo-int._tcp` on the local network, with one-shot multicast DNS queries over
 * IPv4 (RFC 6762 section 5.1) sent from an ordinary port. Receivers answer such a query directly, so nothing here
 * needs port 5353 or a Bonjour or Avahi service.
 *
 * It needs PHP's `sockets` extension. Without it, nothing is found, and a receiver is reached by the address given
 * with {@see ReceiverTarget::address()} or remembered from an earlier connection.
 */
final class Discovery
{
    private function __construct()
    {
    }

    public static function isAvailable(): bool
    {
        return extension_loaded('sockets');
    }

    /**
     * Listens for receivers for a short time, and returns up to `$limit` of them as host and port.
     *
     * @return list<array{0: string, 1: int}>
     */
    public static function browse(float $duration = 2.0, int $limit = 8): array
    {
        if (!self::isAvailable()) {
            return [];
        }
        $found = new DiscoveredServices($limit);
        self::query([[MdnsWire::serviceName(), MdnsWire::TYPE_PTR]], $duration, $found);
        // Answers to the questions below only fill in what this first one found.
        $found->closeInstances();
        if ($found->instances() === []) {
            return [];
        }
        // A responder normally sends the service and its addresses along with the answer. Ask for whatever it did not.
        $missingServices = $found->instancesWithoutService();
        if ($missingServices !== []) {
            self::query(array_map(static fn (string $name): array => [$name, MdnsWire::TYPE_SRV], $missingServices), 1.0, $found);
        }
        $missingAddresses = $found->hostsWithoutAddress();
        if ($missingAddresses !== []) {
            self::query(array_map(static fn (string $name): array => [$name, MdnsWire::TYPE_A], $missingAddresses), 1.0, $found);
        }
        return $found->endpoints();
    }

    /**
     * Sends one query on every interface that can carry it, and hands `$found` the records in the answers that arrive
     * within `$window` seconds. Answers from beyond the local network are ignored.
     *
     * @param list<array{0: string, 1: int}> $questions
     */
    private static function query(array $questions, float $window, DiscoveredServices $found): void
    {
        $id = random_int(1, 65535);
        $query = MdnsWire::encodeQuery($id, $questions);
        $sockets = self::openSockets($query);
        if ($sockets === []) {
            return;
        }
        $started = Clock::now();
        // Asked again a little later, like a browser that keeps watching, so that a receiver that has only just
        // started advertising is found too.
        $resends = [$started + 0.25, $started + 0.75];
        try {
            while (($left = $started + $window - Clock::now()) > 0) {
                $wait = $resends === [] ? $left : max(0.0, min($left, $resends[0] - Clock::now()));
                $read = $sockets;
                $write = null;
                $except = null;
                $seconds = (int) floor($wait);
                $ready = @socket_select($read, $write, $except, $seconds, (int) (($wait - $seconds) * 1e6));
                if (is_int($ready) && $ready > 0) {
                    foreach ($read as $socket) {
                        self::receive($socket, $id, $found);
                    }
                }
                if ($resends !== [] && Clock::now() >= $resends[0]) {
                    array_shift($resends);
                    foreach ($sockets as $socket) {
                        @socket_sendto($socket, $query, strlen($query), 0, MdnsWire::MULTICAST_V4, MdnsWire::PORT);
                    }
                }
            }
        } finally {
            foreach ($sockets as $socket) {
                socket_close($socket);
            }
        }
    }

    private static function receive(\Socket $socket, int $id, DiscoveredServices $found): void
    {
        $buffer = '';
        $from = '';
        $port = 0;
        while (($length = @socket_recvfrom($socket, $buffer, 9000, 0, $from, $port)) !== false) {
            if ($length > 0 && $port === MdnsWire::PORT && LocalNetwork::isLocal($from)) {
                $message = MdnsWire::decode($buffer);
                if ($message !== null && $message['response'] && $message['id'] === $id) {
                    $found->add($message['records']);
                }
            }
        }
    }

    /**
     * Opens one socket per IPv4 address of each interface, and sends the query on each.
     *
     * @return list<\Socket>
     */
    private static function openSockets(string $query): array
    {
        $sockets = [];
        foreach (LocalNetwork::interfaceAddresses() as [$interface, $address]) {
            if (str_starts_with($address, '127.')) {
                continue;
            }
            $socket = @socket_create(AF_INET, SOCK_DGRAM, SOL_UDP);
            if ($socket === false) {
                continue;
            }
            // Bound to the interface's own address, so that the query leaves through it and the answer comes back to it.
            if (!@socket_bind($socket, $address, 0)) {
                socket_close($socket);
                continue;
            }
            @socket_set_option($socket, IPPROTO_IP, IP_MULTICAST_IF, $interface);
            @socket_set_option($socket, IPPROTO_IP, IP_MULTICAST_TTL, 255);
            socket_set_nonblock($socket);
            if (@socket_sendto($socket, $query, strlen($query), 0, MdnsWire::MULTICAST_V4, MdnsWire::PORT) === false) {
                socket_close($socket);
                continue;
            }
            $sockets[] = $socket;
        }
        return $sockets;
    }
}
