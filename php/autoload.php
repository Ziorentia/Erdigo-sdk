<?php

declare(strict_types=1);

/*
 * Loads the SDK's classes without Composer, for a NAS or a server where Composer is not installed:
 *
 *     require '/path/to/Erdigo-sdk/php/autoload.php';
 *
 * With Composer, use its autoloader instead.
 */

if (PHP_INT_SIZE < 8) {
    throw new RuntimeException('The Erdigo SDK needs 64-bit PHP, because the protocol uses 53-bit counters.');
}

spl_autoload_register(static function (string $class): void {
    $prefix = 'Erdigo\\';
    if (strncmp($class, $prefix, strlen($prefix)) !== 0) {
        return;
    }
    $path = __DIR__ . '/src/' . str_replace('\\', '/', substr($class, strlen($prefix))) . '.php';
    if (is_file($path)) {
        require $path;
    }
});
