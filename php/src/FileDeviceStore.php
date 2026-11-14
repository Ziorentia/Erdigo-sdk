<?php

declare(strict_types=1);

namespace Erdigo;

use Erdigo\Core\Hex;
use Erdigo\Core\ItemKind;
use Erdigo\Core\Noise\Noise;

/**
 * Keeps the device key, its pairings and its connection counters in a private folder: mode 0700, with files of mode
 * 0600, on Unix systems, and a folder only you can read on Windows. It refuses to read files other users could read
 * or replace, the way `ssh` treats its key files, and refuses a folder inside the web server's document root.
 *
 * The device key is in `identity.key` in the clear. It is not encrypted at rest: anyone who can read the file as you,
 * or as root, has the key. Pairings and counters are in `pairings.json`. Every file is written through a temporary file
 * and a rename, and made durable before the call returns, so a crash never leaves half a file. A lock file serialises
 * the processes that share the folder, such as the workers of a web server.
 */
final class FileDeviceStore implements LockingDeviceStore
{
    private const PAIRINGS_FILE = 'pairings.json';
    private const KEY_FILE = 'identity.key';
    private const LOCK_FILE = '.lock';
    private const KEY_MAGIC = 'EDK0';
    private const LOCK_WAIT_SECONDS = 30.0;

    public readonly string $directory;

    /** Creates the folder if needed, or checks that an existing one is private. */
    public function __construct(string $directory)
    {
        if ($directory === '' || str_contains($directory, "\0")) {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'the state folder needs a name');
        }
        $this->directory = self::absolute($directory);
        self::refuseDocumentRoot($this->directory);
        try {
            PrivateFile::ensurePrivateDirectory($this->directory);
        } catch (PrivateFileException $error) {
            throw new ErdigoException(
                ErrorKind::Storage,
                $error->getMessage(),
                storageProblem: $error->problem === PrivateFileException::INSECURE ? StorageProblem::Insecure : StorageProblem::Other,
                previous: $error,
            );
        } catch (\RuntimeException $error) {
            throw new ErdigoException(ErrorKind::Storage, 'could not create the state folder', previous: $error);
        }
        // Again, now that links in the path can be followed.
        self::refuseDocumentRoot($this->directory);
    }

    /**
     * A folder of its own for each profile, so that this SDK never shares a device key with the Swift or .NET SDK on
     * the same computer: `~/.local/share/ErdigoSDK.PHP/<profile>` on Linux (or under `$XDG_DATA_HOME`),
     * `~/Library/Application Support/ErdigoSDK.PHP/<profile>` on macOS and `%LOCALAPPDATA%\ErdigoSDK.PHP\<profile>` on
     * Windows. On a web server, pass a folder of your own outside the document root instead.
     */
    public static function defaultDirectory(string $profile = 'default'): string
    {
        if (PHP_OS_FAMILY === 'Windows') {
            $base = getenv('LOCALAPPDATA');
            if (!is_string($base) || $base === '') {
                throw new ErdigoException(ErrorKind::Storage, 'the folder for application data could not be found; pass a state folder');
            }
            return $base . '\\ErdigoSDK.PHP\\' . $profile;
        }
        $home = getenv('HOME');
        if ((!is_string($home) || $home === '') && function_exists('posix_getpwuid') && function_exists('posix_geteuid')) {
            $entry = posix_getpwuid(posix_geteuid());
            $home = is_array($entry) ? ($entry['dir'] ?? '') : '';
        }
        if (!is_string($home) || $home === '') {
            throw new ErdigoException(ErrorKind::Storage, 'the home folder could not be found; pass a state folder');
        }
        if (PHP_OS_FAMILY === 'Darwin') {
            return "{$home}/Library/Application Support/ErdigoSDK.PHP/{$profile}";
        }
        $data = getenv('XDG_DATA_HOME');
        $base = is_string($data) && str_starts_with($data, '/') ? $data : "{$home}/.local/share";
        return "{$base}/ErdigoSDK.PHP/{$profile}";
    }

    private static function absolute(string $path): string
    {
        $isAbsolute = str_starts_with($path, '/') || str_starts_with($path, '\\')
            || preg_match('/^[A-Za-z]:[\\\\\/]/', $path) === 1;
        if (!$isAbsolute) {
            $path = getcwd() . DIRECTORY_SEPARATOR . $path;
        }
        return rtrim($path, '/\\') ?: $path;
    }

    /**
     * Refuses a folder inside the document root of the web server running this PHP, from which the web server could
     * hand out the device key. A PHP program run from the command line has no document root.
     */
    private static function refuseDocumentRoot(string $directory): void
    {
        $root = $_SERVER['DOCUMENT_ROOT'] ?? null;
        if (!is_string($root) || trim($root) === '') {
            return;
        }
        $roots = array_unique(array_filter([self::normalise($root), self::normalise((string) realpath($root))]));
        $paths = array_unique(array_filter([self::normalise($directory), self::normalise((string) realpath($directory))]));
        foreach ($roots as $rootPath) {
            foreach ($paths as $path) {
                if ($path === $rootPath || str_starts_with($path, $rootPath . '/')) {
                    throw new ErdigoException(
                        ErrorKind::Storage,
                        "the state folder is inside the web server's document root, from which the web server could hand out the device key. "
                        . 'Choose a folder outside it, such as /var/lib/erdigo/<site>.',
                        storageProblem: StorageProblem::Insecure,
                    );
                }
            }
        }
    }

    private static function normalise(string $path): string
    {
        if ($path === '') {
            return '';
        }
        $path = rtrim(str_replace('\\', '/', $path), '/');
        $parts = [];
        foreach (explode('/', $path) as $index => $part) {
            if ($part === '..') {
                array_pop($parts);
            } elseif ($part !== '.' && ($part !== '' || $index === 0)) {
                $parts[] = $part;
            }
        }
        $path = implode('/', $parts);
        // Windows and macOS usually ignore case in file names.
        return PHP_OS_FAMILY === 'Windows' || PHP_OS_FAMILY === 'Darwin' ? strtolower($path) : $path;
    }

    private function pathOf(string $name): string
    {
        return $this->directory . DIRECTORY_SEPARATOR . $name;
    }

    /**
     * Serialises access between processes, such as the workers of a web server.
     *
     * @template T
     * @param callable(): T $body
     * @return T
     */
    private function withLock(callable $body): mixed
    {
        $lock = $this->openLock($this->pathOf(self::LOCK_FILE), self::LOCK_WAIT_SECONDS);
        if ($lock === null) {
            throw new ErdigoException(ErrorKind::Storage, 'could not lock the state folder');
        }
        try {
            return $body();
        } finally {
            $lock->release();
        }
    }

    private function openLock(string $path, float $wait): ?ConnectionLock
    {
        $handle = PrivateFile::quietly(static fn () => fopen($path, 'c'));
        if ($handle === false) {
            throw new ErdigoException(ErrorKind::Storage, 'could not lock the state folder');
        }
        if (!PrivateFile::isWindows()) {
            @chmod($path, 0600);
        }
        $giveUp = microtime(true) + $wait;
        while (!flock($handle, LOCK_EX | LOCK_NB)) {
            if (microtime(true) >= $giveUp) {
                fclose($handle);
                return null;
            }
            usleep(10000);
        }
        return new ConnectionLock($handle);
    }

    public function acquireConnectionLock(string $receiverFingerprint, float $wait): ?ConnectionLock
    {
        if (!Hex::isFingerprint($receiverFingerprint)) {
            throw new ErdigoException(ErrorKind::InvalidArgument, 'not a receiver fingerprint');
        }
        return $this->openLock($this->pathOf('connection-' . substr($receiverFingerprint, 0, 16) . '.lock'), $wait);
    }

    private function readPrivate(string $name, StorageProblem $unreadable, int $maxBytes = 4194304): ?string
    {
        try {
            return PrivateFile::readAllBytes($this->pathOf($name), $maxBytes);
        } catch (PrivateFileException $error) {
            if ($error->problem === PrivateFileException::MISSING) {
                return null;
            }
            if ($error->problem === PrivateFileException::INSECURE) {
                throw new ErdigoException(ErrorKind::Storage, $error->getMessage(), storageProblem: StorageProblem::Insecure, previous: $error);
            }
            throw new ErdigoException(
                ErrorKind::Storage,
                "{$name} exists but could not be read. Nothing was changed.",
                storageProblem: $unreadable,
                previous: $error,
            );
        }
    }

    private function writePrivate(string $name, #[\SensitiveParameter] string $data, bool $overwrite = true): void
    {
        try {
            PrivateFile::writeAtomically($this->pathOf($name), $data, $overwrite);
        } catch (PrivateFileException $error) {
            throw new ErdigoException(
                ErrorKind::Storage,
                "{$name} exists but could not be read. Nothing was changed, and no new key was created.",
                storageProblem: StorageProblem::IdentityUnreadable,
                previous: $error,
            );
        } catch (\RuntimeException $error) {
            throw new ErdigoException(ErrorKind::Storage, "could not write {$name}", previous: $error);
        }
    }

    public function loadOrCreateIdentity(): DeviceIdentity
    {
        return $this->withLock(function (): DeviceIdentity {
            $stored = $this->readPrivate(self::KEY_FILE, StorageProblem::IdentityUnreadable, 65536);
            if ($stored !== null) {
                if (strlen($stored) !== strlen(self::KEY_MAGIC) + Noise::DH_LENGTH || !str_starts_with($stored, self::KEY_MAGIC)) {
                    throw new ErdigoException(
                        ErrorKind::Storage,
                        self::KEY_FILE . ' exists but does not hold a device key. It was left as it is. Move it away to create a new key, which means pairing again.',
                        storageProblem: StorageProblem::IdentityDamaged,
                    );
                }
                $identity = DeviceIdentity::fromPrivateKey(substr($stored, strlen(self::KEY_MAGIC)));
                sodium_memzero($stored);
                return $identity;
            }
            // Only a file that is not there at all leads to a new key, and never over an existing file.
            $created = DeviceIdentity::generate();
            $sealed = self::KEY_MAGIC . $created->privateKey();
            try {
                $this->writePrivate(self::KEY_FILE, $sealed, false);
            } finally {
                sodium_memzero($sealed);
            }
            return $created;
        });
    }

    /** @return array{pairings: list<ReceiverPairing>, counters: array<string, int>} */
    private function loadState(): array
    {
        $data = $this->readPrivate(self::PAIRINGS_FILE, StorageProblem::StateUnreadable);
        if ($data === null) {
            return ['pairings' => [], 'counters' => []];
        }
        try {
            return self::decodeState($data);
        } catch (\JsonException | \UnexpectedValueException | \Exception $error) {
            throw new ErdigoException(
                ErrorKind::Storage,
                self::PAIRINGS_FILE . ' could not be read',
                storageProblem: StorageProblem::StateDamaged,
                previous: $error,
            );
        }
    }

    /** @return array{pairings: list<ReceiverPairing>, counters: array<string, int>} */
    private static function decodeState(string $data): array
    {
        $root = json_decode($data, true, 16, JSON_THROW_ON_ERROR);
        $fail = [self::class, 'damaged'];
        if (!is_array($root) || ($root['version'] ?? null) !== 1 || !is_array($root['pairings'] ?? null) || !is_array($root['counters'] ?? null)) {
            $fail('unknown layout');
        }
        $pairings = [];
        foreach ($root['pairings'] as $element) {
            if (!is_array($element) || !is_string($element['label'] ?? null) || !is_string($element['receiverKey'] ?? null)
                || !is_array($element['kinds'] ?? null) || !is_string($element['pairedAt'] ?? null)
                || !is_int($element['counterFloor'] ?? null) || $element['counterFloor'] < 0) {
                $fail('a pairing is not valid');
            }
            $key = Hex::decode($element['receiverKey']);
            if ($key === null || !Noise::isCanonicalPublicKey($key)) {
                $fail('a receiver key is not valid');
            }
            $kinds = [];
            foreach ($element['kinds'] as $kind) {
                $kinds[] = (is_string($kind) ? ItemKind::tryFrom($kind) : null) ?? $fail('unknown kind');
            }
            $host = $element['lastHost'] ?? null;
            $port = $element['lastPort'] ?? null;
            if (($host !== null && !is_string($host)) || ($port !== null && (!is_int($port) || $port < 1 || $port > 65535))) {
                $fail('an address is not valid');
            }
            $pairings[] = new ReceiverPairing(
                $element['label'],
                $key,
                $kinds,
                $host,
                $port,
                new \DateTimeImmutable($element['pairedAt']),
                $element['counterFloor'],
            );
        }
        $counters = [];
        foreach ($root['counters'] as $fingerprint => $value) {
            if (!Hex::isFingerprint((string) $fingerprint) || !is_int($value) || $value < 0) {
                $fail('a counter is not valid');
            }
            $counters[(string) $fingerprint] = $value;
        }
        return ['pairings' => $pairings, 'counters' => $counters];
    }

    private static function damaged(string $why): never
    {
        throw new \UnexpectedValueException($why);
    }

    /** @param array{pairings: list<ReceiverPairing>, counters: array<string, int>} $state */
    private function saveState(array $state): void
    {
        $pairings = [];
        foreach ($state['pairings'] as $pairing) {
            $entry = [
                'label' => $pairing->label,
                'receiverKey' => Hex::encode($pairing->receiverKey),
                'kinds' => array_map(static fn (ItemKind $kind): string => $kind->value, $pairing->kinds),
            ];
            if ($pairing->lastHost !== null) {
                $entry['lastHost'] = $pairing->lastHost;
            }
            if ($pairing->lastPort !== null) {
                $entry['lastPort'] = $pairing->lastPort;
            }
            $entry['pairedAt'] = $pairing->pairedAt->setTimezone(new \DateTimeZone('UTC'))->format('Y-m-d\TH:i:s.uP');
            $entry['counterFloor'] = $pairing->counterFloor;
            $pairings[] = $entry;
        }
        $counters = $state['counters'];
        ksort($counters, SORT_STRING);
        $document = ['version' => 1, 'pairings' => $pairings, 'counters' => $counters === [] ? new \stdClass() : $counters];
        $this->writePrivate(
            self::PAIRINGS_FILE,
            json_encode($document, JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_THROW_ON_ERROR) . "\n",
        );
    }

    public function pairings(): array
    {
        return $this->withLock(fn (): array => $this->loadState()['pairings']);
    }

    public function save(ReceiverPairing $pairing): void
    {
        $this->withLock(function () use ($pairing): void {
            $state = $this->loadState();
            $state['pairings'] = array_values(array_filter(
                $state['pairings'],
                static fn (ReceiverPairing $existing): bool => $existing->receiverKey !== $pairing->receiverKey,
            ));
            $state['pairings'][] = $pairing;
            $fingerprint = $pairing->fingerprint();
            $state['counters'][$fingerprint] = max($state['counters'][$fingerprint] ?? 0, $pairing->counterFloor);
            $this->saveState($state);
        });
    }

    public function updateAddress(string $receiverFingerprint, string $host, int $port): void
    {
        $this->withLock(function () use ($receiverFingerprint, $host, $port): void {
            $state = $this->loadState();
            foreach ($state['pairings'] as $index => $pairing) {
                if ($pairing->fingerprint() === $receiverFingerprint) {
                    $state['pairings'][$index] = $pairing->withAddress($host, $port);
                    $this->saveState($state);
                    return;
                }
            }
        });
    }

    public function removePairing(string $receiverFingerprint): void
    {
        $this->withLock(function () use ($receiverFingerprint): void {
            $state = $this->loadState();
            $state['pairings'] = array_values(array_filter(
                $state['pairings'],
                static fn (ReceiverPairing $pairing): bool => $pairing->fingerprint() !== $receiverFingerprint,
            ));
            unset($state['counters'][$receiverFingerprint]);
            $this->saveState($state);
        });
    }

    public function nextConnectCounter(string $receiverFingerprint, int $nowMs): int
    {
        return $this->withLock(function () use ($receiverFingerprint, $nowMs): int {
            $state = $this->loadState();
            $next = max($nowMs, ($state['counters'][$receiverFingerprint] ?? 0) + 1);
            $state['counters'][$receiverFingerprint] = $next;
            $this->saveState($state);
            return $next;
        });
    }
}
