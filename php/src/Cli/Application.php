<?php

declare(strict_types=1);

namespace Erdigo\Cli;

use Erdigo\Core\Coordinate;
use Erdigo\Core\Hex;
use Erdigo\Core\IntegrationV1;
use Erdigo\Core\ItemKind;
use Erdigo\Core\MarkerColor;
use Erdigo\Core\PairingTicket;
use Erdigo\Core\TrackPoint;
use Erdigo\Core\Utf8;
use Erdigo\Core\WireException;
use Erdigo\ErdigoClient;
use Erdigo\ErdigoException;
use Erdigo\ErrorKind;
use Erdigo\FileDeviceStore;
use Erdigo\Pairing;
use Erdigo\ReceiverPairing;
use Erdigo\ReceiverTarget;
use Erdigo\RetryPolicy;

/**
 * erdigo-send: writes pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network.
 */
final class Application
{
    private const USAGE = <<<'TEXT'
erdigo-send: write pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network.

Pairing
  pair --name NAME --kinds pin,marker,note,photo,track [--label LABEL]
       [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
      The code is read from the keyboard, or from a file only you can read.
      It is never accepted as a command-line argument.
      A bare code means you compare the receiver's key and type yes before the receiver is asked to pair.
      That needs standard input and output to be a terminal, so a bare code is refused without one.
      The QR text or --fingerprint compares the key for you, so nothing is asked.
  list
  forget --to LABEL
  identity
      Prints this device's fingerprint.

Writing
  ping
  pin          --id ID --lat N --lon N [--title TEXT]
  marker       --id ID --lat N --lon N [--title TEXT] [--color COLOR]
  note         --id ID --lat N --lon N (--text TEXT | --text-file FILE)
  photo        --id ID --lat N --lon N --file FILE [--title TEXT]
  track-add    --id ID --lat N --lon N [--ts MILLISECONDS] [--alt METRES] [--title TEXT]
  track-finish --id ID
  move         --id ID --lat N --lon N
  delete       --id ID
  delete-all   --yes
      Options: --to LABEL (needed with several receivers), --host HOST --port PORT, --retry-for SECONDS

Testing without the Erdigo app
  serve --mock
      This edition has no mock receiver of its own. Run the .NET or Swift edition's, for example
      'dotnet run --project windows/src/erdigo-send -- serve --mock', and use its port here.

For pairing and writing commands
  --profile NAME    Use a separate identity and pairings (default: "default").
  --state-dir DIR   Keep state in DIR instead of the default folder. On a web server, use a folder
                    outside the document root.
  --redact          Hide labels, keys and other identifying output, for pasting into a bug report.
                    With pair, it needs the QR text or --fingerprint, because a hidden key cannot be compared.

The device key is kept in identity.key, in a folder only you can read (mode 0700, files 0600). It is not
encrypted. On Windows a pairing code is read only from a code file, and a bare code is refused.

TEXT;

    /** @var resource */
    private $out;
    /** @var resource */
    private $err;

    private function __construct()
    {
        $this->out = defined('STDOUT') ? STDOUT : fopen('php://stdout', 'w');
        $this->err = defined('STDERR') ? STDERR : fopen('php://stderr', 'w');
    }

    /** @param list<string> $argv The arguments as PHP gives them, starting with the program's name. */
    public static function main(array $argv): int
    {
        $application = new self();
        $arguments = array_values(array_slice($argv, 1));
        if ($arguments === [] || in_array($arguments[0], ['help', '--help', '-h'], true)) {
            $application->say(self::USAGE);
            return 0;
        }
        try {
            $application->run($arguments[0], array_slice($arguments, 1));
            return 0;
        } catch (UsageException | ErdigoException | WireException $error) {
            fwrite($application->err, 'erdigo-send: ' . $error->getMessage() . "\n");
            return 1;
        }
    }

    private function say(string $line): void
    {
        fwrite($this->out, $line . "\n");
        fflush($this->out);
    }

    /** @param list<string> $arguments */
    private function run(string $command, array $arguments): void
    {
        switch ($command) {
            case 'pair':
                $this->runPair($arguments);
                return;
            case 'list':
                $this->runList($arguments);
                return;
            case 'forget':
                $this->runForget($arguments);
                return;
            case 'identity':
                $this->runIdentity($arguments);
                return;
            case 'serve':
                throw new UsageException(
                    "this edition has no mock receiver of its own. Run the .NET or Swift edition's, for example "
                    . "'dotnet run --project windows/src/erdigo-send -- serve --mock' in the windows folder, and use its port here.",
                );
            case 'ping':
                $this->runWrite($arguments, static fn (Options $options) => static fn (ErdigoClient $client) => $client->ping());
                return;
            case 'pin':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    $at = self::coordinate($options);
                    $title = $options->optional('title');
                    return static fn (ErdigoClient $client) => $client->putPin($id, $at, $title);
                });
                return;
            case 'marker':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    $at = self::coordinate($options);
                    $title = $options->optional('title');
                    $color = null;
                    $text = $options->optional('color');
                    if ($text !== null) {
                        $color = MarkerColor::tryFrom($text) ?? throw new UsageException(
                            '--color must be one of: ' . implode(', ', array_map(static fn (MarkerColor $each): string => $each->value, MarkerColor::cases())),
                        );
                    }
                    return static fn (ErdigoClient $client) => $client->putMarker($id, $at, $title, $color);
                });
                return;
            case 'note':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    $at = self::coordinate($options);
                    $inline = $options->optional('text');
                    $file = $options->optional('text-file');
                    if (($inline === null) === ($file === null)) {
                        throw new UsageException('give either --text or --text-file');
                    }
                    $text = $inline ?? CommandFiles::readNote((string) $file);
                    return static fn (ErdigoClient $client) => $client->putNote($id, $at, $text);
                });
                return;
            case 'photo':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    $at = self::coordinate($options);
                    $title = $options->optional('title');
                    $data = CommandFiles::readPhoto($options->required('file'));
                    return static fn (ErdigoClient $client) => $client->putPhoto($id, $at, $data, $title);
                });
                return;
            case 'track-add':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    $at = self::coordinate($options);
                    $title = $options->optional('title');
                    $timestamp = null;
                    $text = $options->optional('ts');
                    if ($text !== null) {
                        if (preg_match('/^[+-]?\d{1,19}$/D', $text) !== 1 || (float) $text > PHP_INT_MAX || (float) $text < PHP_INT_MIN) {
                            throw new UsageException('--ts must be whole milliseconds');
                        }
                        $timestamp = (int) $text;
                    }
                    $altitude = null;
                    $text = $options->optional('alt');
                    if ($text !== null) {
                        $altitude = Options::number($text) ?? throw new UsageException('--alt must be a number');
                    }
                    try {
                        $point = new TrackPoint($at, $timestamp, $altitude);
                    } catch (WireException $error) {
                        throw new UsageException($error->detail);
                    }
                    return static fn (ErdigoClient $client) => $client->appendTrack($id, [$point], $title);
                });
                return;
            case 'track-finish':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    return static fn (ErdigoClient $client) => $client->finishTrack($id);
                });
                return;
            case 'move':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    $at = self::coordinate($options);
                    return static fn (ErdigoClient $client) => $client->move($id, $at);
                });
                return;
            case 'delete':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    $id = $options->required('id');
                    return static fn (ErdigoClient $client) => $client->delete($id);
                });
                return;
            case 'delete-all':
                $this->runWrite($arguments, function (Options $options): \Closure {
                    if (!$options->flag('yes')) {
                        throw new UsageException('this deletes every item this device put on the receiver. Add --yes to confirm.');
                    }
                    return static fn (ErdigoClient $client) => $client->deleteAll();
                }, ['yes']);
                return;
            default:
                $this->say(self::USAGE);
                throw new UsageException('unknown command');
        }
    }

    private static function coordinate(Options $options): Coordinate
    {
        try {
            $latitude = $options->requiredNumber('lat');
            $longitude = $options->requiredNumber('lon');
            return new Coordinate($latitude, $longitude);
        } catch (WireException $error) {
            throw new UsageException($error->detail);
        }
    }

    /** @return array{0: FileDeviceStore, 1: bool} The store and whether to redact. */
    private static function context(Options $options): array
    {
        $profile = $options->optional('profile') ?? 'default';
        if ($profile === '' || preg_match('/^[\p{L}\p{M}\p{Nd}\p{Nl}\p{No}_-]+$/uD', $profile) !== 1) {
            throw new UsageException("--profile may contain only letters, digits, '-' and '_'");
        }
        $path = $options->optional('state-dir');
        if ($path !== null) {
            if ($options->optional('profile') !== null) {
                throw new UsageException('--state-dir and --profile cannot be used together, because a state folder holds one identity');
            }
            $directory = $path;
        } else {
            $directory = FileDeviceStore::defaultDirectory($profile);
        }
        if ($options->flag('keychain')) {
            throw new UsageException('--keychain belongs to the macOS SDK. This edition keeps the device key in a file only you can read.');
        }
        return [new FileDeviceStore($directory), $options->flag('redact')];
    }

    private static function shown(bool $redact, string $text): string
    {
        return $redact ? '<redacted>' : $text;
    }

    /** @return list<ItemKind> */
    private static function kinds(string $text): array
    {
        $kinds = [];
        foreach (explode(',', $text) as $piece) {
            $piece = trim($piece, " \t");
            if ($piece === '') {
                continue;
            }
            $kinds[] = ItemKind::tryFrom($piece) ?? throw new UsageException('--kinds takes pin, marker, note, photo and track, separated by commas.');
        }
        if ($kinds === []) {
            throw new UsageException('--kinds needs at least one item kind');
        }
        return ItemKind::canonicalList($kinds);
    }

    private static function target(Options $options): ReceiverTarget
    {
        $host = $options->optional('host');
        $port = $options->optionalPort();
        if ($host === null && $port === null) {
            return ReceiverTarget::discover();
        }
        if ($host === null || $port === null) {
            throw new UsageException('--host and --port go together');
        }
        return ReceiverTarget::address($host, $port);
    }

    private static function selectPairing(FileDeviceStore $store, ?string $label): ReceiverPairing
    {
        $pairings = $store->pairings();
        if ($label !== null) {
            $found = array_values(array_filter($pairings, static fn (ReceiverPairing $pairing): bool => $pairing->label === $label));
            if (count($found) !== 1) {
                throw new UsageException($found === []
                    ? "no receiver is paired under that label. See 'erdigo-send list'."
                    : 'several receivers have that label. Forget one of them first.');
            }
            return $found[0];
        }
        return match (count($pairings)) {
            0 => throw new UsageException("no receiver is paired yet. Run 'erdigo-send pair' first."),
            1 => $pairings[0],
            default => throw new UsageException('several receivers are paired. Choose one with --to LABEL.'),
        };
    }

    private static function readCodeFromKeyboard(): string
    {
        if (PHP_OS_FAMILY === 'Windows') {
            throw new UsageException(
                'this edition cannot read a code from the keyboard on Windows without showing it. Use --code-file with a file only you can read.',
            );
        }
        return Terminal::readSecretLine('Pairing code: ', 1024)
            ?? throw new UsageException('no terminal to read the code from. Use --code-file with a file of mode 0600.');
    }

    /** @param list<string> $arguments */
    private function runPair(array $arguments): void
    {
        $options = new Options($arguments, ['keychain', 'redact']);
        [$store, $redact] = self::context($options);
        $name = $options->required('name');
        $kinds = self::kinds($options->required('kinds'));
        $label = $options->optional('label');
        $codeFile = $options->optional('code-file');
        $fingerprint = $options->optional('fingerprint');
        $fingerprint = $fingerprint === null ? null : Hex::lowercase($fingerprint);
        $where = self::target($options);
        $options->finish();

        if ($fingerprint !== null && !Hex::isFingerprint($fingerprint)) {
            throw new UsageException('--fingerprint must be 64 hexadecimal digits');
        }
        if ($label !== null) {
            Pairing::validateLabel($label);
            foreach ($store->pairings() as $pairing) {
                if ($pairing->label === $label) {
                    throw new UsageException("a receiver is already paired under that label. Choose another, or use 'erdigo-send forget --to LABEL' first.");
                }
            }
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
        if ($ticket->fingerprint === null && $redact) {
            throw new UsageException(
                "--redact hides the receiver's key, so it cannot be compared for a bare code. Use the QR text, or give the full 64-digit key with --fingerprint.",
            );
        }
        // Only a person can compare the key. Whatever a pipe or a file holds was written before the key existed, so it
        // is no answer, and a question sent to a file would be answered unseen. The receiver is not contacted when no
        // answer is possible.
        if ($ticket->fingerprint === null && !Terminal::personIsThere()) {
            throw new UsageException(PHP_OS_FAMILY === 'Windows'
                ? "a bare code needs a person to compare the receiver's key, which this edition cannot ask on Windows. Use the QR text, or give the full 64-digit key with --fingerprint."
                : "a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. Use the QR text, or give the full 64-digit key with --fingerprint.");
        }

        $waiting = 'Waiting for the request to be confirmed on the receiver...';
        $personCompares = $ticket->fingerprint === null;
        $this->say($personCompares ? 'Checking the code with the receiver...' : $waiting);
        $identity = $store->loadOrCreateIdentity();
        $pairing = Pairing::pair(
            $where,
            $ticket,
            $identity,
            $name,
            $kinds,
            $label,
            // A bare code proves only that the other side knows the code, and only a person can tell whether this is
            // the right receiver. This runs before the receiver is asked to pair.
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
        $store->save($pairing);
        $allowed = implode(', ', array_map(static fn (ItemKind $kind): string => $kind->value, $pairing->kinds));
        $this->say("Paired as '" . self::shown($redact, $pairing->label) . "'. Allowed: {$allowed}.");
        if (!$personCompares) {
            $this->say('Receiver key: ' . self::shown($redact, IntegrationV1::shortFingerprint($pairing->fingerprint())));
        }
    }

    /** @param list<string> $arguments */
    private function runList(array $arguments): void
    {
        $options = new Options($arguments, ['keychain', 'redact']);
        [$store, $redact] = self::context($options);
        $options->finish();
        $pairings = $store->pairings();
        if ($pairings === []) {
            $this->say('No receivers are paired.');
        }
        foreach ($pairings as $pairing) {
            $kinds = implode(',', array_map(static fn (ItemKind $kind): string => $kind->value, $pairing->kinds));
            $this->say(self::shown($redact, $pairing->label) . '  key '
                . self::shown($redact, IntegrationV1::shortFingerprint($pairing->fingerprint())) . "  allowed {$kinds}");
        }
    }

    /** @param list<string> $arguments */
    private function runForget(array $arguments): void
    {
        $options = new Options($arguments, ['keychain', 'redact']);
        [$store] = self::context($options);
        $label = $options->required('to');
        $options->finish();
        $pairing = self::selectPairing($store, $label);
        $store->removePairing($pairing->fingerprint());
        $this->say('Forgotten here. To remove this device on the receiver, remove it from the list of connected devices there.');
    }

    /** @param list<string> $arguments */
    private function runIdentity(array $arguments): void
    {
        $options = new Options($arguments, ['keychain', 'redact']);
        [$store, $redact] = self::context($options);
        $options->finish();
        $identity = $store->loadOrCreateIdentity();
        $this->say(self::shown($redact, $identity->fingerprint));
        $identity->clear();
    }

    /**
     * Everything common to the writing commands. `$prepare` reads its own options and returns the work to do.
     *
     * @param list<string> $arguments
     * @param callable(Options): \Closure $prepare
     * @param list<string> $extraFlags
     */
    private function runWrite(array $arguments, callable $prepare, array $extraFlags = []): void
    {
        $options = new Options($arguments, array_merge($extraFlags, ['keychain', 'redact']));
        [$store] = self::context($options);
        $label = $options->optional('to');
        $where = self::target($options);
        $retry = RetryPolicy::upTo(30.0);
        $text = $options->optional('retry-for');
        if ($text !== null) {
            $seconds = Options::number($text);
            if ($seconds === null || $seconds < 0 || $seconds > 1e9) {
                throw new UsageException('--retry-for must be a number of seconds');
            }
            $retry = RetryPolicy::upTo($seconds);
        }
        $body = $prepare($options);
        $options->finish();

        $pairing = self::selectPairing($store, $label);
        $identity = $store->loadOrCreateIdentity();
        $client = new ErdigoClient($identity, $pairing, $store, $where, $retry);
        try {
            $body($client);
        } finally {
            $client->close();
            $identity->clear();
        }
        $this->say('OK');
    }
}
