<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * The colours a marker can have.
 */
enum MarkerColor: string
{
    case Red = 'red';
    case Orange = 'orange';
    case Yellow = 'yellow';
    case Green = 'green';
    case Blue = 'blue';
    case Purple = 'purple';
    case Gray = 'gray';
}
