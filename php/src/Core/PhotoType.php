<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * The only photo formats the protocol allows.
 */
enum PhotoType: string
{
    case Jpeg = 'image/jpeg';
    case Png = 'image/png';

    /** Detects the format from the first bytes. Anything but JPEG or PNG returns null. */
    public static function detect(string $data): ?self
    {
        if (strncmp($data, "\xFF\xD8\xFF", 3) === 0 && strlen($data) >= 3) {
            return self::Jpeg;
        }
        if (strncmp($data, "\x89PNG\r\n\x1A\n", 8) === 0 && strlen($data) >= 8) {
            return self::Png;
        }
        return null;
    }
}
