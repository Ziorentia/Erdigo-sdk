<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * One `GET` to the relay over a socket of its own. PHP's `http://` stream wrapper would follow redirects, which could
 * carry the token elsewhere or to plain HTTP, and blocks signals until the whole answer has come, so it is not used.
 * HTTPS uses PHP's openssl extension, which checks the relay's certificate and name against the system's trusted
 * certificates.
 */
final class RelayHttp
{
    private const MAX_HEADER_BYTES = 65536;

    private function __construct()
    {
    }

    /**
     * @return array{0: int, 1: array<string, string>, 2: string} The status, the headers by lowercase name, and the body.
     * @throws RelayUnreachable When the relay cannot be reached, or the connection fails before the whole answer came.
     * @throws RelayBroken When the answer is not HTTP, or is larger than `$maxBody`.
     */
    public static function get(RelayUrl $relay, string $target, string $token, float $timeout, int $maxBody): array
    {
        $deadline = microtime(true) + $timeout;
        $context = stream_context_create(['ssl' => [
            'verify_peer' => true,
            'verify_peer_name' => true,
            'allow_self_signed' => false,
            'peer_name' => $relay->host,
            'SNI_enabled' => true,
            'disable_compression' => true,
            'crypto_method' => STREAM_CRYPTO_METHOD_TLSv1_2_CLIENT | STREAM_CRYPTO_METHOD_TLSv1_3_CLIENT,
        ]]);
        $address = ($relay->https ? 'tls://' : 'tcp://') . $relay->socketHost() . ':' . $relay->port;
        $warning = '';
        set_error_handler(static function (int $level, string $message) use (&$warning): bool {
            $warning .= $message . "\n";
            return true;
        });
        try {
            $socket = stream_socket_client($address, $errorNumber, $errorText, min(10.0, $timeout), STREAM_CLIENT_CONNECT, $context);
        } finally {
            restore_error_handler();
        }
        if ($socket === false) {
            throw new RelayUnreachable(str_contains($warning, 'certificate') || str_contains($warning, 'did not match expected'));
        }
        try {
            $request = "GET {$target} HTTP/1.1\r\nHost: {$relay->hostHeader()}\r\nAuthorization: Bearer {$token}\r\n"
                . "Accept: application/json\r\nUser-Agent: erdigo-gateway-php\r\nConnection: close\r\n\r\n";
            stream_set_timeout($socket, max(1, (int) ceil($deadline - microtime(true))));
            if (@fwrite($socket, $request) !== strlen($request)) {
                throw new RelayUnreachable(false);
            }
            return self::readAnswer($socket, $deadline, $maxBody);
        } finally {
            fclose($socket);
        }
    }

    /**
     * Reads without blocking, a little at a time, so that a signal to stop is handled at once rather than when the
     * relay answers.
     *
     * @param resource $socket
     * @return array{0: int, 1: array<string, string>, 2: string}
     */
    private static function readAnswer($socket, float $deadline, int $maxBody): array
    {
        stream_set_blocking($socket, false);
        $data = '';
        $headerEnd = false;
        $status = 0;
        $headers = [];
        while (true) {
            $chunk = @fread($socket, 65536);
            if ($chunk === false) {
                throw new RelayUnreachable(false);
            }
            if ($chunk !== '') {
                $data .= $chunk;
                if ($headerEnd === false) {
                    $headerEnd = strpos($data, "\r\n\r\n");
                    if ($headerEnd === false && strlen($data) > self::MAX_HEADER_BYTES) {
                        throw new RelayBroken('the answer has too long a header');
                    }
                    if ($headerEnd !== false) {
                        [$status, $headers] = self::parseHead(substr($data, 0, $headerEnd));
                    }
                }
                // Room for chunk sizes around the largest body allowed.
                if (strlen($data) > $maxBody + self::MAX_HEADER_BYTES + 65536) {
                    throw new RelayBroken('the answer is larger than a relay may send');
                }
                if ($headerEnd !== false && isset($headers['content-length'])
                    && !isset($headers['transfer-encoding'])
                    && strlen($data) - $headerEnd - 4 >= (int) $headers['content-length']) {
                    break;
                }
                continue;
            }
            if (feof($socket)) {
                break;
            }
            if (microtime(true) >= $deadline) {
                throw new RelayUnreachable(false);
            }
            $read = [$socket];
            $write = null;
            $except = null;
            @stream_select($read, $write, $except, 0, 250000);
        }
        if ($headerEnd === false) {
            throw new RelayUnreachable(false);
        }
        $body = substr($data, $headerEnd + 4);
        if (isset($headers['transfer-encoding'])) {
            if (strtolower($headers['transfer-encoding']) !== 'chunked') {
                throw new RelayBroken('the answer uses a transfer encoding other than chunked');
            }
            $body = self::dechunk($body);
        } elseif (isset($headers['content-length'])) {
            $length = (int) $headers['content-length'];
            if (strlen($body) < $length) {
                throw new RelayUnreachable(false);
            }
            $body = substr($body, 0, $length);
        }
        if (strlen($body) > $maxBody) {
            throw new RelayBroken('the answer is larger than a relay may send');
        }
        return [$status, $headers, $body];
    }

    /** @return array{0: int, 1: array<string, string>} */
    private static function parseHead(string $head): array
    {
        $lines = explode("\r\n", $head);
        if (preg_match('#^HTTP/1\.[01] ([1-5][0-9]{2})(?: [^\r\n]*)?$#D', (string) array_shift($lines), $match) !== 1) {
            throw new RelayBroken('the answer is not HTTP');
        }
        $headers = [];
        foreach ($lines as $line) {
            $colon = strpos($line, ':');
            if ($colon === false || $colon === 0) {
                throw new RelayBroken('the answer has a header line that is not valid');
            }
            $name = strtolower(substr($line, 0, $colon));
            $value = trim(substr($line, $colon + 1), " \t");
            if ($name === 'content-length' && (preg_match('/^[0-9]{1,15}$/D', $value) !== 1
                || (isset($headers[$name]) && $headers[$name] !== $value))) {
                throw new RelayBroken('the answer has a Content-Length that is not valid');
            }
            $headers[$name] = $value;
        }
        return [(int) $match[1], $headers];
    }

    private static function dechunk(string $data): string
    {
        $body = '';
        $at = 0;
        while (true) {
            $lineEnd = strpos($data, "\r\n", $at);
            if ($lineEnd === false) {
                throw new RelayUnreachable(false);
            }
            $size = trim(explode(';', substr($data, $at, $lineEnd - $at), 2)[0], " \t");
            if (preg_match('/^[0-9A-Fa-f]{1,8}$/D', $size) !== 1) {
                throw new RelayBroken('the answer has a chunk that is not valid');
            }
            $length = (int) hexdec($size);
            $at = $lineEnd + 2;
            if ($length === 0) {
                return $body;
            }
            if (strlen($data) < $at + $length + 2) {
                throw new RelayUnreachable(false);
            }
            $body .= substr($data, $at, $length);
            $at += $length + 2;
        }
    }
}
