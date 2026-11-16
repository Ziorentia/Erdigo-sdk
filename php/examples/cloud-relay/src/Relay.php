<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

use Erdigo\Core\IntegrationV1;
use Erdigo\Core\JsonWriter;

/**
 * Answers one request of the relay API (docs/cloud-relay.md), as a front controller under a web server or PHP's
 * built-in server. A fetch with `wait` is held for at most MAX_HOLD_SECONDS, not the whole `wait`, because it holds a
 * PHP worker all that time.
 */
final class Relay
{
    public const EPOCH_PATTERN = '/^[A-Za-z0-9_-]{16,64}$/D';
    public const MAX_BODY = 2097152;
    public const MAX_ANSWER = 4194304;
    /** The longest a fetch that finds nothing waits for an operation to arrive. */
    public const MAX_HOLD_SECONDS = 10;
    /** How often a held fetch looks at the queue again. The lock is not held in between, so sources can send. */
    private const HOLD_STEP_SECONDS = 0.5;

    private const ROUTES = ['/v1/ops' => ['GET', 'POST'], '/v1/health' => ['GET']];

    private function __construct()
    {
    }

    /** Handles the request PHP is running for, and writes the answer and one line in PHP's error log. */
    public static function serve(): void
    {
        $displayed = self::displaysErrors((string) ini_get('display_errors'));
        ini_set('display_errors', '0');
        $method = (string) ($_SERVER['REQUEST_METHOD'] ?? 'GET');
        $uri = (string) ($_SERVER['REQUEST_URI'] ?? '/');
        $logPath = '-';
        $note = '';
        $config = null;
        try {
            // PHP writes the warnings it gives before this script runs, such as for a body over post_max_size, into
            // the answer, which then is no longer JSON, and has status 200 whatever this script says.
            if ($displayed) {
                throw new ConfigProblem("PHP's display_errors is on, so PHP's own warnings would go into the answers. Turn it off for the relay.");
            }
            $config = RelayConfig::load();
            [$status, $body, $headers, $logPath, $note] = self::handle($config, $method, $uri);
        } catch (ConfigProblem $problem) {
            error_log('erdigo-relay: not configured: ' . $problem->getMessage());
            [$status, $body, $headers] = [500, '{"error":"not_configured"}', []];
        } catch (\Throwable $error) {
            // The message names a file or a step, never a token or what an operation holds.
            error_log('erdigo-relay: ' . get_class($error) . ': ' . $error->getMessage());
            [$status, $body, $headers] = [500, '{"error":"internal_error"}', []];
        }

        header_remove('X-Powered-By');
        http_response_code($status);
        header('Content-Type: application/json');
        header('Cache-Control: no-store');
        header('X-Content-Type-Options: nosniff');
        foreach ($headers as $name => $value) {
            header("{$name}: {$value}");
        }
        header('Content-Length: ' . strlen($body));
        echo $body;

        if ($config === null || $config->log) {
            $shownMethod = preg_match('/^[A-Z]{1,10}$/D', $method) === 1 ? $method : 'OTHER';
            error_log("erdigo-relay: {$shownMethod} {$logPath} {$status}{$note}");
        }
    }

    /**
     * The answer to one request: its status, body, extra headers, and the path and note for the log.
     *
     * @return array{0: int, 1: string, 2: array<string, string>, 3: string, 4: string}
     */
    private static function handle(RelayConfig $config, string $method, string $uri): array
    {
        $query = '';
        $at = strpos($uri, '?');
        if ($at !== false) {
            $query = substr($uri, $at + 1);
            $uri = substr($uri, 0, $at);
        }
        $path = null;
        if ($config->basePath === '') {
            $path = $uri;
        } elseif (str_starts_with($uri, $config->basePath . '/')) {
            $path = substr($uri, strlen($config->basePath));
        }
        if ($path === null || !isset(self::ROUTES[$path])) {
            return self::refused(new Refusal(404, ['error' => 'not_found']), '-');
        }
        if (!in_array($method, self::ROUTES[$path], true)) {
            return self::refused(
                new Refusal(405, ['error' => 'method_not_allowed'], ['Allow' => implode(', ', self::ROUTES[$path])]),
                $path,
            );
        }
        if ($path === '/v1/health') {
            return [200, '{"ok":true}', [], $path, ''];
        }
        try {
            $token = self::bearerToken();
            $allowed = $method === 'POST' ? $config->sourceTokens : [$config->gatewayToken];
            if ($token === null || !self::isOneOf($token, $allowed)) {
                throw new Refusal(401, ['error' => 'unauthorized']);
            }
            if ($method === 'POST') {
                [$accepted, $lastSeq] = self::receive($config);
                return [200, '{"accepted":' . $accepted . ',"last_seq":' . $lastSeq . '}', [], $path, " accepted={$accepted}"];
            }
            $answer = self::fetch($config, $query);
            $note = " ops={$answer->count}" . ($answer->reset ? ' reset' : '') . ($answer->missed ? ' missed' : '');
            return [200, $answer->json, [], $path, $note];
        } catch (Refusal $refusal) {
            return self::refused($refusal, $path);
        }
    }

    /** @return array{0: int, 1: string, 2: array<string, string>, 3: string, 4: string} */
    private static function refused(Refusal $refusal, string $path): array
    {
        return [$refusal->status, JsonWriter::encode($refusal->body), $refusal->headers, $path, ' ' . $refusal->getMessage()];
    }

    /** @return array{0: int, 1: int} */
    private static function receive(RelayConfig $config): array
    {
        if (!self::isJson((string) ($_SERVER['CONTENT_TYPE'] ?? $_SERVER['HTTP_CONTENT_TYPE'] ?? ''))) {
            throw new Refusal(415, ['error' => 'unsupported_media_type']);
        }
        $announced = (string) ($_SERVER['CONTENT_LENGTH'] ?? $_SERVER['HTTP_CONTENT_LENGTH'] ?? '');
        if (preg_match('/^[0-9]+$/D', $announced) === 1) {
            // PHP drops a body over post_max_size before this script runs, so that too counts as too large.
            $postMax = RelayConfig::bytesSetting((string) ini_get('post_max_size'));
            if (strlen(ltrim($announced, '0')) > 12 || (int) $announced > self::MAX_BODY || ($postMax > 0 && (int) $announced > $postMax)) {
                throw new Refusal(413, ['error' => 'too_large']);
            }
        }
        $body = self::readBody(self::MAX_BODY + 1);
        if (strlen($body) > self::MAX_BODY) {
            throw new Refusal(413, ['error' => 'too_large']);
        }
        $ops = Operations::parseBatch($body);
        unset($body);
        $opJsons = array_map(static fn ($op): string => JsonWriter::encode($op), $ops);
        $store = RelayStore::open($config);
        try {
            return $store->append($opJsons);
        } finally {
            $store->close();
        }
    }

    /**
     * Answers at once if something is waiting. Otherwise it looks again until something arrives or the wait is over,
     * so that a gateway that asks again at once after an empty answer does not ask hundreds of times a second.
     */
    private static function fetch(RelayConfig $config, string $query): FetchAnswer
    {
        $parameters = self::parseQuery($query);
        $until = microtime(true) + min($parameters['wait'] ?? 0, self::MAX_HOLD_SECONDS);
        while (true) {
            $store = RelayStore::open($config);
            try {
                $answer = $store->fetch(
                    $parameters['epoch'] ?? null,
                    $parameters['after'] ?? 0,
                    $parameters['limit'] ?? 100,
                    self::MAX_ANSWER,
                );
            } finally {
                $store->close();
            }
            $left = $until - microtime(true);
            if ($answer->count > 0 || $left <= 0) {
                return $answer;
            }
            usleep((int) (min(self::HOLD_STEP_SECONDS, $left) * 1e6));
        }
    }

    /**
     * Whether PHP shows errors in the answer, from the display_errors setting as PHP reads it. `stderr` counts, since
     * PHP honours it only on the command line.
     */
    public static function displaysErrors(string $setting): bool
    {
        $setting = strtolower(trim($setting));
        if (in_array($setting, ['on', 'yes', 'true', 'stdout', 'stderr'], true)) {
            return true;
        }
        return (int) $setting !== 0;
    }

    /**
     * `epoch`, `after`, `limit` and `wait`, each at most once. PHP's own `$_GET` would let a repeated parameter
     * through, so the query is read here.
     *
     * @return array{epoch?: string, after?: int, limit?: int, wait?: int}
     */
    public static function parseQuery(string $query): array
    {
        $ranges = ['after' => [0, IntegrationV1::MAX_SEQUENCE], 'limit' => [1, 100], 'wait' => [0, 30]];
        $parameters = [];
        foreach ($query === '' ? [] : explode('&', $query) as $part) {
            if ($part === '') {
                continue;
            }
            $pieces = explode('=', $part, 2);
            $name = rawurldecode($pieces[0]);
            $value = isset($pieces[1]) ? rawurldecode($pieces[1]) : null;
            if ($value === null || array_key_exists($name, $parameters)) {
                throw new Refusal(400, ['error' => 'invalid_query']);
            }
            if ($name === 'epoch') {
                if (preg_match(self::EPOCH_PATTERN, $value) !== 1) {
                    throw new Refusal(400, ['error' => 'invalid_query']);
                }
                $parameters['epoch'] = $value;
                continue;
            }
            if (!isset($ranges[$name]) || preg_match('/^(?:0|[1-9][0-9]{0,15})$/D', $value) !== 1
                || (int) $value < $ranges[$name][0] || (int) $value > $ranges[$name][1]) {
                throw new Refusal(400, ['error' => 'invalid_query']);
            }
            $parameters[$name] = (int) $value;
        }
        return $parameters;
    }

    /** `application/json`, with at most a `charset=utf-8` parameter. */
    public static function isJson(string $contentType): bool
    {
        $parts = explode(';', $contentType);
        if (strtolower(trim($parts[0], " \t")) !== 'application/json') {
            return false;
        }
        $charset = false;
        foreach (array_slice($parts, 1) as $parameter) {
            $parameter = trim($parameter, " \t");
            if ($parameter === '') {
                continue;
            }
            if ($charset || preg_match('/^charset=(?:utf-8|"utf-8")$/iD', $parameter) !== 1) {
                return false;
            }
            $charset = true;
        }
        return true;
    }

    private static function bearerToken(): ?string
    {
        $header = $_SERVER['HTTP_AUTHORIZATION'] ?? $_SERVER['REDIRECT_HTTP_AUTHORIZATION'] ?? null;
        if (!is_string($header) && function_exists('getallheaders')) {
            foreach (getallheaders() as $name => $value) {
                if (strtolower((string) $name) === 'authorization') {
                    $header = $value;
                }
            }
        }
        if (!is_string($header) || preg_match('/^Bearer +([^ ]+) *$/iD', $header, $match) !== 1) {
            return null;
        }
        return $match[1];
    }

    /**
     * Compares in constant time: the hashes have one length whatever the tokens' lengths, and every token is compared.
     *
     * @param list<string> $tokens
     */
    private static function isOneOf(string $token, array $tokens): bool
    {
        $given = hash('sha256', $token, true);
        $found = false;
        foreach ($tokens as $candidate) {
            $found = hash_equals(hash('sha256', $candidate, true), $given) || $found;
        }
        return $found;
    }

    private static function readBody(int $maxBytes): string
    {
        $input = fopen('php://input', 'rb');
        if ($input === false) {
            return '';
        }
        $body = '';
        while (!feof($input) && strlen($body) < $maxBytes) {
            $chunk = fread($input, min(65536, $maxBytes - strlen($body)));
            if ($chunk === false || $chunk === '') {
                break;
            }
            $body .= $chunk;
        }
        fclose($input);
        return $body;
    }
}
