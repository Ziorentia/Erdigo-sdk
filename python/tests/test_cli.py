"""The erdigo-send tool: its security rules first, then each command against the test receiver."""

from __future__ import annotations

import os
import sys

import pytest

from erdigo_sdk import cli

from .receiver import TestReceiver

POSIX = os.name == "posix"
SECRET = "SECRT-SECRT-SECRT-SECRT"


def run(capsys, *arguments: str):
    status = cli.main(list(arguments))
    captured = capsys.readouterr()
    return status, captured.out, captured.err


def _private_file(path, text: str):
    path.write_text(text, encoding="utf-8")
    if POSIX:
        os.chmod(path, 0o600)
    return str(path)


def _address(receiver: TestReceiver, state) -> list:
    return ["--host", "127.0.0.1", "--port", str(receiver.port), "--state-dir", str(state)]


@pytest.mark.parametrize("arguments", [["--code", SECRET], [f"--code={SECRET}"]])
def test_the_code_is_never_taken_as_an_argument(capsys, arguments) -> None:
    status, out, err = run(capsys, "pair", "--name", "x", "--kinds", "pin", *arguments)
    assert status == 1
    assert "not accepted as an argument" in err
    assert SECRET not in out + err


@pytest.mark.parametrize("arguments", [[SECRET], [f"--{SECRET}"], [f"--{SECRET}=1"]])
def test_errors_never_repeat_what_was_typed(capsys, arguments) -> None:
    status, out, err = run(capsys, "pin", *arguments)
    assert status == 1
    assert SECRET not in out + err


def test_an_option_and_its_value_go_apart(capsys) -> None:
    status, _, err = run(capsys, "pin", "--id=x")
    assert status == 1 and "write --id and its value as two separate arguments" in err


@pytest.mark.skipif(not POSIX, reason="file modes")
def test_a_code_file_others_can_read_is_refused(capsys, receiver: TestReceiver, tmp_path) -> None:
    code_file = _private_file(tmp_path / "code.txt", receiver.ticket.uri)
    os.chmod(code_file, 0o644)
    status, out, err = run(capsys, "pair", "--name", "x", "--kinds", "pin", "--code-file", code_file, *_address(receiver, tmp_path / "state"))
    assert status == 1
    assert "mode 0600" in err
    assert receiver.record.connections == 0


@pytest.mark.skipif(not POSIX, reason="symbolic links")
def test_a_linked_code_file_is_refused(capsys, receiver: TestReceiver, tmp_path) -> None:
    real = _private_file(tmp_path / "real.txt", receiver.ticket.uri)
    os.symlink(real, tmp_path / "link.txt")
    status, _, err = run(capsys, "pair", "--name", "x", "--kinds", "pin", "--code-file", str(tmp_path / "link.txt"), *_address(receiver, tmp_path / "state"))
    assert status == 1 and "regular file" in err
    assert receiver.record.connections == 0


def test_a_bare_code_without_a_terminal_is_refused_before_contacting_the_receiver(capsys, receiver: TestReceiver, tmp_path) -> None:
    code_file = _private_file(tmp_path / "code.txt", receiver.code.display)
    status, _, err = run(capsys, "pair", "--name", "x", "--kinds", "pin", "--code-file", code_file, *_address(receiver, tmp_path / "state"))
    assert status == 1
    assert "standard input or output is not a terminal" in err
    assert receiver.record.connections == 0


def test_a_bare_code_cannot_be_compared_with_redact(capsys, receiver: TestReceiver, tmp_path) -> None:
    code_file = _private_file(tmp_path / "code.txt", receiver.code.display)
    status, _, err = run(capsys, "pair", "--name", "x", "--kinds", "pin", "--code-file", code_file, "--redact", *_address(receiver, tmp_path / "state"))
    assert status == 1 and "--redact hides the receiver's key" in err
    assert receiver.record.connections == 0


def test_a_bare_code_with_the_full_fingerprint_pairs_without_asking(capsys, receiver: TestReceiver, tmp_path) -> None:
    code_file = _private_file(tmp_path / "code.txt", receiver.code.display)
    status, out, _ = run(
        capsys, "pair", "--name", "x", "--kinds", "pin", "--code-file", code_file, "--fingerprint", receiver.fingerprint.upper(), *_address(receiver, tmp_path / "state")
    )
    assert status == 0 and "Paired as" in out


def test_a_fingerprint_that_disagrees_with_the_qr_text_stops_pairing(capsys, receiver: TestReceiver, tmp_path) -> None:
    code_file = _private_file(tmp_path / "code.txt", receiver.ticket.uri)
    status, _, err = run(capsys, "pair", "--name", "x", "--kinds", "pin", "--code-file", code_file, "--fingerprint", "0" * 64, *_address(receiver, tmp_path / "state"))
    assert status == 1 and "does not match" in err
    assert receiver.record.connections == 0


def test_pair_write_list_identity_and_forget(capsys, receiver: TestReceiver, tmp_path) -> None:
    state = tmp_path / "state"
    code_file = _private_file(tmp_path / "code.txt", "﻿" + receiver.ticket.uri + "\n")
    status, out, err = run(capsys, "pair", "--name", "CLI test", "--kinds", "pin, note,photo,track,marker", "--label", "home", "--code-file", code_file, *_address(receiver, state))
    assert status == 0, err
    assert "Paired as 'home'. Allowed: pin, marker, note, photo, track." in out
    assert f"Receiver key: {receiver.fingerprint[0:4]}-" in out
    address = _address(receiver, state)
    note = tmp_path / "note.txt"
    note.write_bytes("Line one\nLine two".encode("utf-8"))
    photo = tmp_path / "photo.png"
    photo.write_bytes(b"\x89PNG\r\n\x1a\n" + bytes(64))
    for arguments in [
        ["ping"],
        ["pin", "--id", "p", "--lat", "37.5", "--lon", "127", "--title", "Pin"],
        ["marker", "--id", "m", "--lat", "37.5", "--lon", "127", "--color", "green"],
        ["note", "--id", "n", "--lat", "37.5", "--lon", "127", "--text-file", str(note)],
        ["photo", "--id", "ph", "--lat", "37.5", "--lon", "127", "--file", str(photo)],
        ["track-add", "--id", "t", "--lat", "37.5", "--lon", "127", "--ts", "1700000000000", "--alt", "12.5", "--title", "Walk"],
        ["track-finish", "--id", "t"],
        ["move", "--id", "p", "--lat", "37.6", "--lon", "127.1"],
        ["delete", "--id", "m"],
        ["delete-all", "--yes", "--to", "home"],
    ]:
        status, out, err = run(capsys, *arguments, *address)
        assert status == 0, err
        assert out.strip().endswith("OK")
    kinds = [type(envelope.request).__name__ for envelope in receiver.record.requests]
    assert kinds == ["Ping", "PutPin", "PutMarker", "PutNote", "PhotoBegin", "PhotoChunk", "PhotoEnd", "TrackAppend", "TrackFinish", "Move", "Delete", "DeleteAll"]
    assert receiver.record.requests[3].request.text == "Line one\nLine two"

    status, out, _ = run(capsys, "list", "--state-dir", str(state))
    assert status == 0 and out.startswith("home  key ") and "allowed pin,marker,note,photo,track" in out
    status, out, _ = run(capsys, "list", "--state-dir", str(state), "--redact")
    assert "home" not in out and "<redacted>" in out
    status, out, _ = run(capsys, "identity", "--state-dir", str(state))
    assert status == 0 and len(out.strip()) == 64
    status, out, _ = run(capsys, "forget", "--to", "home", "--state-dir", str(state))
    assert status == 0 and "Forgotten here" in out
    status, out, _ = run(capsys, "list", "--state-dir", str(state))
    assert "No receivers are paired." in out


@pytest.mark.parametrize(
    "arguments, message",
    [
        (["delete-all"], "Add --yes to confirm"),
        (["pin", "--id", "p", "--lat", "1", "--lon", "2", "--host", "127.0.0.1"], "--host and --port go together"),
        (["pin", "--id", "p", "--lat", "nan", "--lon", "2"], "--lat must be a number"),
        (["pin", "--id", "p", "--lat", "91", "--lon", "2"], "latitude must be within"),
        (["marker", "--id", "p", "--lat", "1", "--lon", "2", "--color", "pink"], "--color must be one of"),
        (["note", "--id", "n", "--lat", "1", "--lon", "2"], "give either --text or --text-file"),
        (["track-add", "--id", "t", "--lat", "1", "--lon", "2", "--ts", "1.5"], "--ts must be whole milliseconds"),
        (["pin", "--id", "p", "--lat", "1", "--lon", "2", "--keychain"], "--keychain belongs to the Swift SDK"),
        (["pin", "--id", "p", "--lat", "1", "--lon", "2", "--profile", "a", "--state-dir", "b"], "cannot be used together"),
        (["pin", "--id", "p", "--lat", "1", "--lon", "2", "--profile", "a/b"], "--profile may contain only"),
        (["pin", "--id", "p", "--lat", "1", "--lon", "2", "--yes", "1"], "--yes does not apply to this command"),
        (["pin", "--id", "p", "--id", "q", "--lat", "1", "--lon", "2"], "--id was given twice"),
        (["serve", "--mock"], "this edition has no mock receiver"),
        (["teleport"], "unknown command"),
    ],
)
def test_mistakes_are_explained(capsys, tmp_path, arguments, message) -> None:
    if "--state-dir" not in arguments and "--profile" not in arguments and arguments[0] not in ("serve", "teleport"):
        arguments = arguments + ["--state-dir", str(tmp_path / "state")]
    status, _, err = run(capsys, *arguments)
    assert status == 1
    assert message in err


def test_a_note_file_saved_by_notepad_is_sent_without_its_byte_order_mark_or_carriage_returns(capsys, receiver: TestReceiver, tmp_path) -> None:
    """The protocol allows line feeds and tabs in a note, but no carriage return, so the tool takes out what Notepad
    adds, as the other editions' tools do."""
    state = tmp_path / "state"
    code_file = _private_file(tmp_path / "code.txt", receiver.ticket.uri)
    assert run(capsys, "pair", "--name", "x", "--kinds", "note", "--code-file", code_file, *_address(receiver, state))[0] == 0
    note = tmp_path / "note.txt"
    note.write_bytes(b"\xef\xbb\xbf" + "First line\r\nSecond é\r\n\r\nend\r\n".encode("utf-8"))
    status, _, err = run(capsys, "note", "--id", "n", "--lat", "1", "--lon", "2", "--text-file", str(note), *_address(receiver, state))
    assert status == 0, err
    assert receiver.record.requests[-1].request.text == "First line\nSecond é\n\nend\n"


def test_a_carriage_return_on_its_own_is_refused_before_connecting(capsys, receiver: TestReceiver, tmp_path) -> None:
    state = tmp_path / "state"
    code_file = _private_file(tmp_path / "code.txt", receiver.ticket.uri)
    assert run(capsys, "pair", "--name", "x", "--kinds", "note", "--code-file", code_file, *_address(receiver, state))[0] == 0
    welcomes = receiver.record.welcomes
    note = tmp_path / "note.txt"
    note.write_bytes(b"First line\rSecond line")
    status, _, err = run(capsys, "note", "--id", "n", "--lat", "1", "--lon", "2", "--text-file", str(note), *_address(receiver, state))
    assert status == 1
    assert "a note cannot contain control or text-direction characters" in err
    assert receiver.record.welcomes == welcomes and receiver.record.requests == []


@pytest.mark.parametrize(
    ("text", "expected"),
    [
        ("\ufeff\ufeffa", "\ufeffa"),
        ("a\rb", "a\rb"),
        ("a\r\r\nb", "a\r\nb"),
        ("a\n\rb", "a\n\rb"),
        ("a\r", "a\r"),
    ],
)
def test_only_one_byte_order_mark_and_only_cr_lf_pairs_are_changed(text: str, expected: str) -> None:
    assert cli.note_from_file(text) == expected


def test_the_note_limit_counts_the_note_sent_not_the_file(capsys, tmp_path) -> None:
    lines = 4096 // 2
    at_limit = tmp_path / "at-limit.txt"
    at_limit.write_bytes(b"\xef\xbb\xbf" + b"a\r\n" * lines)
    assert cli.read_note(str(at_limit)) == "a\n" * lines
    over = tmp_path / "over.txt"
    over.write_bytes(b"a\r\n" * lines + b"a")
    status, _, err = run(capsys, "note", "--id", "n", "--lat", "1", "--lon", "2", "--text-file", str(over), "--state-dir", str(tmp_path / "state"))
    assert status == 1 and "a note is at most 4096 bytes" in err


def test_files_past_the_limit_are_refused_before_connecting(capsys, tmp_path) -> None:
    big = tmp_path / "big.png"
    big.write_bytes(b"\x89PNG\r\n\x1a\n" + bytes(1_048_576))
    status, _, err = run(capsys, "photo", "--id", "p", "--lat", "1", "--lon", "2", "--file", str(big), "--state-dir", str(tmp_path / "state"))
    assert status == 1 and "a photo is at most 1048576 bytes" in err
    note = tmp_path / "note.txt"
    note.write_bytes(b"x" * 4097)
    status, _, err = run(capsys, "note", "--id", "n", "--lat", "1", "--lon", "2", "--text-file", str(note), "--state-dir", str(tmp_path / "state"))
    assert status == 1 and "a note is at most 4096 bytes" in err


def test_help_and_module_entry_point(capsys) -> None:
    status, out, _ = run(capsys, "help")
    assert status == 0 and "erdigo-send: write pins" in out and "--code-file" in out


@pytest.mark.skipif(not POSIX, reason="pseudo terminals")
# The child runs nothing but exec after the fork, so the test receiver's threads cannot deadlock it.
@pytest.mark.filterwarnings("ignore:This process .* is multi-threaded:DeprecationWarning")
def test_typed_ahead_text_is_not_taken_as_the_answer(receiver: TestReceiver, tmp_path) -> None:
    """A 'yes' typed while the tool was still checking the code, before the key appeared, is discarded."""
    import pty
    import time

    receiver.pair_delay = 1.0
    state = tmp_path / "state"
    arguments = ["pair", "--name", "x", "--kinds", "pin", *_address(receiver, state)]
    pid, master = pty.fork()
    if pid == 0:  # pragma: no cover - the child
        os.execv(sys.executable, [sys.executable, "-m", "erdigo_sdk", *arguments])
    output = b""
    typed_code = False
    typed_early = False
    while True:
        try:
            piece = os.read(master, 4096)
        except OSError:
            break
        if not piece:
            break
        output += piece
        if not typed_code and b"Pairing code: " in output:
            os.write(master, receiver.code.display.encode() + b"\n")
            typed_code = True
        if typed_code and not typed_early and b"Checking the code with the receiver" in output:
            # Typed before the key is on the screen, while the receiver is still answering.
            os.write(master, b"yes\n")
            typed_early = True
        if b"type yes: " in output:
            time.sleep(0.3)
            os.write(master, b"\x04")
            break
    while True:
        try:
            piece = os.read(master, 4096)
        except OSError:
            break
        if not piece:
            break
        output += piece
    _, status = os.waitpid(pid, 0)
    os.close(master)
    assert typed_early
    assert os.waitstatus_to_exitcode(status) == 1, output.decode(errors="replace")
    assert b"was not confirmed" in output
    assert receiver.record.pair_requests == []
