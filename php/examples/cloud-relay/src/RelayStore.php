<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\PrivateFile;

/**
 * The relay's queue, in its data folder: `state.json` with the epoch and the numbers, and one file per waiting
 * operation in `ops`, named by its sequence number and holding it as it is sent to the gateway. A request holds an
 * exclusive lock on `relay.lock` while it uses the queue, so the processes of a web server take turns.
 *
 * The waiting operations are always the numbers from `first` to `last`, because both the gateway's acknowledgements
 * and expiry remove the oldest. `state.json` is replaced whole, and is what makes a change count: operation files are
 * written before it names them, and removed after it stops naming them.
 */
final class RelayStore
{
    private const STATE_FILE = 'state.json';
    private const LOCK_FILE = 'relay.lock';
    private const OPS_FOLDER = 'ops';
    private const HEAD = '/^\{"seq":([0-9]+),"received_ms":([0-9]+),"op":/';

    /** @var resource|null */
    private $lock = null;
    private string $epoch = '';
    /** The oldest waiting operation, or `last + 1` when none is waiting. */
    private int $first = 1;
    /** The last sequence number given in this epoch, 0 before the first. */
    private int $last = 0;
    /** The size of the waiting operations' JSON. */
    private int $bytes = 0;
    /** The highest sequence number dropped because it waited too long. */
    private int $expiredUpTo = 0;
    /** When the last operation was taken. Later ones are never given an earlier time, so the oldest expire first. */
    private int $lastReceivedMs = 0;

    private function __construct(private readonly RelayConfig $config)
    {
    }

    /** Takes the lock and reads the queue. Close it as soon as the request is done with it. */
    public static function open(RelayConfig $config): self
    {
        $store = new self($config);
        $store->takeLock();
        try {
            $store->load();
            $store->expire();
        } catch (\Throwable $error) {
            $store->close();
            throw $error;
        }
        return $store;
    }

    public function close(): void
    {
        if ($this->lock !== null) {
            flock($this->lock, LOCK_UN);
            fclose($this->lock);
            $this->lock = null;
        }
    }

    public function epoch(): string
    {
        return $this->epoch;
    }

    /**
     * Takes a batch whole, or throws `queue_full` and takes nothing.
     *
     * @param list<string> $opJsons Each operation's JSON.
     * @return array{0: int, 1: int} How many were taken, and the sequence number of the last.
     */
    public function append(array $opJsons): array
    {
        $size = array_sum(array_map('strlen', $opJsons));
        if ($this->count() + count($opJsons) > $this->config->maxOps || $this->bytes + $size > $this->config->maxBytes) {
            throw new Refusal(503, ['error' => 'queue_full'], ['Retry-After' => '30']);
        }
        $receivedMs = max($this->nowMs(), $this->lastReceivedMs);
        $written = [];
        try {
            foreach ($opJsons as $index => $opJson) {
                $seq = $this->last + 1 + $index;
                PrivateFile::writeAtomically($this->opPath($seq), self::entry($seq, $receivedMs, $opJson));
                $written[] = $this->opPath($seq);
            }
            $this->last += count($opJsons);
            $this->bytes += $size;
            $this->lastReceivedMs = $receivedMs;
            $this->save();
        } catch (\Throwable $error) {
            foreach ($written as $path) {
                @unlink($path);
            }
            throw $error;
        }
        return [count($opJsons), $this->last];
    }

    /**
     * The answer to `GET /v1/ops`. With the current epoch, every operation up to `$after` is forgotten first.
     */
    public function fetch(?string $epoch, int $after, int $limit, int $maxAnswerBytes): FetchAnswer
    {
        $reset = $epoch !== $this->epoch;
        if ($reset) {
            $from = $this->first;
            $missed = $this->expiredUpTo > 0;
        } else {
            if ($after > $this->last) {
                throw new Refusal(400, ['error' => 'invalid_query']);
            }
            $this->removeUpTo($after);
            $from = max($this->first, $after + 1);
            $missed = $this->expiredUpTo > $after;
        }
        $head = '{"epoch":"' . $this->epoch . '","reset":' . ($reset ? 'true' : 'false')
            . ',"missed":' . ($missed ? 'true' : 'false') . ',"ops":[';
        $size = strlen($head) + 2;
        $entries = [];
        for ($seq = $from; $seq <= $this->last && count($entries) < $limit; $seq++) {
            $entry = @file_get_contents($this->opPath($seq));
            if ($entry === false) {
                throw new \RuntimeException("operation file {$seq} could not be read");
            }
            $added = strlen($entry) + ($entries === [] ? 0 : 1);
            // Always at least one, however large.
            if ($entries !== [] && $size + $added > $maxAnswerBytes) {
                break;
            }
            $entries[] = $entry;
            $size += $added;
        }
        return new FetchAnswer($head . implode(',', $entries) . ']}', count($entries), $reset, $missed);
    }

    private function count(): int
    {
        return $this->last - $this->first + 1;
    }

    private static function entry(int $seq, int $receivedMs, string $opJson): string
    {
        return '{"seq":' . $seq . ',"received_ms":' . $receivedMs . ',"op":' . $opJson . '}';
    }

    private function opPath(int $seq): string
    {
        return $this->config->dataDirectory . DIRECTORY_SEPARATOR . self::OPS_FOLDER . DIRECTORY_SEPARATOR . $seq . '.json';
    }

    /** The time an operation was taken, and the size of its JSON, or null if its file is missing or damaged. */
    private function head(int $seq): ?array
    {
        $handle = @fopen($this->opPath($seq), 'rb');
        if ($handle === false) {
            return null;
        }
        try {
            $status = fstat($handle);
            $start = fread($handle, 80);
        } finally {
            fclose($handle);
        }
        if ($status === false || !is_string($start) || preg_match(self::HEAD, $start, $match) !== 1 || (int) $match[1] !== $seq) {
            return null;
        }
        return [(int) $match[2], $status['size'] - strlen($match[0]) - 1];
    }

    /** Drops the oldest operations while they have waited longer than the relay keeps them. */
    private function expire(): void
    {
        $cutoff = $this->nowMs() - $this->config->keepHours * 3600000;
        $removed = [];
        while ($this->first <= $this->last) {
            $head = $this->head($this->first);
            if ($head !== null && $head[0] >= $cutoff) {
                break;
            }
            $this->bytes -= $head[1] ?? 0;
            $this->expiredUpTo = $this->first;
            $removed[] = $this->first;
            $this->first += 1;
        }
        $this->commitRemoval($removed);
    }

    /** Forgets the operations the gateway is done with. */
    private function removeUpTo(int $seq): void
    {
        $removed = [];
        while ($this->first <= min($seq, $this->last)) {
            $this->bytes -= $this->head($this->first)[1] ?? 0;
            $removed[] = $this->first;
            $this->first += 1;
        }
        $this->commitRemoval($removed);
    }

    /** @param list<int> $removed */
    private function commitRemoval(array $removed): void
    {
        if ($removed === []) {
            return;
        }
        $this->bytes = max(0, $this->bytes);
        $this->save();
        foreach ($removed as $seq) {
            @unlink($this->opPath($seq));
        }
        if ($this->count() === 0) {
            $this->clearOps();
        }
    }

    private function takeLock(): void
    {
        $path = $this->config->dataDirectory . DIRECTORY_SEPARATOR . self::LOCK_FILE;
        $handle = @fopen($path, 'c');
        if ($handle === false) {
            throw new \RuntimeException('the lock file could not be opened');
        }
        if (PHP_OS_FAMILY !== 'Windows') {
            @chmod($path, 0600);
        }
        if (!flock($handle, LOCK_EX)) {
            fclose($handle);
            throw new \RuntimeException('the lock file could not be locked');
        }
        $this->lock = $handle;
    }

    private function load(): void
    {
        $folder = $this->config->dataDirectory . DIRECTORY_SEPARATOR . self::OPS_FOLDER;
        if (!is_dir($folder) && !@mkdir($folder, 0700) && !is_dir($folder)) {
            throw new \RuntimeException('the operations folder could not be created');
        }
        $path = $this->config->dataDirectory . DIRECTORY_SEPARATOR . self::STATE_FILE;
        clearstatcache(true, $path);
        if (!file_exists($path)) {
            $this->startNumbering();
            return;
        }
        $text = @file_get_contents($path);
        if ($text === false) {
            throw new \RuntimeException('state.json could not be read');
        }
        $state = json_decode($text, true, 4);
        $isInt = static fn (mixed $value): bool => is_int($value) && $value >= 0;
        if (!is_array($state) || ($state['version'] ?? null) !== 1 || !is_string($state['epoch'] ?? null)
            || preg_match(Relay::EPOCH_PATTERN, $state['epoch']) !== 1
            || !$isInt($state['first'] ?? null) || !$isInt($state['last'] ?? null) || !$isInt($state['bytes'] ?? null)
            || !$isInt($state['expired_up_to'] ?? null) || !$isInt($state['last_received_ms'] ?? null)
            || $state['first'] < 1 || $state['first'] > $state['last'] + 1) {
            // A queue that cannot be read is started again, with a new epoch, so the gateway knows.
            error_log('erdigo-relay: state.json was damaged, so the queue starts again with a new numbering');
            $this->startNumbering();
            return;
        }
        $this->epoch = $state['epoch'];
        $this->first = $state['first'];
        $this->last = $state['last'];
        $this->bytes = $state['bytes'];
        $this->expiredUpTo = $state['expired_up_to'];
        $this->lastReceivedMs = $state['last_received_ms'];
    }

    private function startNumbering(): void
    {
        $this->epoch = rtrim(strtr(base64_encode(random_bytes(16)), '+/', '-_'), '=');
        $this->first = 1;
        $this->last = 0;
        $this->bytes = 0;
        $this->expiredUpTo = 0;
        $this->lastReceivedMs = 0;
        $this->clearOps();
        $this->save();
    }

    /** Removes every file in the operations folder, including any left by a request that stopped half way. */
    private function clearOps(): void
    {
        $folder = $this->config->dataDirectory . DIRECTORY_SEPARATOR . self::OPS_FOLDER;
        foreach (scandir($folder) ?: [] as $name) {
            if ($name !== '.' && $name !== '..') {
                @unlink($folder . DIRECTORY_SEPARATOR . $name);
            }
        }
    }

    private function save(): void
    {
        $state = [
            'version' => 1,
            'epoch' => $this->epoch,
            'first' => $this->first,
            'last' => $this->last,
            'bytes' => $this->bytes,
            'expired_up_to' => $this->expiredUpTo,
            'last_received_ms' => $this->lastReceivedMs,
        ];
        PrivateFile::writeAtomically(
            $this->config->dataDirectory . DIRECTORY_SEPARATOR . self::STATE_FILE,
            json_encode($state, JSON_THROW_ON_ERROR) . "\n",
        );
    }

    /** Milliseconds since 1970. The tests can move it forward with a file holding an offset. */
    private function nowMs(): int
    {
        $now = (int) floor(microtime(true) * 1000);
        if ($this->config->clockFile !== null) {
            $offset = @file_get_contents($this->config->clockFile);
            if (is_string($offset) && preg_match('/^-?[0-9]{1,15}$/D', trim($offset)) === 1) {
                $now += (int) trim($offset);
            }
        }
        return $now;
    }
}
