<?php

declare(strict_types=1);

namespace Erdigo;

/**
 * What went wrong, for {@see ErdigoException}.
 */
enum ErrorKind: string
{
    /**
     * No receiver answered. A receiver stays silent when it is off, asleep, on another network, when the pairing code
     * is wrong or expired, and when this device is not allowed. These cases cannot be told apart, by design.
     */
    case NoAnswer = 'no_answer';
    /** The receiver's key does not match the fingerprint supplied for pairing. */
    case FingerprintMismatch = 'fingerprint_mismatch';
    /** The person did not confirm the receiver's key, so the receiver was not asked to pair. */
    case KeyNotConfirmed = 'key_not_confirmed';
    /** The person at the receiver declined the request. */
    case PairingDenied = 'pairing_denied';
    /** The person at the receiver did not answer in time. */
    case PairingTimedOut = 'pairing_timed_out';
    /** The code was used twice. The receiver warned its user and removed the pairing made with it. */
    case CodeReused = 'code_reused';
    /** The receiver rejected a request. See {@see ErdigoException::$receiverError}. */
    case Rejected = 'rejected';
    /** The item kind is not among those the person allowed for this device. See {@see ErdigoException::$itemKind}. */
    case NotPermitted = 'not_permitted';
    /** The receiver does not support any protocol version this SDK speaks. See {@see ErdigoException::$supported}. */
    case VersionUnsupported = 'version_unsupported';
    /** Another connection using this device key took over. */
    case Replaced = 'replaced';
    /** Another writer using this device key advanced the sequence number. See {@see ErdigoException::$lastSeq}. */
    case SequenceConflict = 'sequence_conflict';
    /** The receiver broke the protocol. */
    case ProtocolViolation = 'protocol_violation';
    case InvalidArgument = 'invalid_argument';
    /** Stored state could not be used. See {@see ErdigoException::$storageProblem}. */
    case Storage = 'storage';
    /**
     * The request did not finish within the client's `requestTimeout`. It may still have been applied, because the
     * receiver may have acted on it before the answer was lost.
     */
    case TimedOut = 'timed_out';
}
