"""The Noise Protocol Framework, limited to what integration-v1 uses: X25519, ChaCha20-Poly1305 and SHA-256.

The handshake patterns are implemented here, and checked against the official vectors in
``spec/vectors/noise``. The primitives come from the ``cryptography`` package.

Python cannot wipe memory reliably, so keys are not cleared after use. See "Secrets in memory" in the README.
"""

from __future__ import annotations

import enum
import hashlib
import hmac
import secrets
import struct
import threading
from typing import List, Optional, Sequence, Tuple

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.asymmetric.x25519 import X25519PrivateKey, X25519PublicKey
from cryptography.hazmat.primitives.ciphers.aead import ChaCha20Poly1305

DH_LENGTH = 32
HASH_LENGTH = 32
TAG_LENGTH = 16
MAX_MESSAGE_LENGTH = 65535
_MAX_NONCE = 2**64 - 1


class NoiseFailure(enum.Enum):
    INVALID_KEY = "invalid key"
    WEAK_SHARED_SECRET = "weak shared secret"
    TRUNCATED_MESSAGE = "truncated message"
    DECRYPTION_FAILED = "decryption failed"
    NONCE_EXHAUSTED = "nonce exhausted"
    MESSAGE_TOO_LARGE = "message too large"
    WRONG_HANDSHAKE_STATE = "wrong handshake state"
    MISSING_KEY = "missing key"
    #: ``split`` was called a second time. A second pair of cipher states would repeat the first one's keys and nonces.
    ALREADY_SPLIT = "already split"


class NoiseError(Exception):
    """Failures raised by the Noise layer. They deliberately carry no key material."""

    def __init__(self, failure: NoiseFailure) -> None:
        super().__init__(f"Noise: {failure.value}")
        self.failure = failure


def is_canonical_public_key(key: bytes) -> bool:
    """X25519 ignores the top bit of a public key, so one private key has more than one spelling. A receiver identifies
    devices by these bytes, so only the canonical spelling is accepted: below 2^255-19, top bit clear."""
    if len(key) != DH_LENGTH or key[31] & 0x80:
        return False
    return int.from_bytes(key, "little") < 2**255 - 19


def public_key(private_key: bytes) -> bytes:
    """The public key of an X25519 private key."""
    if len(private_key) != DH_LENGTH:
        raise NoiseError(NoiseFailure.INVALID_KEY)
    return X25519PrivateKey.from_private_bytes(bytes(private_key)).public_key().public_bytes_raw()


def new_private_key() -> bytes:
    """A new X25519 private key from the system's secure random source."""
    return secrets.token_bytes(DH_LENGTH)


def dh(private_key: bytes, remote_public_key: bytes) -> bytes:
    """X25519 with an explicit rejection of low-order public keys, which produce an all-zero secret."""
    if len(private_key) != DH_LENGTH or len(remote_public_key) != DH_LENGTH:
        raise NoiseError(NoiseFailure.INVALID_KEY)
    try:
        secret = X25519PrivateKey.from_private_bytes(bytes(private_key)).exchange(
            X25519PublicKey.from_public_bytes(bytes(remote_public_key))
        )
    except ValueError:
        # OpenSSL refuses an all-zero result itself. Either way it is the same failure.
        raise NoiseError(NoiseFailure.WEAK_SHARED_SECRET) from None
    if not any(secret):
        raise NoiseError(NoiseFailure.WEAK_SHARED_SECRET)
    return secret


def _hmac(key: bytes, data: bytes) -> bytes:
    return hmac.new(key, data, hashlib.sha256).digest()


def _hkdf(chaining_key: bytes, input_key_material: bytes, outputs: int) -> List[bytes]:
    """The Noise HKDF. Note that the input keying material is the HMAC message, keyed by the chaining key."""
    temporary = _hmac(chaining_key, input_key_material)
    results = []
    previous = b""
    for index in range(1, outputs + 1):
        previous = _hmac(temporary, previous + bytes([index]))
        results.append(previous)
    return results


class Token(enum.Enum):
    E = "e"
    S = "s"
    EE = "ee"
    ES = "es"
    SE = "se"
    SS = "ss"
    PSK = "psk"


class Pattern:
    """A Noise handshake pattern, with the DH, cipher and hash functions integration-v1 uses."""

    def __init__(self, name: str, responder_static_is_pre_message: bool, messages: Sequence[Sequence[Token]]) -> None:
        self.name = name
        self.responder_static_is_pre_message = responder_static_is_pre_message
        self.messages: Tuple[Tuple[Token, ...], ...] = tuple(tuple(message) for message in messages)
        self.uses_psk = any(Token.PSK in message for message in self.messages)

    @property
    def protocol_name(self) -> str:
        return f"Noise_{self.name}_25519_ChaChaPoly_SHA256"


#: Used for ordinary connections: only a device that already knows the receiver key can start one.
IK = Pattern("IK", True, [[Token.E, Token.ES, Token.S, Token.SS], [Token.E, Token.EE, Token.SE]])

#: Used for pairing: the pairing code is mixed in before anything else, so a party without the code cannot produce a
#: first message the receiver will answer.
XXPSK0 = Pattern("XXpsk0", False, [[Token.PSK, Token.E], [Token.E, Token.EE, Token.S, Token.ES], [Token.S, Token.SE]])


class CipherState:
    """One direction of a Noise session."""

    def __init__(self) -> None:
        self._key: Optional[bytes] = None
        self._nonce = 0

    @property
    def has_key(self) -> bool:
        return self._key is not None

    def initialize(self, key: bytes, start: int = 0) -> None:
        """``start`` is not zero only in tests of what happens as the counter runs out."""
        self._key = bytes(key[:32])
        self._nonce = start

    def _nonce_bytes(self) -> bytes:
        # Noise: 32 bits of zeros followed by the little-endian 64-bit counter.
        return b"\x00\x00\x00\x00" + struct.pack("<Q", self._nonce)

    def encrypt(self, associated_data: bytes, plaintext: bytes) -> bytes:
        if self._key is None:
            return bytes(plaintext)
        # 2^64-1 is reserved by the Noise specification.
        if self._nonce >= _MAX_NONCE:
            raise NoiseError(NoiseFailure.NONCE_EXHAUSTED)
        output = ChaCha20Poly1305(self._key).encrypt(self._nonce_bytes(), bytes(plaintext), bytes(associated_data))
        self._nonce += 1
        return output

    def decrypt(self, associated_data: bytes, ciphertext: bytes) -> bytes:
        if self._key is None:
            return bytes(ciphertext)
        if self._nonce >= _MAX_NONCE:
            raise NoiseError(NoiseFailure.NONCE_EXHAUSTED)
        if len(ciphertext) < TAG_LENGTH:
            raise NoiseError(NoiseFailure.TRUNCATED_MESSAGE)
        try:
            output = ChaCha20Poly1305(self._key).decrypt(self._nonce_bytes(), bytes(ciphertext), bytes(associated_data))
        except InvalidTag:
            raise NoiseError(NoiseFailure.DECRYPTION_FAILED) from None
        self._nonce += 1
        return output


class SymmetricState:
    def __init__(self, protocol_name: str) -> None:
        name = protocol_name.encode("ascii")
        self.hash = name.ljust(HASH_LENGTH, b"\x00") if len(name) <= HASH_LENGTH else hashlib.sha256(name).digest()
        self._chaining_key = self.hash
        self._cipher = CipherState()

    @property
    def has_key(self) -> bool:
        return self._cipher.has_key

    def mix_hash(self, data: bytes) -> None:
        self.hash = hashlib.sha256(self.hash + bytes(data)).digest()

    def mix_key(self, input_key_material: bytes) -> None:
        self._chaining_key, key = _hkdf(self._chaining_key, input_key_material, 2)
        self._cipher.initialize(key)

    def mix_key_and_hash(self, input_key_material: bytes) -> None:
        self._chaining_key, hash_input, key = _hkdf(self._chaining_key, input_key_material, 3)
        self.mix_hash(hash_input)
        self._cipher.initialize(key)

    def encrypt_and_hash(self, plaintext: bytes) -> bytes:
        ciphertext = self._cipher.encrypt(self.hash, plaintext)
        self.mix_hash(ciphertext)
        return ciphertext

    def decrypt_and_hash(self, ciphertext: bytes) -> bytes:
        plaintext = self._cipher.decrypt(self.hash, ciphertext)
        self.mix_hash(ciphertext)
        return plaintext

    def split(self) -> Tuple[CipherState, CipherState]:
        first_key, second_key = _hkdf(self._chaining_key, b"", 2)
        first = CipherState()
        second = CipherState()
        first.initialize(first_key)
        second.initialize(second_key)
        return first, second


class Transport:
    """Encrypts and decrypts messages after a handshake has finished.

    A handshake yields one transport, because :meth:`Handshake.split` succeeds only once. A lock covers each whole
    operation, including the nonce increment, so no key and nonce pair is used twice, whichever thread calls it. The
    lock orders the sealing, not the sending: the caller still has to send frames in the order they were sealed.
    """

    def __init__(self, send: CipherState, receive: CipherState, handshake_hash: bytes) -> None:
        self._send = send
        self._receive = receive
        self._lock = threading.Lock()
        self.handshake_hash = handshake_hash

    def seal(self, plaintext: bytes) -> bytes:
        if len(plaintext) + TAG_LENGTH > MAX_MESSAGE_LENGTH:
            raise NoiseError(NoiseFailure.MESSAGE_TOO_LARGE)
        with self._lock:
            return self._send.encrypt(b"", plaintext)

    def open(self, ciphertext: bytes) -> bytes:
        if len(ciphertext) > MAX_MESSAGE_LENGTH:
            raise NoiseError(NoiseFailure.MESSAGE_TOO_LARGE)
        with self._lock:
            return self._receive.decrypt(b"", ciphertext)

    def close(self) -> None:
        """Forgets both keys. Every later operation fails, rather than falling back to no encryption."""
        with self._lock:
            self._send.initialize(bytes(32), _MAX_NONCE)
            self._receive.initialize(bytes(32), _MAX_NONCE)


class Handshake:
    """The Noise handshake state machine."""

    def __init__(
        self,
        pattern: Pattern,
        initiator: bool,
        prologue: bytes,
        static_key: Optional[bytes],
        remote_static_key: Optional[bytes] = None,
        psks: Sequence[bytes] = (),
        _fixed_ephemeral: Optional[bytes] = None,
    ) -> None:
        """Starts a handshake. ``static_key`` is this side's X25519 private key.

        ``_fixed_ephemeral`` exists only so that published test vectors can be reproduced. Reusing an ephemeral key in
        real traffic breaks the protocol, so the SDK never passes one.
        """
        if any(len(key) != 32 for key in psks):
            raise NoiseError(NoiseFailure.INVALID_KEY)
        if pattern.uses_psk and not psks:
            raise NoiseError(NoiseFailure.MISSING_KEY)
        for key in (static_key, remote_static_key, _fixed_ephemeral):
            if key is not None and len(key) != DH_LENGTH:
                raise NoiseError(NoiseFailure.INVALID_KEY)
        self._pattern = pattern
        self._initiator = initiator
        self._static_key = bytes(static_key) if static_key is not None else None
        self.remote_static_key: Optional[bytes] = bytes(remote_static_key) if remote_static_key is not None else None
        self._psks = [bytes(key) for key in psks]
        self._fixed_ephemeral = bytes(_fixed_ephemeral) if _fixed_ephemeral is not None else None
        self._ephemeral_key: Optional[bytes] = None
        self._remote_ephemeral_key: Optional[bytes] = None
        self._message_index = 0
        self._split_lock = threading.Lock()
        self._has_split = False
        self._symmetric = SymmetricState(pattern.protocol_name)
        self._symmetric.mix_hash(prologue)
        if pattern.responder_static_is_pre_message:
            if initiator:
                if self.remote_static_key is None:
                    raise NoiseError(NoiseFailure.MISSING_KEY)
                self._symmetric.mix_hash(self.remote_static_key)
            else:
                self._symmetric.mix_hash(public_key(self._require(self._static_key)))

    @property
    def is_complete(self) -> bool:
        return self._message_index == len(self._pattern.messages)

    @property
    def handshake_hash(self) -> bytes:
        return self._symmetric.hash

    def _is_my_turn(self) -> bool:
        return (self._message_index % 2 == 0) == self._initiator

    @staticmethod
    def _require(key: Optional[bytes]) -> bytes:
        if key is None:
            raise NoiseError(NoiseFailure.MISSING_KEY)
        return key

    def _mix_dh(self, token: Token) -> None:
        require = self._require
        if token is Token.EE:
            secret = dh(require(self._ephemeral_key), require(self._remote_ephemeral_key))
        elif token is Token.ES:
            secret = (
                dh(require(self._ephemeral_key), require(self.remote_static_key))
                if self._initiator
                else dh(require(self._static_key), require(self._remote_ephemeral_key))
            )
        elif token is Token.SE:
            secret = (
                dh(require(self._static_key), require(self._remote_ephemeral_key))
                if self._initiator
                else dh(require(self._ephemeral_key), require(self.remote_static_key))
            )
        elif token is Token.SS:
            secret = dh(require(self._static_key), require(self.remote_static_key))
        else:
            raise NoiseError(NoiseFailure.WRONG_HANDSHAKE_STATE)
        self._symmetric.mix_key(secret)

    def _static_public_key(self) -> bytes:
        """The public key sent for ``s``. Tests of a peer that misbehaves replace it."""
        return public_key(self._require(self._static_key))

    def _mix_psk(self) -> None:
        if not self._psks:
            raise NoiseError(NoiseFailure.MISSING_KEY)
        self._symmetric.mix_key_and_hash(self._psks.pop(0))

    def write_message(self, payload: bytes = b"") -> bytes:
        if self.is_complete or not self._is_my_turn():
            raise NoiseError(NoiseFailure.WRONG_HANDSHAKE_STATE)
        output = bytearray()
        for token in self._pattern.messages[self._message_index]:
            if token is Token.E:
                key = self._fixed_ephemeral if self._fixed_ephemeral is not None else new_private_key()
                self._ephemeral_key = key
                ephemeral_public = public_key(key)
                output += ephemeral_public
                self._symmetric.mix_hash(ephemeral_public)
                if self._pattern.uses_psk:
                    self._symmetric.mix_key(ephemeral_public)
            elif token is Token.S:
                output += self._symmetric.encrypt_and_hash(self._static_public_key())
            elif token is Token.PSK:
                self._mix_psk()
            else:
                self._mix_dh(token)
        output += self._symmetric.encrypt_and_hash(bytes(payload))
        if len(output) > MAX_MESSAGE_LENGTH:
            raise NoiseError(NoiseFailure.MESSAGE_TOO_LARGE)
        self._message_index += 1
        return bytes(output)

    def read_message(self, message: bytes) -> bytes:
        if self.is_complete or self._is_my_turn():
            raise NoiseError(NoiseFailure.WRONG_HANDSHAKE_STATE)
        if len(message) > MAX_MESSAGE_LENGTH:
            raise NoiseError(NoiseFailure.MESSAGE_TOO_LARGE)
        message = bytes(message)
        offset = 0

        def take(count: int) -> bytes:
            nonlocal offset
            if len(message) - offset < count:
                raise NoiseError(NoiseFailure.TRUNCATED_MESSAGE)
            head = message[offset : offset + count]
            offset += count
            return head

        for token in self._pattern.messages[self._message_index]:
            if token is Token.E:
                remote = take(DH_LENGTH)
                self._remote_ephemeral_key = remote
                self._symmetric.mix_hash(remote)
                if self._pattern.uses_psk:
                    self._symmetric.mix_key(remote)
            elif token is Token.S:
                length = DH_LENGTH + TAG_LENGTH if self._symmetric.has_key else DH_LENGTH
                self.remote_static_key = self._symmetric.decrypt_and_hash(take(length))
            elif token is Token.PSK:
                self._mix_psk()
            else:
                self._mix_dh(token)
        payload = self._symmetric.decrypt_and_hash(message[offset:])
        self._message_index += 1
        return payload

    def split(self) -> Transport:
        """Call once :attr:`is_complete` is true. This succeeds only once, because a second transport would have the
        same keys and start from the same nonces."""
        if not self.is_complete:
            raise NoiseError(NoiseFailure.WRONG_HANDSHAKE_STATE)
        with self._split_lock:
            if self._has_split:
                raise NoiseError(NoiseFailure.ALREADY_SPLIT)
            self._has_split = True
            first, second = self._symmetric.split()
            self._ephemeral_key = None
            if self._initiator:
                return Transport(first, second, self._symmetric.hash)
            return Transport(second, first, self._symmetric.hash)
