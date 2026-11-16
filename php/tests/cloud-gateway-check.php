<?php

declare(strict_types=1);

/*
 * Checks how the PHP cloud gateway (examples/cloud-relay/gateway.php) writes to the app when something goes wrong
 * between them, with the scripted receiver of these tests, which can lose an answer on purpose or never answer, and the
 * relay under PHP's built-in server, on loopback ports the system picks.
 *
 *     php tests/cloud-gateway-check.php
 *
 * The tokens are made here, and go to the programs only through their environment. tests/cloud-relay-smoke.sh checks
 * the rest of the gateway with the .NET or Swift mock receiver. A few checks wait 30 to 50 seconds on purpose, for the
 * gateway to give up; they run side by side, each with a relay and a receiver of its own.
 */

require __DIR__ . '/bootstrap.php';

use Erdigo\Core\ItemKind;
use Erdigo\FileDeviceStore;
use Erdigo\ReceiverPairing;
use Erdigo\Tests\Support\ScriptedReceiver;
use Erdigo\Tests\Support\Temporary;

$phpDirectory = dirname(__DIR__);
$relayScript = $phpDirectory . '/examples/cloud-relay/relay.php';
$gatewayScript = $phpDirectory . '/examples/cloud-relay/gateway.php';
$nullDevice = PHP_OS_FAMILY === 'Windows' ? 'NUL' : '/dev/null';
$work = Temporary::directory();
$failures = 0;
/** @var list<resource> The relays this script started. */
$relays = [];

function check(string $name, bool $passed, string $detail = ''): void
{
    global $failures;
    if ($passed) {
        echo "ok    {$name}\n";
        return;
    }
    $failures += 1;
    echo "FAIL  {$name}" . ($detail === '' ? '' : ": {$detail}") . "\n";
}

function token(): string
{
    return rtrim(strtr(base64_encode(random_bytes(32)), '+/', '-_'), '=');
}

function freePort(): int
{
    $socket = stream_socket_server('tcp://127.0.0.1:0');
    $name = (string) stream_socket_get_name($socket, false);
    fclose($socket);
    return (int) substr((string) strrchr($name, ':'), 1);
}

/** The environment of this process with these variables added. */
function environment(array $changes): array
{
    return array_merge(getenv(), $changes);
}

/** One request to the relay, as a source or as the gateway: [status, decoded answer]. */
function relay(int $port, string $method, string $target, string $token, ?string $body = null): array
{
    $socket = @stream_socket_client("tcp://127.0.0.1:{$port}", $errorNumber, $errorText, 5);
    if ($socket === false) {
        return [0, null];
    }
    stream_set_timeout($socket, 30);
    $head = "{$method} {$target} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\nAuthorization: Bearer {$token}\r\n";
    $head .= $body === null ? "\r\n" : "Content-Type: application/json\r\nContent-Length: " . strlen($body) . "\r\n\r\n{$body}";
    fwrite($socket, $head);
    $answer = (string) stream_get_contents($socket);
    fclose($socket);
    preg_match('#^HTTP/1\.[01] ([0-9]{3})#', $answer, $match);
    $end = strpos($answer, "\r\n\r\n");
    return [(int) ($match[1] ?? 0), $end === false ? null : json_decode(substr($answer, $end + 4), true)];
}

/** Starts a relay of its own under PHP's built-in server, and returns its port once it answers. */
function startRelay(string $name): int
{
    global $work, $relayScript, $nullDevice, $sourceToken, $gatewayToken, $relays;
    $port = freePort();
    $log = "{$work}/{$name}.log";
    $relays[] = proc_open(
        [PHP_BINARY, '-d', 'display_errors=0', '-S', "127.0.0.1:{$port}", $relayScript],
        [0 => ['file', $nullDevice, 'r'], 1 => ['file', $log, 'a'], 2 => ['file', $log, 'a']],
        $pipes,
        null,
        environment([
            'ERDIGO_RELAY_SOURCE_TOKENS' => $sourceToken,
            'ERDIGO_RELAY_GATEWAY_TOKEN' => $gatewayToken,
            'ERDIGO_RELAY_DATA_DIR' => "{$work}/{$name}-data",
        ]),
    );
    for ($round = 0; $round < 100 && relay($port, 'GET', '/v1/health', '')[0] !== 200; $round++) {
        usleep(100000);
    }
    return $port;
}

function stopRelays(): void
{
    global $relays;
    foreach ($relays as $process) {
        if (is_resource($process)) {
            proc_terminate($process);
            proc_close($process);
        }
    }
    $relays = [];
}

/**
 * A gateway state folder already paired with a new scripted receiver, which follows these settings.
 *
 * @return array{0: ScriptedReceiver, 1: string} The receiver and the state folder.
 */
function pairedGateway(string $name, array $settings): array
{
    global $work;
    $stateDirectory = "{$work}/{$name}";
    $store = new FileDeviceStore($stateDirectory);
    $identity = $store->loadOrCreateIdentity();
    $receiver = new ScriptedReceiver(['devices' => [$identity]] + $settings);
    $store->save(new ReceiverPairing('Scripted', $receiver->publicKey, ItemKind::cases(), '127.0.0.1', $receiver->port));
    $identity->clear();
    return [$receiver, $stateDirectory];
}

/**
 * Starts `gateway run --once` in the background, with its output in a file.
 *
 * @return array{process: resource, output: string, started: float}
 */
function startGateway(string $name, int $relayPort, ScriptedReceiver $receiver, string $stateDirectory): array
{
    global $work, $gatewayScript, $nullDevice, $gatewayToken;
    $output = "{$work}/{$name}.out";
    $process = proc_open(
        [PHP_BINARY, $gatewayScript, 'run', '--relay', "http://127.0.0.1:{$relayPort}", '--host', '127.0.0.1', '--port', (string) $receiver->port,
            '--state-dir', $stateDirectory, '--once'],
        [0 => ['file', $nullDevice, 'r'], 1 => ['file', $output, 'a'], 2 => ['file', $output, 'a']],
        $pipes,
        null,
        environment(['ERDIGO_RELAY_TOKEN' => $gatewayToken]),
    );
    return ['process' => $process, 'output' => $output, 'started' => microtime(true)];
}

/**
 * Waits for a gateway started with startGateway, for at most two minutes, and stops it if it is still running then.
 *
 * @param array{process: resource, output: string, started: float} $gateway
 * @return array{0: int|string, 1: string, 2: float} The exit code, or "still running", what it printed, and how many
 *     seconds it ran.
 */
function finishGateway(array $gateway): array
{
    $status = proc_get_status($gateway['process']);
    while ($status['running'] && microtime(true) - $gateway['started'] < 120) {
        usleep(100000);
        $status = proc_get_status($gateway['process']);
    }
    $took = microtime(true) - $gateway['started'];
    if ($status['running']) {
        proc_terminate($gateway['process'], 9);
    }
    proc_close($gateway['process']);
    return [$status['running'] ? 'still running' : $status['exitcode'], (string) file_get_contents($gateway['output']), $took];
}

/** @return list<string> The ids of the operations waiting on a relay. */
function waiting(int $relayPort): array
{
    global $gatewayToken;
    [, $page] = relay($relayPort, 'GET', '/v1/ops', $gatewayToken);
    return array_column(array_column($page['ops'] ?? [], 'op'), 'id');
}

/** @return list<array{lat: float, lon: float, ts: int}> */
function points(int $count): array
{
    $points = [];
    for ($index = 0; $index < $count; $index++) {
        $points[] = ['lat' => 37.5663 + $index / 10000, 'lon' => 126.9779, 'ts' => 1700000000000 + $index * 1000];
    }
    return $points;
}

$sourceToken = token();
$gatewayToken = token();
register_shutdown_function('stopRelays');
$relayPort = startRelay('relay');

// With --once the gateway gives up once an operation has been in trouble for about 30 seconds, however many
// connections the app takes, since the SDK counts its own time from the last connection it made. These start first,
// because they take a while, each with a relay and a receiver of its own.
$giveUps = [];
// An app whose every answer is lost on the way.
[$lostReceiver, $lostState] = pairedGateway('gateway-lost', ['script' => [['lose_answer' => true, 'times' => 100000]]]);
$lostPort = startRelay('relay-lost');
// An app that takes the connection and the request, and never answers.
[$heldReceiver, $heldState] = pairedGateway('gateway-held', ['script' => [['hold_answer' => true, 'times' => 100000]]]);
$heldPort = startRelay('relay-held');
// A healthy app with a slow point rate: the first track uses up the points it allows at once, so the SDK waits about
// 35 seconds before sending the second, whose answer is then lost once. That wait is not trouble.
[$slowReceiver, $slowState] = pairedGateway('gateway-slow', [
    'limits' => ['pointsPerSecond' => 1, 'pointBurst' => 100],
    'script' => [['times' => 1], ['lose_answer' => true]],
]);
$slowPort = startRelay('relay-slow');
[$lostStatus] = relay($lostPort, 'POST', '/v1/ops', $sourceToken, json_encode(['ops' => [
    ['op' => 'track', 'id' => 'walk-lost', 'points' => points(2)],
]]));
[$heldStatus] = relay($heldPort, 'POST', '/v1/ops', $sourceToken, json_encode(['ops' => [
    ['op' => 'track', 'id' => 'walk-held', 'points' => points(2)],
]]));
[$slowStatus] = relay($slowPort, 'POST', '/v1/ops', $sourceToken, json_encode(['ops' => [
    ['op' => 'track', 'id' => 'walk-long', 'points' => points(100)],
    ['op' => 'track', 'id' => 'walk-slow', 'points' => points(35)],
]]));
check('the relays take the operations for the checks that wait', $lostStatus === 200 && $heldStatus === 200 && $slowStatus === 200);
$lostGateway = startGateway('lost', $lostPort, $lostReceiver, $lostState);
$heldGateway = startGateway('held', $heldPort, $heldReceiver, $heldState);
$slowGateway = startGateway('slow', $slowPort, $slowReceiver, $slowState);

// A gateway already paired with a receiver that runs the first request it gets and then loses the answer.
[$receiver, $stateDirectory] = pairedGateway('gateway', ['script' => [['lose_answer' => true]]]);

$track = ['op' => 'track', 'id' => 'walk', 'title' => 'Walk', 'points' => [
    ['lat' => 37.5663, 'lon' => 126.9779, 'ts' => 1700000000000],
    ['lat' => 37.5665, 'lon' => 126.978, 'ts' => 1700000001000],
]];
[$status] = relay($relayPort, 'POST', '/v1/ops', $sourceToken, json_encode(['ops' => [$track, ['op' => 'pin', 'id' => 'after', 'lat' => 37.5663, 'lon' => 126.9779]]]));
check('the relay takes the operations', $status === 200);

$gateway = proc_open(
    [PHP_BINARY, $gatewayScript, 'run', '--relay', "http://127.0.0.1:{$relayPort}", '--host', '127.0.0.1', '--port', (string) $receiver->port,
        '--state-dir', $stateDirectory, '--once'],
    [0 => ['file', $nullDevice, 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']],
    $pipes,
    null,
    environment(['ERDIGO_RELAY_TOKEN' => $gatewayToken]),
);
$output = stream_get_contents($pipes[1]) . stream_get_contents($pipes[2]);
$exitCode = proc_close($gateway);
$printed = $output;
check('with an answer lost, run --once still exits 0', $exitCode === 0, "exit {$exitCode}: {$output}");
check('and it is done with both operations', str_contains($output, 'Done with 2 operations, up to 2 (0 refused)'), $output);
check('the receiver lost the answer to the track', count($receiver->events('answer_lost')) === 1);
$requests = $receiver->requests();
check('the track is sent again with the same sequence number, so the app applies it once', $receiver->seen() === [1, 1, 2]
    && $requests[0] === $requests[1], implode(',', $receiver->seen()));
check('it says it is trying again, once', substr_count($output, 'No answer from the Erdigo app; trying again.') === 1, $output);
$receiver->stop();

// Stopping while the app cannot be reached, without --once: the SDK keeps trying, and SIGTERM still ends it at once.
if (function_exists('pcntl_signal') && PHP_OS_FAMILY !== 'Windows') {
    relay($relayPort, 'POST', '/v1/ops', $sourceToken, json_encode(['ops' => [['op' => 'pin', 'id' => 'waiting', 'lat' => 0, 'lon' => 0]]]));
    $outputFile = "{$work}/running.out";
    $gateway = proc_open(
        [PHP_BINARY, $gatewayScript, 'run', '--relay', "http://127.0.0.1:{$relayPort}", '--host', '127.0.0.1', '--port', (string) $receiver->port,
            '--state-dir', $stateDirectory],
        [0 => ['file', $nullDevice, 'r'], 1 => ['file', $outputFile, 'a'], 2 => ['file', $outputFile, 'a']],
        $pipes,
        null,
        environment(['ERDIGO_RELAY_TOKEN' => $gatewayToken]),
    );
    for ($round = 0; $round < 150 && !str_contains((string) @file_get_contents($outputFile), 'No answer from the Erdigo app'); $round++) {
        usleep(100000);
    }
    usleep(1500000);
    $stopped = microtime(true);
    proc_terminate($gateway, 15);
    $status = proc_get_status($gateway);
    while ($status['running'] && microtime(true) - $stopped < 10) {
        usleep(50000);
        $status = proc_get_status($gateway);
    }
    $took = microtime(true) - $stopped;
    if ($status['running']) {
        proc_terminate($gateway, 9);
    }
    proc_close($gateway);
    $output = (string) file_get_contents($outputFile);
    $printed .= $output;
    check('with the app down, a running gateway says it is trying again', str_contains($output, 'No answer from the Erdigo app; trying again.'), $output);
    check('SIGTERM stops it with code 0 while the SDK is trying again', !$status['running'] && $status['exitcode'] === 0, "exit {$status['exitcode']}: {$output}");
    check('at once', $took < 3.0, sprintf('%.1f s', $took));
    check('the operation stays on the relay', waiting($relayPort) === ['waiting']);
    check('and the place is unchanged', str_contains((string) file_get_contents("{$stateDirectory}/relay-state.json"), '"after":2'));
} else {
    echo "skip  stopping while the app cannot be reached (PHP's pcntl extension is not loaded)\n";
}

// Every answer lost on the way: the app takes every connection, so the SDK's own count never runs out.
[$exitCode, $output, $took] = finishGateway($lostGateway);
$printed .= $output;
$seen = $lostReceiver->seen();
check('with every answer lost, run --once exits 3', $exitCode === 3, "exit {$exitCode}: {$output}");
check('about 30 seconds after the first answer was lost', $took > 29.0 && $took < 45.0, sprintf('%.1f s', $took));
check('it says that the app could not be reached', str_contains($output, 'The Erdigo app could not be reached. The operations stay on the relay.'), $output);
check('and once that it is trying again', substr_count($output, 'No answer from the Erdigo app; trying again.') === 1, $output);
check('the app was sent the track over and over, always with the same sequence number', count($seen) >= 10
    && array_unique($seen) === [1] && count(array_unique(array_map('json_encode', $lostReceiver->requests()))) === 1
    && count($lostReceiver->events('answer_lost')) === count($seen), implode(',', $seen));
check('the operation stays on the relay', waiting($lostPort) === ['walk-lost']);
check('and the place is unchanged', str_contains((string) file_get_contents("{$lostState}/relay-state.json"), '"after":0'));
$lostReceiver->stop();

// An app that never answers: each try ends when the SDK stops waiting for the answer, after 15 seconds.
[$exitCode, $output, $took] = finishGateway($heldGateway);
$printed .= $output;
$seen = $heldReceiver->seen();
check('with an app that takes the request and never answers, run --once exits 3', $exitCode === 3, "exit {$exitCode}: {$output}");
check('within about a minute', $took > 40.0 && $took < 65.0, sprintf('%.1f s', $took));
check('it says that the app could not be reached', str_contains($output, 'The Erdigo app could not be reached. The operations stay on the relay.'), $output);
check('the app was sent the track again on new connections, with the same sequence number', count($seen) >= 2
    && array_unique($seen) === [1] && count($heldReceiver->events('answer_held')) === count($seen), implode(',', $seen));
check('the operation stays on the relay', waiting($heldPort) === ['walk-held']);
$heldReceiver->stop();

// A slow operation is not trouble: only the lost answer after the SDK's wait for the point rate counts.
[$exitCode, $output, $took] = finishGateway($slowGateway);
$printed .= $output;
check('with the SDK pacing a track for about 35 seconds and its answer then lost, run --once exits 0', $exitCode === 0, "exit {$exitCode}: {$output}");
check('after that wait', $took > 33.0, sprintf('%.1f s', $took));
check('and it is done with both tracks', str_contains($output, 'Done with 2 operations, up to 2 (0 refused)'), $output);
check('the paced track is sent again with the same sequence number', $slowReceiver->seen() === [1, 2, 2], implode(',', $slowReceiver->seen()));
check('nothing stays on the relay', waiting($slowPort) === []);
$slowReceiver->stop();

check('nothing printed holds a token', !str_contains($printed, $gatewayToken) && !str_contains($printed, $sourceToken));
stopRelays();

echo "\n" . ($failures === 0 ? "All checks passed.\n" : "{$failures} check(s) failed.\n");
exit($failures === 0 ? 0 : 1);
