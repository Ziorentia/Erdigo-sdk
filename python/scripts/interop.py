"""Pairs this edition's erdigo-send with another edition's mock receiver, and writes every item kind to it.

Usage, from the python folder, with the mock's command after ``--``:

    python scripts/interop.py --cwd ../dotnet -- dotnet run --no-build -c Release --project src/erdigo-send --
    python scripts/interop.py --cwd .. -- swift run erdigo-send

The mock is started with ``serve --mock --code-file FILE``. The script pairs with the QR text from that file, then sends
ping, pin, marker, note, photo (PNG and JPEG, one of them in several chunks), track add and finish, move, delete and
delete-all, and checks with the mock's own ``items`` command that what it holds is what was sent. On macOS and Linux it
also pairs a second device with a bare typed code through a pseudo terminal, answering ``no`` and then ``yes`` to the
key comparison, and checks that a ``no`` saved nothing and asked the mock nothing.

It exits 0 only if every step passed. Each step is printed, so a CI log shows how far it got.
"""

from __future__ import annotations

import argparse
import base64
import os
import queue
import random
import re
import struct
import subprocess
import sys
import tempfile
import threading
import time
import zlib
from pathlib import Path
from typing import List, Optional

# A 32x32 JPEG made with Pillow from a solid colour, as in dotnet/tests/Erdigo.Tests/TestImages.cs.
JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAUDBAQEAwUEBAQFBQUGBwwIBwcHBw8LCwkMEQ8SEhEPERETFhwXExQaFRERGCEYGh0d"
    "Hx8fExciJCIeJBweHx7/2wBDAQUFBQcGBw4ICA4eFBEUHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4eHh4e"
    "Hh4eHh4eHh7/wAARCAAgACADASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUF"
    "BAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVW"
    "V1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi"
    "4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAEC"
    "AxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVm"
    "Z2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq"
    "8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDJooor9SPzcKKKKACiiigAooooA//Z"
)


def png(width: int, height: int, seed: int) -> bytes:
    """A valid RGB PNG of random pixels, which compress so badly that a large one needs several 48000-byte chunks."""
    generator = random.Random(seed)

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    rows = b"".join(b"\x00" + bytes(generator.getrandbits(8) for _ in range(width * 3)) for _ in range(height))
    header = struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(rows, 9)) + chunk(b"IEND", b"")


class Mock:
    """The other edition's mock receiver, with its standard output read on a thread."""

    def __init__(self, command: List[str], cwd: str, code_file: Path, extra: Optional[List[str]] = None) -> None:
        self.lines: "queue.Queue[str]" = queue.Queue()
        self.seen: List[str] = []
        self.process = subprocess.Popen(
            command + ["serve", "--mock", "--code-file", str(code_file), *(extra or [])],
            cwd=cwd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self) -> None:
        assert self.process.stdout is not None
        for line in self.process.stdout:
            self.lines.put(line.rstrip("\n"))
        self.lines.put("<mock exited>")

    def wait_for(self, pattern: str, timeout: float = 240.0) -> re.Match:
        """Waits for a line matching ``pattern``, keeping every line read on the way."""
        give_up = time.monotonic() + timeout
        expression = re.compile(pattern)
        while True:
            remaining = give_up - time.monotonic()
            if remaining <= 0:
                raise AssertionError(f"the mock never printed a line matching {pattern!r}")
            try:
                line = self.lines.get(timeout=remaining)
            except queue.Empty:
                continue
            print(f"    mock: {line}")
            self.seen.append(line)
            if line == "<mock exited>":
                raise AssertionError("the mock exited")
            found = expression.search(line)
            if found:
                return found

    def command(self, text: str) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(text + "\n")
        self.process.stdin.flush()

    def drain(self, seconds: float = 0.5) -> List[str]:
        """Collects what the mock prints within a short time."""
        collected = []
        give_up = time.monotonic() + seconds
        while True:
            remaining = give_up - time.monotonic()
            if remaining <= 0:
                return collected
            try:
                line = self.lines.get(timeout=remaining)
            except queue.Empty:
                return collected
            print(f"    mock: {line}")
            self.seen.append(line)
            collected.append(line)

    def stop(self) -> None:
        try:
            self.command("quit")
            self.process.wait(timeout=10)
        except (OSError, subprocess.TimeoutExpired, ValueError):
            self.process.kill()


def send(*arguments: str, expect_ok: bool = True) -> subprocess.CompletedProcess:
    """Runs this edition's erdigo-send."""
    shown = " ".join(arguments)
    print(f"  erdigo-send {shown}")
    result = subprocess.run(
        [sys.executable, "-m", "erdigo_sdk", *arguments],
        stdin=subprocess.DEVNULL,
        capture_output=True,
        text=True,
        encoding="utf-8",
        timeout=120,
    )
    for line in (result.stdout + result.stderr).splitlines():
        print(f"    {line}")
    if expect_ok:
        if result.returncode != 0:
            raise AssertionError(f"erdigo-send {arguments[0]} failed with exit code {result.returncode}")
        if arguments[0] not in ("pair", "list", "identity", "forget") and "OK" not in result.stdout.splitlines():
            raise AssertionError(f"erdigo-send {arguments[0]} did not print OK")
    return result


def bare_code_with_terminal(arguments: List[str], code: str, answer: str) -> tuple:
    """Runs ``pair`` on a pseudo terminal, types the bare code when asked, then answers the key question."""
    import pty

    pid, master = pty.fork()
    if pid == 0:  # pragma: no cover - the child
        os.execv(sys.executable, [sys.executable, "-m", "erdigo_sdk", *arguments])
    output = b""
    typed_code = False
    answered = False
    key: Optional[str] = None
    give_up = time.monotonic() + 60
    while time.monotonic() < give_up:
        try:
            piece = os.read(master, 4096)
        except OSError:
            break
        if not piece:
            break
        output += piece
        if not typed_code and b"Pairing code: " in output:
            os.write(master, code.encode() + b"\n")
            typed_code = True
        if not answered and b"type yes: " in output:
            found = re.search(rb"Receiver key: ([0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4})", output)
            key = found.group(1).decode() if found else None
            os.write(master, answer.encode() + b"\n")
            answered = True
    _, status = os.waitpid(pid, 0)
    os.close(master)
    text = output.decode("utf-8", "replace")
    for line in text.splitlines():
        print(f"    {line}")
    return os.waitstatus_to_exitcode(status), text, key


def discovery(command: List[str], cwd: str, startup_timeout: float) -> int:
    """Pairs with a mock that listens on the local network and advertises itself, without giving its address. This
    needs the optional zeroconf package, and lets anyone on the network reach the mock while it runs, so it is meant
    for a CI runner."""
    work = Path(tempfile.mkdtemp(prefix="erdigo-discovery-"))
    os.chmod(work, 0o700)
    code_file = work / "pairing.txt"
    state = work / "state"
    print(f"Starting the mock on the local network: {' '.join(command)}")
    mock = Mock(command, cwd, code_file, ["--lan", "--bonjour", "--auto-approve"])
    try:
        port = int(mock.wait_for(r"Listening on the local network, port (\d+)", startup_timeout).group(1))
        mock.wait_for(r"Pairing text written to the code file")
        print("Pairing without an address, so the receiver has to be found on the network")
        result = send("pair", "--name", "Found by discovery", "--kinds", "pin", "--label", "found", "--code-file", str(code_file), "--state-dir", str(state))
        if "Paired as 'found'. Allowed: pin." not in result.stdout:
            raise AssertionError("pairing through discovery failed")
        listed = send("list", "--state-dir", str(state))
        if "found  key " not in listed.stdout:
            raise AssertionError("the pairing found through discovery was not saved")
        send("pin", "--id", "found", "--lat", "1", "--lon", "2", "--state-dir", str(state))
        mock.wait_for(r"\[mock\] #1 put pin")
        print(f"Discovery passed: the mock on port {port} was found and written to.")
        return 0
    finally:
        mock.stop()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--cwd", required=True, help="where to run the mock's command")
    parser.add_argument("--discovery", action="store_true", help="find a mock that advertises on the local network instead")
    parser.add_argument("--startup-timeout", type=float, default=600.0, help="how long the mock may take to start")
    parser.add_argument("mock", nargs=argparse.REMAINDER, help="the mock's command, after --")
    options = parser.parse_args()
    command = options.mock[1:] if options.mock[:1] == ["--"] else options.mock
    if not command:
        parser.error("give the mock's command after --")
    if options.discovery:
        return discovery(command, options.cwd, options.startup_timeout)

    work = Path(tempfile.mkdtemp(prefix="erdigo-interop-"))
    os.chmod(work, 0o700)
    code_file = work / "pairing.txt"
    state = work / "state"
    print(f"Starting the mock: {' '.join(command)}")
    mock = Mock(command, options.cwd, code_file)
    try:
        port = int(mock.wait_for(r"Listening on 127\.0\.0\.1, port (\d+)", options.startup_timeout).group(1))
        mock_key = mock.wait_for(r"Receiver key: ([0-9a-f-]{19})").group(1)
        mock.wait_for(r"Pairing text written to the code file")
        address = ["--host", "127.0.0.1", "--port", str(port), "--state-dir", str(state)]

        print("Pairing with the QR text from the mock's code file")
        result = send("pair", "--name", "Python interop", "--kinds", "pin,marker,note,photo,track", "--label", "mock", "--code-file", str(code_file), *address)
        if f"Receiver key: {mock_key}" not in result.stdout or "Paired as 'mock'. Allowed: pin, marker, note, photo, track." not in result.stdout:
            raise AssertionError("pairing did not report the mock's key and every kind")
        mock.wait_for(r"\[mock\] paired ")

        print("Writing every item kind")
        # Written as bytes, so that the file has LF line endings on Windows too.
        note_file = work / "note.txt"
        note_file.write_bytes("First line\nSecond line, with a link https://example.com that must stay text.".encode("utf-8"))
        big_png = work / "big.png"
        big_png.write_bytes(png(160, 120, seed=1))
        small_jpeg = work / "small.jpg"
        small_jpeg.write_bytes(JPEG)
        chunks = -(-big_png.stat().st_size // 48000)
        if chunks < 2:
            raise AssertionError("the large PNG should need several chunks")
        steps = [
            ("ping",),
            ("pin", "--id", "city-hall", "--lat", "37.5663", "--lon", "126.9779", "--title", "Seoul City Hall"),
            ("marker", "--id", "tower", "--lat", "37.5512", "--lon", "126.9882", "--title", "Tower", "--color", "blue"),
            ("note", "--id", "inline", "--lat", "37.57", "--lon", "126.98", "--text", "Zürich € 東京"),
            ("note", "--id", "from-file", "--lat", "37.571", "--lon", "126.981", "--text-file", str(note_file)),
            ("photo", "--id", "view", "--lat", "37.5665", "--lon", "126.978", "--file", str(big_png), "--title", "View"),
            ("photo", "--id", "small", "--lat", "37.5666", "--lon", "126.9781", "--file", str(small_jpeg)),
            ("track-add", "--id", "walk", "--lat", "37.5663", "--lon", "126.9779", "--ts", "1700000000000", "--alt", "38.5", "--title", "Morning walk"),
            ("track-add", "--id", "walk", "--lat", "37.5665", "--lon", "126.9782", "--ts", "1700000005000"),
            ("track-finish", "--id", "walk"),
            ("move", "--id", "city-hall", "--lat", "37.5664", "--lon", "126.978"),
            ("delete", "--id", "tower"),
        ]
        for step in steps:
            send(*step, *address)
        mock.drain()

        print("A note file saved by Notepad is sent without its byte order mark and carriage returns")
        # The protocol allows line feeds and tabs in a note, but no carriage return (spec section 6.3).
        notepad_file = work / "note-notepad.txt"
        notepad_file.write_bytes(b"\xef\xbb\xbfFirst line\r\nSecond line\r\n")
        send("note", "--id", "notepad", "--lat", "37.572", "--lon", "126.982", "--text-file", str(notepad_file), *address)
        mock.drain()

        print("A carriage return on its own is still refused before anything is sent")
        lone_cr_file = work / "note-lone-cr.txt"
        lone_cr_file.write_bytes(b"First line\rSecond line")
        refused = send("note", "--id", "lone-cr", "--lat", "37.573", "--lon", "126.983", "--text-file", str(lone_cr_file), *address, expect_ok=False)
        if refused.returncode != 1 or "a note cannot contain control or text-direction characters" not in refused.stderr:
            raise AssertionError("a note file with a carriage return on its own was not refused")
        if any(re.match(r"\[mock\] (connected|#\d+ )", line) for line in mock.drain(1.0)):
            raise AssertionError("the mock was contacted for a note that should have been refused first")

        print("Checking what the mock holds")
        mock.command("devices")
        devices = mock.wait_for(r"^1\. '(.*)' ([0-9a-f]{16}) (.*)$")
        if devices.group(1) != "Python interop" or devices.group(3) != "pin,marker,note,photo,track":
            raise AssertionError("the mock does not list this device as paired")
        mock.command("items 1")
        held = mock.drain(2.0)
        expected = {
            "pin city-hall at 37.5664,126.978 title=Seoul City Hall points=0",
            "note inline at 37.57,126.98 title=- points=0",
            "note from-file at 37.571,126.981 title=- points=0",
            "note notepad at 37.572,126.982 title=- points=0",
            "photo view at 37.5665,126.978 title=View points=0",
            "photo small at 37.5666,126.9781 title=- points=0",
            "track walk at -,- title=Morning walk points=2",
        }
        missing = expected - set(held)
        if missing:
            raise AssertionError(f"the mock does not hold: {sorted(missing)}")
        if any(line.startswith("marker tower ") for line in held):
            raise AssertionError("the deleted marker is still there")

        print("Deleting everything this device wrote")
        send("delete-all", "--yes", *address)
        mock.drain()
        mock.command("items 1")
        left = [line for line in mock.drain(2.0) if " at " in line]
        if left:
            raise AssertionError("items are left after delete-all")

        applied = [line for line in mock.seen if re.match(r"\[mock\] #\d+ ", line)]
        rejected = [line for line in mock.seen if line.startswith("[mock] rejected")]
        print(f"The mock applied {len(applied)} requests and rejected {len(rejected)}.")
        if rejected:
            raise AssertionError("the mock rejected a request")

        if os.name == "posix":
            print("Pairing a second device with a bare typed code on a terminal")
            mock.command("code")
            mock.wait_for(r"Pairing text written to the code file")
            qr = code_file.read_text(encoding="utf-8")
            code = re.search(r"code=([0-9A-Z-]+)", qr).group(1)
            fingerprint = re.search(r"fp=([0-9a-f]{64})", qr).group(1)
            second = ["pair", "--name", "Typed code", "--kinds", "pin", "--label", "typed", "--host", "127.0.0.1", "--port", str(port), "--state-dir", str(work / "typed")]
            status, text, key = bare_code_with_terminal(second, code, "no")
            if status != 1 or "was not confirmed" not in text or key != f"{fingerprint[0:4]}-{fingerprint[4:8]}-{fingerprint[8:12]}-{fingerprint[12:16]}":
                raise AssertionError("a 'no' did not stop pairing, or the key shown was not the mock's")
            if any("pairing request" in line or "paired" in line for line in mock.drain(1.0)):
                raise AssertionError("the mock was asked to pair after a 'no'")
            listed = send("list", "--state-dir", str(work / "typed"))
            if "No receivers are paired." not in listed.stdout:
                raise AssertionError("a pairing was saved after a 'no'")
            status, text, key = bare_code_with_terminal(second, code, "yes")
            if status != 0 or "Paired as 'typed'. Allowed: pin." not in text:
                raise AssertionError("a 'yes' did not pair")
            send("pin", "--id", "typed", "--lat", "1", "--lon", "2", "--host", "127.0.0.1", "--port", str(port), "--state-dir", str(work / "typed"))

        print("Interop passed.")
        return 0
    finally:
        mock.stop()


if __name__ == "__main__":
    sys.exit(main())
