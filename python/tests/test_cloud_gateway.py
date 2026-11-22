"""The example gateway, end to end: the example relay, the gateway and the test receiver, over real connections."""

from __future__ import annotations

import datetime
import functools
import ipaddress
import json
import os
import signal
import socket
import ssl
import subprocess
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from types import SimpleNamespace
from typing import Iterator, List, Optional, Sequence, Tuple, Union

import pytest

from erdigo_sdk import Coordinate, Limits, MarkerColor, TrackPoint, messages
from erdigo_sdk import client as erdigo_client
from erdigo_sdk.private_file import FileLock

from .cloud_relay_support import EXAMPLES, PNG_1X1, PNG_1X1_BASE64, FakeClock, RunningRelay, load, new_token, pin
from .receiver import Script, TestReceiver

gateway = load("gateway")

POSIX = os.name == "posix"
SEOUL = Coordinate(37.5663, 126.9779)


@pytest.fixture
def relay_server() -> Iterator[RunningRelay]:
    server = RunningRelay()
    yield server
    server.close()


@pytest.fixture
def receiver() -> Iterator[TestReceiver]:
    running = TestReceiver()
    yield running
    running.close()


@pytest.fixture(autouse=True)
def quick_retries(monkeypatch) -> None:
    """Waits of a few hundredths of a second instead of 1, 2, 4 ... seconds, in the gateway and in the SDK."""
    monkeypatch.setattr(gateway, "backoff", lambda attempt: 0.05)
    monkeypatch.setattr(erdigo_client, "backoff", lambda attempt: 0.05)
    monkeypatch.setattr(gateway, "POLL_SECONDS", 0.1)


class Gateway:
    """Runs the gateway's main() in this process, as its command line would."""

    def __init__(self, capsys, monkeypatch, tmp_path: Path, receiver: TestReceiver) -> None:
        self.capsys = capsys
        self.monkeypatch = monkeypatch
        self.tmp_path = tmp_path
        self.receiver = receiver
        self.state = tmp_path / "state"
        #: Where the gateway reaches the app, when not straight at the receiver: a proxy in front of it.
        self.app_port: Optional[int] = None

    def main(self, *arguments: str, token: Optional[str] = None) -> Tuple[int, str, str]:
        if token is None:
            self.monkeypatch.delenv("ERDIGO_RELAY_TOKEN", raising=False)
        else:
            self.monkeypatch.setenv("ERDIGO_RELAY_TOKEN", token)
        status = gateway.main(list(arguments))
        captured = self.capsys.readouterr()
        return status, captured.out, captured.err

    def address(self) -> List[str]:
        port = self.receiver.port if self.app_port is None else self.app_port
        return ["--host", "127.0.0.1", "--port", str(port), "--state-dir", str(self.state)]

    def pair(self, *extra: str) -> Tuple[int, str, str]:
        code_file = self.tmp_path / "code.txt"
        code_file.write_text(self.receiver.ticket.uri + "\n", encoding="utf-8")
        if POSIX:
            os.chmod(code_file, 0o600)
        return self.main("pair", "--name", "Cloud gateway", "--code-file", str(code_file), *extra, *self.address())

    def run_once(self, relay_url: str, token: str) -> Tuple[int, str, str]:
        return self.main("run", "--relay", relay_url, "--once", *self.address(), token=token)


@pytest.fixture
def tool(capsys, monkeypatch, tmp_path, receiver) -> Gateway:
    return Gateway(capsys, monkeypatch, tmp_path, receiver)


def _requests(receiver: TestReceiver) -> list:
    with receiver.lock:
        return [envelope.request for envelope in receiver.record.requests]


def _every_kind() -> List[dict]:
    return [
        pin("drone-1", title="Drone 1"),
        {"op": "marker", "id": "gate", "lat": 37.5, "lon": 126.9, "title": "Gate", "color": "green"},
        {"op": "note", "id": "note-1", "lat": 37.51, "lon": 126.91, "text": "Line one\nline\ttwo"},
        {"op": "photo", "id": "view", "lat": 37.52, "lon": 126.92, "type": "image/png", "data": PNG_1X1_BASE64, "title": "View"},
        {"op": "track", "id": "flight-7", "title": "Flight 7", "points": [
            {"lat": 37.5663, "lon": 126.9779, "ts": 1700000000000, "alt": 120},
            {"lat": 37.5664, "lon": 126.978},
        ]},
        {"op": "finish_track", "id": "flight-7"},
        {"op": "move", "id": "drone-1", "lat": 37.5665, "lon": 126.9781},
        {"op": "delete", "id": "gate"},
    ]


def test_every_kind_reaches_the_app_once(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    status, out, err = tool.pair()
    assert status == 0, err
    assert "Paired as 'receiver-" in out and "Allowed: pin, marker, note, photo, track." in out
    assert relay_server.post(*_every_kind()).json == {"accepted": 8, "last_seq": 8}

    status, out, err = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 0, err
    assert out == "Done with 8 operations, up to 8\n"
    sent = _requests(receiver)
    assert [type(request).__name__ for request in sent] == [
        "PutPin", "PutMarker", "PutNote", "PhotoBegin", "PhotoChunk", "PhotoEnd", "TrackAppend", "TrackFinish", "Move", "Delete",
    ]
    assert sent[0] == messages.PutPin("drone-1", SEOUL, "Drone 1")
    assert sent[1] == messages.PutMarker("gate", Coordinate(37.5, 126.9), "Gate", MarkerColor.GREEN)
    assert sent[2].text == "Line one\nline\ttwo"
    assert sent[3].header.title == "View" and sent[3].header.size == len(PNG_1X1)
    assert sent[4].data == PNG_1X1
    assert sent[6].title == "Flight 7"
    assert sent[6].points == (TrackPoint(SEOUL, 1700000000000, 120.0), TrackPoint(Coordinate(37.5664, 126.978)))
    assert sent[8] == messages.Move("drone-1", Coordinate(37.5665, 126.9781))
    assert sent[9] == messages.Delete("gate")

    # The place is saved privately, and the relay was told it may forget everything.
    place = json.loads((tool.state / "relay-state.json").read_text(encoding="utf-8"))
    assert place == {"version": 1, "relay": relay_server.url, "epoch": relay_server.queue.epoch, "after": 8}
    if POSIX:
        assert (tool.state / "relay-state.json").stat().st_mode & 0o777 == 0o600
    assert relay_server.held() == []

    # Nothing is applied twice.
    status, out, _ = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 0 and out == ""
    assert len(_requests(receiver)) == len(sent)


def test_output_never_shows_tokens_ids_coordinates_titles_or_text(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    tool.pair("--kinds", "pin")
    relay_server.post(pin("secret-id", title="Secret title"), {"op": "note", "id": "other-id", "lat": 12.345, "lon": 67.891, "text": "Secret text"})
    status, out, err = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 0
    for secret in ("secret-id", "other-id", "Secret title", "Secret text", "37.5663", "12.345", relay_server.gateway, relay_server.source):
        assert secret not in out + err


def test_kinds_the_person_did_not_allow_are_refused_and_skipped(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    assert tool.pair("--kinds", "pin")[0] == 0
    relay_server.post({"op": "marker", "id": "m", "lat": 1, "lon": 2}, pin("p"), {"op": "note", "id": "n", "lat": 1, "lon": 2, "text": "x"})
    status, out, _ = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 0
    assert out.splitlines() == [
        "Refused operation 1 (marker): not_permitted",
        "Refused operation 3 (note): not_permitted",
        "Done with 3 operations, up to 3 (2 refused)",
    ]
    assert [type(request).__name__ for request in _requests(receiver)] == ["PutPin"]
    assert relay_server.held() == []


def test_with_the_app_out_of_reach_once_gives_up_with_exit_code_3(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch) -> None:
    tool.pair()
    relay_server.post(pin("a"), pin("b"))
    receiver.close()
    # Long enough for one wait even where a refused connection takes two seconds, as on Windows.
    monkeypatch.setattr(gateway, "ONCE_GIVE_UP_SECONDS", 3.0)
    monkeypatch.setattr(gateway, "NO_ANSWER_NOTICE_SECONDS", 0.5)
    started = time.monotonic()
    status, out, _ = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 3
    assert 3.0 <= time.monotonic() - started < 30
    # The SDK kept trying for those seconds, and the gateway said so once.
    assert out.splitlines() == [
        "No answer from the Erdigo app; trying again.",
        "The Erdigo app could not be reached. The operations stay on the relay.",
    ]
    assert relay_server.held() == [1, 2]
    assert not (tool.state / "relay-state.json").exists() or json.loads((tool.state / "relay-state.json").read_text())["after"] == 0


def _lose_the_first_answer(receiver: TestReceiver, request_type: type) -> List[int]:
    """Makes the receiver apply the first request of this type and then drop the connection before answering it."""
    lost: List[int] = []

    def answer(envelope: messages.RequestEnvelope) -> None:
        if isinstance(envelope.request, request_type) and not lost:
            lost.append(envelope.seq)
            device = next(iter(receiver.devices.values()))
            device.last_seq = envelope.seq
            raise ConnectionAbortedError()
        return None

    receiver.answer = answer
    return lost


@pytest.mark.parametrize("once", [True, False])
def test_a_lost_answer_is_sent_again_with_the_same_sequence_number(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch, once: bool) -> None:
    """Spec sections 7 and 13: the same request with the same number, so a track's points are not applied twice."""
    tool.pair()
    lost = _lose_the_first_answer(receiver, messages.TrackAppend)
    relay_server.post({"op": "track", "id": "walk", "points": [{"lat": 1, "lon": 2, "ts": 1700000000000}]}, pin("after"))
    if once:
        status, out, err = tool.run_once(relay_server.url, relay_server.gateway)
        lines = out.splitlines()
    else:
        lines = _run_until_done(tool, relay_server, monkeypatch)
        status, err = 0, ""
    assert status == 0, err
    assert lines[0] == "Done with 2 operations, up to 2"
    appended = [envelope.seq for envelope in receiver.record.requests if isinstance(envelope.request, messages.TrackAppend)]
    assert appended == [lost[0], lost[0]]
    assert _saved_after(tool) == 2


def _saved_after(tool: Gateway) -> int:
    # Stopped before its next fetch, the gateway has not yet told the relay what it may forget.
    return json.loads((tool.state / "relay-state.json").read_text(encoding="utf-8"))["after"]


def _run_until_done(tool: Gateway, relay_server: RunningRelay, monkeypatch, react=None, batches: int = 1) -> List[str]:
    """Runs the gateway without --once in this process, and stops it as Ctrl+C would once it has finished some batches."""
    lines: List[str] = []

    def say(line: str) -> None:
        lines.append(line)
        if react is not None:
            react(line)
        if sum(1 for said in lines if said.startswith("Done with")) == batches and line.startswith("Done with"):
            raise KeyboardInterrupt()

    monkeypatch.setattr(gateway, "say", say)
    status, _, err = tool.main("run", "--relay", relay_server.url, *tool.address(), token=relay_server.gateway)
    assert status == 0, err
    assert lines[-1] == "Stopped."
    return lines


def test_without_once_the_gateway_says_when_the_app_does_not_answer_and_goes_on(
    tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch
) -> None:
    tool.pair()
    monkeypatch.setattr(gateway, "NO_ANSWER_NOTICE_SECONDS", 0.3)
    receiver.silent_connections = 10**6
    relay_server.post(pin("a"))

    def the_app_comes_back(line: str) -> None:
        if line.startswith("No answer from the Erdigo app"):
            receiver.silent_connections = 0

    lines = _run_until_done(tool, relay_server, monkeypatch, the_app_comes_back)
    # The SDK kept trying, and the gateway said so once, without a time, since only the SDK knows when it tries next.
    assert lines == ["No answer from the Erdigo app; trying again.", "Done with 1 operation, up to 1", "Stopped."]
    assert receiver.record.silenced >= 2
    assert receiver.seqs() == [1]
    assert _requests(receiver) == [messages.PutPin("a", SEOUL)]
    assert _saved_after(tool) == 1


def test_without_once_a_lost_answer_is_sent_again_with_the_same_number_however_long_the_app_is_away(
    tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch
) -> None:
    """Without --once the SDK keeps the request for as long as the app is out of reach (RetryPolicy.FOREVER). A policy
    with a limit would give up after it, and the gateway would then send the points again as a new request, with a new
    sequence number, which the app would apply a second time."""
    tool.pair()
    # The SDK's clock moves a minute on at every look, so the outage below outlasts any limited policy.
    ahead = [0.0]

    def hurried() -> float:
        ahead[0] += 61.0
        return time.monotonic() + ahead[0]

    monkeypatch.setattr(erdigo_client, "time", SimpleNamespace(monotonic=hurried, sleep=time.sleep, time=time.time))
    monkeypatch.setattr(gateway, "NO_ANSWER_NOTICE_SECONDS", 60.0)
    lost: List[int] = []

    def answer_after_an_outage(envelope: messages.RequestEnvelope) -> None:
        if isinstance(envelope.request, messages.TrackAppend) and not lost:
            # Applied, then the answer is lost, and the app is out of reach for the next few connections.
            lost.append(envelope.seq)
            next(iter(receiver.devices.values())).last_seq = envelope.seq
            receiver.silent_connections = 5
            raise ConnectionAbortedError()
        return None

    receiver.answer = answer_after_an_outage
    relay_server.post({"op": "track", "id": "walk", "points": [{"lat": 1, "lon": 2, "ts": 1700000000000}]}, pin("after"))
    lines = _run_until_done(tool, relay_server, monkeypatch)
    assert lines == ["Done with 2 operations, up to 2", "Stopped."]
    assert receiver.record.silenced == 5
    appended = [envelope.seq for envelope in receiver.record.requests if isinstance(envelope.request, messages.TrackAppend)]
    assert appended == [lost[0], lost[0]]
    # The SDK's clock passed many minutes while it kept trying.
    assert ahead[0] > 5 * 61.0


@pytest.mark.skipif(not POSIX, reason="signals")
@pytest.mark.parametrize("stop", ["SIGTERM", "SIGINT"])
def test_without_once_ctrl_c_and_sigterm_stop_the_gateway_while_the_app_is_out_of_reach(
    tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, stop: str
) -> None:
    """The SDK waits 1, 2, 4 ... seconds between attempts for as long as the app is out of reach. A signal ends that
    wait at once, and the operation stays on the relay."""
    number = getattr(signal, stop)
    if signal.getsignal(number) is signal.SIG_IGN and number == signal.SIGINT:
        pytest.skip("SIGINT is ignored here, as in a shell's background job, and so in the gateway")
    tool.pair()
    relay_server.post(pin("waiting"))
    receiver.close()
    environment = dict(os.environ, ERDIGO_RELAY_TOKEN=relay_server.gateway, PYTHONUNBUFFERED="1")
    process = subprocess.Popen(
        [sys.executable, str(EXAMPLES / "gateway.py"), "run", "--relay", relay_server.url, *tool.address()],
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    said: List[str] = []
    noticed = threading.Event()

    def read() -> None:
        for line in process.stdout:  # type: ignore[union-attr]
            said.append(line.rstrip("\n"))
            noticed.set()

    reader = threading.Thread(target=read, daemon=True)
    reader.start()
    try:
        assert noticed.wait(30), "the gateway did not say that the app does not answer"
        # By now the SDK has tried three times and waits about 4 seconds before the next attempt.
        time.sleep(1.0)
        signalled = time.monotonic()
        process.send_signal(number)
        process.wait(timeout=30)
        took = time.monotonic() - signalled
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
    reader.join(timeout=5)
    err = process.stderr.read()  # type: ignore[union-attr]
    process.stdout.close()  # type: ignore[union-attr]
    process.stderr.close()  # type: ignore[union-attr]
    assert process.returncode == 0, err
    assert took < 3
    assert said == ["No answer from the Erdigo app; trying again.", "Stopped."]
    assert relay_server.held() == [1]


# Below zero rather than zero, so that the connection counts as idle even when the clock has not moved: on Windows,
# Python before 3.13 reads a clock that moves in steps of about 16 ms.
@pytest.mark.parametrize("idle_seconds, connections", [(-1.0, 2), (240.0, 1)])
def test_a_connection_left_idle_is_closed_before_the_next_batch(
    tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch, idle_seconds: float, connections: int
) -> None:
    tool.pair()
    monkeypatch.setattr(gateway, "IDLE_SECONDS", idle_seconds)
    relay_server.post(pin("a"))

    def send_more(line: str) -> None:
        if line == "Done with 1 operation, up to 1":
            relay_server.post(pin("b"))

    lines = _run_until_done(tool, relay_server, monkeypatch, send_more, batches=2)
    assert lines == ["Done with 1 operation, up to 1", "Done with 1 operation, up to 2", "Stopped."]
    assert receiver.record.welcomes == connections
    # Nothing was sent twice.
    assert receiver.seqs() == [1, 2]


def test_operations_come_again_after_the_app_returns(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch) -> None:
    """The first operation reaches the app, then it goes away: the second waits on the relay for the next run."""
    tool.pair()
    monkeypatch.setattr(gateway, "ONCE_GIVE_UP_SECONDS", 0.5)
    relay_server.post(pin("a"), pin("b"))

    def first_only(envelope):
        if isinstance(envelope.request, messages.PutPin) and envelope.request.id == "b":
            receiver.drop_connections()
            receiver.silent_connections = 1000
        return None

    receiver.answer = first_only
    assert tool.run_once(relay_server.url, relay_server.gateway)[0] == 3
    assert relay_server.held() == [1, 2]
    receiver.silent_connections = 0
    receiver.answer = lambda envelope: None
    status, out, _ = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 0 and out == "Done with 1 operation, up to 2\n"
    assert relay_server.held() == []


def _cut(connection: socket.socket) -> None:
    try:
        connection.shutdown(socket.SHUT_RDWR)
    except OSError:
        pass
    try:
        connection.close()
    except OSError:
        pass


class AnswerProxy:
    """A TCP proxy in front of the test receiver that keeps back the receiver's answer to the first request on every
    connection: the third frame it sends, after the handshake reply and welcome. The receiver has applied the request.

    In mode "lose" it then cuts both sides, as a network that drops the answer would. In mode "hold" it passes nothing
    more on and keeps the connection open, as an app that takes the request and never answers. In mode "pass" it passes
    everything on. The mode may change while it runs."""

    __test__ = False

    def __init__(self, receiver_port: int, mode: str) -> None:
        self.receiver_port = receiver_port
        self.mode = mode
        #: Answers kept back so far.
        self.kept_back = 0
        self._lock = threading.Lock()
        self._connections: List[socket.socket] = []
        self._stopped = False
        self._server = socket.create_server(("127.0.0.1", 0))
        # The accept loop wakes up often enough to notice close(), as the test receiver's does.
        self._server.settimeout(0.05)
        self.port = self._server.getsockname()[1]
        self._accepting = threading.Thread(target=self._accept, daemon=True)
        self._accepting.start()

    def close(self) -> None:
        self._stopped = True
        self._accepting.join(timeout=5)
        self._server.close()
        with self._lock:
            for connection in self._connections:
                _cut(connection)

    def _accept(self) -> None:
        while not self._stopped:
            try:
                device, _ = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            device.settimeout(None)
            try:
                app = socket.create_connection(("127.0.0.1", self.receiver_port))
            except OSError:
                _cut(device)
                continue
            with self._lock:
                self._connections += [device, app]
            threading.Thread(target=self._requests, args=(device, app), daemon=True).start()
            threading.Thread(target=self._answers, args=(device, app), daemon=True).start()

    @staticmethod
    def _requests(device: socket.socket, app: socket.socket) -> None:
        try:
            while True:
                data = device.recv(65536)
                if not data:
                    break
                app.sendall(data)
        except OSError:
            pass
        finally:
            _cut(device)
            _cut(app)

    def _answers(self, device: socket.socket, app: socket.socket) -> None:
        pending = b""
        frames = 0
        try:
            while True:
                data = app.recv(65536)
                if not data:
                    break
                pending += data
                while len(pending) >= 2 and len(pending) >= 2 + int.from_bytes(pending[:2], "big"):
                    size = 2 + int.from_bytes(pending[:2], "big")
                    frame, pending = pending[:size], pending[size:]
                    frames += 1
                    if frames == 3 and self.mode != "pass":
                        with self._lock:
                            self.kept_back += 1
                        if self.mode == "hold":
                            # Until the device gives up on the connection, which ends the other direction.
                            while app.recv(65536):
                                pass
                        return
                    device.sendall(frame)
        except OSError:
            pass
        finally:
            _cut(device)
            _cut(app)


TWO_POINTS = [{"lat": 1, "lon": 2}, {"lat": 1.5, "lon": 2.5}]


def _track_appends(receiver: TestReceiver) -> List[int]:
    with receiver.lock:
        return [envelope.seq for envelope in receiver.record.requests if isinstance(envelope.request, messages.TrackAppend)]


@pytest.mark.parametrize("mode", ["lose", "hold"])
def test_with_once_an_app_whose_answers_never_arrive_is_given_up(
    tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch, mode: str
) -> None:
    """The app takes every connection, but its answers are lost on the way, or it never sends them. The SDK starts its
    own count again at each new connection, so it would go on for ever. The gateway counts from the first lost
    connection, gives up after ONCE_GIVE_UP_SECONDS, and the operation stays on the relay."""
    assert tool.pair()[0] == 0
    proxy = AnswerProxy(receiver.port, mode)
    tool.app_port = proxy.port
    monkeypatch.setattr(gateway, "ONCE_GIVE_UP_SECONDS", 1.5)
    monkeypatch.setattr(gateway, "NO_ANSWER_NOTICE_SECONDS", 0.5)
    monkeypatch.setattr(gateway, "WATCH_SECONDS", 0.01)
    # Long enough between attempts for the watcher to see that the connection is gone, as 1, 2, 4 ... seconds are.
    monkeypatch.setattr(erdigo_client, "backoff", lambda attempt: 0.2)
    # The SDK stops waiting for an answer after half a second rather than 15.
    monkeypatch.setattr(gateway, "ErdigoClient", functools.partial(erdigo_client.ErdigoClient, ack_timeout=0.5))
    relay_server.post({"op": "track", "id": "walk", "points": TWO_POINTS})
    # Were the gateway never to give up, the answers would get through after a while, and the test would fail rather
    # than hang.
    rescue = threading.Timer(30, setattr, (proxy, "mode", "pass"))
    rescue.start()
    try:
        started = time.monotonic()
        status, out, err = tool.run_once(relay_server.url, relay_server.gateway)
        took = time.monotonic() - started
    finally:
        rescue.cancel()
        proxy.close()
    assert status == 3, err
    assert out.splitlines() == [
        "No answer from the Erdigo app; trying again.",
        "The Erdigo app could not be reached. The operations stay on the relay.",
    ]
    assert 1.5 <= took < 15
    # Several connections, each of which started the SDK's own count again.
    assert proxy.kept_back >= 2
    # The same request every time, with the same number, so the app applied the points once.
    appended = _track_appends(receiver)
    assert len(appended) == proxy.kept_back and set(appended) == {appended[0]}
    assert relay_server.held() == [1]
    assert not (tool.state / "relay-state.json").exists() or _saved_after(tool) == 0


def test_without_once_an_app_whose_answers_are_lost_is_waited_for(
    tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch
) -> None:
    """Without --once nothing is given up: the SDK sends the same request again until an answer gets through."""
    assert tool.pair()[0] == 0
    proxy = AnswerProxy(receiver.port, "lose")
    tool.app_port = proxy.port
    # The limit of --once, which does not apply here, is passed long before the answers get through.
    monkeypatch.setattr(gateway, "ONCE_GIVE_UP_SECONDS", 0.3)
    monkeypatch.setattr(gateway, "NO_ANSWER_NOTICE_SECONDS", 1.0)
    monkeypatch.setattr(gateway, "WATCH_SECONDS", 0.01)
    monkeypatch.setattr(erdigo_client, "backoff", lambda attempt: 0.1)
    relay_server.post({"op": "track", "id": "walk", "points": TWO_POINTS})

    def let_answers_through(line: str) -> None:
        if line.startswith("No answer from the Erdigo app"):
            proxy.mode = "pass"

    try:
        lines = _run_until_done(tool, relay_server, monkeypatch, let_answers_through)
    finally:
        proxy.close()
    assert lines == ["No answer from the Erdigo app; trying again.", "Done with 1 operation, up to 1", "Stopped."]
    appended = _track_appends(receiver)
    assert len(appended) == proxy.kept_back + 1 and proxy.kept_back >= 3
    assert set(appended) == {appended[0]}
    assert _saved_after(tool) == 1


@pytest.mark.parametrize("not_now_first", [False, True])
def test_with_once_time_spent_pacing_points_is_not_counted(
    tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch, not_now_first: bool
) -> None:
    """A track's points wait for the app's allowance before they are sent, here about two seconds for the second track,
    four times the time --once allows an app in trouble. That wait is not trouble, even right after the app answered
    "not now", so a slow operation over a working connection is never given up on."""
    # Room for the first track's 100 points at once, and then 50 points a second.
    receiver.limits = Limits(points_per_s=50, point_burst=100)
    assert tool.pair()[0] == 0
    monkeypatch.setattr(gateway, "ONCE_GIVE_UP_SECONDS", 0.5)
    monkeypatch.setattr(gateway, "WATCH_SECONDS", 0.01)
    refused: List[int] = []

    def answer(envelope: messages.RequestEnvelope) -> Optional[object]:
        if not_now_first and isinstance(envelope.request, messages.TrackAppend) and envelope.request.id == "b" and not refused:
            refused.append(envelope.seq)
            return messages.ReceiverError("internal_error", envelope.seq)
        return None

    receiver.answer = answer
    points = [{"lat": 1 + index / 1000, "lon": 2} for index in range(100)]
    relay_server.post({"op": "track", "id": "a", "points": points}, {"op": "track", "id": "b", "points": points})
    started = time.monotonic()
    status, out, err = tool.run_once(relay_server.url, relay_server.gateway)
    took = time.monotonic() - started
    assert status == 0, err
    retried = ["The Erdigo app answered internal_error; trying again in 1 second."] if not_now_first else []
    assert out.splitlines() == retried + ["Done with 2 operations, up to 2"]
    # The second track waited for its points once, or twice when it was sent again after "not now".
    assert took >= (3.5 if not_now_first else 1.5)
    assert _track_appends(receiver) == ([1, 2, 2] if not_now_first else [1, 2])
    assert relay_server.held() == []


def test_a_transient_refusal_is_tried_again(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    tool.pair()
    receiver.answer = Script(lambda envelope: messages.ReceiverError("internal_error", envelope.seq), times=2)
    relay_server.post(pin("a"))
    status, out, _ = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 0
    assert out.splitlines() == [
        "The Erdigo app answered internal_error; trying again in 1 second.",
        "The Erdigo app answered internal_error; trying again in 1 second.",
        "Done with 1 operation, up to 1",
    ]
    # internal_error does not use up the number, so the third try is the same request with the same number.
    assert receiver.seqs() == [1, 1, 1]


def test_a_refusal_for_good_is_printed_and_skipped(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    tool.pair()
    receiver.answer = lambda envelope: messages.ReceiverError("kind_conflict", envelope.seq) if getattr(envelope.request, "id", "") == "clash" else None
    relay_server.post(pin("clash"), {"op": "move", "id": "clash", "lat": 1, "lon": 2}, pin("fine"))
    status, out, _ = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 0
    assert out.splitlines() == [
        "Refused operation 1 (pin): kind_conflict",
        "Refused operation 2 (move): kind_conflict",
        "Done with 3 operations, up to 3 (2 refused)",
    ]
    assert _requests(receiver)[-1] == messages.PutPin("fine", SEOUL)


def test_a_relay_that_starts_again_is_followed(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver, monkeypatch) -> None:
    tool.pair()
    relay_server.post(pin("before"))
    assert tool.run_once(relay_server.url, relay_server.gateway)[0] == 0
    port, source, token = relay_server.port, relay_server.source, relay_server.gateway
    relay_server.close()
    restarted = RunningRelay(port=port, source=source, gateway=token)
    try:
        restarted.post(pin("after-1"), pin("after-2"))
        status, out, _ = tool.run_once(restarted.url, token)
        assert status == 0
        assert out.splitlines() == ["The relay started a new numbering; starting from its oldest operation.", "Done with 2 operations, up to 2"]
        assert [request.id for request in _requests(receiver)] == ["before", "after-1", "after-2"]
        assert json.loads((tool.state / "relay-state.json").read_text())["epoch"] == restarted.queue.epoch
    finally:
        restarted.close()


def test_another_relay_address_starts_from_its_oldest_operation(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    tool.pair()
    relay_server.post(pin("first"))
    assert tool.run_once(relay_server.url, relay_server.gateway)[0] == 0
    other = RunningRelay(gateway=relay_server.gateway)
    try:
        other.post(pin("second"))
        status, out, _ = tool.run_once(other.url, other.gateway)
        # A new relay, not a new numbering of the same one.
        assert status == 0 and out == "Done with 1 operation, up to 1\n"
    finally:
        other.close()


def test_expired_operations_are_reported(tool: Gateway, receiver: TestReceiver) -> None:
    clock = FakeClock()
    server = RunningRelay(clock=clock, keep_seconds=3600)
    try:
        tool.pair()
        server.post(pin("old"))
        clock.advance(3601)
        server.post(pin("new"))
        status, out, _ = tool.run_once(server.url, server.gateway)
        assert status == 0
        assert out.splitlines() == ["Some operations expired on the relay before they were fetched.", "Done with 1 operation, up to 2"]
    finally:
        server.close()


def test_expired_operations_are_reported_once_across_runs(tool: Gateway, receiver: TestReceiver) -> None:
    """The relay says 'missed' until the gateway moves past what expired. A scheduled job says so once, not each run."""
    clock = FakeClock()
    server = RunningRelay(clock=clock, keep_seconds=3600)
    try:
        tool.pair()
        server.post(pin("a"))
        assert tool.run_once(server.url, server.gateway)[1] == "Done with 1 operation, up to 1\n"
        server.post(pin("b"))
        clock.advance(7200)
        assert tool.run_once(server.url, server.gateway)[1] == "Some operations expired on the relay before they were fetched.\n"
        assert json.loads((tool.state / "relay-state.json").read_text())["missed_said"] is True
        assert tool.run_once(server.url, server.gateway)[1] == ""
        server.post(pin("c"))
        assert tool.run_once(server.url, server.gateway)[1] == "Done with 1 operation, up to 3\n"
        assert "missed_said" not in json.loads((tool.state / "relay-state.json").read_text())
        assert tool.run_once(server.url, server.gateway)[1] == ""
    finally:
        server.close()


class _StubRelay:
    """Gives scripted answers to the gateway's fetches, then stops it as Ctrl+C would."""

    def __init__(self, answers: Sequence[Tuple[int, dict, bytes]]) -> None:
        self.answers = list(answers)
        self.places: List[Tuple[Optional[str], int]] = []

    def fetch(self, epoch: Optional[str], after: int, wait: int, seconds: Optional[float] = None) -> Tuple[int, str, bytes]:
        self.places.append((epoch, after))
        if not self.answers:
            raise KeyboardInterrupt()
        status, headers, body = self.answers.pop(0)
        return status, headers["Content-Type"], body


class _QuietApp:
    #: What the gateway's watch looks at: present, as for a client with a connection.
    limits = object()

    def __init__(self) -> None:
        self.pins: List[str] = []

    def connect(self) -> None:
        pass

    def put_pin(self, item_id: str, at: Coordinate, title: Optional[str] = None) -> None:
        self.pins.append(item_id)

    def close(self) -> None:
        pass


def test_expired_operations_are_reported_once_for_each_place(tmp_path: Path, monkeypatch) -> None:
    """The relay goes on saying 'missed' until the gateway moves past what expired. A gateway that keeps running says
    so once, and again only from another place."""
    lines: List[str] = []
    monkeypatch.setattr(gateway, "say", lines.append)
    first, second = "E" * 16, "F" * 16
    relay_stub = _StubRelay([
        answer(epoch=first, reset=True, missed=True),
        answer(epoch=first, reset=False, missed=True),
        answer(epoch=first, reset=False, missed=True, ops=[entry(3, GOOD)]),
        answer(epoch=first, reset=False, missed=True),
        answer(epoch=first, reset=False, missed=True),
        answer(epoch=second, reset=True, missed=True),
        answer(epoch=second, reset=False, missed=True),
    ])
    app = _QuietApp()
    place = gateway.Place("http://127.0.0.1:9", None, 0)
    running = gateway.Gateway(relay_stub, app, str(tmp_path), place, once=False)  # type: ignore[arg-type]
    assert running.run() == 0
    assert lines == [
        "Some operations expired on the relay before they were fetched.",
        "Done with 1 operation, up to 3",
        "Some operations expired on the relay before they were fetched.",
        "The relay started a new numbering; starting from its oldest operation.",
        "Some operations expired on the relay before they were fetched.",
        "Stopped.",
    ]
    assert relay_stub.places == [(None, 0), (first, 0), (first, 0), (first, 3), (first, 3), (first, 3), (second, 0), (second, 0)]
    assert app.pins == ["p"]


def test_numbers_too_large_for_a_float_are_refused_and_skipped(tool: Gateway, receiver: TestReceiver) -> None:
    """A relay of your own may pass on what the example relays refuse. One such operation must not hold up the rest."""
    tool.pair()
    huge = 10**400
    ops = [
        {"op": "pin", "id": "p", "lat": huge, "lon": 2},
        {"op": "move", "id": "p", "lat": 1, "lon": -huge},
        {"op": "track", "id": "t", "points": [{"lat": 1, "lon": 2, "alt": huge}]},
        {"op": "track", "id": "t", "points": [{"lat": 1, "lon": 2, "ts": huge}]},
        GOOD,
    ]
    status, out, err, _ = _run_against(tool, [answer(ops=[entry(index + 1, op) for index, op in enumerate(ops)]), answer(reset=False)])
    assert status == 0, err
    assert out.splitlines() == [
        "Refused operation 1 (pin): invalid_argument",
        "Refused operation 2 (move): invalid_argument",
        "Refused operation 3 (track): invalid_argument",
        "Refused operation 4 (track): invalid_argument",
        "Done with 5 operations, up to 5 (4 refused)",
    ]
    assert _requests(receiver) == [messages.PutPin("p", Coordinate(1, 2))]


# The command line and the environment


def test_mistakes_exit_with_code_1(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    address = tool.address()
    token = relay_server.gateway
    cases = [
        (["run", "--relay", "http://relay.example.com", "--once", *address], token, "--relay must start with https://"),
        (["run", "--relay", "http://192.0.2.1:8080", "--once", *address], token, "--relay must start with https://"),
        (["run", "--relay", "ftp://127.0.0.1", *address], token, "--relay must be an https:// address"),
        (["run", "--relay", "https://user:pass@relay.example.com", *address], token, "without a user name"),
        (["run", "--relay", "https://relay.example.com/?x=1", *address], token, "without a user name"),
        (["run", "--relay", "https://relay..example.com", "--once", *address], token, "host name that is not valid"),
        (["run", "--relay", f"https://{'a' * 64}.example.com", "--once", *address], token, "host name that is not valid"),
        (["run", "--relay", relay_server.url, "--once", *address], None, "set ERDIGO_RELAY_TOKEN"),
        (["run", "--relay", relay_server.url, "--once", *address], "short", "is not a valid token"),
        (["run", "--relay", relay_server.url, "--once", *address], token, "holds no pairing"),
        (["run", "--relay", relay_server.url, "--host", "127.0.0.1", "--state-dir", str(tool.state)], token, "--host and --port go together"),
        (["run", "--once", *address], token, "--relay is required"),
        (["run", "--relay", relay_server.url, "--token", token, *address], token, "not accepted as arguments"),
        (["run", "--relay", relay_server.url, "--profile", "a", "--state-dir", "b"], token, "cannot be used together"),
        (["run", "--relay", relay_server.url, "--profile", "a/b"], token, "--profile may contain only"),
        (["run", "--relay", relay_server.url, "--name", "x", *address], token, "--name does not apply"),
        (["pair", "--kinds", "pin", *address], None, "--name is required"),
        (["pair", "--name", "x", "--kinds", "circle", *address], None, "--kinds takes"),
        # An empty value, as from an unset shell variable, is not taken to mean every kind.
        (["pair", "--name", "x", "--kinds", "", *address], None, "--kinds needs at least one item kind"),
        (["pair", "--name", "x", "--kinds", ",", *address], None, "--kinds needs at least one item kind"),
        (["pair", "--name", "x", "--code", "AAAAA", *address], None, "not accepted as arguments"),
        (["fly"], None, "unknown command"),
    ]
    for arguments, given, message in cases:
        status, out, err = tool.main(*arguments, token=given)
        assert status == 1, arguments
        assert message in err, (arguments, err)
        assert token not in out + err
    assert receiver.record.connections == 0
    assert relay_server.logs == []


def test_a_wrong_token_exits_with_code_2(tool: Gateway, relay_server: RunningRelay) -> None:
    tool.pair()
    relay_server.post(pin("a"))
    wrong = new_token()
    status, out, err = tool.run_once(relay_server.url, wrong)
    assert status == 2 and "refused the token" in err
    status, _, err = tool.run_once(relay_server.url, relay_server.source)
    assert status == 2 and "refused the token" in err
    assert relay_server.held() == [1]
    assert wrong not in out + err


def test_a_profile_holds_one_pairing(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    assert tool.pair()[0] == 0
    status, _, err = tool.pair()
    assert status == 1 and "already holds a pairing" in err
    assert len(receiver.record.pair_requests) == 1


def test_two_gateways_cannot_share_a_profile(tool: Gateway, relay_server: RunningRelay) -> None:
    tool.pair()
    with FileLock(str(tool.state / ".gateway.lock")):
        status, _, err = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 1 and "another gateway is running" in err


def test_a_damaged_place_is_not_overwritten(tool: Gateway, relay_server: RunningRelay) -> None:
    tool.pair()
    damaged = tool.state / "relay-state.json"
    damaged.write_text("{not json", encoding="utf-8")
    if POSIX:
        os.chmod(damaged, 0o600)
    status, _, err = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 1 and "relay-state.json is damaged" in err
    assert damaged.read_text(encoding="utf-8") == "{not json"


@pytest.mark.skipif(not POSIX, reason="file modes")
def test_a_place_others_can_read_is_refused(tool: Gateway, relay_server: RunningRelay) -> None:
    tool.pair()
    relay_server.post(pin("a"))
    assert tool.run_once(relay_server.url, relay_server.gateway)[0] == 0
    os.chmod(tool.state / "relay-state.json", 0o644)
    status, _, err = tool.run_once(relay_server.url, relay_server.gateway)
    assert status == 1 and "mode 0600" in err


def test_help(tool: Gateway) -> None:
    status, out, _ = tool.main("help")
    assert status == 0 and "gateway.py run --relay URL" in out and "ERDIGO_RELAY_TOKEN" in out
    assert "3 with --once, the app or the relay could not be reached for about 30 seconds" in " ".join(out.split())


# Answers that break the relay API, from a relay written for these tests


Scripted = Union[str, Tuple[int, dict, bytes]]


def answer(epoch: str = "E" * 16, reset: bool = True, ops: Sequence[object] = (), missed: bool = False, **extra: object) -> Tuple[int, dict, bytes]:
    body = {"epoch": epoch, "reset": reset, "missed": missed, "ops": list(ops), **extra}
    return 200, {"Content-Type": "application/json"}, json.dumps(body).encode()


def entry(seq: object, op: object, received_ms: object = 1_700_000_000_000) -> dict:
    return {"seq": seq, "received_ms": received_ms, "op": op}


class ScriptedRelay:
    """Answers each GET /v1/ops with the next scripted answer. 'drop' closes the connection without one, and 'huge'
    sends a valid answer of more than 8 MiB without saying how long it is."""

    def __init__(self, answers: Sequence[Scripted]) -> None:
        self.answers = list(answers)
        self.paths: List[str] = []
        scripted = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *_: object) -> None:
                pass

            def do_GET(self) -> None:
                scripted.paths.append(self.path)
                reply = scripted.answers.pop(0) if len(scripted.answers) > 1 else scripted.answers[0]
                if reply == "drop":
                    self.close_connection = True
                    return
                if reply == "huge":
                    self.close_connection = True
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Connection", "close")
                    self.end_headers()
                    # A valid answer, with white space after it that takes it past 8 MiB.
                    valid = answer()[2]
                    try:
                        self.wfile.write(valid + b" " * (8 * 1024 * 1024 + 1 - len(valid)))
                    except OSError:
                        pass
                    return
                status, headers, body = reply  # type: ignore[misc]
                self.send_response(status)
                for name, value in headers.items():
                    self.send_header(name, value)
                if "Content-Length" not in headers:
                    self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.server.daemon_threads = True
        self.url = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True).start()

    def close(self) -> None:
        self.server.shutdown()
        self.server.server_close()


def _run_against(tool: Gateway, answers: Sequence[Scripted], path: str = "") -> Tuple[int, str, str, ScriptedRelay]:
    scripted = ScriptedRelay(answers)
    try:
        status, out, err = tool.run_once(scripted.url + path, new_token())
    finally:
        scripted.close()
    return status, out, err, scripted


GOOD = {"op": "pin", "id": "p", "lat": 1, "lon": 2}


@pytest.mark.parametrize(
    "answers",
    [
        [(200, {"Content-Type": "application/json"}, b"not json")],
        [(200, {"Content-Type": "application/json"}, b'{"epoch":"EEEEEEEEEEEEEEEE","epoch":"EEEEEEEEEEEEEEEE","reset":true,"missed":false,"ops":[]}')],
        [(200, {"Content-Type": "text/html"}, answer()[2])],
        [answer(epoch="short")],
        [answer(epoch="E" * 16 + "!")],
        [answer(reset=False)],
        [answer(reset="yes")],
        [answer(extra=1)],
        [(200, {"Content-Type": "application/json"}, b'{"epoch":"EEEEEEEEEEEEEEEE","reset":true,"missed":false}')],
        [answer(ops={"seq": 1})],
        [answer(ops=[entry(index + 1, GOOD) for index in range(101)])],
        [answer(ops=[entry(0, GOOD)])],
        [answer(ops=[entry(1.0, GOOD)])],
        [answer(ops=[entry(True, GOOD)])],
        [answer(ops=[entry(2, GOOD), entry(2, GOOD)])],
        [answer(ops=[entry(3, GOOD), entry(2, GOOD)])],
        [answer(ops=[entry(2**53, GOOD)])],
        [answer(ops=[entry(1, GOOD, received_ms=-1)])],
        [answer(ops=[entry(1, GOOD, received_ms="now")])],
        [answer(ops=[entry(1, [GOOD])])],
        [answer(ops=[{"seq": 1, "op": GOOD}])],
        [answer(ops=[{**entry(1, GOOD), "extra": 1}])],
        # The second answer goes back to an operation the gateway finished, or resets while keeping the epoch.
        [answer(ops=[entry(5, GOOD)]), answer(reset=False, ops=[entry(5, GOOD)])],
        [answer(ops=[entry(5, GOOD)]), answer(reset=True)],
        [answer(ops=[entry(5, GOOD)]), answer(epoch="F" * 16, reset=False)],
        [(404, {"Content-Type": "application/json"}, b'{"error":"not_found"}')],
        [(400, {"Content-Type": "application/json"}, b'{"error":"invalid_query"}')],
        [(302, {"Location": "http://127.0.0.1:9/v1/ops"}, b"")],
        [(200, {"Content-Type": "application/json", "Content-Length": str(9 * 1024 * 1024)}, b"")],
        ["huge"],
    ],
)
def test_answers_that_break_the_api_exit_with_code_2(tool: Gateway, receiver: TestReceiver, answers: List[Scripted]) -> None:
    tool.pair()
    status, _, err, _ = _run_against(tool, answers)
    assert status == 2, err
    assert "gateway: the relay" in err


def test_server_errors_and_lost_connections_are_tried_again(tool: Gateway, receiver: TestReceiver) -> None:
    tool.pair()
    status, out, err, scripted = _run_against(tool, ["drop", (503, {}, b"busy"), (502, {"Content-Type": "text/html"}, b"<html>"), "drop", answer()])
    assert status == 0, err
    # The second lost connection was a kept-alive one, which is opened again at once without a word.
    assert out.splitlines() == [
        "The relay could not be reached; trying again in 1 second.",
        "The relay answered with an error (503); trying again in 1 second.",
        "The relay answered with an error (502); trying again in 1 second.",
    ]
    assert len(scripted.paths) == 5


class _SilentRelay:
    """Takes every connection and never answers."""

    def __init__(self) -> None:
        self._server = socket.create_server(("127.0.0.1", 0))
        self._server.settimeout(0.05)
        self.url = f"http://127.0.0.1:{self._server.getsockname()[1]}"
        self._held: List[socket.socket] = []
        self._stopped = False
        self._accepting = threading.Thread(target=self._accept, daemon=True)
        self._accepting.start()

    def close(self) -> None:
        self._stopped = True
        self._accepting.join(timeout=5)
        for connection in self._held:
            connection.close()
        self._server.close()

    def _accept(self) -> None:
        while not self._stopped:
            try:
                connection, _ = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            self._held.append(connection)


def _free_port() -> int:
    """A loopback port nothing listens on."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


@pytest.mark.parametrize("relay", ["nothing listening", "server errors", "no answer"])
def test_with_the_relay_out_of_reach_once_gives_up_with_exit_code_3(tool: Gateway, receiver: TestReceiver, monkeypatch, relay: str) -> None:
    """docs/cloud-relay.md: with --once the gateway gives up after about 30 seconds, counting the time each try takes,
    so a relay that takes the connection and never answers is given up on in that time too."""
    tool.pair()
    monkeypatch.setattr(gateway, "ONCE_GIVE_UP_SECONDS", 1.5)
    server: Optional[Union[ScriptedRelay, _SilentRelay]] = None
    if relay == "nothing listening":
        url = f"http://127.0.0.1:{_free_port()}"
    elif relay == "server errors":
        server = ScriptedRelay([(503, {"Content-Type": "application/json"}, b'{"error":"queue_full"}')])
        url = server.url
    else:
        server = _SilentRelay()
        url = server.url
    connections = receiver.record.connections
    started = time.monotonic()
    try:
        status, out, err = tool.run_once(url, new_token())
    finally:
        if server is not None:
            server.close()
    took = time.monotonic() - started
    assert status == 3, err
    assert err == ""
    lines = out.splitlines()
    assert lines[-1] == "The relay could not be reached."
    tried_again = "The relay answered with an error (503)" if relay == "server errors" else "The relay could not be reached"
    assert all(line.startswith(f"{tried_again}; trying again in ") for line in lines[:-1]), lines
    if relay == "no answer":
        # One try, which used all the time allowed rather than the 45 seconds a read may otherwise take.
        assert lines == ["The relay could not be reached."]
    # The last try gets a second at least.
    assert 1.5 <= took < 1.5 + 1.0 + 3.0
    assert receiver.record.connections == connections
    assert not (tool.state / "relay-state.json").exists()


def test_without_once_the_relay_is_tried_again_however_long_it_is_away(tool: Gateway, receiver: TestReceiver, monkeypatch) -> None:
    tool.pair()
    monkeypatch.setattr(gateway, "ONCE_GIVE_UP_SECONDS", 0.2)
    monkeypatch.setattr(gateway, "backoff", lambda attempt: 0.1)
    scripted = ScriptedRelay(["drop"] * 3 + [(503, {}, b"busy")] * 3 + [answer(ops=[entry(1, GOOD)])])
    try:
        lines = _run_until_done(tool, SimpleNamespace(url=scripted.url, gateway=new_token()), monkeypatch)  # type: ignore[arg-type]
    finally:
        scripted.close()
    assert lines == ["The relay could not be reached; trying again in 1 second."] * 3 + [
        "The relay answered with an error (503); trying again in 1 second."
    ] * 3 + ["Done with 1 operation, up to 1", "Stopped."]
    assert _requests(receiver) == [messages.PutPin("p", Coordinate(1, 2))]


def test_the_query_and_the_path_prefix(tool: Gateway, receiver: TestReceiver) -> None:
    tool.pair()
    status, _, err, scripted = _run_against(tool, [answer(ops=[entry(7, GOOD)]), answer(reset=False)], path="/erdigo/relay/")
    assert status == 0, err
    assert scripted.paths == [
        "/erdigo/relay/v1/ops?after=0&limit=100&wait=0",
        "/erdigo/relay/v1/ops?epoch=EEEEEEEEEEEEEEEE&after=7&limit=100&wait=0",
    ]


def test_operations_the_gateway_cannot_map_are_refused_and_skipped(tool: Gateway, receiver: TestReceiver) -> None:
    tool.pair()
    ops = [
        {"op": "circle", "id": "x"},
        {**GOOD, "speed": 3},
        {**GOOD, "lat": 91},
        {"op": "photo", "id": "ph", "lat": 0, "lon": 0, "type": "image/jpeg", "data": PNG_1X1_BASE64},
        {**GOOD, "id": "a b"},
        {"op": "note", "id": "n", "lat": 0, "lon": 0, "text": "a\rb"},
        {"op": "marker", "id": "m", "lat": 0, "lon": 0, "color": "pink"},
        {"op": "track", "id": "t", "points": [{"lat": 1, "lon": 2, "ts": 1.5}]},
        {"op": "pin", "id": "p", "lat": "1", "lon": 2},
        # A relay that cannot tell 5 from 5.0 may send a whole time as a fraction.
        {"op": "track", "id": "t", "points": [{"lat": 1, "lon": 2, "ts": 1700000000000.0}]},
        GOOD,
    ]
    status, out, err, _ = _run_against(tool, [answer(ops=[entry(index + 1, op) for index, op in enumerate(ops)]), answer(reset=False)])
    assert status == 0, err
    assert out.splitlines() == [
        "Refused operation 1 (unknown): invalid_op",
        "Refused operation 2 (pin): invalid_op",
        "Refused operation 3 (pin): invalid_argument",
        "Refused operation 4 (photo): invalid_op",
        "Refused operation 5 (pin): invalid_argument",
        "Refused operation 6 (note): invalid_argument",
        "Refused operation 7 (marker): invalid_argument",
        "Refused operation 8 (track): invalid_argument",
        "Refused operation 9 (pin): invalid_op",
        "Done with 11 operations, up to 11 (9 refused)",
    ]
    assert _requests(receiver) == [
        messages.TrackAppend("t", None, (TrackPoint(Coordinate(1, 2), 1700000000000),)),
        messages.PutPin("p", Coordinate(1, 2)),
    ]


# HTTPS and a gateway that keeps running


def _certificate(tmp_path: Path) -> Tuple[Path, Path]:
    """A certificate authority, and a certificate it signed for localhost and 127.0.0.1."""
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

    now = datetime.datetime.now(datetime.timezone.utc)
    authority_key = ec.generate_private_key(ec.SECP256R1())
    authority_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Test authority")])
    authority = (
        x509.CertificateBuilder()
        .subject_name(authority_name)
        .issuer_name(authority_name)
        .public_key(authority_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(hours=1))
        .not_valid_after(now + datetime.timedelta(days=1))
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .add_extension(x509.KeyUsage(False, False, False, False, False, True, True, False, False), critical=True)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(authority_key.public_key()), critical=False)
        .sign(authority_key, hashes.SHA256())
    )
    key = ec.generate_private_key(ec.SECP256R1())
    certificate = (
        x509.CertificateBuilder()
        .subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "localhost")]))
        .issuer_name(authority_name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(hours=1))
        .not_valid_after(now + datetime.timedelta(days=1))
        .add_extension(x509.SubjectAlternativeName([x509.DNSName("localhost"), x509.IPAddress(ipaddress.ip_address("127.0.0.1"))]), critical=False)
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.KeyUsage(True, False, False, False, False, False, False, False, False), critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .add_extension(x509.SubjectKeyIdentifier.from_public_key(key.public_key()), critical=False)
        .add_extension(x509.AuthorityKeyIdentifier.from_issuer_public_key(authority_key.public_key()), critical=False)
        .sign(authority_key, hashes.SHA256())
    )
    authority_file = tmp_path / "authority.pem"
    authority_file.write_bytes(authority.public_bytes(serialization.Encoding.PEM))
    chain_file = tmp_path / "server.pem"
    chain_file.write_bytes(
        key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
        + certificate.public_bytes(serialization.Encoding.PEM)
    )
    return authority_file, chain_file


def test_a_relay_behind_https(tool: Gateway, receiver: TestReceiver, tmp_path: Path, monkeypatch) -> None:
    authority, chain = _certificate(tmp_path)
    server_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server_context.load_cert_chain(chain)
    trusting = ssl.create_default_context(cafile=str(authority))
    server = RunningRelay(tls=server_context, client_tls=trusting)
    try:
        monkeypatch.setattr(gateway, "tls_context", lambda: ssl.create_default_context(cafile=str(authority)))
        tool.pair()
        server.post(pin("secure"))
        status, out, err = tool.run_once(f"https://localhost:{server.port}", server.gateway)
        assert status == 0, err
        assert out == "Done with 1 operation, up to 1\n"
        assert json.loads((tool.state / "relay-state.json").read_text())["relay"] == f"https://localhost:{server.port}"
        # Without the test authority the certificate is not trusted, and nothing is fetched.
        monkeypatch.setattr(gateway, "tls_context", ssl.create_default_context)
        server.post(pin("not-fetched"))
        untrusting = gateway.RelayClient(gateway.RelayAddress.parse(f"https://localhost:{server.port}"), server.gateway)
        with pytest.raises(gateway.RelayUnreachable):
            untrusting.fetch(None, 0, 0)
        assert server.held() == [2]
    finally:
        server.close()


def test_a_kept_alive_connection_the_relay_closed_is_opened_again_quietly(relay_server: RunningRelay) -> None:
    relay_server.server.read_seconds = 0.2
    client = gateway.RelayClient(gateway.RelayAddress.parse(relay_server.url), relay_server.gateway)
    try:
        assert client.fetch(None, 0, 0)[0] == 200
        time.sleep(0.6)
        assert client.fetch(None, 0, 0)[0] == 200
    finally:
        client.close()


class _TricklingRelay:
    """Answers each request with scripted bytes, written a few at a time with a pause after each piece."""

    def __init__(self, replies: Sequence[Tuple[bytes, bytes, int, float]], close_after_each: bool = False) -> None:
        self.replies = list(replies)
        self.close_after_each = close_after_each
        self.connections = 0
        self._stopped = False
        self._server = socket.create_server(("127.0.0.1", 0))
        self._server.settimeout(0.05)
        self.url = f"http://127.0.0.1:{self._server.getsockname()[1]}"
        self._accepting = threading.Thread(target=self._accept, daemon=True)
        self._accepting.start()

    def close(self) -> None:
        self._stopped = True
        self._accepting.join(timeout=5)
        self._server.close()

    def _accept(self) -> None:
        while not self._stopped:
            try:
                connection, _ = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            self.connections += 1
            threading.Thread(target=self._serve, args=(connection,), daemon=True).start()

    def _serve(self, connection: socket.socket) -> None:
        received = b""
        try:
            with connection:
                connection.settimeout(30)
                while self.replies:
                    while b"\r\n\r\n" not in received:
                        chunk = connection.recv(65536)
                        if not chunk:
                            return
                        received += chunk
                    received = received.split(b"\r\n\r\n", 1)[1]
                    head, reply, piece, pause = self.replies.pop(0)
                    connection.sendall(head)
                    for start in range(0, len(reply), piece):
                        connection.sendall(reply[start : start + piece])
                        time.sleep(pause)
                    if self.close_after_each:
                        return
        except OSError:
            return


def _head(body: bytes, length: bool = True) -> bytes:
    return b"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" + (b"Content-Length: %d\r\n" % len(body) if length else b"Connection: close\r\n") + b"\r\n"


@pytest.mark.parametrize("reply", ["body", "body without a length", "headers"])
def test_a_whole_answer_must_arrive_within_the_time_allowed(monkeypatch, reply: str) -> None:
    """A relay that sends a byte at a time, each well within the read timeout, is cut off all the same, whether in its
    headers or in its body."""
    monkeypatch.setattr(gateway, "ANSWER_SECONDS", 1.0)
    body = answer()[2]
    if reply == "headers":
        script = (b"", b"HTTP/1.1 200 OK\r\n" + b"X-Slow: 1\r\n" * 100, 1, 0.1)
    else:
        script = (_head(body, reply == "body"), body, 1, 0.1)
    trickling = _TricklingRelay([script])
    client = gateway.RelayClient(gateway.RelayAddress.parse(trickling.url), new_token())
    started = time.monotonic()
    try:
        with pytest.raises(gateway.RelayUnreachable) as raised:
            client.fetch(None, 0, 0)
    finally:
        client.close()
        trickling.close()
    assert raised.value.timed_out
    assert 1.0 <= time.monotonic() - started < 4.0


def test_an_answer_that_arrives_in_time_is_kept(monkeypatch) -> None:
    monkeypatch.setattr(gateway, "ANSWER_SECONDS", 10.0)
    body = answer(ops=[entry(1, GOOD)])[2]
    trickling = _TricklingRelay([(_head(body), body, 16, 0.05)])
    client = gateway.RelayClient(gateway.RelayAddress.parse(trickling.url), new_token())
    try:
        assert client.fetch(None, 0, 0) == (200, "application/json", body)
    finally:
        client.close()
        trickling.close()


def test_an_answer_cut_short_is_tried_again_and_does_not_break_the_api(tool: Gateway, receiver: TestReceiver) -> None:
    tool.pair()
    body = answer()[2]
    trickling = _TricklingRelay([(_head(body), body[:10], 10, 0.0), (_head(body), body, len(body), 0.0)], close_after_each=True)
    try:
        status, out, err = tool.run_once(trickling.url, new_token())
    finally:
        trickling.close()
    assert status == 0, err
    assert out.splitlines() == ["The relay could not be reached; trying again in 1 second."]


def test_an_answer_that_took_too_long_is_not_asked_for_again_at_once(monkeypatch) -> None:
    """Only a kept-alive connection that the relay closed is opened again without a word."""
    monkeypatch.setattr(gateway, "ANSWER_SECONDS", 1.0)
    body = answer()[2]
    trickling = _TricklingRelay([(_head(body), body, len(body), 0.0), (_head(body), body, 1, 0.1), (_head(body), body, len(body), 0.0)])
    client = gateway.RelayClient(gateway.RelayAddress.parse(trickling.url), new_token())
    try:
        assert client.fetch(None, 0, 0)[0] == 200
        started = time.monotonic()
        with pytest.raises(gateway.RelayUnreachable):
            client.fetch(None, 0, 0)
        assert time.monotonic() - started < 4.0
    finally:
        client.close()
        trickling.close()
    assert trickling.connections == 1


@pytest.mark.skipif(not POSIX, reason="signals")
def test_a_running_gateway_waits_on_the_relay_and_stops_on_sigterm(tool: Gateway, relay_server: RunningRelay, receiver: TestReceiver) -> None:
    tool.pair()
    environment = dict(os.environ, ERDIGO_RELAY_TOKEN=relay_server.gateway, PYTHONUNBUFFERED="1")
    process = subprocess.Popen(
        [sys.executable, str(EXAMPLES / "gateway.py"), "run", "--relay", relay_server.url, *tool.address()],
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )
    try:
        deadline = time.monotonic() + 30
        while relay_server.server._active == 0 and time.monotonic() < deadline:
            time.sleep(0.05)
        # The gateway is now waiting on the relay, so a new operation reaches the app at once.
        time.sleep(0.5)
        assert relay_server.logs == []
        posted = time.monotonic()
        relay_server.post(pin("live"))
        while not _requests(receiver) and time.monotonic() < deadline:
            time.sleep(0.05)
        assert _requests(receiver) == [messages.PutPin("live", SEOUL)]
        assert time.monotonic() - posted < 5
        # Its place is saved before it fetches again.
        while json.loads((tool.state / "relay-state.json").read_text())["after"] != 1 and time.monotonic() < deadline:
            time.sleep(0.05)
    finally:
        process.send_signal(signal.SIGTERM)
        out, err = process.communicate(timeout=30)
    assert process.returncode == 0, err
    assert out.splitlines() == ["Done with 1 operation, up to 1", "Stopped."]
    # The waiting fetch may answer before the POST that woke it is logged.
    assert sorted(relay_server.logs[:2]) == ["GET /v1/ops 200 ops=1", "POST /v1/ops 200 accepted=1"]
