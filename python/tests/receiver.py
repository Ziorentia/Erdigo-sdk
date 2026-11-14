"""A small receiver for the tests, with answers a test can script, including ones a real receiver would not give.

It is not a mock receiver for people to use: it holds no items, checks only what the tests need, and answers every
request with an ``ack`` unless told otherwise. The interop script checks this edition against the real mocks of the
Swift and .NET editions.
"""

from __future__ import annotations

import socket
import threading
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional

from erdigo_sdk import messages, noise, protocol
from erdigo_sdk.model import ALL_KINDS, LIMITS_V1, Limits, PairingCode, PairingTicket
from erdigo_sdk.protocol import WireError

#: An answer to one request: None for an ``ack``, or the message to send instead.
Answer = Callable[[messages.RequestEnvelope], Optional[object]]


@dataclass
class Device:
    key: bytes
    kinds: tuple
    counter: int = 0
    last_seq: int = 0


@dataclass
class Record:
    """What the receiver saw, for the tests to check."""

    connections: int = 0
    pair_first_messages: int = 0
    pair_requests: List[messages.PairRequest] = field(default_factory=list)
    hellos: int = 0
    welcomes: int = 0
    requests: List[messages.RequestEnvelope] = field(default_factory=list)


class TestReceiver:
    __test__ = False  # Not a test class, whatever pytest thinks of its name.

    def __init__(self, limits: Limits = LIMITS_V1) -> None:
        self.static_key = noise.new_private_key()
        self.public_key = noise.public_key(self.static_key)
        self.fingerprint = protocol.fingerprint(self.public_key)
        self.limits = limits
        self.code = PairingCode.generate()
        self.devices: Dict[bytes, Device] = {}
        self.record = Record()
        self.lock = threading.Lock()
        #: How pairing ends: "approve", "deny", "code_reused" or "silent".
        self.pairing_answer = "approve"
        self.approved_kinds: Optional[tuple] = None
        #: Seconds to wait before pairing message 2, as a slow network would.
        self.pair_delay = 0.0
        #: Sent in place of the receiver's static key in pairing message 2, to test a device's checks.
        self.advertised_static: Optional[bytes] = None
        self.answer: Answer = lambda envelope: None
        self.welcome_kinds: Optional[tuple] = None
        self.hello_answer: Optional[object] = None
        self._server = socket.create_server(("127.0.0.1", 0))
        # The accept loop wakes up often enough to notice close(). See there.
        self._server.settimeout(0.05)
        self.port = self._server.getsockname()[1]
        self._stopped = False
        self._connections: List[socket.socket] = []
        self._accepting = threading.Thread(target=self._accept, daemon=True)
        self._accepting.start()

    @property
    def ticket(self) -> PairingTicket:
        return PairingTicket(self.code, self.fingerprint)

    def close(self) -> None:
        """Stops listening before it returns, so that a new connection is refused on every system.

        Closing a socket that another thread is blocked in accept() on does not stop it listening on Linux: the
        blocked call keeps it open, and the next connection is accepted and served. So the accept loop, which wakes
        up every 50 milliseconds, is stopped first, and only then is the socket closed.
        """
        self._stopped = True
        if threading.current_thread() is not self._accepting:
            self._accepting.join(timeout=5)
        self._server.close()
        with self.lock:
            for connection in self._connections:
                try:
                    connection.close()
                except OSError:
                    pass

    def drop_connections(self) -> None:
        with self.lock:
            for connection in self._connections:
                try:
                    connection.shutdown(socket.SHUT_RDWR)
                    connection.close()
                except OSError:
                    pass
            self._connections.clear()

    def seqs(self) -> List[int]:
        with self.lock:
            return [envelope.seq for envelope in self.record.requests]

    def add_device(self, public_key: bytes, kinds: tuple = ALL_KINDS) -> None:
        self.devices[bytes(public_key)] = Device(bytes(public_key), tuple(kinds))

    # Serving

    def _accept(self) -> None:
        while not self._stopped:
            try:
                connection, _ = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            connection.settimeout(None)
            with self.lock:
                self._connections.append(connection)
                self.record.connections += 1
            threading.Thread(target=self._serve, args=(connection,), daemon=True).start()

    @staticmethod
    def _read_exactly(connection: socket.socket, count: int) -> bytes:
        data = b""
        while len(data) < count:
            piece = connection.recv(count - len(data))
            if not piece:
                raise ConnectionError("closed")
            data += piece
        return data

    def _read_frame(self, connection: socket.socket) -> bytes:
        length = int.from_bytes(self._read_exactly(connection, 2), "big")
        return self._read_exactly(connection, length)

    @staticmethod
    def _send_frame(connection: socket.socket, body: bytes) -> None:
        connection.sendall(len(body).to_bytes(2, "big") + body)

    def _serve(self, connection: socket.socket) -> None:
        try:
            connection.settimeout(20)
            mode = protocol.parse_preamble(self._read_exactly(connection, 5))
            if mode is protocol.Mode.PAIR:
                self._serve_pairing(connection)
            elif mode is protocol.Mode.CONNECT:
                self._serve_connection(connection)
        except (OSError, ConnectionError, noise.NoiseError, WireError):
            pass
        finally:
            try:
                connection.close()
            except OSError:
                pass

    def _serve_pairing(self, connection: socket.socket) -> None:
        prologue = protocol.preamble(protocol.Mode.PAIR)
        advertised = self.advertised_static

        class Misreported(noise.Handshake):
            def _static_public_key(self) -> bytes:
                return advertised if advertised is not None else super()._static_public_key()

        handshake = Misreported(noise.XXPSK0, False, prologue, self.static_key, psks=[self.code.preshared_key()])
        first = self._read_frame(connection)
        with self.lock:
            self.record.pair_first_messages += 1
        handshake.read_message(first)  # A wrong code fails here, and the receiver stays silent.
        if self.pair_delay:
            threading.Event().wait(self.pair_delay)
        self._send_frame(connection, handshake.write_message())
        third = self._read_frame(connection)
        request = messages.decode_pair_request(handshake.read_message(third))
        with self.lock:
            self.record.pair_requests.append(request)
        transport = handshake.split()
        if self.pairing_answer == "silent":
            # Nobody decides: keep the connection open until the device gives up.
            while connection.recv(1):
                pass
            return
        self._send_frame(connection, transport.seal(messages.encode_receiver_message(messages.PairPending())))
        if self.pairing_answer == "deny":
            answer: object = messages.PairDenied()
        elif self.pairing_answer == "code_reused":
            answer = messages.ReceiverError("code_reused", fatal=True)
        else:
            kinds = self.approved_kinds if self.approved_kinds is not None else request.kinds
            device_key = handshake.remote_static_key
            self.add_device(device_key, kinds)
            answer = messages.PairApproved(tuple(kinds), self.devices[device_key].counter)
        self._send_frame(connection, transport.seal(messages.encode_receiver_message(answer)))

    def _serve_connection(self, connection: socket.socket) -> None:
        prologue = protocol.preamble(protocol.Mode.CONNECT)
        handshake = noise.Handshake(noise.IK, False, prologue, self.static_key)
        first = self._read_frame(connection)
        counter = int.from_bytes(handshake.read_message(first), "big")
        device = self.devices.get(bytes(handshake.remote_static_key or b""))
        if device is None or counter <= device.counter:
            return  # Silence.
        device.counter = counter
        self._send_frame(connection, handshake.write_message())
        transport = handshake.split()
        messages.decode_hello(transport.open(self._read_frame(connection)))
        with self.lock:
            self.record.hellos += 1
        if self.hello_answer is not None:
            self._send_frame(connection, transport.seal(messages.encode_receiver_message(self.hello_answer)))
            return
        kinds = self.welcome_kinds if self.welcome_kinds is not None else device.kinds
        welcome = messages.Welcome(1, device.last_seq, tuple(kinds), self.limits)
        self._send_frame(connection, transport.seal(messages.encode_receiver_message(welcome)))
        with self.lock:
            self.record.welcomes += 1
        while True:
            envelope = messages.decode_request(transport.open(self._read_frame(connection)), self.limits)
            with self.lock:
                self.record.requests.append(envelope)
            reply = self.answer(envelope)
            if reply is None:
                if envelope.seq > device.last_seq:
                    device.last_seq = envelope.seq
                reply = messages.Ack(envelope.seq, False)
            self._send_frame(connection, transport.seal(messages.encode_receiver_message(reply)))
            if isinstance(reply, messages.ReceiverError) and (reply.fatal or reply.code in ("paused", "replaced")):
                return


class Script:
    """Answers the first ``times`` requests as told, then lets requests run."""

    def __init__(self, answer: Callable[[messages.RequestEnvelope], messages.ReceiverError], times: int = 1) -> None:
        self._answer = answer
        self._times = times
        self._count = 0
        self._lock = threading.Lock()

    def __call__(self, envelope: messages.RequestEnvelope) -> Optional[object]:
        with self._lock:
            self._count += 1
            return self._answer(envelope) if self._count <= self._times else None
