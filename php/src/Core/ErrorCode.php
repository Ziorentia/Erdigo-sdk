<?php

declare(strict_types=1);

namespace Erdigo\Core;

/**
 * The error codes of spec section 10, spelled as they are on the wire.
 */
enum ErrorCode: string
{
    case VersionUnsupported = 'version_unsupported';
    case InvalidMessage = 'invalid_message';
    case InvalidId = 'invalid_id';
    case InvalidCoordinate = 'invalid_coordinate';
    case InvalidText = 'invalid_text';
    case NotPermitted = 'not_permitted';
    case NotFound = 'not_found';
    case KindConflict = 'kind_conflict';
    case InvalidState = 'invalid_state';
    case StaleSeq = 'stale_seq';
    case SeqConflict = 'seq_conflict';
    case LimitItems = 'limit_items';
    case LimitPhotos = 'limit_photos';
    case LimitTracks = 'limit_tracks';
    case LimitDisk = 'limit_disk';
    case TooLarge = 'too_large';
    case PhotoInvalid = 'photo_invalid';
    case RateLimited = 'rate_limited';
    case TooManyErrors = 'too_many_errors';
    case Paused = 'paused';
    case Replaced = 'replaced';
    case CodeReused = 'code_reused';
    case InternalError = 'internal_error';

    /** Retryable errors do not use up the sequence number. */
    public function isRetryable(): bool
    {
        return $this === self::RateLimited || $this === self::Paused || $this === self::InternalError;
    }
}
