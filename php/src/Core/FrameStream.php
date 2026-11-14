<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * An ordered byte stream over TCP with the integration-v1 framing: a two-byte big-endian length followed by one Noise
 * message.
 *
 * The socket is non-blocking, and every read and write waits with `stream_select` until the deadline, so a peer that
 * stops reading or stops sending cannot hold a request longer than its deadline.
 */
final class FrameStream
{
    /** @var resource|null */
    private $socket;
    private string $buffer = '';
    private ?float $deadline = null;

    /** @param resource $socket */
    private function __construct($socket)
    {
        $this->socket = $socket;
        stream_set_blocking($socket, false);
        stream_set_write_buffer($socket, 0);
        stream_set_read_buffer($socket, 0);
    }

    /** Connects to a receiver, or fails with {@see FrameStreamException::CONNECTION_FAILED} or `TIMED_OUT`. */
    public static function connect(string $host, int $port, float $timeout): self
    {
        if ($port < 1 || $port > 65535 || $host === '' || strpbrk($host, "/ \t\r\n") !== false) {
            throw new FrameStreamException(FrameStreamException::CONNECTION_FAILED);
        }
        $bare = self::isBracketed($host) ? substr($host, 1, -1) : $host;
        $address = str_contains($bare, ':') ? "[{$bare}]" : $bare;
        $context = stream_context_create(['socket' => ['tcp_nodelay' => true]]);
        $errorNumber = 0;
        $errorText = '';
        $started = Clock::now();
        $socket = @stream_socket_client(
            "tcp://{$address}:{$port}",
            $errorNumber,
            $errorText,
            max(0.001, $timeout),
            STREAM_CLIENT_CONNECT,
            $context,
        );
        if ($socket === false) {
            throw new FrameStreamException(
                Clock::now() - $started >= $timeout ? FrameStreamException::TIMED_OUT : FrameStreamException::CONNECTION_FAILED,
            );
        }
        return new self($socket);
    }

    private static function isBracketed(string $host): bool
    {
        return str_starts_with($host, '[') && str_ends_with($host, ']');
    }

    /** Wraps a connection a listener accepted. @param resource $socket */
    public static function fromAccepted($socket): self
    {
        return new self($socket);
    }

    /**
     * Fails reads and writes with {@see FrameStreamException::TIMED_OUT} once `$seconds` have passed, until
     * {@see self::disarmDeadline()} is called.
     */
    public function armDeadline(float $seconds): void
    {
        $this->deadline = Clock::now() + max(0.0, $seconds);
    }

    public function disarmDeadline(): void
    {
        $this->deadline = null;
    }

    /** Bytes received but not yet read. After a failed read, this says whether the peer sent anything. */
    public function bufferedByteCount(): int
    {
        return strlen($this->buffer);
    }

    /** @return array{0: array<int, resource>, 1: int, 2: int}|null The sockets and the time to wait, or null to wait without end. */
    private function remaining(): ?array
    {
        if ($this->deadline === null) {
            return null;
        }
        $left = $this->deadline - Clock::now();
        if ($left <= 0) {
            $this->close();
            throw new FrameStreamException(FrameStreamException::TIMED_OUT);
        }
        $seconds = (int) floor($left);
        return [[], $seconds, (int) (($left - $seconds) * 1e6)];
    }

    /** @return resource */
    private function socket()
    {
        if ($this->socket === null) {
            throw new FrameStreamException(FrameStreamException::CLOSED);
        }
        return $this->socket;
    }

    private function wait(bool $forWriting): void
    {
        $socket = $this->socket();
        $time = $this->remaining();
        $read = $forWriting ? [] : [$socket];
        $write = $forWriting ? [$socket] : [];
        $except = null;
        $ready = @stream_select($read, $write, $except, $time === null ? null : $time[1], $time === null ? null : $time[2]);
        if ($ready === false) {
            // Interrupted by a signal: the caller tries again, and the deadline still holds.
            return;
        }
        if ($ready === 0) {
            $this->remaining();
        }
    }

    public function send(string $data): void
    {
        while ($data !== '') {
            $this->wait(true);
            $written = @fwrite($this->socket(), $data);
            if ($written === false) {
                $this->close();
                throw new FrameStreamException(FrameStreamException::CLOSED);
            }
            if ($written === 0) {
                if (feof($this->socket())) {
                    $this->close();
                    throw new FrameStreamException(FrameStreamException::CLOSED);
                }
                continue;
            }
            $data = (string) substr($data, $written);
        }
    }

    private function receiveSome(): void
    {
        $this->wait(false);
        $chunk = @fread($this->socket(), 65536);
        if ($chunk === false) {
            $this->close();
            throw new FrameStreamException(FrameStreamException::CLOSED);
        }
        if ($chunk === '') {
            if (feof($this->socket())) {
                $this->close();
                throw new FrameStreamException(FrameStreamException::CLOSED);
            }
            return;
        }
        $this->buffer .= $chunk;
    }

    public function readExactly(int $count): string
    {
        while (strlen($this->buffer) < $count) {
            $this->receiveSome();
        }
        $head = substr($this->buffer, 0, $count);
        $this->buffer = (string) substr($this->buffer, $count);
        return $head;
    }

    /** Reads one frame and returns its body. Refuses a frame longer than `$maxLength` before reading it. */
    public function readFrame(int $maxLength = IntegrationV1::MAX_FRAME_LENGTH): string
    {
        $header = $this->readExactly(2);
        $length = (ord($header[0]) << 8) | ord($header[1]);
        if ($length > $maxLength) {
            $this->close();
            throw new FrameStreamException(FrameStreamException::FRAME_TOO_LARGE);
        }
        return $this->readExactly($length);
    }

    public static function frame(string $body): string
    {
        if (strlen($body) > IntegrationV1::MAX_FRAME_LENGTH) {
            throw new FrameStreamException(FrameStreamException::FRAME_TOO_LARGE);
        }
        return pack('n', strlen($body)) . $body;
    }

    public function sendFrame(string $body): void
    {
        $this->send(self::frame($body));
    }

    /** Stops sending once everything already sent has gone out, so the peer can read it before the stream ends. */
    public function finishSending(): void
    {
        if ($this->socket !== null) {
            @stream_socket_shutdown($this->socket, STREAM_SHUT_WR);
        }
    }

    public function isOpen(): bool
    {
        return $this->socket !== null;
    }

    /** Closes the connection. Later reads and writes fail. */
    public function close(): void
    {
        if ($this->socket !== null) {
            @fclose($this->socket);
            $this->socket = null;
        }
    }

    /**
     * The peer's address, for remembering where a receiver was found: without brackets or an IPv6 zone, and an IPv4
     * address mapped into IPv6 written as IPv4.
     *
     * @return array{0: string, 1: int}|null
     */
    public function remoteAddress(): ?array
    {
        if ($this->socket === null) {
            return null;
        }
        $name = @stream_socket_get_name($this->socket, true);
        if (!is_string($name) || $name === '') {
            return null;
        }
        $colon = strrpos($name, ':');
        if ($colon === false) {
            return null;
        }
        $host = substr($name, 0, $colon);
        $port = (int) substr($name, $colon + 1);
        if (str_starts_with($host, '[') && str_ends_with($host, ']')) {
            $host = substr($host, 1, -1);
        }
        $percent = strpos($host, '%');
        if ($percent !== false) {
            $host = substr($host, 0, $percent);
        }
        if (str_starts_with(strtolower($host), '::ffff:') && filter_var(substr($host, 7), FILTER_VALIDATE_IP, FILTER_FLAG_IPV4) !== false) {
            $host = substr($host, 7);
        }
        return $port > 0 ? [$host, $port] : null;
    }
}
