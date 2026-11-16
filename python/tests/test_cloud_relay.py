"""The example cloud relay against docs/cloud-relay.md and every case in docs/cloud-relay-cases.json."""

from __future__ import annotations

import base64
import http.client
import json
import os
import re
import secrets
import signal
import socket
import subprocess
import sys
import threading
import time
from typing import Iterator, List, Optional, Tuple

import pytest

from .cloud_relay_support import CASES, EXAMPLES, FakeClock, RunningRelay, new_token, pin, raw_exchange, relay

POSIX = os.name == "posix"


@pytest.fixture
def running() -> Iterator[RunningRelay]:
    server = RunningRelay()
    yield server
    server.close()


def _nothing_taken(server: RunningRelay) -> None:
    assert server.held() == []


# The shared cases


@pytest.mark.parametrize("case", CASES["valid_ops"], ids=[case["name"] for case in CASES["valid_ops"]])
def test_valid_operations_are_taken_and_given_back_unchanged(running: RunningRelay, case: dict) -> None:
    reply = running.post(case["op"])
    assert reply.status == 200, reply.body
    assert reply.json == {"accepted": 1, "last_seq": 1}
    fetched = running.fetch().json["ops"]
    assert [entry["seq"] for entry in fetched] == [1]
    assert fetched[0]["op"] == case["op"]


@pytest.mark.parametrize("case", CASES["invalid_ops"], ids=[case["name"] for case in CASES["invalid_ops"]])
def test_invalid_operations_are_refused_and_nothing_is_taken(running: RunningRelay, case: dict) -> None:
    reply = running.post(case["op"])
    assert reply.status == 400
    assert reply.json["error"] == "invalid_op" and reply.json["index"] == 0
    assert isinstance(reply.json["reason"], str) and reply.json["reason"]
    _nothing_taken(running)


def _raw_body(case: dict) -> bytes:
    return base64.b64decode(case["body_base64"]) if "body_base64" in case else case["body"].encode("utf-8")


@pytest.mark.parametrize("case", CASES["raw_bodies"], ids=[case["name"] for case in CASES["raw_bodies"]])
def test_raw_bodies_are_refused_with_one_of_the_listed_errors(running: RunningRelay, case: dict) -> None:
    reply = running.post_body(_raw_body(case))
    assert reply.status == 400
    assert reply.json["error"] in case["errors"]
    _nothing_taken(running)


# Order of checks, methods and tokens


@pytest.mark.parametrize(
    "method, path, status, error",
    [
        ("GET", "/", 404, "not_found"),
        ("GET", "/v1", 404, "not_found"),
        ("GET", "/v1/ops/", 404, "not_found"),
        ("POST", "/v1/ops/extra", 404, "not_found"),
        ("GET", "/v2/ops", 404, "not_found"),
        ("PUT", "/v1/ops", 405, "method_not_allowed"),
        ("DELETE", "/v1/ops", 405, "method_not_allowed"),
        ("PATCH", "/v1/ops", 405, "method_not_allowed"),
        ("BREW", "/v1/ops", 405, "method_not_allowed"),
        ("POST", "/v1/health", 405, "method_not_allowed"),
        ("GET", "/v1/ops", 401, "unauthorized"),
        ("POST", "/v1/ops", 401, "unauthorized"),
    ],
)
def test_unknown_paths_then_wrong_methods_then_tokens(running: RunningRelay, method: str, path: str, status: int, error: str) -> None:
    # No token at all, so a 404 or 405 shows those checks come first.
    reply = running.request(method, path, b"{}", {"Content-Type": "application/json"})
    assert reply.status == status
    assert reply.json == {"error": error}
    assert reply.headers["content-type"] == "application/json"


@pytest.mark.parametrize(
    "request_bytes, status",
    [
        (b"GET /v1/health HTTP/1.1\r\nHost: x\r\nX-Long: " + b"a" * 70000 + b"\r\n\r\n", 431),
        (b"GET /v1/health HTTP/1.1\r\n" + b"".join(b"X-%d: 1\r\n" % index for index in range(120)) + b"\r\n", 431),
        (b"GET /" + b"a" * 70000 + b" HTTP/1.1\r\nHost: x\r\n\r\n", 414),
    ],
)
def test_requests_too_large_or_malformed_to_read_get_json_errors(running: RunningRelay, request_bytes: bytes, status: int) -> None:
    answer = raw_exchange(running.port, request_bytes)
    head, _, body = answer.partition(b"\r\n\r\n")
    assert head.startswith(b"HTTP/1.1 %d " % status) or head.startswith(b"HTTP/1.0 %d " % status), head
    assert b"content-type: application/json" in head.lower()
    assert json.loads(body)["error"] in ("too_large", "bad_request")


def test_a_request_line_that_is_not_http_gets_a_json_error(running: RunningRelay) -> None:
    # The standard library answers it as HTTP/0.9 would, with the body alone.
    assert raw_exchange(running.port, b"NOT HTTP AT ALL\r\n\r\n").endswith(b'{"error":"bad_request"}')


def test_head_gets_405_without_a_body(running: RunningRelay) -> None:
    reply = running.request("HEAD", "/v1/health")
    assert reply.status == 405 and reply.body == b""


def test_health_needs_no_token(running: RunningRelay) -> None:
    reply = running.request("GET", "/v1/health")
    assert reply.status == 200 and reply.json == {"ok": True}
    assert reply.headers["content-type"] == "application/json"


@pytest.mark.parametrize("header", [None, "Bearer", "Bearer ", "Basic {source}", "Bearer x{source}", "Bearer {gateway}", "Token {source}"])
def test_sending_needs_a_source_token(running: RunningRelay, header) -> None:
    headers = {"Content-Type": "application/json"}
    if header is not None:
        headers["Authorization"] = header.format(source=running.source, gateway=running.gateway)
    reply = running.request("POST", "/v1/ops", json.dumps({"ops": [pin()]}).encode(), headers)
    assert reply.status == 401 and reply.json == {"error": "unauthorized"}
    _nothing_taken(running)


@pytest.mark.parametrize("header", [None, "Bearer {source}", "Basic {gateway}", "Bearer {gateway}x"])
def test_fetching_needs_the_gateway_token(running: RunningRelay, header) -> None:
    assert running.post(pin()).status == 200
    headers = {} if header is None else {"Authorization": header.format(source=running.source, gateway=running.gateway)}
    reply = running.request("GET", "/v1/ops", headers=headers)
    assert reply.status == 401 and reply.json == {"error": "unauthorized"}
    assert running.held() == [1]


def test_the_scheme_is_case_insensitive_and_any_source_token_works() -> None:
    first, second = new_token(), new_token()
    server = RunningRelay(source=first)
    server.server.tokens = relay.Tokens([first, second], server.gateway)
    try:
        for token in (first, second):
            reply = server.request("POST", "/v1/ops", json.dumps({"ops": [pin()]}).encode(), {"Content-Type": "application/json", "Authorization": f"bearer {token}"})
            assert reply.status == 200
    finally:
        server.close()


def test_two_authorization_headers_are_refused(running: RunningRelay) -> None:
    request = (
        f"GET /v1/ops HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {running.gateway}\r\nAuthorization: Bearer {running.gateway}\r\n"
        "Connection: close\r\n\r\n"
    ).encode()
    assert b" 401 " in raw_exchange(running.port, request).split(b"\r\n", 1)[0]


# Content type and size


@pytest.mark.parametrize("content_type", [None, "text/plain", "application/x-www-form-urlencoded", "application/json; charset=latin-1", "application/jsonp"])
def test_bodies_that_are_not_json_get_415(running: RunningRelay, content_type) -> None:
    reply = running.post_body(json.dumps({"ops": [pin()]}).encode(), content_type)
    assert reply.status == 415 and reply.json == {"error": "unsupported_media_type"}
    _nothing_taken(running)


@pytest.mark.parametrize("content_type", ["application/json", "application/json; charset=utf-8", "Application/JSON; Charset=UTF-8", 'application/json;charset="utf-8"'])
def test_json_with_or_without_a_utf8_charset_is_accepted(running: RunningRelay, content_type: str) -> None:
    assert running.post_body(json.dumps({"ops": [pin()]}).encode(), content_type).status == 200


def test_the_token_is_checked_before_the_content_type(running: RunningRelay) -> None:
    assert running.post_body(b"x", "text/plain", auth="wrong").status == 401


def test_a_body_announced_as_too_large_is_refused_without_being_read(running: RunningRelay) -> None:
    request = (
        f"POST /v1/ops HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {running.source}\r\nContent-Type: application/json\r\n"
        f"Content-Length: {2 * 1024 * 1024 + 1}\r\n\r\n"
    ).encode()
    answer = raw_exchange(running.port, request)
    status, _, rest = answer.partition(b"\r\n")
    assert b" 413 " in status
    assert b"connection: close" in rest.lower()
    assert answer.endswith(b'{"error":"too_large"}')


def test_a_body_of_exactly_two_mebibytes_is_read(running: RunningRelay) -> None:
    body = json.dumps({"ops": [pin()]}).encode()
    body += b" " * (2 * 1024 * 1024 - len(body))
    assert running.post_body(body).status == 200
    assert running.held() == [1]


@pytest.mark.parametrize("framing", ["Transfer-Encoding: chunked\r\n", "", "Content-Length: 12abc\r\n", "Content-Length: 5\r\nContent-Length: 5\r\n"])
def test_a_body_without_one_plain_length_gets_411(running: RunningRelay, framing: str) -> None:
    request = (
        f"POST /v1/ops HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {running.source}\r\nContent-Type: application/json\r\n{framing}\r\n"
    ).encode()
    answer = raw_exchange(running.port, request)
    assert b" 411 " in answer.split(b"\r\n", 1)[0]
    assert answer.endswith(b'{"error":"length_required"}')
    _nothing_taken(running)


def test_continue_is_sent_only_once_the_headers_pass(running: RunningRelay) -> None:
    body = json.dumps({"ops": [pin()]}).encode()
    head = (
        f"POST /v1/ops HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {running.source}\r\nContent-Type: application/json\r\n"
        f"Content-Length: {len(body)}\r\nExpect: 100-continue\r\nConnection: close\r\n\r\n"
    ).encode()
    with socket.create_connection(("127.0.0.1", running.port), timeout=10) as connection:
        connection.sendall(head)
        assert connection.recv(1024).startswith(b"HTTP/1.1 100 Continue\r\n\r\n")
        connection.sendall(body)
        answer = b""
        while not answer.endswith(b"}"):
            answer += connection.recv(1024)
    assert b" 200 " in answer.split(b"\r\n", 1)[0]
    too_large = head.replace(f"Content-Length: {len(body)}".encode(), b"Content-Length: 3000000")
    answer = raw_exchange(running.port, too_large)
    assert answer.startswith(b"HTTP/1.1 413 ")


def _post_whole_body(port: int, body: bytes, headers: dict, connection: Optional[http.client.HTTPConnection] = None) -> Tuple[int, bytes, bool]:
    """Posts as most HTTP clients do: the whole body, without waiting for 100 Continue, and only then the answer."""
    own = connection is None
    connection = connection or http.client.HTTPConnection("127.0.0.1", port, timeout=30)
    try:
        connection.request("POST", "/v1/ops", body, headers)
        response = connection.getresponse()
        return response.status, response.read(), response.will_close
    finally:
        if own:
            connection.close()


def test_a_body_refused_for_its_token_or_type_is_read_first_so_the_answer_arrives(running: RunningRelay) -> None:
    """Closing on an unread body would reset the connection, and a client still sending would see that, not the answer."""
    body = json.dumps({"ops": [_photo(1_000_000)]}).encode()
    connection = http.client.HTTPConnection("127.0.0.1", running.port, timeout=30)
    try:
        for _ in range(5):
            refused = _post_whole_body(running.port, body, {"Content-Type": "application/json", "Authorization": f"Bearer {new_token()}"}, connection)
            assert refused == (401, b'{"error":"unauthorized"}', False)
            refused = _post_whole_body(running.port, body, {"Content-Type": "text/plain", "Authorization": f"Bearer {running.source}"}, connection)
            assert refused == (415, b'{"error":"unsupported_media_type"}', False)
        # The same connection goes on.
        status, answer, _ = _post_whole_body(running.port, body, {"Content-Type": "application/json", "Authorization": f"Bearer {running.source}"}, connection)
        assert status == 200 and json.loads(answer) == {"accepted": 1, "last_seq": 1}
    finally:
        connection.close()


def test_a_body_refused_as_too_large_gets_its_answer_before_the_connection_closes(running: RunningRelay) -> None:
    body = b" " * (3 * 1024 * 1024)
    for _ in range(5):
        status, answer, closes = _post_whole_body(running.port, body, {"Content-Type": "application/json", "Authorization": f"Bearer {running.source}"})
        assert (status, answer, closes) == (413, b'{"error":"too_large"}', True)
    _nothing_taken(running)


def _linger_against(running: RunningRelay, piece: bytes, pause: float) -> float:
    """Gets a 413 for a body announced as too large, then keeps sending pieces of it without closing. Returns how long
    the relay went on reading after its answer."""
    head = (
        f"POST /v1/ops HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {running.source}\r\nContent-Type: application/json\r\n"
        "Content-Length: 100000000\r\n\r\n"
    ).encode()
    stop = threading.Event()
    with socket.create_connection(("127.0.0.1", running.port), timeout=10) as connection:

        def keep_sending() -> None:
            try:
                while not stop.wait(pause):
                    connection.sendall(piece)
            except OSError:
                pass

        connection.sendall(head)
        answer = b""
        while not answer.endswith(b'{"error":"too_large"}'):
            chunk = connection.recv(65536)
            assert chunk, answer
            answer += chunk
        assert answer.startswith(b"HTTP/1.1 413 ")
        answered = time.monotonic()
        sender = threading.Thread(target=keep_sending, daemon=True)
        sender.start()
        try:
            while running.server._active and time.monotonic() - answered < 8:
                time.sleep(0.02)
            assert running.server._active == 0
            return time.monotonic() - answered
        finally:
            stop.set()
            sender.join(timeout=10)


def test_lingering_after_a_refusal_ends_after_a_while(running: RunningRelay) -> None:
    # A byte at a time, each well within the read timeout, so only the time allowed for lingering ends it.
    running.server.linger_seconds = 0.5
    assert _linger_against(running, b" ", 0.1) < 4.0


def test_lingering_after_a_refusal_reads_only_so_much(running: RunningRelay) -> None:
    running.server.linger_bytes = 64 * 1024
    assert _linger_against(running, b" " * 65536, 0.0) < 4.0


# Batches and limits


def test_one_hundred_operations_in_one_post(running: RunningRelay) -> None:
    reply = running.post(*[pin(f"p{index}") for index in range(100)])
    assert reply.status == 200 and reply.json == {"accepted": 100, "last_seq": 100}
    assert running.post(*[pin(f"p{index}") for index in range(101)]).json["error"] == "invalid_body"


def _photo(size: int) -> dict:
    data = b"\x89PNG\r\n\x1a\n" + bytes(size - 8)
    return {"op": "photo", "id": "big", "lat": 0, "lon": 0, "type": "image/png", "data": base64.b64encode(data).decode("ascii")}


def test_a_photo_of_exactly_one_mebibyte_is_taken_and_one_byte_more_is_not(running: RunningRelay) -> None:
    assert running.post(_photo(1048576)).status == 200
    reply = running.post(_photo(1048577))
    assert reply.status == 400 and reply.json["error"] == "invalid_op" and reply.json["index"] == 0
    assert running.held() == [1]


def test_a_batch_is_taken_whole_or_not_at_all(running: RunningRelay) -> None:
    reply = running.post(pin("a"), pin("b"), pin("c", lat=91))
    assert reply.status == 400 and reply.json["error"] == "invalid_op" and reply.json["index"] == 2
    _nothing_taken(running)
    assert running.post(pin("a"), pin("b")).json == {"accepted": 2, "last_seq": 2}


def test_queue_full_when_too_many_operations_wait() -> None:
    server = RunningRelay(limits=relay.Limits(queue_ops=3))
    try:
        assert server.post(pin("a"), pin("b")).status == 200
        reply = server.post(pin("c"), pin("d"))
        assert reply.status == 503 and reply.json == {"error": "queue_full"}
        assert reply.headers["retry-after"] == "30"
        assert server.held() == [1, 2]
        assert server.post(pin("c")).json == {"accepted": 1, "last_seq": 3}
        assert server.post(pin("d")).status == 503
        # Once the gateway is done with some, there is room again.
        epoch = server.fetch().json["epoch"]
        assert server.fetch(epoch=epoch, after=2).status == 200
        assert server.post(pin("d"), pin("e")).json == {"accepted": 2, "last_seq": 5}
    finally:
        server.close()


def test_queue_full_when_the_waiting_operations_are_too_large() -> None:
    size = len(relay.encode_op(pin("a")))
    server = RunningRelay(limits=relay.Limits(queue_bytes=size * 2))
    try:
        assert server.post(pin("a")).status == 200
        assert server.post(pin("b"), pin("c")).status == 503
        assert server.post(pin("b")).status == 200
        assert server.post(pin("c")).status == 503
        assert server.held() == [1, 2]
    finally:
        server.close()


# Fetching


def test_the_first_fetch_resets_and_names_the_epoch(running: RunningRelay) -> None:
    running.post(pin("a"), pin("b"))
    answer = running.fetch().json
    assert set(answer) == {"epoch", "reset", "missed", "ops"}
    assert re.fullmatch(r"[A-Za-z0-9_-]{16,64}", answer["epoch"])
    assert answer["reset"] is True and answer["missed"] is False
    assert [entry["seq"] for entry in answer["ops"]] == [1, 2]
    assert all(isinstance(entry["received_ms"], int) and entry["received_ms"] > 1_600_000_000_000 for entry in answer["ops"])
    assert answer["ops"][1]["op"] == pin("b")


def test_a_matching_epoch_forgets_up_to_after(running: RunningRelay) -> None:
    running.post(pin("a"), pin("b"), pin("c"))
    epoch = running.fetch().json["epoch"]
    answer = running.fetch(epoch=epoch, after=2).json
    assert answer["reset"] is False and answer["epoch"] == epoch
    assert [entry["seq"] for entry in answer["ops"]] == [3]
    assert running.held() == [3]
    # Fetching the same place again changes nothing more.
    assert [entry["seq"] for entry in running.fetch(epoch=epoch, after=2).json["ops"]] == [3]
    assert running.fetch(epoch=epoch, after=3).json["ops"] == []
    assert running.held() == []
    assert running.post(pin("d")).json["last_seq"] == 4


def test_another_epoch_forgets_nothing(running: RunningRelay) -> None:
    running.post(pin("a"), pin("b"))
    answer = running.fetch(epoch="A" * 22, after=2).json
    assert answer["reset"] is True and answer["epoch"] != "A" * 22
    assert [entry["seq"] for entry in answer["ops"]] == [1, 2]
    # 'after' means nothing with another epoch, even past the last number.
    assert running.fetch(epoch="B" * 16, after=99).json["reset"] is True
    assert running.held() == [1, 2]


def test_after_past_the_last_sequence_number_is_a_bad_parameter(running: RunningRelay) -> None:
    running.post(pin("a"))
    epoch = running.fetch().json["epoch"]
    reply = running.fetch(epoch=epoch, after=2)
    assert reply.status == 400 and reply.json == {"error": "invalid_query"}
    assert running.held() == [1]


@pytest.mark.parametrize(
    "query",
    [
        "after=1&after=1", "limit=0", "limit=101", "wait=31", "after=-1", "after=1.5", "after=", "after=+1", "limit=1e2",
        "after=9007199254740992", "epoch=short", "epoch=" + "a" * 65, "epoch=" + "a" * 15 + ".", "epoch=" + "a" * 20 + "%20",
        "since=1", "after=1&", "x", "epoch=" + "a" * 20 + "&epoch=" + "a" * 20,
    ],
)
def test_bad_query_parameters(running: RunningRelay, query: str) -> None:
    reply = running.request("GET", f"/v1/ops?{query}", auth="gateway")
    assert reply.status == 400 and reply.json == {"error": "invalid_query"}


def test_the_largest_parameters_are_allowed(running: RunningRelay) -> None:
    reply = running.fetch(epoch="a" * 64, after=9007199254740991, limit=100, wait=0)
    assert reply.status == 200 and reply.json["reset"] is True


def test_paging_by_limit(running: RunningRelay) -> None:
    running.post(*[pin(f"p{index}") for index in range(5)])
    first = running.fetch(limit=2).json
    epoch = first["epoch"]
    assert [entry["seq"] for entry in first["ops"]] == [1, 2]
    assert [entry["seq"] for entry in running.fetch(epoch=epoch, after=2, limit=2).json["ops"]] == [3, 4]
    assert [entry["seq"] for entry in running.fetch(epoch=epoch, after=4, limit=2).json["ops"]] == [5]


def test_an_answer_stops_before_four_mebibytes(running: RunningRelay) -> None:
    # Three of the largest photos make more than 4 MiB of answer.
    for _ in range(3):
        assert running.post(_photo(1048576)).status == 200
    reply = running.fetch()
    assert len(reply.body) <= 4 * 1024 * 1024
    assert [entry["seq"] for entry in reply.json["ops"]] == [1, 2]
    epoch = reply.json["epoch"]
    assert [entry["seq"] for entry in running.fetch(epoch=epoch, after=2).json["ops"]] == [3]


def test_an_answer_holds_one_operation_even_if_it_alone_is_over_the_limit() -> None:
    server = RunningRelay(limits=relay.Limits(page_bytes=100))
    try:
        server.post(pin("a", title="x" * 150), pin("b"))
        answer = server.fetch().json
        assert [entry["seq"] for entry in answer["ops"]] == [1]
    finally:
        server.close()


def test_expired_operations_are_dropped_and_reported_as_missed() -> None:
    clock = FakeClock()
    server = RunningRelay(clock=clock, keep_seconds=3600)
    try:
        server.post(pin("a"))
        clock.advance(1800)
        server.post(pin("b"))
        clock.advance(1801)
        answer = server.fetch().json
        assert answer["missed"] is True and [entry["seq"] for entry in answer["ops"]] == [2]
        epoch = answer["epoch"]
        # Once past the dropped one, nothing is missed.
        assert server.fetch(epoch=epoch, after=1).json["missed"] is False
        clock.advance(1800)
        answer = server.fetch(epoch=epoch, after=1).json
        assert answer["missed"] is True and answer["ops"] == []
        # Expired operations also free their room in the queue.
        assert server.post(pin("c")).json["last_seq"] == 3
    finally:
        server.close()


def test_operations_the_gateway_finished_do_not_count_as_missed() -> None:
    clock = FakeClock()
    server = RunningRelay(clock=clock, keep_seconds=3600)
    try:
        server.post(pin("a"), pin("b"))
        epoch = server.fetch().json["epoch"]
        assert server.fetch(epoch=epoch, after=2).json["ops"] == []
        clock.advance(7200)
        server.post(pin("c"))
        answer = server.fetch(epoch=epoch, after=2).json
        assert answer["missed"] is False and [entry["seq"] for entry in answer["ops"]] == [3]
    finally:
        server.close()


def test_received_ms_is_when_the_relay_took_the_operation() -> None:
    clock = FakeClock()
    server = RunningRelay(clock=clock)
    try:
        server.post(pin("a"))
        clock.advance(2.5)
        server.post(pin("b"))
        times = [entry["received_ms"] for entry in server.fetch().json["ops"]]
        assert times[1] - times[0] == 2500
    finally:
        server.close()


# Waiting


def test_a_waiting_fetch_answers_as_soon_as_something_arrives(running: RunningRelay) -> None:
    results: List[object] = []
    started = time.monotonic()
    waiting = threading.Thread(target=lambda: results.append(running.fetch(wait=20)))
    waiting.start()
    time.sleep(0.5)
    # Others are served while it waits.
    assert running.request("GET", "/v1/health").status == 200
    assert running.post(pin("a")).status == 200
    waiting.join(timeout=10)
    assert results and [entry["seq"] for entry in results[0].json["ops"]] == [1]  # type: ignore[attr-defined]
    assert time.monotonic() - started < 10


def test_a_waiting_fetch_with_nothing_to_give_answers_after_the_wait(running: RunningRelay) -> None:
    started = time.monotonic()
    answer = running.fetch(wait=1).json
    elapsed = time.monotonic() - started
    assert answer["ops"] == [] and 0.9 <= elapsed < 5


def test_a_fetch_without_wait_answers_at_once(running: RunningRelay) -> None:
    started = time.monotonic()
    assert running.fetch().json["ops"] == []
    assert time.monotonic() - started < 1


def test_stopping_answers_waiting_fetches() -> None:
    server = RunningRelay()
    results: List[object] = []
    waiting = threading.Thread(target=lambda: results.append(server.fetch(wait=30)))
    waiting.start()
    time.sleep(0.3)
    started = time.monotonic()
    server.close()
    waiting.join(timeout=10)
    assert time.monotonic() - started < 5


# Connections


def test_requests_share_a_kept_alive_connection(running: RunningRelay) -> None:
    import http.client

    connection = http.client.HTTPConnection("127.0.0.1", running.port, timeout=10)
    try:
        for index in range(3):
            connection.request("POST", "/v1/ops", json.dumps({"ops": [pin(f"p{index}")]}), {"Content-Type": "application/json", "Authorization": f"Bearer {running.source}"})
            response = connection.getresponse()
            assert response.status == 200 and json.loads(response.read())["last_seq"] == index + 1
    finally:
        connection.close()


def test_a_client_that_stops_sending_is_cut_off(running: RunningRelay) -> None:
    running.server.read_seconds = 0.5
    with socket.create_connection(("127.0.0.1", running.port), timeout=10) as connection:
        connection.sendall(f"POST /v1/ops HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {running.source}\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{{".encode())
        started = time.monotonic()
        try:
            data = connection.recv(1024)
        except OSError:
            data = b""
        assert data == b"" and time.monotonic() - started < 5
    _nothing_taken(running)


def test_a_client_that_sends_headers_slowly_is_cut_off(running: RunningRelay) -> None:
    running.server.read_seconds = 0.5
    with socket.create_connection(("127.0.0.1", running.port), timeout=10) as connection:
        started = time.monotonic()
        connection.sendall(b"GET /v1/health HTTP/1.1\r\n")
        try:
            for _ in range(20):
                time.sleep(0.2)
                connection.sendall(b"X-Slow: 1\r\n")
        except OSError:
            pass
        assert time.monotonic() - started < 3


def test_one_log_line_per_request_without_tokens_or_contents(running: RunningRelay) -> None:
    secret_title = "Secret title 12345"
    running.post(pin("hidden-id", title=secret_title))
    running.post_body(b"{", auth="source")
    running.fetch()
    running.request("GET", f"/v1/ops?token={running.gateway}", auth="gateway")
    running.request("GET", f"/elsewhere/{running.source}")
    running.request("GET", "/v1/health")
    # A line is written once its answer has gone out, so the last one may come a moment after the answer.
    deadline = time.monotonic() + 5
    while len(running.logs) < 6 and time.monotonic() < deadline:
        time.sleep(0.01)
    assert len(running.logs) == 6
    for line in running.logs:
        for secret in (running.source, running.gateway, secret_title, "hidden-id", "37.5663"):
            assert secret not in line
    assert running.logs[0] == "POST /v1/ops 200 accepted=1"
    assert running.logs[2] == "GET /v1/ops 200 ops=1"
    assert running.logs[4] == "GET - 404"


# Checking the command line and the environment


def test_tokens_are_checked_at_start_up() -> None:
    good, other = new_token(), new_token()
    accepted = relay.Tokens.from_environment({"ERDIGO_RELAY_SOURCE_TOKENS": f" {good} , {other},", "ERDIGO_RELAY_GATEWAY_TOKEN": new_token()})
    assert accepted.role(good) == "source" and accepted.role(other) == "source"
    for environment in [
        {},
        {"ERDIGO_RELAY_GATEWAY_TOKEN": good},
        {"ERDIGO_RELAY_SOURCE_TOKENS": ",", "ERDIGO_RELAY_GATEWAY_TOKEN": good},
        {"ERDIGO_RELAY_SOURCE_TOKENS": good},
        {"ERDIGO_RELAY_SOURCE_TOKENS": "a" * 31, "ERDIGO_RELAY_GATEWAY_TOKEN": good},
        {"ERDIGO_RELAY_SOURCE_TOKENS": "a" * 257, "ERDIGO_RELAY_GATEWAY_TOKEN": good},
        {"ERDIGO_RELAY_SOURCE_TOKENS": "a" * 31 + "+", "ERDIGO_RELAY_GATEWAY_TOKEN": good},
        {"ERDIGO_RELAY_SOURCE_TOKENS": f"{other},{'b' * 31}\u00e9", "ERDIGO_RELAY_GATEWAY_TOKEN": good},
        {"ERDIGO_RELAY_SOURCE_TOKENS": other, "ERDIGO_RELAY_GATEWAY_TOKEN": "c" * 20},
        {"ERDIGO_RELAY_SOURCE_TOKENS": f"{other},{good}", "ERDIGO_RELAY_GATEWAY_TOKEN": good},
    ]:
        with pytest.raises(relay.UsageError) as raised:
            relay.Tokens.from_environment(environment)
        assert good not in str(raised.value) and other not in str(raised.value)
    alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~"
    shortest = "".join(secrets.choice(alphabet) for _ in range(32))
    longest = "-_.~" + "".join(secrets.choice(alphabet) for _ in range(252))
    edges = relay.Tokens.from_environment({"ERDIGO_RELAY_SOURCE_TOKENS": shortest, "ERDIGO_RELAY_GATEWAY_TOKEN": longest})
    assert edges.role(shortest) == "source" and edges.role(longest) == "gateway"


@pytest.mark.parametrize(
    "text, behind_proxy, expected",
    [
        ("127.0.0.1:0", False, ("127.0.0.1", 0, socket.AF_INET)),
        ("localhost:8080", False, ("127.0.0.1", 8080, socket.AF_INET)),
        ("[::1]:0", False, ("::1", 0, socket.AF_INET6)),
        ("127.0.0.2:1", False, ("127.0.0.2", 1, socket.AF_INET)),
        ("0.0.0.0:8080", True, ("0.0.0.0", 8080, socket.AF_INET)),
        ("[::]:8080", True, ("::", 8080, socket.AF_INET6)),
    ],
)
def test_listen_addresses_that_are_accepted(text: str, behind_proxy: bool, expected: tuple) -> None:
    assert relay.parse_listen(text, behind_proxy) == expected


@pytest.mark.parametrize("text", ["0.0.0.0:8080", "192.0.2.1:8080", "[::]:8080", "example.com:8080", "127.0.0.1", "127.0.0.1:", "127.0.0.1:65536", "::1:80", "[::1]", "[::1]80", ":80"])
def test_listen_addresses_that_are_refused(text: str) -> None:
    with pytest.raises(relay.UsageError):
        relay.parse_listen(text, False)


@pytest.mark.parametrize(
    "arguments",
    [["--keep-hours", "0"], ["--keep-hours", "169"], ["--keep-hours", "1.5"], ["--listen"], ["--listen=127.0.0.1:0"], ["--port", "1"],
     ["--listen", "127.0.0.1:0", "--listen", "127.0.0.1:0"], ["--listen", "0.0.0.0:0"]],
)
def test_command_line_mistakes(arguments: List[str]) -> None:
    with pytest.raises(relay.UsageError):
        relay.parse_arguments(arguments)


def test_command_line_defaults() -> None:
    assert relay.parse_arguments([]) == relay.Settings("127.0.0.1", 8080, socket.AF_INET, 24)
    assert relay.parse_arguments(["--keep-hours", "168", "--behind-proxy", "--listen", "0.0.0.0:0"]).keep_hours == 168


# The program itself


def _environment(source: str, gateway: str) -> dict:
    environment = dict(os.environ)
    environment.update({"ERDIGO_RELAY_SOURCE_TOKENS": source, "ERDIGO_RELAY_GATEWAY_TOKEN": gateway, "PYTHONUNBUFFERED": "1"})
    return environment


def _start(arguments: List[str], environment: dict) -> subprocess.Popen:
    return subprocess.Popen(
        [sys.executable, str(EXAMPLES / "relay.py"), *arguments],
        env=environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
    )


def _has_ipv6_loopback() -> bool:
    try:
        with socket.socket(socket.AF_INET6, socket.SOCK_STREAM) as probe:
            probe.bind(("::1", 0))
        return True
    except OSError:
        return False


@pytest.mark.parametrize("listen, pattern", [("127.0.0.1:0", r"http://127\.0\.0\.1:(\d+)"), ("[::1]:0", r"http://\[::1\]:(\d+)")])
def test_the_program_says_where_it_listens_serves_and_stops(listen: str, pattern: str) -> None:
    if listen.startswith("[") and not _has_ipv6_loopback():
        pytest.skip("this computer has no IPv6 loopback")
    source, gateway = new_token(), new_token()
    process = _start(["--listen", listen], _environment(source, gateway))
    try:
        line = process.stdout.readline()  # type: ignore[union-attr]
        found = re.fullmatch(f"Relay listening on {pattern}\n", line)
        assert found, line
        port = int(found.group(1))
        assert port != 0
        host = "::1" if listen.startswith("[") else "127.0.0.1"
        with socket.create_connection((host, port), timeout=10) as connection:
            connection.sendall(b"GET /v1/health HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n")
            answer = b""
            while not answer.endswith(b"}"):
                chunk = connection.recv(4096)
                assert chunk
                answer += chunk
            assert answer.startswith(b"HTTP/1.1 200 ") and answer.endswith(b'\r\n\r\n{"ok":true}')
    finally:
        if POSIX:
            process.send_signal(signal.SIGTERM)
        else:
            process.terminate()
        _, err = process.communicate(timeout=20)
    if POSIX:
        assert process.returncode == 0
        assert "Relay stopped." in err
    assert source not in err and gateway not in err


@pytest.mark.skipif(not POSIX, reason="signals")
def test_ctrl_c_stops_it_cleanly() -> None:
    process = _start(["--listen", "127.0.0.1:0"], _environment(new_token(), new_token()))
    assert process.stdout.readline().startswith("Relay listening on http://127.0.0.1:")  # type: ignore[union-attr]
    process.send_signal(signal.SIGINT)
    process.communicate(timeout=20)
    assert process.returncode == 0


@pytest.mark.parametrize(
    "arguments, source, gateway, message",
    [
        (["--listen", "127.0.0.1:0"], "", "{gateway}", "ERDIGO_RELAY_SOURCE_TOKENS"),
        (["--listen", "127.0.0.1:0"], "short-token", "{gateway}", "is not valid"),
        (["--listen", "127.0.0.1:0"], "{source}", "", "ERDIGO_RELAY_GATEWAY_TOKEN"),
        (["--listen", "127.0.0.1:0"], "{source}", "{source}", "must differ"),
        (["--listen", "0.0.0.0:0"], "{source}", "{gateway}", "--behind-proxy"),
        (["--keep-hours", "0", "--listen", "127.0.0.1:0"], "{source}", "{gateway}", "--keep-hours"),
    ],
)
def test_the_program_refuses_to_start_with_exit_code_1(arguments: List[str], source: str, gateway: str, message: str) -> None:
    tokens = {"source": new_token(), "gateway": new_token()}
    environment = _environment(source.format(**tokens), gateway.format(**tokens))
    result = subprocess.run([sys.executable, str(EXAMPLES / "relay.py"), *arguments], env=environment, capture_output=True, text=True, timeout=30)
    assert result.returncode == 1
    assert message in result.stderr and result.stdout == ""
    assert tokens["source"] not in result.stderr and tokens["gateway"] not in result.stderr


def test_the_relay_needs_nothing_but_the_standard_library() -> None:
    source = (EXAMPLES / "relay.py").read_text(encoding="utf-8")
    imported = set(re.findall(r"^(?:from|import) ([a-z_.]+)", source, re.MULTILINE))
    assert not any(name.split(".")[0] in ("erdigo_sdk", "cryptography", "zeroconf") for name in imported)
