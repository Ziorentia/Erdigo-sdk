<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\Cli\CommandFiles;
use Erdigo\Cli\Terminal;
use Erdigo\Cli\UsageException;
use Erdigo\Core\Clock;
use Erdigo\Core\Coordinate;
use Erdigo\Core\Hex;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\JsonObject;
use Erdigo\Core\JsonParseException;
use Erdigo\Core\MarkerColor;
use Erdigo\Core\PairingTicket;
use Erdigo\Core\StrictJson;
use Erdigo\Core\TrackPoint;
use Erdigo\Core\Utf8;
use Erdigo\Core\WireException;
use Erdigo\ErdigoClient;
use Erdigo\ErdigoException;
use Erdigo\ErrorKind;
use Erdigo\FileDeviceStore;
use Erdigo\Pairing;
use Erdigo\PrivateFile;
use Erdigo\ReceiverTarget;
use Erdigo\RetryPolicy;

/**
 * The gateway of docs/cloud-relay.md: `pair` once, then `run`, which fetches operations from the relay and writes them
 * to the Erdigo app through the SDK, as a paired device.
 */
final class Gateway
{
    private const USAGE = <<<'TEXT'
gateway: brings operations from an Erdigo cloud relay to the Erdigo app on this local network, as a paired device.

  pair --name NAME [--kinds LIST] [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
       [--profile NAME] [--state-dir DIR]
      Pairs once, as 'erdigo-send pair' does. The code or the QR text is read from the keyboard, or from
      --code-file, a file only you can read. It is never accepted as a command-line argument.
      --kinds is pin,marker,note,photo,track by default.

  run --relay URL [--host HOST --port PORT] [--once] [--profile NAME] [--state-dir DIR]
      Fetches from the relay and writes to the app until it is stopped. The gateway token is read from
      ERDIGO_RELAY_TOKEN. --relay must start with https://, or http:// for a relay on this computer.
      --once stops when the relay has nothing more.

  Without --host and --port the app is found on the local network. --profile is "cloud-gateway" by
  default; the gateway's key, its pairing and its place in the relay's queue are kept in that profile's
  folder, or in --state-dir.

  run exits with 0 when stopped or, with --once, when nothing more is waiting; 1 for a mistake in the
  command line or the environment; 2 when the relay refuses the token or breaks its API; 3 when, with
  --once, the app or the relay could not be reached within about 30 seconds.

TEXT;

    private const DEFAULT_PROFILE = 'cloud-gateway';
    private const WAIT_SECONDS = 25;
    private const MAX_ANSWER_BYTES = 8 * 1024 * 1024;
    private const ONCE_GIVE_UP_SECONDS = 30.0;
    private const APP_UNREACHABLE = 'The Erdigo app could not be reached. The operations stay on the relay.';
    /** How long to wait after an empty answer that came at once, from a relay that cannot hold a request open. */
    private const IDLE_PAUSE_SECONDS = 3.0;
    /** Receiver errors that say "not now" rather than "never" about an operation. */
    private const NOT_NOW = ['internal_error', 'rate_limited', 'too_many_errors', 'paused', 'replaced', 'stale_seq', 'seq_conflict'];

    /** @var resource */
    private $out;
    /** @var resource */
    private $err;
    /** True while an operation is being written and its place saved, when a signal to stop waits for the end. */
    private bool $inCall = false;
    private bool $stopRequested = false;
    private bool $once = false;
    /** How many times the SDK has started to connect during the current try of the operation being written. */
    private int $attempts = 0;
    /** Whether the SDK had a connection to the app when the current try began. */
    private bool $connectedAtStart = false;
    /** When the first sign of trouble with the operation being written showed, or null while there has been none. */
    private ?float $troubleSince = null;
    /** Whether the gateway has said, for the operation being written, that the app does not answer. */
    private bool $troubleSaid = false;
    private int $batchDone = 0;
    private int $batchRefused = 0;
    private int $batchLast = 0;
    /** @var resource|null Held while `run` runs, so that two gateways never share a place in the relay's queue. */
    private $runLock = null;

    private function __construct()
    {
        $this->out = defined('STDOUT') ? STDOUT : fopen('php://stdout', 'w');
        $this->err = defined('STDERR') ? STDERR : fopen('php://stderr', 'w');
    }

    /** @param list<string> $argv */
    public static function main(array $argv): int
    {
        $gateway = new self();
        $arguments = array_values(array_slice($argv, 1));
        if ($arguments === [] || in_array($arguments[0], ['help', '--help', '-h'], true)) {
            fwrite($gateway->out, self::USAGE);
            return 0;
        }
        try {
            switch ($arguments[0]) {
                case 'pair':
                    $gateway->pair(array_slice($arguments, 1));
                    return 0;
                case 'run':
                    $gateway->run(array_slice($arguments, 1));
                    return 0;
                default:
                    throw new UsageException("unknown command. See 'gateway help'.");
            }
        } catch (GatewayExit $exit) {
            $gateway->finishBatch();
            if ($exit->getMessage() !== '') {
                fwrite($gateway->err, ($exit->exitCode === 1 ? 'gateway: ' : '') . $exit->getMessage() . "\n");
            }
            return $exit->exitCode;
        } catch (UsageException | ErdigoException | WireException $error) {
            fwrite($gateway->err, 'gateway: ' . $error->getMessage() . "\n");
            return 1;
        } catch (\Throwable $error) {
            // Its message, without the trace, which shows paths and arguments.
            fwrite($gateway->err, 'gateway: unexpected error: ' . $error->getMessage() . "\n");
            return 1;
        }
    }

    private function say(string $line): void
    {
        fwrite($this->out, $line . "\n");
        fflush($this->out);
    }

    // Pairing

    /** @param list<string> $arguments */
    private function pair(array $arguments): void
    {
        $options = new GatewayOptions(
            $arguments,
            ['name', 'kinds', 'host', 'port', 'code-file', 'fingerprint', 'profile', 'state-dir'],
            [],
        );
        $store = self::store($options);
        $name = $options->required('name');
        $kinds = self::kinds($options->optional('kinds') ?? 'pin,marker,note,photo,track');
        $codeFile = $options->optional('code-file');
        $fingerprint = $options->optional('fingerprint');
        $fingerprint = $fingerprint === null ? null : Hex::lowercase($fingerprint);
        $where = self::target($options);
        if ($fingerprint !== null && !Hex::isFingerprint($fingerprint)) {
            throw new UsageException('--fingerprint must be 64 hexadecimal digits');
        }
        if ($store->pairings() !== []) {
            throw new UsageException(
                "this profile already holds a pairing, and a gateway's profile holds exactly one. Use another --profile "
                . "or --state-dir, or remove that pairing first with 'php bin/erdigo-send forget' and the same --profile or --state-dir.",
            );
        }

        $text = $codeFile !== null ? CommandFiles::readCodeFile($codeFile) : self::readCodeFromKeyboard();
        try {
            $ticket = PairingTicket::parse($text);
        } catch (WireException $error) {
            throw new UsageException($error->detail);
        } finally {
            sodium_memzero($text);
        }
        if ($fingerprint !== null) {
            if ($ticket->fingerprint !== null && $ticket->fingerprint !== $fingerprint) {
                throw new ErdigoException(ErrorKind::FingerprintMismatch);
            }
            $ticket = new PairingTicket($ticket->code, $fingerprint);
        }
        // Only a person can compare the key, so a bare code needs one at a terminal, before the receiver is contacted.
        if ($ticket->fingerprint === null && !Terminal::personIsThere()) {
            throw new UsageException(PHP_OS_FAMILY === 'Windows'
                ? "a bare code needs a person to compare the receiver's key, which this edition cannot ask on Windows. Use the QR text, or give the full 64-digit key with --fingerprint."
                : "a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.");
        }

        $waiting = 'Waiting for the request to be confirmed in the Erdigo app...';
        $personCompares = $ticket->fingerprint === null;
        $this->say($personCompares ? 'Checking the code with the receiver...' : $waiting);
        $identity = $store->loadOrCreateIdentity();
        try {
            $pairing = Pairing::pair(
                $where,
                $ticket,
                $identity,
                $name,
                $kinds,
                confirmKey: function (string $key) use ($waiting): bool {
                    $this->say("Receiver key: {$key}");
                    $answer = Terminal::ask('If this matches the key shown on the receiver, type yes: ');
                    if ($answer === null || Hex::lowercase(Utf8::trim($answer)) !== 'yes') {
                        $this->say('');
                        return false;
                    }
                    $this->say($waiting);
                    return true;
                },
            );
        } finally {
            $identity->clear();
        }
        $store->save($pairing);
        $allowed = implode(', ', array_map(static fn (ItemKind $kind): string => $kind->value, $pairing->kinds));
        $this->say("Paired as '{$pairing->label}'. Allowed: {$allowed}.");
        if (!$personCompares) {
            $this->say('Receiver key: ' . IntegrationV1::shortFingerprint($pairing->fingerprint()));
        }
    }

    private static function readCodeFromKeyboard(): string
    {
        if (PHP_OS_FAMILY === 'Windows') {
            throw new UsageException('this edition cannot read a code from the keyboard on Windows without showing it. Use --code-file with a file only you can read.');
        }
        return Terminal::readSecretLine('Pairing code: ', 1024)
            ?? throw new UsageException('no terminal to read the code from. Use --code-file with a file of mode 0600.');
    }

    /** @return list<ItemKind> */
    private static function kinds(string $text): array
    {
        $kinds = [];
        foreach (explode(',', $text) as $piece) {
            $piece = trim($piece, " \t");
            if ($piece !== '') {
                $kinds[] = ItemKind::tryFrom($piece) ?? throw new UsageException('--kinds takes pin, marker, note, photo and track, separated by commas.');
            }
        }
        if ($kinds === []) {
            throw new UsageException('--kinds needs at least one item kind');
        }
        return ItemKind::canonicalList($kinds);
    }

    private static function store(GatewayOptions $options): FileDeviceStore
    {
        $profile = $options->optional('profile');
        $path = $options->optional('state-dir');
        if ($profile !== null && $path !== null) {
            throw new UsageException('--state-dir and --profile cannot be used together, because a state folder holds one identity');
        }
        if ($profile !== null && ($profile === '' || preg_match('/^[\p{L}\p{M}\p{Nd}\p{Nl}\p{No}_-]+$/uD', $profile) !== 1)) {
            throw new UsageException("--profile may contain only letters, digits, '-' and '_'");
        }
        return new FileDeviceStore($path ?? FileDeviceStore::defaultDirectory($profile ?? self::DEFAULT_PROFILE));
    }

    private static function target(GatewayOptions $options): ReceiverTarget
    {
        $host = $options->optional('host');
        $port = $options->optional('port');
        if ($host === null && $port === null) {
            return ReceiverTarget::discover();
        }
        if ($host === null || $port === null) {
            throw new UsageException('--host and --port go together');
        }
        if (preg_match('/^[0-9]{1,5}$/D', $port) !== 1 || (int) $port < 1 || (int) $port > 65535) {
            throw new UsageException('--port must be between 1 and 65535');
        }
        return ReceiverTarget::address($host, (int) $port);
    }

    // Running

    /** @param list<string> $arguments */
    private function run(array $arguments): void
    {
        $options = new GatewayOptions($arguments, ['relay', 'host', 'port', 'profile', 'state-dir'], ['once']);
        $relay = RelayUrl::parse($options->required('relay'));
        $once = $options->flag('once');
        $target = self::target($options);
        $token = getenv('ERDIGO_RELAY_TOKEN');
        if (!is_string($token) || $token === '') {
            throw new UsageException('set ERDIGO_RELAY_TOKEN to the gateway token. A token is never taken as a command-line argument.');
        }
        if (preg_match(RelayConfig::TOKEN_PATTERN, $token) !== 1) {
            throw new UsageException("ERDIGO_RELAY_TOKEN is not a token: 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.");
        }
        if ($relay->https && !extension_loaded('openssl')) {
            throw new UsageException("PHP's openssl extension is needed for an https:// relay.");
        }
        $store = self::store($options);
        $this->lockState($store->directory);
        $pairings = $store->pairings();
        if (count($pairings) !== 1) {
            throw new UsageException($pairings === []
                ? "no pairing. Run 'gateway pair' first, with the same --profile or --state-dir."
                : "this profile holds several pairings, and a gateway's profile must hold exactly one.");
        }
        $place = RelayPlace::load($store->directory, $relay->url);
        $identity = $store->loadOrCreateIdentity();
        $this->once = $once;
        // While the app cannot be reached, the SDK holds the request and resends it with the same sequence number
        // (spec sections 7 and 13), so an answer lost on the way does not make the app apply it twice. Only --once
        // gives up. The SDK counts its time from the last connection it made, so the gateway keeps its own count as
        // well (see noteTrouble).
        $retry = $once ? RetryPolicy::upTo(self::ONCE_GIVE_UP_SECONDS) : RetryPolicy::forever();
        $watched = new WatchedStore($store, function (): void {
            if ($this->stopRequested) {
                throw new GatewayExit(0);
            }
            $this->attempts += 1;
            // The first connection of a try that began without one is the usual start. Any other means that the SDK
            // lost its connection, or could not make one, and is about to try the same request again.
            if ($this->attempts === 1 && !$this->connectedAtStart) {
                return;
            }
            $this->noteTrouble();
            if (!$this->troubleSaid) {
                $this->troubleSaid = true;
                // The SDK does not say when it tries next, so neither does this line.
                $this->say('No answer from the Erdigo app; trying again.');
            }
        });
        $client = new ErdigoClient($identity, $pairings[0], $watched, $target, $retry);
        $this->handleSignals();
        try {
            $this->loop($relay, $token, $store->directory, $place, $client, $once);
        } finally {
            $client->close();
            $identity->clear();
        }
    }

    private function lockState(string $directory): void
    {
        $path = $directory . DIRECTORY_SEPARATOR . 'gateway.lock';
        $handle = PrivateFile::quietly(static fn () => fopen($path, 'c'));
        if ($handle === false) {
            throw new GatewayExit(1, 'the state folder cannot be used: gateway.lock could not be opened');
        }
        if (!PrivateFile::isWindows()) {
            @chmod($path, 0600);
        }
        if (!flock($handle, LOCK_EX | LOCK_NB)) {
            throw new GatewayExit(1, 'another gateway is running with this profile or state folder');
        }
        $this->runLock = $handle;
    }

    private function loop(RelayUrl $relay, string $token, string $directory, RelayPlace $place, ErdigoClient $client, bool $once): void
    {
        $missedShownAt = null;
        while (true) {
            $started = Clock::now();
            $page = $this->fetch($relay, $token, $place, $once);
            if ($page['reset']) {
                if ($place->epoch !== null) {
                    $this->say('The relay started a new numbering; starting from its oldest operation.');
                }
                $place = $place->withEpoch($page['epoch']);
                $place->save($directory);
            }
            if ($page['missed'] && $missedShownAt !== [$place->epoch, $place->after]) {
                $this->say('Some operations expired on the relay before they were fetched.');
                $missedShownAt = [$place->epoch, $place->after];
            }
            if ($page['ops'] === []) {
                if ($once) {
                    return;
                }
                // The app closes an idle connection after five minutes, so do not keep one waiting.
                $client->close();
                if (Clock::now() - $started < 1.0) {
                    $this->pause(self::IDLE_PAUSE_SECONDS);
                }
                continue;
            }
            foreach ($page['ops'] as [$seq, $op]) {
                $refusal = $this->apply($client, $seq, $op, $once);
                if ($refusal !== null) {
                    $this->batchRefused += 1;
                    $this->say("Refused operation {$seq} (" . (Operations::knownName($op) ?? 'unknown') . "): {$refusal}");
                }
                $place = $place->withAfter($seq);
                $place->save($directory);
                $this->batchDone += 1;
                $this->batchLast = $seq;
                $this->inCall = false;
                if ($this->stopRequested) {
                    throw new GatewayExit(0);
                }
            }
            $this->finishBatch();
        }
    }

    /** Prints what the operations fetched last came to, if any were handled. */
    private function finishBatch(): void
    {
        if ($this->batchDone > 0) {
            $this->say('Done with ' . $this->batchDone . ' operation' . ($this->batchDone === 1 ? '' : 's')
                . ", up to {$this->batchLast} ({$this->batchRefused} refused)");
        }
        $this->batchDone = 0;
        $this->batchRefused = 0;
    }

    /**
     * Fetches the next operations, waiting and trying again while the relay cannot be reached.
     *
     * @return array{epoch: string, reset: bool, missed: bool, ops: list<array{0: int, 1: JsonObject}>}
     */
    private function fetch(RelayUrl $relay, string $token, RelayPlace $place, bool $once): array
    {
        $wait = $once ? 0 : self::WAIT_SECONDS;
        $query = ($place->epoch === null ? '' : 'epoch=' . $place->epoch . '&') . 'after=' . $place->after . '&limit=100&wait=' . $wait;
        $attempt = 0;
        $outageStart = null;
        while (true) {
            $certificate = false;
            $tryStart = Clock::now();
            $timeout = $wait + 15.0;
            if ($once && $outageStart !== null) {
                // A try never runs past the time --once allows, so a relay that never answers is given up on in time.
                $timeout = max(1.0, min($timeout, self::ONCE_GIVE_UP_SECONDS - ($tryStart - $outageStart)));
            }
            try {
                [$status, $headers, $body] = RelayHttp::get($relay, $relay->path . '/v1/ops?' . $query, $token, $timeout, self::MAX_ANSWER_BYTES);
                if ($status === 200) {
                    return self::readPage($headers, $body, $place);
                }
                if ($status === 401) {
                    throw new GatewayExit(2, 'The relay refused the token.');
                }
                if ($status < 500 && $status !== 408 && $status !== 429) {
                    throw new GatewayExit(2, "The relay answered with something that does not follow its API (HTTP {$status}).");
                }
            } catch (RelayBroken $broken) {
                throw new GatewayExit(2, 'The relay answered with something that does not follow its API: ' . $broken->getMessage() . '.');
            } catch (RelayUnreachable $unreachable) {
                $certificate = $unreachable->certificate;
            }
            $now = Clock::now();
            // Counted from the start of the first failed try, so the time a try takes counts as well.
            $outageStart ??= $tryStart;
            if ($once && $now - $outageStart >= self::ONCE_GIVE_UP_SECONDS) {
                throw new GatewayExit(3, $certificate ? "The relay's certificate could not be verified." : 'The relay could not be reached.');
            }
            $delay = $this->delay($attempt++, $once, $now - $outageStart);
            $this->say(($certificate ? "The relay's certificate could not be verified" : 'The relay could not be reached')
                . '; trying again in ' . self::seconds($delay) . '.');
            $this->pause($delay);
        }
    }

    /**
     * The relay's answer, checked against the API before anything in it is used.
     *
     * @param array<string, string> $headers
     * @return array{epoch: string, reset: bool, missed: bool, ops: list<array{0: int, 1: JsonObject}>}
     */
    private static function readPage(array $headers, string $body, RelayPlace $place): array
    {
        $broken = static fn (string $why) => new GatewayExit(2, "The relay answered with something that does not follow its API: {$why}.");
        if (!Relay::isJson($headers['content-type'] ?? '')) {
            throw $broken('the answer is not JSON');
        }
        try {
            $root = StrictJson::parse($body, Operations::MAX_DEPTH);
        } catch (JsonParseException) {
            throw $broken('the answer is not strict JSON');
        }
        if (!$root instanceof JsonObject) {
            throw $broken('the answer is not a JSON object');
        }
        $fields = $root->fields;
        $keys = array_map('strval', array_keys($fields));
        sort($keys);
        if ($keys !== ['epoch', 'missed', 'ops', 'reset']) {
            throw $broken('the answer does not have exactly epoch, reset, missed and ops');
        }
        if (!is_string($fields['epoch']) || preg_match(Relay::EPOCH_PATTERN, $fields['epoch']) !== 1) {
            throw $broken('the epoch is not valid');
        }
        if (!is_bool($fields['reset']) || !is_bool($fields['missed'])) {
            throw $broken('reset and missed are not true or false');
        }
        if ($fields['reset'] !== ($place->epoch === null || $fields['epoch'] !== $place->epoch)) {
            throw $broken('reset does not agree with the epoch');
        }
        if (!is_array($fields['ops']) || count($fields['ops']) > 100) {
            throw $broken('ops is not a list of at most 100 operations');
        }
        $previous = $fields['reset'] ? 0 : $place->after;
        $ops = [];
        foreach ($fields['ops'] as $entry) {
            $entryFields = $entry instanceof JsonObject ? $entry->fields : [];
            $entryKeys = array_map('strval', array_keys($entryFields));
            sort($entryKeys);
            if ($entryKeys !== ['op', 'received_ms', 'seq']) {
                throw $broken('an entry does not have exactly seq, received_ms and op');
            }
            $seq = $entryFields['seq'];
            if (!is_int($seq) || $seq <= $previous || $seq > IntegrationV1::MAX_SEQUENCE) {
                throw $broken('the sequence numbers do not go up from the place asked for');
            }
            if (!is_int($entryFields['received_ms']) || $entryFields['received_ms'] < 0 || $entryFields['received_ms'] > IntegrationV1::MAX_SEQUENCE) {
                throw $broken('received_ms is not a time');
            }
            if (!$entryFields['op'] instanceof JsonObject) {
                throw $broken('an operation is not a JSON object');
            }
            $ops[] = [$seq, $entryFields['op']];
            $previous = $seq;
        }
        return ['epoch' => $fields['epoch'], 'reset' => $fields['reset'], 'missed' => $fields['missed'], 'ops' => $ops];
    }

    /**
     * Writes one operation, waiting and trying again while the app cannot take it. Returns null when the app took it,
     * or the code of a refusal that would not change on another try. With --once, gives up with exit code 3 once the
     * operation has been in trouble for ONCE_GIVE_UP_SECONDS (see noteTrouble).
     */
    private function apply(ErdigoClient $client, int $seq, JsonObject $op, bool $once): ?string
    {
        // Until the caller has saved the place, a signal to stop waits.
        $this->inCall = true;
        if (Operations::problem($op) !== null) {
            return 'invalid_op';
        }
        $attempt = 0;
        $this->troubleSince = null;
        $this->troubleSaid = false;
        while (true) {
            $this->inCall = true;
            $this->attempts = 0;
            $this->connectedAtStart = $client->limits() !== null;
            try {
                self::write($client, $op);
                return null;
            } catch (WireException $error) {
                return $error->errorCode->value;
            } catch (ErdigoException $error) {
                $refusal = self::refusal($error);
                if ($refusal !== null) {
                    return $refusal;
                }
                if ($error->kind === ErrorKind::Storage) {
                    throw new GatewayExit(1, 'the state folder cannot be used: ' . $error->getMessage());
                }
                $noAnswer = in_array($error->kind, [ErrorKind::NoAnswer, ErrorKind::TimedOut], true);
                if ($noAnswer && $once) {
                    // A stop asked for while the SDK made its last try is still a stop.
                    if ($this->stopRequested) {
                        throw new GatewayExit(0);
                    }
                    // The SDK has been trying for as long as --once allows.
                    throw new GatewayExit(3, self::APP_UNREACHABLE);
                }
                // Not the operation's fault, such as internal_error or rate_limited, which leave it unapplied (spec
                // section 10). It is sent again later, as a new request.
                $reason = $noAnswer ? null : self::notNowReason($error);
            }
            $this->inCall = false;
            if ($this->stopRequested) {
                throw new GatewayExit(0);
            }
            // Counted from the end of this try, so that the time the SDK spent pacing it is not.
            $elapsed = $this->noteTrouble();
            $delay = $this->delay($attempt++, $once, $elapsed);
            // This line says when, so the one without a time is not needed for this operation any more.
            $this->troubleSaid = true;
            $this->say(($reason === null ? 'No answer from the Erdigo app' : "The Erdigo app did not take operation {$seq} now ({$reason})")
                . '; trying again in ' . self::seconds($delay) . '.');
            $this->pause($delay);
        }
    }

    /**
     * Notes a sign of trouble with the operation being written: the SDK connecting again because the connection it had
     * for the operation was lost, or an answer that says "not now". Returns how long the trouble has lasted, counted
     * from its first sign, and with --once gives up with exit code 3 once that is ONCE_GIVE_UP_SECONDS.
     *
     * The SDK's RetryPolicy::upTo counts from the moment a connection was lost and starts again at every new
     * connection, so an app that takes every connection but never answers, or whose answers are all lost, would keep it
     * trying for ever. This count goes on across connections until the app takes the operation. It starts only at a
     * sign of trouble, so a healthy operation that the SDK paces slowly, up to about 100 seconds for 100 track points,
     * is never given up. An app that never answers shows trouble only once the SDK stops waiting for the answer, after
     * 15 seconds, so that case ends after about 50 seconds.
     */
    private function noteTrouble(): float
    {
        $now = Clock::now();
        $this->troubleSince ??= $now;
        $elapsed = $now - $this->troubleSince;
        if ($this->once && $elapsed >= self::ONCE_GIVE_UP_SECONDS) {
            // Called from the store as the SDK is about to connect again, this also ends that SDK call.
            throw new GatewayExit(3, self::APP_UNREACHABLE);
        }
        return $elapsed;
    }

    private static function write(ErdigoClient $client, JsonObject $op): void
    {
        $fields = $op->fields;
        $at = static fn (): Coordinate => new Coordinate($fields['lat'], $fields['lon']);
        switch ($fields['op']) {
            case 'pin':
                $client->putPin($fields['id'], $at(), $fields['title'] ?? null);
                return;
            case 'marker':
                $color = isset($fields['color']) ? MarkerColor::from($fields['color']) : null;
                $client->putMarker($fields['id'], $at(), $fields['title'] ?? null, $color);
                return;
            case 'note':
                $client->putNote($fields['id'], $at(), $fields['text']);
                return;
            case 'photo':
                $client->putPhoto($fields['id'], $at(), (string) Operations::decodePhoto($fields['data']), $fields['title'] ?? null);
                return;
            case 'track':
                $points = array_map(static function (JsonObject $point): TrackPoint {
                    $values = $point->fields;
                    return new TrackPoint(new Coordinate($values['lat'], $values['lon']), $values['ts'] ?? null, $values['alt'] ?? null);
                }, $fields['points']);
                $client->appendTrack($fields['id'], $points, $fields['title'] ?? null);
                return;
            case 'finish_track':
                $client->finishTrack($fields['id']);
                return;
            case 'move':
                $client->move($fields['id'], $at());
                return;
            case 'delete':
                $client->delete($fields['id']);
                return;
        }
        throw new ErdigoException(ErrorKind::InvalidArgument, 'not an operation');
    }

    /** The code of a refusal that another try would not change, or null if the app may take the operation later. */
    private static function refusal(ErdigoException $error): ?string
    {
        if ($error->kind === ErrorKind::NotPermitted || $error->kind === ErrorKind::InvalidArgument) {
            return $error->kind->value;
        }
        if ($error->kind !== ErrorKind::Rejected) {
            return null;
        }
        $code = $error->receiverError?->code ?? '';
        if (in_array($code, self::NOT_NOW, true)) {
            return null;
        }
        // A code this gateway does not know is a refusal for good (spec section 10). It is printed only if it looks like one.
        return preg_match('/^[a-z_]{1,40}$/D', $code) === 1 ? $code : 'rejected';
    }

    private static function notNowReason(ErdigoException $error): string
    {
        $code = $error->receiverError?->code;
        return is_string($code) && preg_match('/^[a-z_]{1,40}$/D', $code) === 1 ? $code : $error->kind->value;
    }

    /** 1, 2, 4 ... up to 60 seconds with some random variation, and with --once no later than the give-up time. */
    private function delay(int $attempt, bool $once, float $elapsed): float
    {
        $delay = ErdigoClient::backoff($attempt);
        return $once ? max(0.1, min($delay, self::ONCE_GIVE_UP_SECONDS - $elapsed)) : $delay;
    }

    private static function seconds(float $delay): string
    {
        $whole = max(1, (int) round($delay));
        return $whole === 1 ? '1 second' : "{$whole} seconds";
    }

    private function pause(float $seconds): void
    {
        // A signal ends the sleep early, and its handler stops the gateway.
        usleep((int) ($seconds * 1e6));
    }

    /**
     * Stops on Ctrl+C or SIGTERM with exit code 0: at once while waiting, after the operation being written once its
     * place is saved, or, while the app cannot be reached, before the SDK tries again. Without the pcntl extension the
     * signal ends PHP as usual, which is just as safe, because the place is only ever saved whole.
     */
    private function handleSignals(): void
    {
        if (!function_exists('pcntl_async_signals') || !function_exists('pcntl_signal')) {
            return;
        }
        pcntl_async_signals(true);
        foreach ([SIGINT, SIGTERM] as $signal) {
            pcntl_signal($signal, function (): void {
                $this->stopRequested = true;
                if (!$this->inCall) {
                    $this->finishBatch();
                    exit(0);
                }
            });
        }
    }
}
