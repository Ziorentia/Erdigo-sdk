<?php

declare(strict_types=1);

namespace Erdigo\Tests\Support;

use Erdigo\Core\IntegrationV1;
use Erdigo\Core\Noise\Noise;
use Erdigo\Core\PairingCode;
use Erdigo\Core\PairingTicket;
use Erdigo\DeviceIdentity;

/**
 * Starts scripted-receiver.php in a process of its own, and reads what it logged.
 */
final class ScriptedReceiver
{
    public readonly string $publicKey;
    public readonly string $fingerprint;
    public readonly int $port;
    public readonly ?PairingCode $code;
    /** @var resource */
    private $process;
    /** @var array<int, resource> */
    private array $pipes;
    private readonly string $logPath;
    private readonly string $configPath;
    private readonly string $silencePath;

    /**
     * @param array<string, mixed> $settings `pairing` (decision, kinds, counter), `devices`, `script`, `missing`, `limits`.
     */
    public function __construct(array $settings = [])
    {
        $private = random_bytes(32);
        $this->publicKey = Noise::publicKey($private);
        $this->fingerprint = IntegrationV1::fingerprint($this->publicKey);
        $directory = Temporary::directory();
        $this->logPath = $directory . '/receiver.log';
        $this->configPath = $directory . '/receiver.json';
        $this->silencePath = $directory . '/silence';
        $this->code = isset($settings['pairing']) ? PairingCode::generate() : null;
        if ($this->code !== null) {
            $settings['pairing']['code'] = $this->code->canonical;
        }
        if (isset($settings['devices'])) {
            $settings['devices'] = array_map(static function ($device): array {
                if ($device instanceof DeviceIdentity) {
                    return ['public' => bin2hex($device->publicKey), 'kinds' => ['pin', 'marker', 'note', 'photo', 'track']];
                }
                return $device;
            }, $settings['devices']);
        }
        $settings['receiver_private'] = bin2hex($private);
        $settings['log'] = $this->logPath;
        $settings['silence'] = $this->silencePath;
        file_put_contents($this->logPath, '');
        $this->silenceNextConnections(0);
        file_put_contents($this->configPath, json_encode($settings, JSON_THROW_ON_ERROR));
        $process = proc_open(
            [PHP_BINARY, __DIR__ . '/scripted-receiver.php', $this->configPath],
            [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['file', $directory . '/receiver.err', 'a']],
            $pipes,
        );
        if (!is_resource($process)) {
            throw new \RuntimeException('the scripted receiver did not start');
        }
        $this->process = $process;
        $this->pipes = $pipes;
        $line = fgets($pipes[1]);
        if ($line === false || preg_match('/^port (\d+)/', $line, $match) !== 1) {
            $this->stop();
            throw new \RuntimeException('the scripted receiver did not start: ' . (string) @file_get_contents($directory . '/receiver.err'));
        }
        $this->port = (int) $match[1];
    }

    /** The QR text for this receiver's code. */
    public function qrText(): string
    {
        return (string) (new PairingTicket($this->code ?? throw new \LogicException('no pairing code'), $this->fingerprint))->uri();
    }

    /**
     * Makes the receiver close the next `$count` connections as soon as it accepts them, without reading or answering
     * anything, as a receiver does with a connection from an address that has too many unproven ones. Each is logged as
     * a `silent` event, `why` being `told to`.
     */
    public function silenceNextConnections(int $count): void
    {
        // Replaced in one step, so that the receiver never reads a half-written number.
        file_put_contents($this->silencePath . '.new', (string) $count);
        rename($this->silencePath . '.new', $this->silencePath);
    }

    /** @return list<array<string, mixed>> */
    public function events(?string $name = null): array
    {
        // Give the receiver a moment to write what it saw last.
        usleep(150000);
        $events = [];
        foreach (file($this->logPath, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) ?: [] as $line) {
            $event = json_decode($line, true);
            if (is_array($event) && ($name === null || $event['event'] === $name)) {
                $events[] = $event;
            }
        }
        return $events;
    }

    /** @return list<int> The sequence numbers of every request received, in order. */
    public function seen(): array
    {
        return array_map(static fn (array $event): int => $event['seq'], $this->events('request'));
    }

    /** @return list<array<string, mixed>> Every request received, decoded. */
    public function requests(): array
    {
        return array_map(static fn (array $event): array => json_decode($event['json'], true), $this->events('request'));
    }

    public function connections(): int
    {
        return count($this->events('connected'));
    }

    public function stop(): void
    {
        if (is_resource($this->process)) {
            foreach ($this->pipes as $pipe) {
                if (is_resource($pipe)) {
                    fclose($pipe);
                }
            }
            proc_terminate($this->process);
            proc_close($this->process);
        }
    }

    public function __destruct()
    {
        $this->stop();
    }
}
