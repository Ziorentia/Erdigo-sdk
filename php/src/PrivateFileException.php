<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * Why {@see PrivateFile} refused a file.
 */
final class PrivateFileException extends \RuntimeException
{
    /** There is no such file. */
    public const MISSING = 'missing';
    /** The file exists but could not be opened or read. */
    public const UNREADABLE = 'unreadable';
    /** The file is a link or not a regular file, or someone other than its owner could read or replace it. */
    public const INSECURE = 'insecure';
    /** The file is larger than the caller allows. */
    public const TOO_LARGE = 'too large';

    public function __construct(public readonly string $problem, string $message, ?\Throwable $previous = null)
    {
        parent::__construct($message, 0, $previous);
    }
}
