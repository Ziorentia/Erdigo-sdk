<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * Why a {@see FrameStream} operation failed.
 */
final class FrameStreamException extends \RuntimeException
{
    public const CLOSED = 'closed';
    public const TIMED_OUT = 'timed out';
    public const FRAME_TOO_LARGE = 'frame too large';
    public const CONNECTION_FAILED = 'connection failed';

    public function __construct(public readonly string $failure)
    {
        parent::__construct("connection: {$failure}");
    }
}
