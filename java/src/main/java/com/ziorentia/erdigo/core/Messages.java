package com.ziorentia.erdigo.core;

import java.util.List;

/** The messages of integration-v1 other than requests. */
public final class Messages {
    private Messages() {
    }

    /** A request with its sequence number. */
    public record RequestEnvelope(long seq, Request request) {
    }

    /** The first message on a connection, sent by the device. */
    public record Hello(List<Integer> versions, String sdk) {
        public Hello {
            versions = List.copyOf(versions);
        }
    }

    /** The payload of the third pairing message. */
    public record PairRequest(List<Integer> versions, String sdk, String name, List<ItemKind> kinds) {
        public PairRequest {
            versions = List.copyOf(versions);
            kinds = List.copyOf(kinds);
        }
    }

    /** The receiver's answer to {@code hello}. */
    public record Welcome(int version, long lastSeq, List<ItemKind> kinds, Limits limits) {
        public Welcome {
            kinds = List.copyOf(kinds);
        }
    }

    /**
     * An error answer. {@code code} is kept as text, because a device must treat a code it does not know as a permanent
     * failure. The other fields may be null.
     */
    public record ReceiverError(String code, Long seq, Long lastSeq, Long retryAfterMs, List<Integer> supported, boolean fatal) {
        public ReceiverError {
            supported = supported == null ? null : List.copyOf(supported);
        }

        public ReceiverError(ErrorCode code, Long seq, Long lastSeq, Long retryAfterMs, List<Integer> supported, boolean fatal) {
            this(code.wire(), seq, lastSeq, retryAfterMs, supported, fatal);
        }

        public boolean is(ErrorCode other) {
            return other.wire().equals(code);
        }
    }

    /** The receiver's answers while a person decides on a pairing request. */
    public sealed interface PairStatus {
        record Pending() implements PairStatus {
        }

        /** {@code counter} is the highest connect counter the receiver has accepted from this device. */
        record Approved(List<ItemKind> kinds, long counter) implements PairStatus {
            public Approved {
                kinds = List.copyOf(kinds);
            }
        }

        record Denied() implements PairStatus {
        }
    }

    /** Everything a receiver sends after a handshake. */
    public sealed interface ReceiverMessage {
        record WelcomeMessage(Welcome welcome) implements ReceiverMessage {
        }

        record Ack(long seq, boolean duplicate) implements ReceiverMessage {
        }

        record Error(ReceiverError error) implements ReceiverMessage {
        }

        record PairStatusMessage(PairStatus status) implements ReceiverMessage {
        }
    }
}
