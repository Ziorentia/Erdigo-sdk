<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * An exclusive lock on a file, held until {@see self::release()} or until the object goes away. The operating system
 * releases it as well if the process ends.
 */
final class ConnectionLock
{
    /** @var resource|null */
    private $handle;

    /** @param resource $handle A file already locked with LOCK_EX. @internal */
    public function __construct($handle)
    {
        $this->handle = $handle;
    }

    public function release(): void
    {
        if ($this->handle !== null) {
            flock($this->handle, LOCK_UN);
            fclose($this->handle);
            $this->handle = null;
        }
    }

    public function __destruct()
    {
        $this->release();
    }
}
