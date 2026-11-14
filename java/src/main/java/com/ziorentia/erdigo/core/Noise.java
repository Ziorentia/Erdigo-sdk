package com.ziorentia.erdigo.core;

import java.math.BigInteger;
import java.security.GeneralSecurityException;
import java.security.InvalidKeyException;
import java.security.KeyFactory;
import java.security.PrivateKey;
import java.security.PublicKey;
import java.security.SecureRandom;
import java.security.spec.NamedParameterSpec;
import java.security.spec.XECPrivateKeySpec;
import java.security.spec.XECPublicKeySpec;
import java.util.Arrays;
import javax.crypto.AEADBadTagException;
import javax.crypto.Cipher;
import javax.crypto.KeyAgreement;
import javax.crypto.Mac;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;

/**
 * The primitives of the Noise layer, all from the JDK: X25519 ({@code XDH}), ChaCha20-Poly1305 and SHA-256.
 *
 * <p>Keys are kept as raw 32-byte arrays, as Noise and the protocol write them, and converted to the JDK's key
 * specifications only for the moment they are used.
 */
public final class Noise {
    public static final int DH_LENGTH = 32;
    public static final int HASH_LENGTH = 32;
    public static final int TAG_LENGTH = 16;
    public static final int MAX_MESSAGE_LENGTH = 65535;

    /** 2^255 - 19. */
    private static final BigInteger PRIME = BigInteger.ONE.shiftLeft(255).subtract(BigInteger.valueOf(19));
    private static final byte[] BASE_POINT = basePoint();
    private static final SecureRandom RANDOM = new SecureRandom();

    private Noise() {
    }

    private static byte[] basePoint() {
        byte[] point = new byte[DH_LENGTH];
        point[0] = 9;
        return point;
    }

    /**
     * X25519 ignores the top bit of a public key, so one private key has more than one spelling. A receiver identifies
     * devices by these bytes, so it accepts only the canonical spelling: below 2^255-19, top bit clear.
     */
    public static boolean isCanonicalPublicKey(byte[] key) {
        if (key == null || key.length != DH_LENGTH || (key[31] & 0x80) != 0) {
            return false;
        }
        // Values from 2^255-19 up to 2^255-1 are not canonical either.
        boolean isAtLeastPrime = key[31] == 0x7F && (key[0] & 0xFF) >= 0xED;
        for (int index = 1; index <= 30 && isAtLeastPrime; index++) {
            isAtLeastPrime = key[index] == (byte) 0xFF;
        }
        return !isAtLeastPrime;
    }

    /** A new X25519 private key from the system's secure random source. */
    public static byte[] newPrivateKey() {
        byte[] key = new byte[DH_LENGTH];
        RANDOM.nextBytes(key);
        return key;
    }

    /** Fills {@code output} from the system's secure random source. */
    public static void randomBytes(byte[] output) {
        RANDOM.nextBytes(output);
    }

    /** The public key of an X25519 private key: the scalar multiplied by the base point. */
    public static byte[] publicKey(byte[] privateKey) {
        if (privateKey == null || privateKey.length != DH_LENGTH) {
            throw new NoiseException(NoiseException.Failure.INVALID_KEY);
        }
        return x25519(privateKey, BASE_POINT);
    }

    /** X25519 with an explicit rejection of low-order public keys, which produce an all-zero secret. */
    public static byte[] dh(byte[] privateKey, byte[] publicKey) {
        if (privateKey == null || publicKey == null || privateKey.length != DH_LENGTH || publicKey.length != DH_LENGTH) {
            throw new NoiseException(NoiseException.Failure.INVALID_KEY);
        }
        byte[] secret = x25519(privateKey, publicKey);
        // Constant-time OR across every byte.
        int accumulated = 0;
        for (byte value : secret) {
            accumulated |= value;
        }
        if (accumulated == 0) {
            throw new NoiseException(NoiseException.Failure.WEAK_SHARED_SECRET);
        }
        return secret;
    }

    /**
     * The X25519 function of RFC 7748 on raw keys. The JDK takes the scalar as little-endian bytes and clamps it
     * itself, and takes the u-coordinate as a number, so the public key's bytes are read as RFC 7748 section 5 says:
     * little-endian, with the top bit masked, reduced modulo 2^255-19.
     */
    private static byte[] x25519(byte[] scalar, byte[] uBytes) {
        byte[] bigEndian = new byte[DH_LENGTH];
        for (int index = 0; index < DH_LENGTH; index++) {
            bigEndian[index] = uBytes[DH_LENGTH - 1 - index];
        }
        bigEndian[0] &= 0x7F;
        BigInteger u = new BigInteger(1, bigEndian).mod(PRIME);
        try {
            KeyFactory factory = KeyFactory.getInstance("XDH");
            PrivateKey privateKey = factory.generatePrivate(new XECPrivateKeySpec(NamedParameterSpec.X25519, scalar));
            PublicKey publicKey = factory.generatePublic(new XECPublicKeySpec(NamedParameterSpec.X25519, u));
            KeyAgreement agreement = KeyAgreement.getInstance("XDH");
            agreement.init(privateKey);
            agreement.doPhase(publicKey, true);
            byte[] secret = agreement.generateSecret();
            if (secret.length != DH_LENGTH) {
                throw new NoiseException(NoiseException.Failure.INVALID_KEY);
            }
            return secret;
        } catch (InvalidKeyException error) {
            // The JDK refuses a point of small order itself, which is the all-zero result checked for above.
            throw new NoiseException(NoiseException.Failure.WEAK_SHARED_SECRET);
        } catch (GeneralSecurityException error) {
            throw new IllegalStateException("X25519 is missing from this Java runtime", error);
        }
    }

    /** HMAC-SHA256. */
    public static byte[] hmac(byte[] key, byte[] data) {
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            // An empty key is allowed by HMAC, but not by SecretKeySpec, so it is given as a block of zeros, which
            // HMAC treats the same way.
            mac.init(new SecretKeySpec(key.length == 0 ? new byte[64] : key, "HmacSHA256"));
            return mac.doFinal(data);
        } catch (GeneralSecurityException error) {
            throw new IllegalStateException("HmacSHA256 is missing from this Java runtime", error);
        }
    }

    /** The Noise HKDF. The input keying material is the HMAC message, keyed by the chaining key. */
    static byte[][] hkdf(byte[] chainingKey, byte[] inputKeyMaterial, int outputs) {
        byte[] temporary = hmac(chainingKey, inputKeyMaterial);
        byte[][] results = new byte[outputs][];
        byte[] previous = new byte[0];
        for (int index = 1; index <= outputs; index++) {
            byte[] message = Arrays.copyOf(previous, previous.length + 1);
            message[message.length - 1] = (byte) index;
            previous = hmac(temporary, message);
            Arrays.fill(message, (byte) 0);
            results[index - 1] = previous;
        }
        Arrays.fill(temporary, (byte) 0);
        return results;
    }

    /** HKDF-SHA256 of RFC 5869, with an empty info field. */
    public static byte[] hkdfSha256(byte[] inputKeyMaterial, byte[] salt, int length) {
        if (length < 1 || length > 32) {
            throw new IllegalArgumentException("one block of output at most");
        }
        byte[] pseudorandomKey = hmac(salt, inputKeyMaterial);
        byte[] block = hmac(pseudorandomKey, new byte[] {1});
        Arrays.fill(pseudorandomKey, (byte) 0);
        byte[] output = Arrays.copyOf(block, length);
        Arrays.fill(block, (byte) 0);
        return output;
    }

    /**
     * ChaCha20-Poly1305 with the Noise nonce: 32 zero bits, then the 64-bit counter in little-endian order. Each call
     * uses a new cipher object, because the JDK refuses to encrypt twice with one key and nonce on the same object.
     */
    static byte[] encrypt(byte[] key, long counter, byte[] associatedData, byte[] plaintext) {
        try {
            Cipher cipher = Cipher.getInstance("ChaCha20-Poly1305");
            cipher.init(Cipher.ENCRYPT_MODE, new SecretKeySpec(key, "ChaCha20"), new IvParameterSpec(nonce(counter)));
            cipher.updateAAD(associatedData);
            return cipher.doFinal(plaintext);
        } catch (GeneralSecurityException error) {
            throw new IllegalStateException("ChaCha20-Poly1305 is missing from this Java runtime", error);
        }
    }

    static byte[] decrypt(byte[] key, long counter, byte[] associatedData, byte[] ciphertext) {
        Cipher cipher;
        try {
            cipher = Cipher.getInstance("ChaCha20-Poly1305");
            cipher.init(Cipher.DECRYPT_MODE, new SecretKeySpec(key, "ChaCha20"), new IvParameterSpec(nonce(counter)));
        } catch (GeneralSecurityException error) {
            throw new IllegalStateException("ChaCha20-Poly1305 is missing from this Java runtime", error);
        }
        try {
            cipher.updateAAD(associatedData);
            return cipher.doFinal(ciphertext);
        } catch (AEADBadTagException error) {
            throw new NoiseException(NoiseException.Failure.DECRYPTION_FAILED);
        } catch (GeneralSecurityException error) {
            throw new NoiseException(NoiseException.Failure.DECRYPTION_FAILED);
        }
    }

    /** 32 bits of zeros followed by the little-endian 64-bit counter. */
    static byte[] nonce(long counter) {
        byte[] nonce = new byte[12];
        for (int index = 0; index < 8; index++) {
            nonce[4 + index] = (byte) (counter >>> (8 * index));
        }
        return nonce;
    }
}
