"""How the client takes error answers, including ones a real receiver would not give by itself.

The same cases as windows/tests/Erdigo.Tests/AnswerHandlingTests.cs. Above all: an answer that ends the connection is
decided on before any rate-limit retry, so a fatal ``rate_limited`` is never resent.
"""

from __future__ import annotations

import pytest

from erdigo_sdk import Coordinate, ErdigoError, ErrorKind, RetryPolicy
from erdigo_sdk.messages import ReceiverError

from .conftest import Paired
from .receiver import Script, TestReceiver


def _error(code: str, seq=None, **fields) -> ReceiverError:
    return ReceiverError(code, seq, fields.get("last_seq"), fields.get("retry_after_ms"), fields.get("supported"), fields.get("fatal", False))


def _scripted(receiver: TestReceiver, script: Script, retry: RetryPolicy = RetryPolicy.up_to(10)) -> Paired:
    # Retrying for a while means a wrongly classified answer would show up as a second connection.
    device = Paired(receiver, retry=retry)
    receiver.answer = script
    return device


def _connections(receiver: TestReceiver) -> int:
    return receiver.record.welcomes


# Answers that end the connection


def test_fatal_rate_limited_ends_the_connection_with_one_error(receiver: TestReceiver) -> None:
    script = Script(lambda request: _error("rate_limited", request.seq, retry_after_ms=50, fatal=True))
    device = _scripted(receiver, script)
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is ErrorKind.REJECTED
    assert raised.value.receiver_error.code == "rate_limited"
    assert raised.value.receiver_error.fatal
    # Sent once, never resent on the closed connection or on a new one.
    assert receiver.seqs() == [1]
    assert _connections(receiver) == 1
    assert device.client.limits is None


@pytest.mark.parametrize(
    "code, expected, number_for_another_request",
    [
        ("rate_limited", ErrorKind.REJECTED, False),
        ("rate_limited", ErrorKind.REJECTED, True),
        ("too_many_errors", ErrorKind.REJECTED, False),
        ("too_many_errors", ErrorKind.REJECTED, True),
        ("internal_error", ErrorKind.REJECTED, False),
        ("replaced", ErrorKind.REPLACED, False),
        ("replaced", ErrorKind.REPLACED, True),
        ("seq_conflict", ErrorKind.SEQUENCE_CONFLICT, False),
        ("stale_seq", ErrorKind.SEQUENCE_CONFLICT, True),
        ("future_code", ErrorKind.REJECTED, True),
    ],
)
def test_fatal_answers_end_the_connection_as_their_code_says(receiver: TestReceiver, code: str, expected: ErrorKind, number_for_another_request: bool) -> None:
    """A fatal answer is about the connection, so a number it carries for another request changes nothing."""
    script = Script(
        lambda request: _error(
            code,
            request.seq + 7 if number_for_another_request else request.seq,
            last_seq=1000 if number_for_another_request else None,
            retry_after_ms=50,
            fatal=True,
        )
    )
    device = _scripted(receiver, script)
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is expected
    assert receiver.seqs() == [1]
    assert _connections(receiver) == 1
    assert device.client.limits is None
    if expected is ErrorKind.SEQUENCE_CONFLICT:
        # The number in an answer to another request is not taken as where the receiver stands.
        assert raised.value.last_seq == 0


def test_paused_still_means_reconnect_and_resend_with_the_same_number(receiver: TestReceiver) -> None:
    device = _scripted(receiver, Script(lambda request: _error("paused", fatal=True)))
    device.client.ping()
    assert receiver.seqs() == [1, 1]
    assert _connections(receiver) == 2


def test_paused_without_retrying_reports_no_answer(receiver: TestReceiver) -> None:
    device = _scripted(receiver, Script(lambda request: _error("paused", fatal=True)), RetryPolicy.NONE)
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is ErrorKind.NO_ANSWER
    assert receiver.seqs() == [1]


# Answers that keep the connection


@pytest.mark.parametrize("answer_carries_the_number", [True, False])
def test_rate_limited_is_resent_with_the_same_number_on_the_same_connection(receiver: TestReceiver, answer_carries_the_number: bool) -> None:
    # A receiver leaves out the number when it limits a message before reading it.
    script = Script(lambda request: _error("rate_limited", request.seq if answer_carries_the_number else None, retry_after_ms=20), times=2)
    device = _scripted(receiver, script)
    device.client.ping()
    device.client.ping()
    assert receiver.seqs() == [1, 1, 1, 2]
    assert _connections(receiver) == 1


def test_receiver_that_never_stops_limiting_is_given_up_on(receiver: TestReceiver) -> None:
    script = Script(lambda request: _error("rate_limited", request.seq, retry_after_ms=1), times=10**9)
    device = _scripted(receiver, script)
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is ErrorKind.REJECTED and raised.value.receiver_error.code == "rate_limited"
    # The first answer and twenty more, all for the same number.
    assert receiver.seqs() == [1] * 21
    assert _connections(receiver) == 1


@pytest.mark.parametrize("code", ["not_found", "rate_limited", "stale_seq"])
def test_answer_for_another_number_is_a_protocol_violation_and_changes_no_numbers(receiver: TestReceiver, code: str) -> None:
    script = Script(lambda request: _error(code, request.seq + 5, last_seq=1000, retry_after_ms=20))
    device = _scripted(receiver, script)
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is ErrorKind.PROTOCOL_VIOLATION
    assert device.client.limits is None
    # The next request starts from what the receiver says on a new connection, not from the stray number.
    device.client.ping()
    assert receiver.seqs() == [1, 1]


def test_stale_number_for_this_request_moves_the_number_on(receiver: TestReceiver) -> None:
    device = _scripted(receiver, Script(lambda request: _error("stale_seq", request.seq, last_seq=10)))
    with pytest.raises(ErdigoError) as raised:
        device.client.ping()
    assert raised.value.kind is ErrorKind.SEQUENCE_CONFLICT
    assert raised.value.last_seq == 10
    device.client.ping()
    assert receiver.seqs() == [1, 11]
    assert _connections(receiver) == 1


@pytest.mark.parametrize("answer_carries_the_number", [True, False])
def test_rejection_keeps_the_connection_and_uses_up_the_number_only_when_it_says_which_request(
    receiver: TestReceiver, answer_carries_the_number: bool
) -> None:
    device = _scripted(receiver, Script(lambda request: _error("not_found", request.seq if answer_carries_the_number else None)))
    with pytest.raises(ErdigoError) as raised:
        device.client.move("missing", Coordinate(37.5796, 126.977))
    assert raised.value.kind is ErrorKind.REJECTED and raised.value.receiver_error.code == "not_found"
    assert device.client.limits is not None
    device.client.ping()
    assert receiver.seqs() == ([1, 2] if answer_carries_the_number else [1, 1])
    assert _connections(receiver) == 1
