package com.ziorentia.erdigo.sdk;

import com.ziorentia.erdigo.core.ItemKind;
import com.ziorentia.erdigo.core.Messages.ReceiverError;
import java.util.List;

/** Failures reported by the SDK. {@link #kind()} says what happened. */
public final class ErdigoException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    /** What went wrong. */
    public enum Kind {
        /**
         * No receiver answered. A receiver stays silent when it is off, asleep, on another network, when the pairing code
         * is wrong or expired, and when this device is not allowed. These cases cannot be told apart, by design.
         */
        NO_ANSWER,
        /** The receiver's key does not match the fingerprint supplied for pairing. */
        FINGERPRINT_MISMATCH,
        /** The person did not confirm the receiver's key, so the receiver was not asked to pair. */
        KEY_NOT_CONFIRMED,
        /** The person at the receiver declined the request. */
        PAIRING_DENIED,
        /** The person at the receiver did not answer in time. */
        PAIRING_TIMED_OUT,
        /** The code was used twice. The receiver warned its user and removed the pairing made with it. */
        CODE_REUSED,
        /** The receiver rejected a request. See {@link #receiverError()}. */
        REJECTED,
        /** The item kind is not among those the person allowed for this device. See {@link #itemKind()}. */
        NOT_PERMITTED,
        /** The receiver does not support any protocol version this SDK speaks. See {@link #supported()}. */
        VERSION_UNSUPPORTED,
        /** Another connection using this device key took over. */
        REPLACED,
        /** Another writer using this device key advanced the sequence number. See {@link #lastSeq()}. */
        SEQUENCE_CONFLICT,
        /** The receiver broke the protocol. */
        PROTOCOL_VIOLATION,
        INVALID_ARGUMENT,
        /** Stored state could not be used. See {@link #storageProblem()}. */
        STORAGE,
        /** The thread was interrupted. Its interrupt flag is still set. */
        INTERRUPTED,
    }

    /** What was wrong with stored state. */
    public enum StorageProblem {
        NONE,
        /** The device key file exists but its contents are not a device key. A new key was not created. */
        IDENTITY_DAMAGED,
        /** The device key file exists but could not be opened or read. A new key was not created. */
        IDENTITY_UNREADABLE,
        /** The pairing file exists but its contents could not be understood. */
        STATE_DAMAGED,
        /** The pairing file exists but could not be opened or read. */
        STATE_UNREADABLE,
        /** A folder or file could be read or replaced by someone else, so it was not used. */
        INSECURE,
        /** Anything else, such as a full disk. */
        OTHER,
    }

    private final Kind kind;
    private final String detail;
    // Not carried when an exception is serialized: neither type is serializable, and both are details of one answer.
    private final transient ReceiverError receiverError;
    private final ItemKind itemKind;
    private final transient List<Integer> supported;
    private final Long lastSeq;
    private final StorageProblem storageProblem;

    private ErdigoException(Builder builder) {
        super(describe(builder), builder.cause);
        kind = builder.kind;
        detail = builder.detail;
        receiverError = builder.receiverError;
        itemKind = builder.itemKind;
        supported = builder.supported == null ? null : List.copyOf(builder.supported);
        lastSeq = builder.lastSeq;
        storageProblem = builder.kind == Kind.STORAGE && builder.storageProblem == StorageProblem.NONE
                ? StorageProblem.OTHER
                : builder.storageProblem;
    }

    public ErdigoException(Kind kind) {
        this(new Builder(kind));
    }

    public ErdigoException(Kind kind, String detail) {
        this(new Builder(kind).detail(detail));
    }

    static Builder builder(Kind kind) {
        return new Builder(kind);
    }

    public Kind kind() {
        return kind;
    }

    public String detail() {
        return detail;
    }

    /** The receiver's answer, for {@link Kind#REJECTED} and some others, or null. */
    public ReceiverError receiverError() {
        return receiverError;
    }

    public ItemKind itemKind() {
        return itemKind;
    }

    public List<Integer> supported() {
        return supported;
    }

    public Long lastSeq() {
        return lastSeq;
    }

    public StorageProblem storageProblem() {
        return storageProblem;
    }

    private static String describe(Builder builder) {
        switch (builder.kind) {
            case NO_ANSWER:
                return "No receiver answered. It may be off, asleep or on another network, the pairing code may be wrong or expired, or this device may have been removed.";
            case FINGERPRINT_MISMATCH:
                return "The receiver's key does not match the expected fingerprint. Pairing was stopped.";
            case KEY_NOT_CONFIRMED:
                return "The receiver's key was not confirmed. No pairing was saved, no items were sent, and the receiver was not asked to pair.";
            case PAIRING_DENIED:
                return "The request was declined on the receiver.";
            case PAIRING_TIMED_OUT:
                return "Nobody confirmed the request on the receiver in time.";
            case CODE_REUSED:
                return "This pairing code was already used. The receiver removed the pairing made with it. Ask for a new code.";
            case REJECTED:
                return "The receiver rejected the request: " + (builder.receiverError == null ? "unknown" : builder.receiverError.code()) + ".";
            case NOT_PERMITTED:
                return "This device is not allowed to send '" + (builder.itemKind == null ? "unknown" : builder.itemKind.wire()) + "' items.";
            case VERSION_UNSUPPORTED:
                return "The receiver supports protocol versions " + (builder.supported == null ? "[]" : builder.supported.toString())
                        + ", which this SDK does not speak. Update the SDK.";
            case REPLACED:
                return "Another connection using this device key took over.";
            case SEQUENCE_CONFLICT:
                return "Another program is writing with this device key at the same time.";
            case PROTOCOL_VIOLATION:
                return "The receiver broke the protocol: " + builder.detail + ".";
            case INTERRUPTED:
                return "Interrupted.";
            default:
                return builder.detail != null ? builder.detail : builder.kind.toString();
        }
    }

    /** Collects the optional parts of an exception. */
    static final class Builder {
        private final Kind kind;
        private String detail;
        private ReceiverError receiverError;
        private ItemKind itemKind;
        private List<Integer> supported;
        private Long lastSeq;
        private StorageProblem storageProblem = StorageProblem.NONE;
        private Throwable cause;

        Builder(Kind kind) {
            this.kind = kind;
        }

        Builder detail(String value) {
            detail = value;
            return this;
        }

        Builder receiverError(ReceiverError value) {
            receiverError = value;
            return this;
        }

        Builder itemKind(ItemKind value) {
            itemKind = value;
            return this;
        }

        Builder supported(List<Integer> value) {
            supported = value;
            return this;
        }

        Builder lastSeq(Long value) {
            lastSeq = value;
            return this;
        }

        Builder storageProblem(StorageProblem value) {
            storageProblem = value;
            return this;
        }

        Builder cause(Throwable value) {
            cause = value;
            return this;
        }

        ErdigoException build() {
            return new ErdigoException(this);
        }
    }
}
