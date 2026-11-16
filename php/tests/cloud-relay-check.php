<?php

declare(strict_types=1);

/*
 * Checks the PHP cloud relay (examples/cloud-relay/relay.php) against docs/cloud-relay.md and every case in
 * docs/cloud-relay-cases.json, over real HTTP, with PHP's built-in server on loopback ports the system picks.
 *
 *     php tests/cloud-relay-check.php
 *
 * Run it from a full clone, since it reads docs/. The tokens are made here, and go to the relay only through its
 * environment. It needs no network beyond this computer. The relay runs with display_errors off, as it must.
 */

$phpDirectory = dirname(__DIR__);
$relayScript = $phpDirectory . '/examples/cloud-relay/relay.php';
$casesFile = dirname($phpDirectory) . '/docs/cloud-relay-cases.json';
$cases = json_decode((string) file_get_contents($casesFile), false, 512, JSON_THROW_ON_ERROR);

$work = sys_get_temp_dir() . '/erdigo-relay-check-' . bin2hex(random_bytes(6));
mkdir($work, 0700);
$work = realpath($work);
$servers = [];
$failures = 0;

register_shutdown_function(static function () use (&$servers, $work): void {
    foreach ($servers as $server) {
        $server->stop();
    }
    removeTree($work);
});

function removeTree(string $path): void
{
    if (is_dir($path) && !is_link($path)) {
        foreach (scandir($path) ?: [] as $entry) {
            if ($entry !== '.' && $entry !== '..') {
                removeTree($path . '/' . $entry);
            }
        }
        @rmdir($path);
    } elseif (file_exists($path) || is_link($path)) {
        @unlink($path);
    }
}

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
    $socket = stream_socket_server('tcp://127.0.0.1:0', $errorNumber, $errorText);
    $name = stream_socket_get_name($socket, false);
    fclose($socket);
    return (int) substr((string) strrchr((string) $name, ':'), 1);
}

/** The environment of this process with some variables added or, given null, taken away. */
function environment(array $changes): array
{
    $environment = getenv();
    foreach ($changes as $name => $value) {
        if ($value === null) {
            unset($environment[$name]);
        } else {
            $environment[$name] = (string) $value;
        }
    }
    return $environment;
}

final class Server
{
    /** @var resource */
    private $process;
    public readonly int $port;
    public int $requests = 0;

    /** @param list<string> $phpSettings Given to PHP with -d, after display_errors=0. */
    public function __construct(public readonly array $settings, public readonly string $logFile, int $workers = 0, array $phpSettings = [])
    {
        global $relayScript;
        $this->port = freePort();
        $changes = $settings;
        $changes['PHP_CLI_SERVER_WORKERS'] = $workers > 1 ? $workers : null;
        $command = [PHP_BINARY];
        foreach (['display_errors=0', ...$phpSettings] as $setting) {
            array_push($command, '-d', $setting);
        }
        $this->process = proc_open(
            [...$command, '-S', '127.0.0.1:' . $this->port, $relayScript],
            [0 => ['file', PHP_OS_FAMILY === 'Windows' ? 'NUL' : '/dev/null', 'r'], 1 => ['file', $logFile, 'a'], 2 => ['file', $logFile, 'a']],
            $pipes,
            dirname($relayScript),
            environment($changes),
        );
        for ($round = 0; $round < 200; $round++) {
            $socket = @stream_socket_client('tcp://127.0.0.1:' . $this->port, $errorNumber, $errorText, 0.2);
            if ($socket !== false) {
                fclose($socket);
                return;
            }
            usleep(50000);
        }
        throw new RuntimeException('the built-in server did not start');
    }

    public function stop(): void
    {
        if (!is_resource($this->process)) {
            return;
        }
        // The built-in server's workers outlive their parent, so they are stopped first.
        $status = proc_get_status($this->process);
        if ($status['running'] && PHP_OS_FAMILY !== 'Windows') {
            exec('pkill -TERM -P ' . (int) $status['pid'] . ' 2>/dev/null');
        }
        proc_terminate($this->process);
        proc_close($this->process);
    }

    /**
     * @param array<string, string> $headers
     * @return array{0: int, 1: array<string, string>, 2: string, 3: mixed}
     */
    public function request(string $method, string $target, array $headers = [], ?string $body = null, bool $chunked = false): array
    {
        return self::answer($this->send($method, $target, $headers, $body, $chunked));
    }

    /** @return resource */
    public function send(string $method, string $target, array $headers = [], ?string $body = null, bool $chunked = false)
    {
        $socket = stream_socket_client('tcp://127.0.0.1:' . $this->port, $errorNumber, $errorText, 5);
        stream_set_timeout($socket, 60);
        $this->requests += 1;
        $head = "{$method} {$target} HTTP/1.1\r\nHost: 127.0.0.1:{$this->port}\r\nConnection: close\r\n";
        foreach ($headers as $name => $value) {
            $head .= "{$name}: {$value}\r\n";
        }
        if ($body !== null && $chunked) {
            $head .= "Transfer-Encoding: chunked\r\n\r\n";
            foreach (str_split($body, 65536) as $piece) {
                $head .= dechex(strlen($piece)) . "\r\n" . $piece . "\r\n";
            }
            $head .= "0\r\n\r\n";
        } elseif ($body !== null) {
            $head .= 'Content-Length: ' . strlen($body) . "\r\n\r\n" . $body;
        } else {
            $head .= "\r\n";
        }
        for ($written = 0; $written < strlen($head);) {
            $count = fwrite($socket, substr($head, $written));
            if ($count === false || $count === 0) {
                break;
            }
            $written += $count;
        }
        return $socket;
    }

    /** @param resource $socket */
    public static function answer($socket): array
    {
        $data = stream_get_contents($socket);
        fclose($socket);
        $end = strpos((string) $data, "\r\n\r\n");
        if ($end === false) {
            return [0, [], '', null];
        }
        $lines = explode("\r\n", substr($data, 0, $end));
        preg_match('#^HTTP/1\.[01] ([0-9]{3})#', array_shift($lines), $match);
        $headers = [];
        foreach ($lines as $line) {
            [$name, $value] = array_map('trim', explode(':', $line, 2)) + [1 => ''];
            $headers[strtolower($name)] = $value;
        }
        $body = substr($data, $end + 4);
        return [(int) ($match[1] ?? 0), $headers, $body, json_decode($body, true)];
    }

    public function log(): string
    {
        return (string) @file_get_contents($this->logFile);
    }
}

$source = token();
$secondSource = token();
$gatewayToken = token();
$tokens = ['ERDIGO_RELAY_SOURCE_TOKENS' => "{$source}, {$secondSource}", 'ERDIGO_RELAY_GATEWAY_TOKEN' => $gatewayToken];
$json = ['Content-Type' => 'application/json'];
$asSource = ['Authorization' => "Bearer {$source}"] + $json;
$asGateway = ['Authorization' => "Bearer {$gatewayToken}"];

function startServer(array $settings, string $name, int $workers = 0, array $phpSettings = []): Server
{
    global $servers, $work;
    $server = new Server($settings, "{$work}/{$name}.log", $workers, $phpSettings);
    $servers[] = $server;
    return $server;
}

function post(Server $server, string $body, ?array $headers = null): array
{
    global $asSource;
    return $server->request('POST', '/v1/ops', $headers ?? $asSource, $body);
}

function postOps(Server $server, array $ops): array
{
    return post($server, json_encode(['ops' => $ops], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_PRESERVE_ZERO_FRACTION));
}

function fetchOps(Server $server, string $query = ''): array
{
    global $asGateway;
    return $server->request('GET', '/v1/ops' . ($query === '' ? '' : "?{$query}"), $asGateway);
}

function pin(string $id = 'p1', ?string $title = null): array
{
    return ['op' => 'pin', 'id' => $id, 'lat' => 37.5663, 'lon' => 126.9779] + ($title === null ? [] : ['title' => $title]);
}

/** Sends one more pin, and gives its sequence number, which shows how many operations the relay has taken. */
function probe(Server $server): int
{
    [, , , $answer] = postOps($server, [pin('probe')]);
    return (int) ($answer['last_seq'] ?? -1);
}

function photoData(int $bytes): string
{
    return base64_encode("\x89PNG\r\n\x1A\n" . random_bytes($bytes - 8));
}

function runCheck(array $settings, array $phpSettings = []): array
{
    global $relayScript;
    $command = [PHP_BINARY];
    foreach ($phpSettings as $setting) {
        array_push($command, '-d', $setting);
    }
    $process = proc_open(
        [...$command, $relayScript, 'check'],
        [0 => ['file', PHP_OS_FAMILY === 'Windows' ? 'NUL' : '/dev/null', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']],
        $pipes,
        null,
        environment($settings),
    );
    $output = stream_get_contents($pipes[1]) . stream_get_contents($pipes[2]);
    fclose($pipes[1]);
    fclose($pipes[2]);
    return [proc_close($process), $output];
}

function modeOf(string $path): int
{
    clearstatcache(true, $path);
    return fileperms($path) & 0777;
}

// Settings ------------------------------------------------------------------------------------------------------------

$cleared = [
    'ERDIGO_RELAY_SOURCE_TOKENS' => null, 'ERDIGO_RELAY_GATEWAY_TOKEN' => null, 'ERDIGO_RELAY_DATA_DIR' => null,
    'ERDIGO_RELAY_KEEP_HOURS' => null, 'ERDIGO_RELAY_CONFIG' => null, 'ERDIGO_RELAY_BASE_PATH' => null,
    'ERDIGO_RELAY_LOG' => null, 'DOCUMENT_ROOT' => null,
];
$good = $tokens + ['ERDIGO_RELAY_DATA_DIR' => "{$work}/check-data"] + $cleared;
$settingCases = [
    ['complete settings are accepted', [], 0],
    ['no source token is refused', ['ERDIGO_RELAY_SOURCE_TOKENS' => null], 1],
    ['a source token of 31 characters is refused', ['ERDIGO_RELAY_SOURCE_TOKENS' => str_repeat('a', 31)], 1],
    ['a source token of 32 characters is accepted', ['ERDIGO_RELAY_SOURCE_TOKENS' => str_repeat('a', 32)], 0],
    ['a source token of 256 characters is accepted', ['ERDIGO_RELAY_SOURCE_TOKENS' => str_repeat('a', 256)], 0],
    ['a source token of 257 characters is refused', ['ERDIGO_RELAY_SOURCE_TOKENS' => str_repeat('a', 257)], 1],
    ['a source token with a character outside the set is refused', ['ERDIGO_RELAY_SOURCE_TOKENS' => str_repeat('a', 40) . '+'], 1],
    ['every character of the set is accepted', ['ERDIGO_RELAY_SOURCE_TOKENS' => 'AZaz09-_.~' . str_repeat('x', 30)], 0],
    ['an empty entry among the source tokens is refused', ['ERDIGO_RELAY_SOURCE_TOKENS' => "{$source},"], 1],
    ['no gateway token is refused', ['ERDIGO_RELAY_GATEWAY_TOKEN' => null], 1],
    ['a short gateway token is refused', ['ERDIGO_RELAY_GATEWAY_TOKEN' => 'short'], 1],
    ['a gateway token equal to a source token is refused', ['ERDIGO_RELAY_GATEWAY_TOKEN' => $secondSource], 1],
    ['no data folder is refused', ['ERDIGO_RELAY_DATA_DIR' => null], 1],
    ['a relative data folder is refused', ['ERDIGO_RELAY_DATA_DIR' => 'relay-data'], 1],
    ['a data folder inside the document root is refused', ['DOCUMENT_ROOT' => $work], 1],
    ['a data folder outside the document root is accepted', ['DOCUMENT_ROOT' => "{$work}/www"], 0],
    ['keeping for 0 hours is refused', ['ERDIGO_RELAY_KEEP_HOURS' => '0'], 1],
    ['keeping for 1 hour is accepted', ['ERDIGO_RELAY_KEEP_HOURS' => '1'], 0],
    ['keeping for 168 hours is accepted', ['ERDIGO_RELAY_KEEP_HOURS' => '168'], 0],
    ['keeping for 169 hours is refused', ['ERDIGO_RELAY_KEEP_HOURS' => '169'], 1],
    ['keeping for 1.5 hours is refused', ['ERDIGO_RELAY_KEEP_HOURS' => '1.5'], 1],
    ['a base path is accepted', ['ERDIGO_RELAY_BASE_PATH' => '/erdigo-relay'], 0],
    ['a base path that is not a path is refused', ['ERDIGO_RELAY_BASE_PATH' => 'erdigo relay'], 1],
];
foreach ($settingCases as [$name, $changes, $expected]) {
    [$status, $output] = runCheck(array_merge($good, $changes));
    check("settings: {$name}", $status === $expected, "exit {$status}: {$output}");
    if (str_contains($output, $source) || str_contains($output, $gatewayToken)) {
        check("settings: {$name}, without showing a token", false);
    }
}
check('settings: the data folder is created private', is_dir("{$work}/check-data") && modeOf("{$work}/check-data") === 0700);
foreach ([['1M', 1], ['2097151', 1], ['2M', 0], ['0', 0]] as [$size, $expected]) {
    [$status, $output] = runCheck($good, ["post_max_size={$size}"]);
    check("settings: PHP's post_max_size of {$size} is " . ($expected === 0 ? 'accepted' : 'refused'), $status === $expected, "exit {$status}: {$output}");
}

// Settings from a file outside the web root.
$configFile = "{$work}/relay.ini";
file_put_contents($configFile, "ERDIGO_RELAY_SOURCE_TOKENS = \"{$source}\"\nERDIGO_RELAY_GATEWAY_TOKEN = \"{$gatewayToken}\"\n"
    . "ERDIGO_RELAY_DATA_DIR = \"{$work}/check-data\"\n");
chmod($configFile, 0600);
[$status] = runCheck(['ERDIGO_RELAY_CONFIG' => $configFile] + $cleared);
check('settings: a private settings file is read', $status === 0);
chmod($configFile, 0644);
[$status] = runCheck(['ERDIGO_RELAY_CONFIG' => $configFile] + $cleared);
check('settings: a settings file others can read is refused', $status === 1);
chmod($configFile, 0600);
file_put_contents($configFile, "ERDIGO_RELAY_SOURCE_TOKEN = \"{$source}\"\n", FILE_APPEND);
[$status] = runCheck(['ERDIGO_RELAY_CONFIG' => $configFile] + $cleared);
check('settings: a settings file with an unknown name is refused', $status === 1);

// Under a web server, a relay with a setting wrong answers every request with an error and logs why.
$broken = startServer($tokens + ['ERDIGO_RELAY_DATA_DIR' => null] + $cleared, 'broken');
[$status, , , $answer] = $broken->request('GET', '/v1/health');
check('a relay without a data folder answers 500 not_configured', $status === 500 && $answer === ['error' => 'not_configured']);
[$status] = post($broken, json_encode(['ops' => [pin()]]));
check('a relay without a data folder takes nothing', $status === 500);
check('it says why in its log', str_contains($broken->log(), 'ERDIGO_RELAY_DATA_DIR is not set'));

// PHP's own settings. With display_errors on, PHP writes the warnings it gives before the relay runs into the answer,
// which is then HTML with status 200, and a post_max_size under 2 MiB empties valid batches before the relay sees them.
$working = $tokens + ['ERDIGO_RELAY_DATA_DIR' => "{$work}/php-settings"] + $cleared;
foreach (['display_errors=1', 'display_errors=stderr', 'post_max_size=1M'] as $setting) {
    $misconfigured = startServer($working, 'php-' . strtr($setting, '=', '-'), 0, [$setting]);
    [$status, $headers, , $answer] = $misconfigured->request('GET', '/v1/health');
    check("a relay with {$setting} answers 500 not_configured", $status === 500 && $answer === ['error' => 'not_configured']
        && ($headers['content-type'] ?? '') === 'application/json');
    check('and says why in its log', str_contains($misconfigured->log(), explode('=', $setting)[0]));
    $misconfigured->stop();
}
$limited = startServer($working, 'post-max-size', 0, ['post_max_size=3M']);
$overLimit = str_repeat(' ', 3 * 1048576 + 1);
foreach ([
    ['a body over post_max_size is 413 too_large', '/v1/ops', $asSource, 413, ['error' => 'too_large']],
    ['without a token, it is 401', '/v1/ops', $json, 401, ['error' => 'unauthorized']],
    ['to the health path, it is 405', '/v1/health', $json, 405, ['error' => 'method_not_allowed']],
] as [$name, $path, $headers, $expectedStatus, $expectedAnswer]) {
    [$status, $answerHeaders, $body, $answer] = $limited->request('POST', $path, $headers, $overLimit);
    check($name . ', in JSON', $status === $expectedStatus && $answer === $expectedAnswer
        && ($answerHeaders['content-type'] ?? '') === 'application/json', "{$status} " . substr($body, 0, 200));
}
[$status] = $limited->request('POST', '/v1/ops', $asSource, json_encode(['ops' => [pin()]]));
check('a relay with post_max_size of 3M takes a batch', $status === 200);
$limited->stop();

// The API ------------------------------------------------------------------------------------------------------------

$dataDirectory = "{$work}/data";
$settings = $tokens + ['ERDIGO_RELAY_DATA_DIR' => $dataDirectory] + $cleared;
$relay = startServer($settings, 'relay');

[$status, $headers, $body, $answer] = $relay->request('GET', '/v1/health');
check('health answers 200 {"ok":true}', $status === 200 && $body === '{"ok":true}');
check('answers are JSON', ($headers['content-type'] ?? '') === 'application/json');
check('answers are not cached', ($headers['cache-control'] ?? '') === 'no-store');
check('the data folder is created private', modeOf($dataDirectory) === 0700);

foreach (['/', '/v1', '/v1/', '/v1/ops/', '/v1/opsx', '/v2/ops', '/v1/health/x', '/V1/OPS', '/relay.php', '/v1/ops/../ops'] as $path) {
    [$status, $headers, , $answer] = $relay->request('GET', $path, $asGateway);
    check("GET {$path} is 404", $status === 404 && $answer === ['error' => 'not_found'] && ($headers['content-type'] ?? '') === 'application/json');
}
[$status, , , $answer] = $relay->request('DELETE', '/nowhere');
check('an unknown path with an unknown method is 404', $status === 404 && $answer === ['error' => 'not_found']);
foreach ([['PUT', '/v1/ops'], ['DELETE', '/v1/ops'], ['PATCH', '/v1/ops'], ['POST', '/v1/health'], ['PUT', '/v1/health']] as [$method, $path]) {
    [$status, $headers, , $answer] = $relay->request($method, $path, [], '{}');
    check("{$method} {$path} without a token is 405", $status === 405 && $answer === ['error' => 'method_not_allowed']);
}
[$status, $headers] = $relay->request('DELETE', '/v1/ops');
check('a 405 says which methods are allowed', ($headers['allow'] ?? '') === 'GET, POST');
[$status] = $relay->request('HEAD', '/v1/health');
check('HEAD is 405', $status === 405);

$pinBody = json_encode(['ops' => [pin()]]);
$unauthorized = [
    'no token' => $json,
    'an unknown token' => ['Authorization' => 'Bearer ' . token()] + $json,
    'the gateway token' => ['Authorization' => "Bearer {$gatewayToken}"] + $json,
    'Basic authentication' => ['Authorization' => 'Basic ' . base64_encode("user:{$source}")] + $json,
    'a token with more after it' => ['Authorization' => "Bearer {$source} {$source}"] + $json,
    'a prefix of the token' => ['Authorization' => 'Bearer ' . substr($source, 0, 40)] + $json,
    'an empty bearer' => ['Authorization' => 'Bearer '] + $json,
];
foreach ($unauthorized as $name => $headers) {
    [$status, , , $answer] = post($relay, $pinBody, $headers);
    check("POST with {$name} is 401", $status === 401 && $answer === ['error' => 'unauthorized']);
}
foreach (['no token' => [], 'a source token' => ['Authorization' => "Bearer {$source}"], 'an unknown token' => ['Authorization' => 'Bearer ' . token()]] as $name => $headers) {
    [$status, , , $answer] = $relay->request('GET', '/v1/ops', $headers);
    check("GET with {$name} is 401", $status === 401 && $answer === ['error' => 'unauthorized']);
}
[$status] = post($relay, 'hello', ['Content-Type' => 'text/plain']);
check('401 comes before 415', $status === 401);
[$status] = post($relay, str_repeat(' ', 2097153), $json);
check('401 comes before 413', $status === 401);
[$status] = $relay->request('GET', '/v1/ops?limit=0');
check('401 comes before a bad query', $status === 401);
[$status] = post($relay, $pinBody, ['Authorization' => "bearer {$source}"] + $json);
check('the scheme may be written in lower case', $status === 200);
[$status] = post($relay, $pinBody, ['Authorization' => "Bearer {$secondSource}"] + $json);
check('each source token may send', $status === 200);

foreach (['text/plain', 'application/jsonx', 'application/json; charset=latin1', 'application/json; foo=bar', 'application/x-www-form-urlencoded', 'application/json; charset=utf-8; charset=utf-8'] as $type) {
    [$status, , , $answer] = post($relay, $pinBody, ['Authorization' => "Bearer {$source}", 'Content-Type' => $type]);
    check("Content-Type {$type} is 415", $status === 415 && $answer === ['error' => 'unsupported_media_type']);
}
[$status, , , $answer] = post($relay, $pinBody, ['Authorization' => "Bearer {$source}"]);
check('no Content-Type is 415', $status === 415 && $answer === ['error' => 'unsupported_media_type']);
foreach (['application/json; charset=utf-8', 'application/json;charset=UTF-8', 'Application/JSON', 'application/json; charset="utf-8"'] as $type) {
    [$status] = post($relay, $pinBody, ['Authorization' => "Bearer {$source}", 'Content-Type' => $type]);
    check("Content-Type {$type} is accepted", $status === 200);
}

[$status, , , $answer] = post($relay, str_repeat(' ', 2097153));
check('a body of 2 MiB and one byte is 413', $status === 413 && $answer === ['error' => 'too_large']);
$photoOp = ['op' => 'photo', 'id' => 'big', 'lat' => 0, 'lon' => 0, 'type' => 'image/png', 'data' => photoData(1048576)];
$exact = json_encode(['ops' => [$photoOp]], JSON_UNESCAPED_SLASHES);
$exact .= str_repeat(' ', 2097152 - strlen($exact));
[$status] = post($relay, $exact);
check('a body of exactly 2 MiB is taken', $status === 200);
[$status, , , $answer] = $relay->request('POST', '/v1/ops', $asSource, $pinBody, true);
check('a body without Content-Length is read up to the limit', $status === 200 && ($answer['accepted'] ?? 0) === 1);
[$status, , , $answer] = $relay->request('POST', '/v1/ops', $asSource, str_repeat(' ', 2097153), true);
check('a body without Content-Length over the limit is 413', $status === 413 && $answer === ['error' => 'too_large']);

// Every case of the shared file.
$last = probe($relay);
foreach ($cases->valid_ops as $case) {
    [$status, , $body, $answer] = post($relay, json_encode(['ops' => [$case->op]], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_PRESERVE_ZERO_FRACTION));
    $ok = $status === 200 && $answer === ['accepted' => 1, 'last_seq' => $last + 1];
    check("valid: {$case->name}", $ok, "{$status} {$body}");
    $last = $ok ? $last + 1 : probe($relay);
}
foreach ($cases->invalid_ops as $case) {
    [$status, , $body, $answer] = post($relay, json_encode(['ops' => [$case->op]], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE | JSON_PRESERVE_ZERO_FRACTION));
    check(
        "invalid: {$case->name}",
        $status === 400 && ($answer['error'] ?? null) === 'invalid_op' && ($answer['index'] ?? null) === 0
            && is_string($answer['reason'] ?? null) && array_keys($answer) === ['error', 'index', 'reason'],
        "{$status} {$body}",
    );
}
check('no invalid operation was taken', probe($relay) === $last + 1);
$last += 1;
foreach ($cases->raw_bodies as $case) {
    $raw = isset($case->body_base64) ? base64_decode($case->body_base64) : $case->body;
    [$status, , $body, $answer] = post($relay, $raw);
    check("raw: {$case->name}", $status === 400 && in_array($answer['error'] ?? null, $case->errors, true), "{$status} {$body}");
}
check('no raw body was taken', probe($relay) === $last + 1);
$last += 1;

[$status, , , $answer] = postOps($relay, array_fill(0, 100, pin()));
check('100 operations are taken', $status === 200 && $answer === ['accepted' => 100, 'last_seq' => $last + 100]);
$last += 100;
[$status, , , $answer] = postOps($relay, array_fill(0, 101, pin()));
check('101 operations are invalid_body', $status === 400 && ($answer['error'] ?? null) === 'invalid_body');
[$status] = postOps($relay, [$photoOp]);
check('a photo of exactly 1048576 bytes is taken', $status === 200);
$last += 1;
[$status, , , $answer] = postOps($relay, [['data' => photoData(1048577)] + $photoOp]);
check('a photo of 1048577 bytes is invalid_op', $status === 400 && ($answer['error'] ?? null) === 'invalid_op');
[$status, , , $answer] = postOps($relay, [pin('a'), pin('b'), ['op' => 'pin', 'id' => 'c', 'lat' => 91, 'lon' => 0], pin('d')]);
check('a batch with a bad operation is refused at its index', $status === 400 && ($answer['error'] ?? null) === 'invalid_op' && ($answer['index'] ?? null) === 2);
check('and none of it is taken', probe($relay) === $last + 1);
$last += 1;
check('the relay never answers with a token or what was sent', !str_contains($relay->log(), $source) && !str_contains($relay->log(), 'Seoul City Hall'));
check('the relay logs one line per request', substr_count($relay->log(), 'erdigo-relay: ') === $relay->requests, substr_count($relay->log(), 'erdigo-relay: ') . " lines, {$relay->requests} requests");

// Fetching. Start from a queue of known operations.
$relay->stop();
removeTree($dataDirectory);
$relay = startServer($settings, 'relay2');
$sent = [];
for ($index = 1; $index <= 5; $index++) {
    $sent[] = pin("p{$index}", "Pin {$index}");
}
$sent[] = ['op' => 'track', 'id' => 't1', 'title' => 'Flight 7', 'points' => [['lat' => 1.5, 'lon' => 2, 'ts' => 1700000000000, 'alt' => -12.25]]];
$before = (int) floor(microtime(true) * 1000);
[$status, , , $answer] = postOps($relay, $sent);
check('a batch is numbered from 1 in a new epoch', $status === 200 && $answer === ['accepted' => 6, 'last_seq' => 6]);
[$status, $headers, $body, $page] = fetchOps($relay);
$epoch = $page['epoch'] ?? '';
check('a fetch without an epoch is a reset', $status === 200 && ($page['reset'] ?? null) === true && ($page['missed'] ?? null) === false);
check('the epoch has the form the API gives', preg_match('/^[A-Za-z0-9_-]{16,64}$/D', $epoch) === 1);
check('the answer has exactly epoch, reset, missed and ops', array_keys($page) === ['epoch', 'reset', 'missed', 'ops']);
check('the operations come in order', array_column($page['ops'], 'seq') === [1, 2, 3, 4, 5, 6]);
check('each operation is as it was sent', array_column($page['ops'], 'op') === $sent);
$received = $page['ops'][0]['received_ms'] ?? 0;
check('received_ms is when it was taken', is_int($received) && $received >= $before - 1000 && $received <= $before + 60000);
check('every entry has exactly seq, received_ms and op', array_keys($page['ops'][0]) === ['seq', 'received_ms', 'op']);
[, , , $page] = fetchOps($relay, "epoch={$epoch}&after=0");
check('a matching epoch is not a reset', ($page['reset'] ?? null) === false && count($page['ops']) === 6);
[, , , $page] = fetchOps($relay, "epoch={$epoch}&after=2&limit=2");
check('after and limit page through', array_column($page['ops'] ?? [], 'seq') === [3, 4]);
[, , , $page] = fetchOps($relay, 'epoch=AAAAAAAAAAAAAAAAAAAAAA&after=5');
check('another epoch is a reset from the oldest held, forgetting nothing', ($page['reset'] ?? null) === true && array_column($page['ops'], 'seq') === [3, 4, 5, 6]);
[, , , $page] = fetchOps($relay, 'after=4');
check('no epoch forgets nothing', array_column($page['ops'] ?? [], 'seq') === [3, 4, 5, 6]);
[$status, , , $answer] = fetchOps($relay, "epoch={$epoch}&after=7");
check("'after' past the last number is invalid_query", $status === 400 && $answer === ['error' => 'invalid_query']);
[$status] = fetchOps($relay, 'epoch=AAAAAAAAAAAAAAAAAAAAAA&after=7');
check("'after' past the last number with another epoch is a reset", $status === 200);
[, , , $page] = fetchOps($relay, "epoch={$epoch}&after=6");
check('fetching after the last forgets everything', ($page['ops'] ?? null) === [] && ($page['reset'] ?? null) === false);
[, , , $page] = fetchOps($relay);
check('a forgotten operation is not sent again', ($page['ops'] ?? null) === [] && ($page['reset'] ?? null) === true);
check('forgotten operations leave no file', glob("{$dataDirectory}/ops/*") === []);
// With nothing waiting, a fetch is held for its wait, but no longer than 10 seconds.
foreach ([[0, 0.0, 1.0], [2, 1.8, 4.0], [30, 9.5, 13.0]] as [$wait, $least, $most]) {
    $started = microtime(true);
    [$status, , , $page] = fetchOps($relay, "epoch={$epoch}&after=6&wait={$wait}");
    $took = microtime(true) - $started;
    check("with nothing waiting, wait={$wait} is answered after {$least} to {$most} seconds",
        $status === 200 && ($page['ops'] ?? null) === [] && $took >= $least && $took <= $most, sprintf('%.1f s', $took));
}

foreach (['limit=0', 'limit=101', 'limit=-1', 'limit=1.5', 'limit=abc', 'limit=', 'limit', 'wait=31', 'wait=-1', 'after=-1', 'after=1.5',
    'after=9007199254740992', 'after=01', 'epoch=short', 'epoch=' . str_repeat('a', 65), 'epoch=aaaaaaaaaaaaaaa%2B', 'after=1&after=1',
    "epoch={$epoch}&epoch={$epoch}", 'since=1', 'Limit=5', 'limit[]=5'] as $query) {
    [$status, , , $answer] = fetchOps($relay, $query);
    check("the query {$query} is invalid_query", $status === 400 && $answer === ['error' => 'invalid_query']);
}
foreach (['limit=1', 'limit=100', 'wait=0', 'wait=30', 'after=0', 'after=9007199254740991&epoch=AAAAAAAAAAAAAAAA', 'epoch=' . str_repeat('a', 64)] as $query) {
    [$status] = fetchOps($relay, $query);
    check("the query {$query} is accepted", $status === 200);
}

// The queue and its files.
check('state.json is private', modeOf("{$dataDirectory}/state.json") === 0600);
postOps($relay, [pin('kept')]);
$files = glob("{$dataDirectory}/ops/*.json");
check('an operation file is private', $files !== [] && modeOf($files[0]) === 0600);
$relay->stop();
$relay = startServer($settings, 'relay3');
[, , , $page] = fetchOps($relay);
check('the queue and its epoch outlast the server', ($page['epoch'] ?? '') === $epoch && array_column($page['ops'], 'seq') === [7]);
$relay->stop();
file_put_contents("{$dataDirectory}/state.json", '{"version":1,');
$relay = startServer($settings, 'relay4');
[, , , $page] = fetchOps($relay, "epoch={$epoch}&after=7");
check('a damaged state starts a new numbering', ($page['reset'] ?? null) === true && ($page['epoch'] ?? $epoch) !== $epoch && $page['ops'] === []);
check('which the log mentions', str_contains($relay->log(), 'state.json was damaged'));

// Answers are cut at 4 MiB, but always hold one operation.
for ($index = 0; $index < 3; $index++) {
    postOps($relay, [['id' => "photo{$index}"] + $photoOp]);
}
[, , $body, $page] = fetchOps($relay, 'limit=100');
check('an answer stops before 4 MiB', count($page['ops'] ?? []) === 2 && strlen($body) <= 4194304, strlen($body) . ' bytes');
$secondEpoch = $page['epoch'];
[, , , $page] = fetchOps($relay, "epoch={$secondEpoch}&after=2");
check('the rest comes next', array_column($page['ops'] ?? [], 'seq') === [3]);
fetchOps($relay, "epoch={$secondEpoch}&after=3");

// A base path, for a relay under a folder of a web site.
$relay->stop();
$based = startServer(['ERDIGO_RELAY_BASE_PATH' => '/erdigo/relay/'] + $settings, 'based');
[$status] = $based->request('GET', '/erdigo/relay/v1/health');
check('with a base path, its paths answer', $status === 200);
foreach (['/v1/health', '/erdigo/v1/health', '/erdigo/relayv1/health'] as $path) {
    [$status] = $based->request('GET', $path);
    check("with a base path, {$path} is 404", $status === 404);
}
$based->stop();

// Limits on the queue, made small for the test.
$small = startServer(['ERDIGO_RELAY_TEST_LIMITS' => 'ops=5,bytes=1000', 'ERDIGO_RELAY_DATA_DIR' => "{$work}/small"] + $settings, 'small');
[$status] = postOps($small, [pin('a'), pin('b'), pin('c')]);
check('a queue with room takes a batch', $status === 200);
[$status, $headers, , $answer] = postOps($small, [pin('d'), pin('e'), pin('f')]);
check('a batch over the number of operations is 503 queue_full', $status === 503 && $answer === ['error' => 'queue_full']);
check('queue_full says Retry-After: 30', ($headers['retry-after'] ?? '') === '30');
[$status, , , $answer] = postOps($small, [pin('d'), pin('e')]);
check('a batch that just fits is taken whole', $status === 200 && $answer === ['accepted' => 2, 'last_seq' => 5]);
[, , , $page] = fetchOps($small);
check('nothing of a refused batch was taken', array_column(array_column($page['ops'], 'op'), 'id') === ['a', 'b', 'c', 'd', 'e']);
fetchOps($small, "epoch={$page['epoch']}&after=5");
$long = str_repeat('a', 200);
[$status] = postOps($small, [pin('g', $long), pin('h', $long), pin('i', $long)]);
check('forgotten operations make room', $status === 200);
[$status, , , $answer] = postOps($small, [pin('j', $long)]);
check('a batch over the size of the queue is 503 queue_full', $status === 503 && $answer === ['error' => 'queue_full']);
$small->stop();

// Expiry, with the relay's clock moved forward instead of waiting.
$clock = "{$work}/clock";
file_put_contents($clock, '0');
$hour = 3600000;
$expiring = startServer(['ERDIGO_RELAY_KEEP_HOURS' => '1', 'ERDIGO_RELAY_TEST_CLOCK_FILE' => $clock, 'ERDIGO_RELAY_DATA_DIR' => "{$work}/expiring"] + $settings, 'expiring');
postOps($expiring, [pin('old')]);
file_put_contents($clock, (string) ($hour + 60000));
postOps($expiring, [pin('new')]);
[, , , $page] = fetchOps($expiring);
$expiringEpoch = $page['epoch'];
check('an operation older than the keeping time is dropped', array_column(array_column($page['ops'], 'op'), 'id') === ['new']);
check('and a reset says some were missed', ($page['missed'] ?? null) === true);
[, , , $page] = fetchOps($expiring, "epoch={$expiringEpoch}&after=2");
check('missed is false once past the dropped ones', ($page['missed'] ?? null) === false);
postOps($expiring, [pin('fetched')]);
fetchOps($expiring, "epoch={$expiringEpoch}&after=3");
file_put_contents($clock, (string) (3 * $hour));
[, , , $page] = fetchOps($expiring, "epoch={$expiringEpoch}&after=3");
check('operations the gateway is done with do not count as missed', ($page['missed'] ?? null) === false);
postOps($expiring, [pin('waiting')]);
file_put_contents($clock, (string) (3 * $hour + 30 * 60000));
[, , , $page] = fetchOps($expiring, "epoch={$expiringEpoch}&after=3");
check('an operation within the keeping time is kept', array_column($page['ops'], 'seq') === [4] && ($page['missed'] ?? null) === false);
file_put_contents($clock, (string) (4 * $hour + 60000));
[, , , $page] = fetchOps($expiring, "epoch={$expiringEpoch}&after=3");
check('it expires after the keeping time, and is missed', ($page['ops'] ?? null) === [] && ($page['missed'] ?? null) === true);
$expiring->stop();
file_put_contents($clock, '0');
$daily = startServer(['ERDIGO_RELAY_TEST_CLOCK_FILE' => $clock, 'ERDIGO_RELAY_DATA_DIR' => "{$work}/daily"] + $settings, 'daily');
postOps($daily, [pin('a')]);
file_put_contents($clock, (string) (23 * $hour));
[, , , $page] = fetchOps($daily);
check('by default an operation is kept for 23 hours', count($page['ops'] ?? []) === 1);
file_put_contents($clock, (string) (24 * $hour + 60000));
[, , , $page] = fetchOps($daily);
check('and dropped after 24', ($page['ops'] ?? null) === [] && ($page['missed'] ?? null) === true);
$daily->stop();

// Several requests at once, with several workers of the built-in server, which Windows does not have.
if (PHP_OS_FAMILY !== 'Windows') {
    $busy = startServer(['ERDIGO_RELAY_DATA_DIR' => "{$work}/busy"] + $settings, 'busy', 4);
    $sockets = [];
    for ($index = 0; $index < 8; $index++) {
        $ops = [];
        for ($op = 0; $op < 10; $op++) {
            $ops[] = pin("w{$index}-{$op}");
        }
        $sockets[] = $busy->send('POST', '/v1/ops', $asSource, json_encode(['ops' => $ops]));
    }
    $lastSeqs = [];
    foreach ($sockets as $socket) {
        [$status, , , $answer] = Server::answer($socket);
        $lastSeqs[] = $status === 200 ? $answer['last_seq'] : -1;
    }
    sort($lastSeqs);
    check('batches sent at once are numbered one after another', $lastSeqs === [10, 20, 30, 40, 50, 60, 70, 80], implode(',', $lastSeqs));
    [, , , $page] = fetchOps($busy);
    $seqs = array_column($page['ops'] ?? [], 'seq');
    $ids = array_column(array_column($page['ops'] ?? [], 'op'), 'id');
    $together = true;
    foreach (array_chunk($ids, 10) as $batch) {
        $together = $together && count(array_unique(array_map(static fn (string $id): string => explode('-', $id)[0], $batch))) === 1;
    }
    check('and none is lost or split', $seqs === range(1, 80) && $together);

    // A held fetch does not keep the queue locked, and answers as soon as something arrives.
    $busyEpoch = $page['epoch'] ?? '';
    fetchOps($busy, "epoch={$busyEpoch}&after=80");
    $started = microtime(true);
    $waiting = $busy->send('GET', "/v1/ops?epoch={$busyEpoch}&after=80&wait=30", $asGateway);
    usleep(1000000);
    [$status] = postOps($busy, [pin('arrived')]);
    $sentAfter = microtime(true) - $started;
    [, , , $page] = Server::answer($waiting);
    $took = microtime(true) - $started;
    check('a source can send while a fetch is held', $status === 200 && $sentAfter < 2.5, sprintf('%.1f s', $sentAfter));
    check('and the held fetch answers with it at once', array_column($page['ops'] ?? [], 'seq') === [81] && $took < 3.5, sprintf('%.1f s', $took));
    $busy->stop();
} else {
    echo "skip  requests at once (PHP's built-in server has no workers on Windows)\n";
}

echo "\n" . ($failures === 0 ? "All checks passed.\n" : "{$failures} check(s) failed.\n");
exit($failures === 0 ? 0 : 1);
