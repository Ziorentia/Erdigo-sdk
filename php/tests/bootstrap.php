<?php

declare(strict_types=1);

require __DIR__ . '/../autoload.php';

spl_autoload_register(static function (string $class): void {
    $prefix = 'Erdigo\\Tests\\';
    if (strncmp($class, $prefix, strlen($prefix)) !== 0) {
        return;
    }
    $path = __DIR__ . '/' . str_replace('\\', '/', substr($class, strlen($prefix))) . '.php';
    if (is_file($path)) {
        require $path;
    }
});
