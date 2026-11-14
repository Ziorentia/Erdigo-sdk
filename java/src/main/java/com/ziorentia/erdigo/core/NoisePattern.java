package com.ziorentia.erdigo.core;

import java.util.List;

/**
 * A Noise handshake pattern, limited to the functions integration-v1 uses: {@code 25519}, {@code ChaChaPoly} and
 * {@code SHA256}.
 */
public final class NoisePattern {
    /** The tokens of a handshake message. */
    public enum Token {
        E,
        S,
        EE,
        ES,
        SE,
        SS,
        PSK,
    }

    /** Used for ordinary connections: only a device that already knows the receiver key can start one. */
    public static final NoisePattern IK = new NoisePattern(
            "IK", true, List.of(List.of(Token.E, Token.ES, Token.S, Token.SS), List.of(Token.E, Token.EE, Token.SE)));

    /**
     * Used for pairing: the pairing code is mixed in before anything else, so a party without the code cannot produce
     * a first message the receiver will answer.
     */
    public static final NoisePattern XX_PSK0 = new NoisePattern(
            "XXpsk0",
            false,
            List.of(List.of(Token.PSK, Token.E), List.of(Token.E, Token.EE, Token.S, Token.ES), List.of(Token.S, Token.SE)));

    private final String name;
    private final boolean responderStaticIsPreMessage;
    private final List<List<Token>> messages;

    /** Lets the test suite run official vectors for patterns this protocol does not use. */
    NoisePattern(String name, boolean responderStaticIsPreMessage, List<List<Token>> messages) {
        this.name = name;
        this.responderStaticIsPreMessage = responderStaticIsPreMessage;
        this.messages = List.copyOf(messages);
    }

    public String name() {
        return name;
    }

    boolean responderStaticIsPreMessage() {
        return responderStaticIsPreMessage;
    }

    List<List<Token>> messages() {
        return messages;
    }

    boolean usesPsk() {
        return messages.stream().anyMatch(message -> message.contains(Token.PSK));
    }

    public String protocolName() {
        return "Noise_" + name + "_25519_ChaChaPoly_SHA256";
    }
}
