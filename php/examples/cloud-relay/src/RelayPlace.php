<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\Core\IntegrationV1;
use Erdigo\PrivateFile;
use Erdigo\PrivateFileException;

/**
 * The gateway's place in a relay's queue, kept in `relay-state.json` in the state folder: the relay's address, its
 * epoch and the last sequence number the gateway is done with. The file is replaced whole, through a temporary file
 * and a rename, and only the user running the gateway can read it, as the SDK does with its own files.
 */
final class RelayPlace
{
    public const FILE = 'relay-state.json';

    public function __construct(public readonly string $relay, public readonly ?string $epoch, public readonly int $after)
    {
    }

    /** The place saved for this relay, or the start of its queue if none is saved or it was saved for another relay. */
    public static function load(string $directory, string $relay): self
    {
        try {
            $text = PrivateFile::readAllBytes($directory . DIRECTORY_SEPARATOR . self::FILE, 65536);
        } catch (PrivateFileException $error) {
            if ($error->problem === PrivateFileException::MISSING) {
                return new self($relay, null, 0);
            }
            throw new GatewayExit(1, $error->problem === PrivateFileException::INSECURE
                ? self::FILE . ' must be a regular file that only you can read (mode 0600)'
                : self::FILE . ' could not be read');
        }
        $state = json_decode($text, true, 4);
        if (!is_array($state) || array_keys($state) !== ['relay', 'epoch', 'after'] || !is_string($state['relay'])
            || !($state['epoch'] === null || (is_string($state['epoch']) && preg_match(Relay::EPOCH_PATTERN, $state['epoch']) === 1))
            || !is_int($state['after']) || $state['after'] < 0 || $state['after'] > IntegrationV1::MAX_SEQUENCE) {
            throw new GatewayExit(1, self::FILE . ' is damaged. It was left as it is; move it away to start again from the oldest operation the relay holds.');
        }
        if ($state['relay'] !== $relay) {
            return new self($relay, null, 0);
        }
        return new self($relay, $state['epoch'], $state['after']);
    }

    public function save(string $directory): void
    {
        $text = json_encode(['relay' => $this->relay, 'epoch' => $this->epoch, 'after' => $this->after], JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);
        try {
            PrivateFile::writeAtomically($directory . DIRECTORY_SEPARATOR . self::FILE, $text . "\n");
        } catch (\RuntimeException) {
            throw new GatewayExit(1, self::FILE . ' could not be written, so the gateway cannot keep its place');
        }
    }

    public function withEpoch(string $epoch): self
    {
        return new self($this->relay, $epoch, 0);
    }

    public function withAfter(int $after): self
    {
        return new self($this->relay, $this->epoch, $after);
    }
}
