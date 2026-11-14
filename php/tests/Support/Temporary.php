<?php

declare(strict_types=1);

namespace Erdigo\Tests\Support;

/**
 * Temporary folders, removed when the tests end.
 */
final class Temporary
{
    /** @var list<string> */
    private static array $made = [];

    public static function directory(): string
    {
        $path = sys_get_temp_dir() . DIRECTORY_SEPARATOR . 'erdigo-php-' . bin2hex(random_bytes(6));
        mkdir($path, 0700);
        if (self::$made === []) {
            register_shutdown_function([self::class, 'removeAll']);
        }
        self::$made[] = $path;
        return realpath($path) ?: $path;
    }

    /** Removes a folder made by the code under test, as well, when the tests end. */
    public static function adopt(string $path): void
    {
        self::$made[] = $path;
    }

    public static function removeAll(): void
    {
        foreach (self::$made as $path) {
            self::remove($path);
        }
        self::$made = [];
    }

    private static function remove(string $path): void
    {
        if (is_link($path) || is_file($path)) {
            @chmod($path, 0600);
            @unlink($path);
            return;
        }
        if (!is_dir($path)) {
            return;
        }
        @chmod($path, 0700);
        foreach (scandir($path) ?: [] as $entry) {
            if ($entry !== '.' && $entry !== '..') {
                self::remove($path . DIRECTORY_SEPARATOR . $entry);
            }
        }
        @rmdir($path);
    }
}
