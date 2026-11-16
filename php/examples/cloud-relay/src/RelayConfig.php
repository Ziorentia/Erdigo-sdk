<?php

declare(strict_types=1);

namespace Erdigo\Examples\CloudRelay;

/**
 * The relay's settings. Each is read from the web server's environment (SetEnv, fastcgi_param, env[] in a PHP-FPM
 * pool), and otherwise from the INI file named by ERDIGO_RELAY_CONFIG, which belongs outside the web root.
 */
final class RelayConfig
{
    public const TOKEN_PATTERN = '/^[A-Za-z0-9._~-]{32,256}$/D';
    public const MAX_OPS = 10000;
    public const MAX_BYTES = 64 * 1024 * 1024;

    private const SETTINGS = [
        'ERDIGO_RELAY_SOURCE_TOKENS', 'ERDIGO_RELAY_GATEWAY_TOKEN', 'ERDIGO_RELAY_DATA_DIR', 'ERDIGO_RELAY_KEEP_HOURS',
        'ERDIGO_RELAY_BASE_PATH', 'ERDIGO_RELAY_LOG',
    ];

    /** @param list<string> $sourceTokens */
    private function __construct(
        public readonly array $sourceTokens,
        public readonly string $gatewayToken,
        public readonly string $dataDirectory,
        public readonly int $keepHours,
        public readonly string $basePath,
        public readonly bool $log,
        public readonly int $maxOps,
        public readonly int $maxBytes,
        public readonly ?string $clockFile,
    ) {
    }

    /**
     * Reads and checks every setting, and creates the data folder if it is missing.
     *
     * @throws ConfigProblem Saying what is wrong, never with a token in it.
     */
    public static function load(): self
    {
        $file = self::readConfigFile();
        $value = static fn (string $name): ?string => self::fromEnvironment($name) ?? $file[$name] ?? null;

        $sources = $value('ERDIGO_RELAY_SOURCE_TOKENS');
        if ($sources === null) {
            throw new ConfigProblem('ERDIGO_RELAY_SOURCE_TOKENS is not set. Give one or more source tokens, separated by commas.');
        }
        $sourceTokens = [];
        foreach (explode(',', $sources) as $token) {
            $token = trim($token, " \t");
            if (preg_match(self::TOKEN_PATTERN, $token) !== 1) {
                throw new ConfigProblem(
                    'ERDIGO_RELAY_SOURCE_TOKENS holds something that is not a token. A token is 32 to 256 characters '
                    . "from A-Z, a-z, 0-9, '-', '_', '.' and '~', and tokens are separated by commas.",
                );
            }
            $sourceTokens[] = $token;
        }
        $gatewayToken = $value('ERDIGO_RELAY_GATEWAY_TOKEN');
        if ($gatewayToken === null || preg_match(self::TOKEN_PATTERN, $gatewayToken) !== 1) {
            throw new ConfigProblem(
                "ERDIGO_RELAY_GATEWAY_TOKEN must be a token: 32 to 256 characters from A-Z, a-z, 0-9, '-', '_', '.' and '~'.",
            );
        }
        if (in_array($gatewayToken, $sourceTokens, true)) {
            throw new ConfigProblem('ERDIGO_RELAY_GATEWAY_TOKEN must differ from every source token.');
        }

        $keepText = $value('ERDIGO_RELAY_KEEP_HOURS') ?? '24';
        if (preg_match('/^[0-9]{1,3}$/D', $keepText) !== 1 || (int) $keepText < 1 || (int) $keepText > 168) {
            throw new ConfigProblem('ERDIGO_RELAY_KEEP_HOURS must be a whole number of hours from 1 to 168.');
        }

        $basePath = rtrim($value('ERDIGO_RELAY_BASE_PATH') ?? '', '/');
        if (preg_match('#^(?:/[A-Za-z0-9._~-]+)*$#D', $basePath) !== 1) {
            throw new ConfigProblem('ERDIGO_RELAY_BASE_PATH must be a path such as /erdigo-relay, or be left unset.');
        }

        $logText = strtolower($value('ERDIGO_RELAY_LOG') ?? '1');
        if (!in_array($logText, ['0', '1', 'on', 'off'], true)) {
            throw new ConfigProblem('ERDIGO_RELAY_LOG must be 1 or 0.');
        }

        // PHP empties a body over post_max_size before this script runs, so a lower one would turn valid batches away.
        $postMax = self::bytesSetting((string) ini_get('post_max_size'));
        if ($postMax > 0 && $postMax < Relay::MAX_BODY) {
            throw new ConfigProblem("PHP's post_max_size is below the 2 MiB a request may hold. Set it to 2M or more for the relay.");
        }

        $dataDirectory = self::dataDirectory($value('ERDIGO_RELAY_DATA_DIR'));

        // For the tests only: smaller queue limits, and a clock that can be moved forward.
        $maxOps = self::MAX_OPS;
        $maxBytes = self::MAX_BYTES;
        $limits = self::fromEnvironment('ERDIGO_RELAY_TEST_LIMITS');
        if ($limits !== null) {
            if (preg_match('/^ops=([0-9]{1,9}),bytes=([0-9]{1,12})$/D', $limits, $match) !== 1) {
                throw new ConfigProblem('ERDIGO_RELAY_TEST_LIMITS is not in the form ops=N,bytes=N.');
            }
            $maxOps = (int) $match[1];
            $maxBytes = (int) $match[2];
        }

        return new self(
            $sourceTokens,
            $gatewayToken,
            $dataDirectory,
            (int) $keepText,
            $basePath,
            $logText === '1' || $logText === 'on',
            $maxOps,
            $maxBytes,
            self::fromEnvironment('ERDIGO_RELAY_TEST_CLOCK_FILE'),
        );
    }

    /** A size such as `8M` from php.ini, in bytes. 0 means no limit, or a form this does not read. */
    public static function bytesSetting(string $text): int
    {
        $text = trim($text);
        if (preg_match('/^([0-9]{1,12})\s*([kKmMgG]?)$/D', $text, $match) !== 1) {
            return 0;
        }
        $multiplier = ['' => 1, 'k' => 1024, 'm' => 1048576, 'g' => 1073741824][strtolower($match[2])];
        return (int) $match[1] * $multiplier;
    }

    /** A setting from the web server or the environment. An empty one counts as not set. */
    private static function fromEnvironment(string $name): ?string
    {
        // Apache gives variables set with SetEnv a REDIRECT_ prefix after an internal rewrite.
        foreach ([$_SERVER[$name] ?? null, getenv($name), $_SERVER['REDIRECT_' . $name] ?? null] as $value) {
            if (is_string($value) && $value !== '') {
                return $value;
            }
        }
        return null;
    }

    /** @return array<string, string> */
    private static function readConfigFile(): array
    {
        $path = self::fromEnvironment('ERDIGO_RELAY_CONFIG');
        if ($path === null) {
            return [];
        }
        if (!self::isAbsolute($path)) {
            throw new ConfigProblem('ERDIGO_RELAY_CONFIG must be an absolute path.');
        }
        clearstatcache(true, $path);
        if (!is_file($path) || !is_readable($path)) {
            throw new ConfigProblem('The file named by ERDIGO_RELAY_CONFIG cannot be read.');
        }
        if (PHP_OS_FAMILY !== 'Windows' && (fileperms($path) & 0007) !== 0) {
            throw new ConfigProblem('The file named by ERDIGO_RELAY_CONFIG holds tokens, so other users must not be able to read it (chmod 600 or 640).');
        }
        if (self::insideDocumentRoot($path)) {
            throw new ConfigProblem("The file named by ERDIGO_RELAY_CONFIG is inside the web server's document root. Move it outside.");
        }
        $settings = @parse_ini_file($path, false, INI_SCANNER_RAW);
        if (!is_array($settings)) {
            throw new ConfigProblem('The file named by ERDIGO_RELAY_CONFIG is not an INI file.');
        }
        $read = [];
        foreach ($settings as $name => $setting) {
            if (!in_array($name, self::SETTINGS, true) || !is_string($setting)) {
                throw new ConfigProblem('The file named by ERDIGO_RELAY_CONFIG has a setting the relay does not know.');
            }
            if ($setting !== '') {
                $read[$name] = $setting;
            }
        }
        return $read;
    }

    private static function dataDirectory(?string $path): string
    {
        if ($path === null) {
            throw new ConfigProblem('ERDIGO_RELAY_DATA_DIR is not set. Give a folder outside the web root, where the relay keeps its queue.');
        }
        if (!self::isAbsolute($path) || str_contains($path, "\0")) {
            throw new ConfigProblem('ERDIGO_RELAY_DATA_DIR must be an absolute path.');
        }
        $path = rtrim($path, '/\\') ?: $path;
        clearstatcache(true, $path);
        if (!file_exists($path) && !@mkdir($path, 0700, true) && !is_dir($path)) {
            throw new ConfigProblem('The folder ERDIGO_RELAY_DATA_DIR could not be created.');
        }
        if (!is_dir($path) || !is_writable($path)) {
            throw new ConfigProblem('ERDIGO_RELAY_DATA_DIR must be a folder the web server can write to.');
        }
        if (self::insideDocumentRoot($path)) {
            throw new ConfigProblem("ERDIGO_RELAY_DATA_DIR is inside the web server's document root, from which the queue could be read. Choose a folder outside it.");
        }
        return $path;
    }

    private static function isAbsolute(string $path): bool
    {
        return str_starts_with($path, '/') || preg_match('/^[A-Za-z]:[\\\\\/]/', $path) === 1;
    }

    /**
     * Whether a path is inside the document root of the web server running this PHP. PHP's built-in server runs this
     * script for every request and serves no files, so its document root does not count.
     */
    private static function insideDocumentRoot(string $path): bool
    {
        if (PHP_SAPI === 'cli-server') {
            return false;
        }
        // From the command line, `check` can be given the web server's document root in the environment.
        $root = $_SERVER['DOCUMENT_ROOT'] ?? '';
        if (!is_string($root) || trim($root) === '') {
            $root = getenv('DOCUMENT_ROOT');
        }
        if (!is_string($root) || trim($root) === '') {
            return false;
        }
        $normalise = static function (string $text): string {
            $text = rtrim(str_replace('\\', '/', $text), '/');
            return PHP_OS_FAMILY === 'Windows' || PHP_OS_FAMILY === 'Darwin' ? strtolower($text) : $text;
        };
        $roots = array_filter([$normalise($root), $normalise((string) realpath($root))]);
        $paths = array_filter([$normalise($path), $normalise((string) realpath($path))]);
        foreach ($roots as $rootPath) {
            foreach ($paths as $candidate) {
                if ($candidate === $rootPath || str_starts_with($candidate, $rootPath . '/')) {
                    return true;
                }
            }
        }
        return false;
    }
}
