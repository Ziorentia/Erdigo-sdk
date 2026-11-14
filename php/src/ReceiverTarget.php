<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * Where to look for a receiver.
 */
final class ReceiverTarget
{
    private function __construct(public readonly ?string $host, public readonly ?int $port)
    {
    }

    /**
     * Try the remembered address, then up to eight receivers advertised on the local network, which needs PHP's
     * `sockets` extension. A receiver this device is not paired with stays silent. An advertisement proves nothing
     * about who is behind it, so use {@see self::address()} where that matters. See the threat model.
     */
    public static function discover(): self
    {
        return new self(null, null);
    }

    /** Try this address only. The Erdigo app keeps its port, so an address found once keeps working. */
    public static function address(string $host, int $port): self
    {
        if ($host === '') {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'the host cannot be empty');
        }
        if ($port < 1 || $port > 65535) {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'the port must be between 1 and 65535');
        }
        return new self($host, $port);
    }

    public function isDiscover(): bool
    {
        return $this->host === null;
    }

    /**
     * The address given or remembered, which can be tried without looking around the network.
     *
     * @return list<array{0: string, 1: int}>
     */
    public function knownEndpoints(?ReceiverPairing $pairing): array
    {
        if ($this->host !== null && $this->port !== null) {
            return [[$this->host, $this->port]];
        }
        if ($pairing !== null && $pairing->lastHost !== null && $pairing->lastPort !== null) {
            return [[$pairing->lastHost, $pairing->lastPort]];
        }
        return [];
    }

    /**
     * Every endpoint to try when pairing, where no address is remembered.
     *
     * @return list<array{0: string, 1: int}>
     */
    public function pairingCandidates(): array
    {
        return $this->isDiscover() ? Discovery::browse() : $this->knownEndpoints(null);
    }
}
