<?php

declare(strict_types=1);

/*
 * The Erdigo cloud gateway. It runs on the same local network as the Erdigo app, fetches operations from a cloud relay
 * over HTTPS, and writes them to the app through the SDK, as a paired device:
 *
 *     php examples/cloud-relay/gateway.php pair --name "Cloud gateway" --code-file /path/to/qr-text
 *     ERDIGO_RELAY_TOKEN=... php examples/cloud-relay/gateway.php run --relay https://relay.example.com
 *
 * `php examples/cloud-relay/gateway.php help` lists the options. docs/cloud-relay.md describes what it does.
 */

if (PHP_SAPI !== 'cli') {
    exit(1);
}

require __DIR__ . '/src/autoload.php';

foreach (['sodium', 'hash'] as $extension) {
    if (!extension_loaded($extension)) {
        fwrite(STDERR, "gateway: PHP's {$extension} extension is needed.\n");
        exit(1);
    }
}

exit(Erdigo\Examples\CloudRelay\Gateway::main($argv));
