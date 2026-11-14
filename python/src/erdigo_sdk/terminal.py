"""Reading from the person at the keyboard, and only from a terminal."""

from __future__ import annotations

import os
import sys
from typing import Optional

_WINDOWS = sys.platform == "win32"


class UsageError(Exception):
    """A mistake in how the tool was called. Its message never repeats what was typed."""


def person_is_there() -> bool:
    """True if both standard input and standard output are a terminal, so that a person can see a question and answer
    it."""
    try:
        return sys.stdin is not None and sys.stdout is not None and sys.stdin.isatty() and sys.stdout.isatty()
    except (AttributeError, ValueError, OSError):
        return False


def discard_typed_ahead() -> None:
    """Discards anything typed so far. Only an answer given after a question has appeared counts as an answer to it."""
    if _WINDOWS:
        import msvcrt

        while msvcrt.kbhit():
            msvcrt.getwch()
        return
    import termios

    try:
        termios.tcflush(sys.stdin.fileno(), termios.TCIFLUSH)
    except (termios.error, OSError, ValueError):
        pass


def read_answer(max_length: int = 256) -> Optional[str]:
    """Reads one line typed on standard input, which is a terminal here. Returns None at the end of input."""
    line = sys.stdin.readline(max_length + 1)
    if line == "":
        return None
    if len(line) > max_length and not line.endswith("\n"):
        raise UsageError("the text typed is too long")
    return line.rstrip("\r\n")


def read_secret_line(prompt: str, max_length: int) -> Optional[str]:
    """Shows ``prompt`` and reads one line from the terminal without showing what is typed.

    On macOS and Linux it reads from the terminal itself, whatever standard input is, as ``readpassphrase`` does, and
    returns None if there is no terminal. On Windows it reads from the console, and returns None if standard input is
    not one. An empty string means the end of input.
    """
    if _WINDOWS:
        return _read_secret_windows(prompt, max_length)
    return _read_secret_unix(prompt, max_length)


def _read_secret_unix(prompt: str, max_length: int) -> Optional[str]:
    import termios

    try:
        descriptor = os.open("/dev/tty", os.O_RDWR | getattr(os, "O_NOCTTY", 0))
    except OSError:
        return None
    try:
        try:
            saved = termios.tcgetattr(descriptor)
        except termios.error:
            return None
        quiet = list(saved)
        quiet[3] = quiet[3] & ~termios.ECHO
        # Anything typed before the prompt is discarded, as readpassphrase does.
        termios.tcsetattr(descriptor, termios.TCSAFLUSH, quiet)
        buffer = bytearray()
        try:
            os.write(descriptor, prompt.encode("utf-8"))
            while len(buffer) <= max_length:
                piece = os.read(descriptor, 1)
                if not piece or piece == b"\n":
                    break
                buffer += piece
            if len(buffer) > max_length:
                raise UsageError("the text typed is too long")
            try:
                return buffer.decode("utf-8").rstrip("\r")
            except UnicodeDecodeError:
                raise UsageError("the pairing code has invalid characters") from None
        finally:
            termios.tcsetattr(descriptor, termios.TCSAFLUSH, saved)
            os.write(descriptor, b"\n")
            for index in range(len(buffer)):
                buffer[index] = 0
    finally:
        os.close(descriptor)


def _read_secret_windows(prompt: str, max_length: int) -> Optional[str]:
    import msvcrt

    if not sys.stdin.isatty():
        return None
    sys.stderr.write(prompt)
    sys.stderr.flush()
    characters = []
    try:
        while True:
            character = msvcrt.getwch()
            if character in ("\r", "\n"):
                return "".join(characters)
            if character in ("\x04", "\x1a"):
                return ""
            if character == "\x03":
                raise KeyboardInterrupt
            if character in ("\b", "\x7f"):
                if characters:
                    characters.pop()
                continue
            if character in ("\x00", "\xe0"):
                # The first half of a special key, such as an arrow. Its second half is dropped too.
                msvcrt.getwch()
                continue
            if ord(character) < 0x20:
                continue
            if len(characters) == max_length:
                raise UsageError("the text typed is too long")
            characters.append(character)
    finally:
        sys.stderr.write("\n")
        sys.stderr.flush()
        characters.clear()
