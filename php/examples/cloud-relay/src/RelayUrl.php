<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\Cli\UsageException;

/**
 * The relay's address given to `gateway run --relay`: `https://`, or `http://` for a relay on this computer. It may have
 * a path, for a relay under a folder of a web site, and nothing else: no user name, query or fragment.
 */
final class RelayUrl
{
    private function __construct(
        public readonly bool $https,
        public readonly string $host,
        public readonly int $port,
        public readonly string $path,
        public readonly string $url,
    ) {
    }

    public static function parse(string $text): self
    {
        $pattern = "#^([A-Za-z]+)://(\\[[0-9A-Fa-f:.]+\\]|[A-Za-z0-9.-]+)(?::([0-9]{1,5}))?(/[A-Za-z0-9._~!$&'()*+,;=:@%/-]*)?$#D";
        if (preg_match($pattern, $text, $match) !== 1) {
            throw new UsageException('--relay must be an address such as https://relay.example.com, with no user name, query or fragment');
        }
        $scheme = strtolower($match[1]);
        if ($scheme !== 'https' && $scheme !== 'http') {
            throw new UsageException('--relay must start with https://');
        }
        $host = strtolower($match[2]);
        if (str_starts_with($host, '[')) {
            $host = substr($host, 1, -1);
            if (filter_var($host, FILTER_VALIDATE_IP, FILTER_FLAG_IPV6) === false) {
                throw new UsageException('--relay has an IPv6 address that is not valid');
            }
        }
        $https = $scheme === 'https';
        $defaultPort = $https ? 443 : 80;
        $port = ($match[3] ?? '') === '' ? $defaultPort : (int) $match[3];
        if ($port < 1 || $port > 65535) {
            throw new UsageException('--relay has a port that is not between 1 and 65535');
        }
        if (!$https && !self::isLoopback($host)) {
            throw new UsageException('--relay must start with https://. Plain http:// is accepted only for a relay on this computer (127.0.0.1, ::1 or localhost).');
        }
        $path = rtrim($match[4] ?? '', '/');
        $shownHost = str_contains($host, ':') ? "[{$host}]" : $host;
        $url = $scheme . '://' . $shownHost . ($port === $defaultPort ? '' : ':' . $port) . $path;
        return new self($https, $host, $port, $path, $url);
    }

    public static function isLoopback(string $host): bool
    {
        if ($host === 'localhost') {
            return true;
        }
        if (filter_var($host, FILTER_VALIDATE_IP, FILTER_FLAG_IPV4) !== false) {
            return str_starts_with($host, '127.');
        }
        return filter_var($host, FILTER_VALIDATE_IP, FILTER_FLAG_IPV6) !== false && inet_pton($host) === inet_pton('::1');
    }

    /** The host as a socket address takes it, with brackets around an IPv6 address. */
    public function socketHost(): string
    {
        return str_contains($this->host, ':') ? "[{$this->host}]" : $this->host;
    }

    public function hostHeader(): string
    {
        $default = $this->https ? 443 : 80;
        return $this->socketHost() . ($this->port === $default ? '' : ':' . $this->port);
    }
}
