<?php

declare(strict_types=1);

/*
 * The Erdigo cloud relay, for a web server: Apache, nginx with PHP-FPM, or shared hosting. Every request goes to this
 * script. For testing on one computer, PHP's built-in server does the same:
 *
 *     php -d display_errors=0 -S 127.0.0.1:8080 examples/cloud-relay/relay.php
 *
 * It reads ERDIGO_RELAY_SOURCE_TOKENS, ERDIGO_RELAY_GATEWAY_TOKEN and ERDIGO_RELAY_DATA_DIR from the environment, as
 * php/README.md says, and needs PHP's display_errors off and post_max_size of at least 2M. From the command line, `php relay.php check` checks them and exits with 1 if one is wrong.
 * The API is in docs/cloud-relay.md.
 */

require __DIR__ . '/src/autoload.php';

use Erdigo\Examples\CloudRelay\ConfigProblem;
use Erdigo\Examples\CloudRelay\Relay;
use Erdigo\Examples\CloudRelay\RelayConfig;
use Erdigo\Examples\CloudRelay\RelayStore;

if (PHP_SAPI !== 'cli') {
    Relay::serve();
    return;
}

$usage = "usage: php relay.php check\n"
    . "Checks the relay's settings in the environment, creates its data folder if needed, and exits with 1 if one is wrong.\n"
    . "Run it as the user the web server runs PHP as. The relay itself runs under a web server, or under\n"
    . "'php -d display_errors=0 -S 127.0.0.1:PORT relay.php' for testing.\n";
$arguments = array_slice($argv, 1);
if ($arguments === ['help'] || $arguments === ['--help']) {
    echo $usage;
    exit(0);
}
if ($arguments !== ['check']) {
    fwrite(STDERR, $usage);
    exit(1);
}
try {
    $config = RelayConfig::load();
    RelayStore::open($config)->close();
} catch (ConfigProblem $problem) {
    fwrite(STDERR, 'relay: ' . $problem->getMessage() . "\n");
    exit(1);
} catch (\Throwable $error) {
    fwrite(STDERR, 'relay: the data folder cannot be used: ' . $error->getMessage() . "\n");
    exit(1);
}
$count = count($config->sourceTokens);
echo "The settings are complete: {$count} source token" . ($count === 1 ? '' : 's')
    . ", operations kept for {$config->keepHours} hours.\n"
    . "The web server's PHP also needs display_errors off, which the relay checks on each request.\n";
exit(0);
