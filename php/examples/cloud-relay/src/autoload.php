<?php

declare(strict_types=1);

// Loads the SDK and the classes of this example, without Composer.

require_once __DIR__ . '/../../../autoload.php';

spl_autoload_register(static function (string $class): void {
    $prefix = 'Erdigo\\Examples\\CloudRelay\\';
    if (strncmp($class, $prefix, strlen($prefix)) !== 0) {
        return;
    }
    $path = __DIR__ . '/' . str_replace('\\', '/', substr($class, strlen($prefix))) . '.php';
    if (is_file($path)) {
        require $path;
    }
});
