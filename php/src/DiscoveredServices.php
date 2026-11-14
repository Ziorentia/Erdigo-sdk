<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * What the answers to one browse have said, kept to what the browse can use. Each answer is read in order of
 * dependence: service instances, then their services, then the addresses of those services' hosts. However many
 * answers arrive, this holds at most `$limit` instances, one service for each, and a few addresses for each host.
 *
 * @internal
 */
final class DiscoveredServices
{
    public const ADDRESSES_PER_HOST = 4;

    /** @var list<string> */
    private array $instances = [];
    /** @var array<string, array{0: string, 1: int}> */
    private array $services = [];
    /** @var array<string, list<string>> */
    private array $addresses = [];
    private bool $instancesClosed = false;

    public function __construct(private readonly int $limit)
    {
    }

    private static function key(string $name): string
    {
        return Core\Hex::lowercase(rtrim($name, '.'));
    }

    /** @return list<string> */
    public function instances(): array
    {
        return $this->instances;
    }

    /** How many records are kept, for checking that this stays bounded. */
    public function keptRecords(): int
    {
        return count($this->instances) + count($this->services) + array_sum(array_map('count', $this->addresses));
    }

    /** @param list<array{name: string, type: int, ttl: int, target?: string, port?: int, address?: string}> $records */
    public function add(array $records): void
    {
        if (!$this->instancesClosed) {
            foreach ($records as $record) {
                if (count($this->instances) < $this->limit && $record['type'] === MdnsWire::TYPE_PTR
                    && MdnsWire::namesEqual($record['name'], MdnsWire::serviceName()) && isset($record['target'])
                    && $record['ttl'] > 0 && !in_array(self::key($record['target']), $this->instances, true)) {
                    $this->instances[] = self::key($record['target']);
                }
            }
        }
        foreach ($records as $record) {
            // The first usable service for an instance stands, as a resolver would keep the first it heard.
            $name = self::key($record['name']);
            if ($record['type'] === MdnsWire::TYPE_SRV && isset($record['target'], $record['port']) && $record['port'] !== 0
                && in_array($name, $this->instances, true) && !isset($this->services[$name])) {
                $this->services[$name] = [self::key($record['target']), $record['port']];
            }
        }
        $hosts = array_map(static fn (array $service): string => $service[0], $this->services);
        foreach ($records as $record) {
            $name = self::key($record['name']);
            if (!isset($record['address']) || !in_array($name, $hosts, true)) {
                continue;
            }
            $known = $this->addresses[$name] ?? [];
            if (!in_array($record['address'], $known, true) && count($known) < self::ADDRESSES_PER_HOST) {
                $known[] = $record['address'];
            }
            $this->addresses[$name] = $known;
        }
    }

    /** From now on no new instances are taken, only what completes the ones already found. */
    public function closeInstances(): void
    {
        $this->instancesClosed = true;
    }

    /** @return list<string> */
    public function instancesWithoutService(): array
    {
        return array_values(array_filter($this->instances, fn (string $instance): bool => !isset($this->services[$instance])));
    }

    /** @return list<string> */
    public function hostsWithoutAddress(): array
    {
        $hosts = array_unique(array_map(static fn (array $service): string => $service[0], array_values($this->services)));
        return array_values(array_filter($hosts, fn (string $host): bool => !isset($this->addresses[$host])));
    }

    /**
     * One address for each instance that has a service and an address, in the order the instances were found.
     *
     * @return list<array{0: string, 1: int}>
     */
    public function endpoints(): array
    {
        $endpoints = [];
        foreach ($this->instances as $instance) {
            $service = $this->services[$instance] ?? null;
            if ($service === null || !isset($this->addresses[$service[0]][0])) {
                continue;
            }
            $endpoints[] = [$this->addresses[$service[0]][0], $service[1]];
        }
        return $endpoints;
    }
}
