<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * The kinds of item a device can write.
 */
enum ItemKind: string
{
    case Pin = 'pin';
    case Marker = 'marker';
    case Note = 'note';
    case Photo = 'photo';
    case Track = 'track';

    /**
     * Every kind, in the order the protocol lists them.
     *
     * @return list<ItemKind>
     */
    public static function all(): array
    {
        return [self::Pin, self::Marker, self::Note, self::Photo, self::Track];
    }

    /**
     * The kinds given, without repeats, in the protocol's order.
     *
     * @param iterable<ItemKind> $kinds
     * @return list<ItemKind>
     */
    public static function canonicalList(iterable $kinds): array
    {
        $wanted = [];
        foreach ($kinds as $kind) {
            $wanted[$kind->value] = true;
        }
        return array_values(array_filter(self::all(), static fn (ItemKind $kind): bool => isset($wanted[$kind->value])));
    }

    /** `pin`, `marker` and `note` are carried by `put`. `photo` and `track` have their own messages. */
    public function usesPut(): bool
    {
        return $this === self::Pin || $this === self::Marker || $this === self::Note;
    }
}
