"""Pairs this device with a receiver, using the one-time code a person reads from the receiver's screen."""

from __future__ import annotations

from typing import Callable, Iterable, Optional, Tuple

from . import discovery, messages, noise, protocol
from .discovery import Endpoint, ReceiverTarget
from .errors import ErdigoError, ErrorKind
from .identity import DeviceIdentity, ReceiverPairing
from .messages import PairApproved, PairDenied, PairPending, PairRequest, ReceiverError
from .model import ItemKind, PairingCode, PairingTicket, canonical_kinds, sanitize_device_name, validate_title
from .protocol import ErrorCode, WireError
from .stream import FramedStream, StreamError, StreamFailure

SDK_NAME = "erdigo-python/0.1.0"

#: Shows a person the receiver's key, in the form the receiver shows it (the first 16 digits of its fingerprint), and
#: returns whether it matches the key on the receiver's screen.
ConfirmReceiverKey = Callable[[str], bool]


def validate_label(label: str) -> None:
    try:
        validate_title(label)
    except WireError:
        raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "the receiver label cannot be empty or contain control characters") from None


def pair(
    target: ReceiverTarget,
    ticket: PairingTicket,
    identity: DeviceIdentity,
    device_name: str,
    kinds: Iterable[ItemKind],
    label: Optional[str] = None,
    handshake_timeout: float = 5.0,
    approval_timeout: float = 120.0,
    confirm_key: Optional[ConfirmReceiverKey] = None,
) -> ReceiverPairing:
    """Pairs with a receiver and returns what to store about it.

    A ticket with a fingerprint (from the QR text) is compared here, and pairing stops on a mismatch. A typed code
    proves only that the other side knows the code, so it needs ``confirm_key``: it runs before the receiver hears
    anything about this device, so the receiver still shows its key. If it returns false, this raises
    :attr:`ErrorKind.KEY_NOT_CONFIRMED`, the code stays valid, and the receiver has seen neither this device's key nor
    its name. If it returns true, the key the person confirmed is compared as if it came from a QR code.

    ``confirm_key`` must be answered by a person who can see the receiver's screen. See the threat model.
    """
    if not isinstance(ticket, PairingTicket):
        raise TypeError("ticket must be a PairingTicket; use PairingTicket.parse")
    name = sanitize_device_name(device_name)
    if not name:
        raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "the device name cannot be empty")
    wanted = canonical_kinds(kinds)
    if not wanted:
        raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "choose at least one item kind")
    if label is not None:
        validate_label(label)

    if ticket.fingerprint is None:
        if confirm_key is None:
            raise ErdigoError(
                ErrorKind.INVALID_ARGUMENT, "a typed code needs confirm_key, because a person has to compare the receiver's key"
            )
        found = _receiver_fingerprint(target, ticket.code, handshake_timeout)
        if not confirm_key(protocol.short_fingerprint(found)):
            raise ErdigoError(ErrorKind.KEY_NOT_CONFIRMED)
        ticket = PairingTicket(ticket.code, found)

    request = PairRequest((protocol.VERSION,), SDK_NAME, name, tuple(wanted))
    last_error = ErdigoError(ErrorKind.NO_ANSWER)
    for endpoint in discovery.candidates(target):
        try:
            return _attempt(endpoint, ticket, identity, request, label, handshake_timeout, approval_timeout)
        except ErdigoError as error:
            if error.kind is ErrorKind.NO_ANSWER:
                continue
            # A definite answer from a receiver holding this code ends the search.
            last_error = error
            break
    raise last_error


def _receiver_fingerprint(target: ReceiverTarget, code: PairingCode, handshake_timeout: float) -> str:
    """Finds the key of the receiver that holds this code, and stops there, so that a person can compare it. This runs
    the exchange up to message 2, as spec section 4.1 describes."""
    # The handshake needs a key, but uses it only in message 3, which is never sent here.
    unused = DeviceIdentity.generate()
    last_error = ErdigoError(ErrorKind.NO_ANSWER)
    for endpoint in discovery.candidates(target):
        with FramedStream(endpoint.host, endpoint.port) as stream:
            try:
                _, receiver_key = _read_receiver_key(stream, code, unused, handshake_timeout)
                return protocol.fingerprint(receiver_key)
            except ErdigoError as error:
                if error.kind is ErrorKind.NO_ANSWER:
                    continue
                last_error = error
                break
    raise last_error


def _read_receiver_key(
    stream: FramedStream, code: PairingCode, identity: DeviceIdentity, handshake_timeout: float
) -> Tuple[noise.Handshake, bytes]:
    """Runs the handshake up to the receiver's answer. This device's key and name have not been sent yet."""
    prologue = protocol.preamble(protocol.Mode.PAIR)
    try:
        stream.connect(handshake_timeout)
        handshake = noise.Handshake(noise.XXPSK0, True, prologue, identity.private_key, psks=[code.preshared_key()])
        first = handshake.write_message()
        stream.arm(handshake_timeout)
        stream.send(prologue + _frame(first))
        second = stream.read_frame(protocol.MAX_HANDSHAKE_FRAME_LENGTH)
        # Reading this message proves the receiver knows the code too.
        handshake.read_message(second)
    except (StreamError, noise.NoiseError) as error:
        raise ErdigoError(ErrorKind.NO_ANSWER) from error
    receiver_key = handshake.remote_static_key
    if receiver_key is None or not noise.is_canonical_public_key(receiver_key):
        raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "the receiver sent an unusable key")
    return handshake, receiver_key


def _frame(body: bytes) -> bytes:
    return len(body).to_bytes(2, "big") + body


def _attempt(
    endpoint: Endpoint,
    ticket: PairingTicket,
    identity: DeviceIdentity,
    request: PairRequest,
    label: Optional[str],
    handshake_timeout: float,
    approval_timeout: float,
) -> ReceiverPairing:
    with FramedStream(endpoint.host, endpoint.port) as stream:
        handshake, receiver_key = _read_receiver_key(stream, ticket.code, identity, handshake_timeout)
        found = protocol.fingerprint(receiver_key)
        if ticket.fingerprint is not None and ticket.fingerprint != found:
            raise ErdigoError(ErrorKind.FINGERPRINT_MISMATCH)
        try:
            third = handshake.write_message(messages.encode_pair_request(request))
            stream.send_frame(third)
            transport = handshake.split()
        except (StreamError, noise.NoiseError) as error:
            raise ErdigoError(ErrorKind.NO_ANSWER) from error

        # The person now decides, which can take a while.
        stream.arm(approval_timeout)
        try:
            while True:
                try:
                    answer = messages.decode_receiver_message(transport.open(stream.read_frame()))
                except StreamError as error:
                    if error.failure is StreamFailure.TIMED_OUT:
                        raise ErdigoError(ErrorKind.PAIRING_TIMED_OUT) from error
                    raise ErdigoError(ErrorKind.NO_ANSWER) from error
                except WireError as error:
                    raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, error.detail) from error
                except noise.NoiseError as error:
                    raise ErdigoError(ErrorKind.NO_ANSWER) from error

                if isinstance(answer, PairPending):
                    continue
                if isinstance(answer, PairDenied):
                    raise ErdigoError(ErrorKind.PAIRING_DENIED)
                if isinstance(answer, PairApproved):
                    address = stream.remote_address
                    return ReceiverPairing(
                        label if label is not None else f"receiver-{found[:8]}",
                        receiver_key,
                        answer.kinds,
                        address[0] if address else None,
                        address[1] if address else None,
                        counter_floor=answer.counter,
                    )
                if isinstance(answer, ReceiverError):
                    if answer.code == ErrorCode.CODE_REUSED.value:
                        raise ErdigoError(ErrorKind.CODE_REUSED, receiver_error=answer)
                    if answer.code == ErrorCode.VERSION_UNSUPPORTED.value:
                        raise ErdigoError(ErrorKind.VERSION_UNSUPPORTED, receiver_error=answer, supported=answer.supported or ())
                    raise ErdigoError(ErrorKind.REJECTED, receiver_error=answer)
                raise ErdigoError(ErrorKind.PROTOCOL_VIOLATION, "unexpected message during pairing")
        finally:
            transport.close()
