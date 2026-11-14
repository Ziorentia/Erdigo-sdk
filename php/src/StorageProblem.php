<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * What was wrong with stored state.
 */
enum StorageProblem: string
{
    case None = 'none';
    /** The device key file exists but its contents are not a device key. A new key was not created. */
    case IdentityDamaged = 'identity_damaged';
    /** The device key file exists but could not be opened or read. A new key was not created. */
    case IdentityUnreadable = 'identity_unreadable';
    /** The pairing file exists but its contents could not be understood. */
    case StateDamaged = 'state_damaged';
    /** The pairing file exists but could not be opened or read. */
    case StateUnreadable = 'state_unreadable';
    /** A folder or file could be read or replaced by someone else, or is in the web server's document root, so it was not used. */
    case Insecure = 'insecure';
    /** Anything else, such as a full disk. */
    case Other = 'other';
}
