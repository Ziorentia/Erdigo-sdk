"""Writing every item kind, ordering, reconnection, pacing and time limits, against the test receiver."""

from __future__ import annotations

import hashlib
import socket
import threading
import time

import pytest

from erdigo_sdk import Coordinate, ErdigoError, ErrorKind, ItemKind, MarkerColor, RetryPolicy, TrackPoint
from erdigo_sdk import messages
from erdigo_sdk.client import backoff
from erdigo_sdk.model import Limits

from .conftest import Paired
from .receiver import TestReceiver

PALACE = Coordinate(37.5796, 126.977)
PNG = b"\x89PNG\r\n\x1a\n" + bytes(100_000)


def _requests(receiver: TestReceiver) -> list:
    with receiver.lock:
        return [envelope.request for envelope in receiver.record.requests]


def test_every_item_kind_is_sent_as_the_protocol_says(paired: Paired) -> None:
    client = paired.client
    client.ping()
    client.put_pin("pin-1", PALACE, "Palace gate")
    client.put_marker("marker-1", PALACE, "Tower", MarkerColor.BLUE)
    client.put_note("note-1", PALACE, "First line\nSecond line")
    client.put_photo("photo-1", PALACE, PNG, "View")
    client.append_track("walk", [TrackPoint(PALACE, 1_700_000_000_000, 38.5), TrackPoint(Coordinate(37.58, 126.98))], "Morning walk")
    client.finish_track("walk")
    client.move("pin-1", Coordinate(37.58, 126.98))
    client.delete("marker-1")
    client.delete_all()
    sent = _requests(paired.receiver)
    assert paired.receiver.seqs() == list(range(1, len(sent) + 1))
    kinds = [type(request).__name__ for request in sent]
    assert kinds == [
        "Ping", "PutPin", "PutMarker", "PutNote", "PhotoBegin", "PhotoChunk", "PhotoChunk", "PhotoChunk", "PhotoEnd",
        "TrackAppend", "TrackFinish", "Move", "Delete", "DeleteAll",
    ]
    begin = sent[4].header
    assert begin.size == len(PNG) and begin.chunks == 3 and begin.sha256 == hashlib.sha256(PNG).hexdigest()
    assert b"".join(request.data for request in sent[5:8]) == PNG
    assert [request.index for request in sent[5:8]] == [0, 1, 2]
    assert sent[9].points[0] == TrackPoint(PALACE, 1_700_000_000_000, 38.5)
    assert sent[2].color is MarkerColor.BLUE
    # One connection for everything.
    assert paired.receiver.record.welcomes == 1


def test_long_tracks_go_in_batches_of_one_hundred_with_the_title_once(receiver: TestReceiver) -> None:
    receiver.limits = Limits(point_burst=1000, points_per_s=1000)
    device = Paired(receiver)
    points = [TrackPoint(Coordinate(1.0, index / 1000)) for index in range(250)]
    device.client.append_track("walk", points, "Walk")
    sent = _requests(receiver)
    assert [len(request.points) for request in sent] == [100, 100, 50]
    assert [request.title for request in sent] == ["Walk", None, None]
    device.close()


def test_a_kind_the_person_did_not_allow_is_refused_without_sending(receiver: TestReceiver) -> None:
    device = Paired(receiver, kinds=[ItemKind.PIN])
    with pytest.raises(ErdigoError) as raised:
        device.client.put_note("n", PALACE, "text")
    assert raised.value.kind is ErrorKind.NOT_PERMITTED and raised.value.item_kind is ItemKind.NOTE
    with pytest.raises(ErdigoError):
        device.client.put_photo("p", PALACE, PNG)
    assert _requests(receiver) == []
    device.close()


@pytest.mark.parametrize(
    "call",
    [
        lambda client: client.put_pin("", PALACE),
        lambda client: client.put_pin("a b", PALACE),
        lambda client: client.put_pin("x" * 65, PALACE),
        lambda client: client.put_pin("ok", PALACE, ""),
        lambda client: client.put_pin("ok", PALACE, "a‮b"),
        lambda client: client.put_note("ok", PALACE, ""),
        lambda client: client.put_note("ok", PALACE, "x" * 4097),
        lambda client: client.put_note("ok", PALACE, "bell\x07"),
        lambda client: client.put_photo("ok", PALACE, b"GIF89a"),
        lambda client: client.put_photo("ok", PALACE, b"\xff\xd8\xff" + bytes(1_048_576)),
        lambda client: client.append_track("ok", []),
        lambda client: client.put_pin("ok", (1.0, 2.0)),
    ],
)
def test_bad_requests_are_refused_before_anything_is_sent(paired: Paired, call) -> None:
    with pytest.raises(ErdigoError) as raised:
        call(paired.client)
    assert raised.value.kind is ErrorKind.INVALID_ARGUMENT
    assert paired.receiver.record.connections == 1  # Only the pairing.


def test_reconnects_and_resends_with_the_same_number_after_a_lost_answer(receiver: TestReceiver) -> None:
    device = Paired(receiver)
    device.client.ping()
    lost = {"done": False}

    def lose_first_answer(envelope):
        if envelope.seq == 2 and not lost["done"]:
            # Applied, and then the answer is lost, so the new connection's welcome says 2 was used.
            lost["done"] = True
            next(iter(receiver.devices.values())).last_seq = 2
            receiver.drop_connections()
            raise ConnectionError("dropped")
        return None

    receiver.answer = lose_first_answer
    device.client.put_pin("pin", PALACE)
    # Sent again with the same number, so the receiver repeats its answer instead of applying it twice.
    assert receiver.seqs() == [1, 2, 2]
    assert receiver.record.welcomes == 2
    device.close()


def _close_and_time_a_refusal(receiver: TestReceiver) -> float:
    """Closes the receiver, checks that its port now refuses connections, and returns how long a refusal takes.

    Windows takes about two seconds to refuse a connection to a closed port on this computer, because it tries again
    before giving up. Linux and macOS refuse at once."""
    receiver.close()
    started = time.monotonic()
    try:
        socket.create_connection(("127.0.0.1", receiver.port), timeout=10).close()
    except OSError:
        return time.monotonic() - started
    pytest.fail("the test receiver still accepts connections after it was closed")


def test_an_unreachable_receiver_is_retried_for_as_long_as_allowed_then_reported(receiver: TestReceiver) -> None:
    device = Paired(receiver, retry=RetryPolicy.up_to(1.5))
    refusal = _close_and_time_a_refusal(receiver)
    started = time.monotonic()
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    elapsed = time.monotonic() - started
    assert raised.value.kind is ErrorKind.NO_ANSWER
    # The 1.5 seconds count from the first failure, and a last attempt starts at their end at the latest. So it takes
    # at least that long, and at most an attempt, the time allowed and one more attempt, with a second to spare.
    assert 1.4 <= elapsed < refusal + 1.5 + refusal + 1.0


def test_a_request_with_a_timeout_stops_at_it(receiver: TestReceiver) -> None:
    device = Paired(receiver, retry=RetryPolicy.FOREVER)
    _close_and_time_a_refusal(receiver)
    started = time.monotonic()
    with pytest.raises(TimeoutError):
        device.client.ping(timeout=1.0)
    assert time.monotonic() - started < 3


def test_a_silent_receiver_is_not_waited_for_past_the_answer_time(receiver: TestReceiver) -> None:
    device = Paired(receiver, retry=RetryPolicy.NONE)
    device.client._ack_timeout = 0.5
    receiver.answer = lambda envelope: time.sleep(3)
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is ErrorKind.NO_ANSWER


def test_requests_from_several_threads_run_one_at_a_time_in_order(paired: Paired) -> None:
    errors = []

    def write(index: int) -> None:
        try:
            paired.client.put_pin(f"pin-{index}", PALACE)
        except Exception as error:  # pragma: no cover - reported below
            errors.append(error)

    threads = [threading.Thread(target=write, args=(index,)) for index in range(8)]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    assert errors == []
    assert paired.receiver.seqs() == list(range(1, 9))


def test_messages_are_paced_by_the_announced_rate(receiver: TestReceiver) -> None:
    receiver.limits = Limits(msgs_per_s=10, msg_burst=2)
    device = Paired(receiver)
    started = time.monotonic()
    for _ in range(5):
        device.client.ping()
    # Two at once, then three more at ten a second.
    assert time.monotonic() - started >= 0.25
    device.close()


def test_backoff_doubles_up_to_a_minute_with_jitter() -> None:
    for attempt, base in [(0, 1), (1, 2), (2, 4), (5, 32), (6, 60), (20, 60)]:
        for _ in range(20):
            assert base * 0.8 <= backoff(attempt) <= base * 1.2


def test_a_welcome_that_changes_the_last_number_continues_from_it(receiver: TestReceiver) -> None:
    device = Paired(receiver)
    next(iter(receiver.devices.values())).last_seq = 41
    device.client.ping()
    assert receiver.seqs() == [42]
    device.close()


def test_version_unsupported_is_reported(receiver: TestReceiver) -> None:
    receiver.hello_answer = messages.ReceiverError("version_unsupported", supported=(2,), fatal=True)
    device = Paired(receiver)
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is ErrorKind.VERSION_UNSUPPORTED and raised.value.supported == (2,)


def test_the_counter_is_written_before_it_is_sent_and_always_grows(paired: Paired) -> None:
    fingerprint = paired.pairing.fingerprint
    first = paired.store.next_connect_counter(fingerprint, 5)
    assert paired.store.next_connect_counter(fingerprint, 5) == first + 1
    paired.client.ping()
    device = next(iter(paired.receiver.devices.values()))
    assert device.counter > first + 1


def test_the_quickstart_example_pairs_and_writes(receiver: TestReceiver, tmp_path, monkeypatch) -> None:
    import importlib.util
    import sys

    from .support import PYTHON_ROOT

    specification = importlib.util.spec_from_file_location("quickstart", PYTHON_ROOT / "examples" / "quickstart.py")
    example = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(example)
    monkeypatch.setattr(example, "default_directory", lambda profile: str(tmp_path / profile))
    monkeypatch.setattr(example.getpass, "getpass", lambda prompt: receiver.ticket.uri)
    monkeypatch.setattr(sys, "argv", ["quickstart.py", "--host", "127.0.0.1", "--port", str(receiver.port)])
    assert example.main() == 0
    assert example.main() == 0  # The second run reuses the pairing.
    assert len(receiver.record.pair_requests) == 1
    assert [type(envelope.request).__name__ for envelope in receiver.record.requests] == ["PutPin", "TrackAppend", "TrackFinish"] * 2
