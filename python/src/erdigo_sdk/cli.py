"""erdigo-send: write pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network."""

from __future__ import annotations

import math
import sys
import unicodedata
from typing import Callable, Dict, Iterable, List, Optional, Sequence, Set

from . import protocol, terminal
from .client import ErdigoClient, RetryPolicy
from .discovery import DISCOVER, ReceiverTarget
from .errors import ErdigoError, ErrorKind
from .identity import ReceiverPairing
from .model import LIMITS_V1, ItemKind, MarkerColor, PairingTicket, TrackPoint, Coordinate
from .pairing import pair
from .private_file import PrivateFileError, PrivateFileProblem, read_private
from .protocol import WireError
from .store import FileDeviceStore, default_directory
from .terminal import UsageError

USAGE = """\
erdigo-send: write pins, markers, notes, photos and tracks to a paired Erdigo receiver on the local network.

Pairing
  pair --name NAME --kinds pin,marker,note,photo,track [--label LABEL]
       [--host HOST --port PORT] [--code-file FILE] [--fingerprint HEX]
      The code is read from the keyboard, or from a file only you can read.
      It is never accepted as a command-line argument.
      A bare code means you compare the receiver's key and type yes before the receiver is asked to pair.
      That needs standard input and output to be a terminal, so a bare code is refused without one.
      The QR text or --fingerprint compares the key for you, so nothing is asked.
  list
  forget --to LABEL
  identity
      Prints this device's fingerprint.

Writing
  ping
  pin          --id ID --lat N --lon N [--title TEXT]
  marker       --id ID --lat N --lon N [--title TEXT] [--color COLOR]
  note         --id ID --lat N --lon N (--text TEXT | --text-file FILE)
  photo        --id ID --lat N --lon N --file FILE [--title TEXT]
  track-add    --id ID --lat N --lon N [--ts MILLISECONDS] [--alt METRES] [--title TEXT]
  track-finish --id ID
  move         --id ID --lat N --lon N
  delete       --id ID
  delete-all   --yes
      Options: --to LABEL (needed with several receivers), --host HOST --port PORT, --retry-for SECONDS
      Without --host and --port, finding the receiver on the network needs the optional zeroconf package.

Testing without the Erdigo app
  serve --mock
      This edition has no mock receiver. Run the one from the .NET or Swift SDK in this repository, which this
      tool works with: 'dotnet run --project src/erdigo-send -- serve --mock' in windows/, or
      'swift run erdigo-send serve --mock' at the top of the repository.

For pairing and writing commands
  --profile NAME    Use a separate identity and pairings (default: "default").
  --state-dir DIR   Keep state in DIR instead of the default folder.
  --redact          Hide labels, keys and other identifying output, for pasting into a bug report.
                    With pair, it needs the QR text or --fingerprint, because a hidden key cannot be compared.

On Windows the device key is encrypted with DPAPI for the current Windows user, in %LOCALAPPDATA%\\ErdigoSDK.Python.
Elsewhere it is kept in a file of mode 0600.
"""

#: Every option the tool has. Errors name an option only if it is on this list, because anything else that was typed
#: might be a pairing code.
KNOWN_OPTIONS = {
    "alt", "ask", "auto-approve", "bonjour", "code-file", "color", "file", "fingerprint", "host", "id", "keychain", "kinds", "label", "lan",
    "lat", "lon", "mock", "name", "port", "profile", "redact", "retry-for", "state-dir", "text", "text-file", "title", "to", "ts", "yes",
}


def _option_description(name: str) -> str:
    return f"--{name}" if name in KNOWN_OPTIONS else "an unknown option"


class Options:
    """A deliberately small option parser: ``--name value`` pairs and bare flags, nothing else."""

    def __init__(self, arguments: Sequence[str], allowed_flags: Iterable[str]) -> None:
        allowed = set(allowed_flags)
        self._values: Dict[str, str] = {}
        self._flags: Set[str] = set()
        self._used: Set[str] = set()
        index = 0
        while index < len(arguments):
            argument = arguments[index]
            # Errors here never repeat what was typed, because it might be a pairing code.
            if not argument.startswith("--"):
                raise UsageError("unexpected argument. Options start with --, and a pairing code is never given as an argument.")
            name = argument[2:]
            if name == "code" or name.startswith("code="):
                raise UsageError(
                    "the pairing code is not accepted as an argument, because arguments appear in the process list and shell history. "
                    "Type it when asked, or use --code-file."
                )
            if "=" in name:
                raise UsageError(f"write {_option_description(name.split('=', 1)[0])} and its value as two separate arguments")
            if name in allowed:
                self._flags.add(name)
                index += 1
                continue
            if name not in KNOWN_OPTIONS:
                raise UsageError("an unknown option was given. See 'erdigo-send help'.")
            if index + 1 >= len(arguments):
                raise UsageError(f"--{name} needs a value")
            if name in self._values:
                raise UsageError(f"--{name} was given twice")
            self._values[name] = arguments[index + 1]
            index += 2

    def flag(self, name: str) -> bool:
        self._used.add(name)
        return name in self._flags

    def optional(self, name: str) -> Optional[str]:
        self._used.add(name)
        return self._values.get(name)

    def required(self, name: str) -> str:
        value = self.optional(name)
        if value is None:
            raise UsageError(f"--{name} is required")
        return value

    def required_float(self, name: str) -> float:
        value = _parse_float(self.required(name))
        if value is None:
            raise UsageError(f"--{name} must be a number")
        return value

    def optional_port(self) -> Optional[int]:
        text = self.optional("port")
        if text is None:
            return None
        if not text.isascii() or not text.isdigit() or int(text) > 65535:
            raise UsageError("--port must be between 0 and 65535")
        return int(text)

    def finish(self) -> None:
        """Call after reading every option the command understands."""
        unknown = sorted(name for name in list(self._values) + list(self._flags) if name not in self._used)
        if unknown:
            raise UsageError(f"--{unknown[0]} does not apply to this command")


def _parse_float(text: str) -> Optional[float]:
    stripped = text.strip()
    # Only plain decimal numbers, as the other tools accept: no 'nan', 'inf' or underscores.
    if not stripped or not all(character in "0123456789+-.eE" for character in stripped):
        return None
    try:
        value = float(stripped)
    except ValueError:
        return None
    return value if math.isfinite(value) else None


def _decode_utf8(data: bytes, what: str) -> str:
    try:
        return data.decode("utf-8", errors="strict")
    except UnicodeDecodeError:
        raise UsageError(f"the {what} file is not UTF-8 text") from None


def read_code_file(path: str) -> str:
    """Reads a file only if nobody else can read it, as ssh does for its keys."""
    try:
        data = read_private(path, 1024)
    except PrivateFileError as error:
        if error.problem is PrivateFileProblem.INSECURE:
            raise UsageError(
                "the code file must be a regular file that only you can read"
                if sys.platform == "win32"
                else "the code file must be a regular file you own, with mode 0600, so that nobody else can read it"
            ) from None
        if error.problem is PrivateFileProblem.TOO_LARGE:
            raise UsageError("the code file is too large") from None
        raise UsageError("could not open the code file") from None
    # A byte order mark is how some editors mark UTF-8. It is not part of the code.
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    return _decode_utf8(data, "code")


def _read_code_from_keyboard() -> str:
    code = terminal.read_secret_line("Pairing code: ", 1024)
    if code is None:
        raise UsageError(
            "no terminal to read the code from. Use --code-file with a file only you can read."
            if sys.platform == "win32"
            else "no terminal to read the code from. Use --code-file with a file of mode 0600."
        )
    return code


def read_at_most(path: str, max_bytes: int, what: str, too_large: str) -> bytes:
    """Reads a file named on the command line. Its size is not trusted: nothing past the limit is read, so a huge
    file, or one that keeps growing while it is read, is refused before any connection is made."""
    try:
        handle = open(path, "rb")
    except FileNotFoundError:
        raise UsageError(f"the {what} file does not exist") from None
    except (OSError, ValueError):
        raise UsageError(f"could not open the {what} file") from None
    with handle:
        try:
            data = handle.read(max_bytes + 1)
        except OSError:
            raise UsageError(f"could not read the {what} file") from None
    if len(data) > max_bytes:
        raise UsageError(too_large)
    return data


NOTE_TOO_LARGE = f"a note is at most {LIMITS_V1.note_bytes} bytes of UTF-8"
PHOTO_TOO_LARGE = f"a photo is at most {LIMITS_V1.photo_bytes} bytes"


class _Context:
    def __init__(self, store: FileDeviceStore, redact: bool) -> None:
        self.store = store
        self.redact = redact

    def shown(self, text: str) -> str:
        return "<redacted>" if self.redact else text


def _is_profile_character(character: str) -> bool:
    if character in "-_":
        return True
    return unicodedata.category(character) in ("Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No")


def _make_context(options: Options) -> _Context:
    profile = options.optional("profile") or "default"
    if options.optional("profile") == "" or not all(_is_profile_character(character) for character in profile):
        raise UsageError("--profile may contain only letters, digits, '-' and '_'")
    path = options.optional("state-dir")
    if path is not None:
        if options.optional("profile") is not None:
            raise UsageError("--state-dir and --profile cannot be used together, because a state folder holds one identity")
        directory = path
    else:
        directory = default_directory(profile)
    if options.flag("keychain"):
        raise UsageError(
            "--keychain belongs to the macOS SDK. This edition keeps the device key with DPAPI on Windows, and in a file of mode 0600 elsewhere."
        )
    return _Context(FileDeviceStore(directory), options.flag("redact"))


#: Spaces and tabs, but not line breaks, as the other tools trim them from each kind.
_SPACES = "".join(chr(code) for code in range(0x3001) if code == 0x09 or unicodedata.category(chr(code)) == "Zs")


def _parse_kinds(text: str) -> List[ItemKind]:
    kinds: List[ItemKind] = []
    for piece in text.split(","):
        if not piece:
            continue
        kind = ItemKind.from_wire(piece.strip(_SPACES))
        if kind is None:
            raise UsageError("--kinds takes pin, marker, note, photo and track, separated by commas.")
        if kind not in kinds:
            kinds.append(kind)
    if not kinds:
        raise UsageError("--kinds needs at least one item kind")
    return kinds


def _target(options: Options) -> ReceiverTarget:
    host = options.optional("host")
    port = options.optional_port()
    if host is None and port is None:
        return DISCOVER
    if host is not None and port is not None:
        return ReceiverTarget.address(host, port)
    raise UsageError("--host and --port go together")


def _select_pairing(context: _Context, label: Optional[str]) -> ReceiverPairing:
    pairings = context.store.pairings()
    if label is not None:
        found = [pairing for pairing in pairings if pairing.label == label]
        if len(found) != 1:
            raise UsageError(
                "no receiver is paired under that label. See 'erdigo-send list'."
                if not found
                else "several receivers have that label. Forget one of them first."
            )
        return found[0]
    if not pairings:
        raise UsageError("no receiver is paired yet. Run 'erdigo-send pair' first.")
    if len(pairings) > 1:
        raise UsageError("several receivers are paired. Choose one with --to LABEL.")
    return pairings[0]


def _parse_coordinate(options: Options) -> Coordinate:
    latitude = options.required_float("lat")
    longitude = options.required_float("lon")
    try:
        return Coordinate(latitude, longitude)
    except WireError as error:
        raise UsageError(error.detail) from None


def run_pair(arguments: Sequence[str]) -> None:
    options = Options(arguments, ["keychain", "redact"])
    context = _make_context(options)
    name = options.required("name")
    kinds = _parse_kinds(options.required("kinds"))
    label = options.optional("label")
    code_file = options.optional("code-file")
    fingerprint = options.optional("fingerprint")
    fingerprint = fingerprint.lower() if fingerprint is not None else None
    where = _target(options)
    options.finish()

    if fingerprint is not None and not protocol.is_fingerprint(fingerprint):
        raise UsageError("--fingerprint must be 64 hexadecimal digits")
    if label is not None and any(pairing.label == label for pairing in context.store.pairings()):
        raise UsageError("a receiver is already paired under that label. Choose another, or use 'erdigo-send forget --to LABEL' first.")
    text = read_code_file(code_file) if code_file is not None else _read_code_from_keyboard()
    try:
        ticket = PairingTicket.parse(text)
    except WireError as error:
        raise UsageError(error.detail) from None
    if fingerprint is not None:
        if ticket.fingerprint is not None and ticket.fingerprint != fingerprint:
            raise ErdigoError(ErrorKind.FINGERPRINT_MISMATCH)
        ticket = PairingTicket(ticket.code, fingerprint)
    if ticket.fingerprint is None and context.redact:
        raise UsageError(
            "--redact hides the receiver's key, so it cannot be compared for a bare code. Use the QR text, or give the full 64-digit key with --fingerprint."
        )
    # Only a person can compare the key. Whatever a pipe or a file holds was written before the key existed, so it is
    # no answer, and a question sent to a file would be answered unseen. The receiver is not contacted when no answer
    # is possible.
    if ticket.fingerprint is None and not terminal.person_is_there():
        raise UsageError(
            "a bare code needs a person to compare the receiver's key, and standard input or output is not a terminal. "
            "Use the QR text, or give the full 64-digit key with --fingerprint."
        )

    waiting = "Waiting for the request to be confirmed on the receiver..."
    person_compares = ticket.fingerprint is None
    print("Checking the code with the receiver..." if person_compares else waiting, flush=True)
    identity = context.store.load_or_create_identity()

    def confirm(key: str) -> bool:
        # Only an answer given after seeing the key counts, so anything typed before it is discarded. Standard input
        # is a terminal here, because a bare code is refused otherwise.
        terminal.discard_typed_ahead()
        print(f"Receiver key: {key}")
        print("If this matches the key shown on the receiver, type yes: ", end="", flush=True)
        answer = terminal.read_answer(256)
        if answer is None or answer.strip().lower() != "yes":
            print()
            return False
        print(waiting, flush=True)
        return True

    pairing = pair(where, ticket, identity, name, kinds, label, confirm_key=confirm)
    context.store.save(pairing)
    print(f"Paired as '{context.shown(pairing.label)}'. Allowed: {', '.join(kind.value for kind in pairing.kinds)}.")
    if not person_compares:
        print(f"Receiver key: {context.shown(protocol.short_fingerprint(pairing.fingerprint))}")


def run_list(arguments: Sequence[str]) -> None:
    options = Options(arguments, ["keychain", "redact"])
    context = _make_context(options)
    options.finish()
    pairings = context.store.pairings()
    if not pairings:
        print("No receivers are paired.")
    for pairing in pairings:
        kinds = ",".join(kind.value for kind in pairing.kinds)
        print(f"{context.shown(pairing.label)}  key {context.shown(protocol.short_fingerprint(pairing.fingerprint))}  allowed {kinds}")


def run_forget(arguments: Sequence[str]) -> None:
    options = Options(arguments, ["keychain", "redact"])
    context = _make_context(options)
    label = options.required("to")
    options.finish()
    pairing = _select_pairing(context, label)
    context.store.remove_pairing(pairing.fingerprint)
    print("Forgotten here. To remove this device on the receiver, remove it from the list of connected devices there.")


def run_identity(arguments: Sequence[str]) -> None:
    options = Options(arguments, ["keychain", "redact"])
    context = _make_context(options)
    options.finish()
    identity = context.store.load_or_create_identity()
    print(context.shown(identity.fingerprint))


def run_write(arguments: Sequence[str], prepare: Callable[[Options], Callable[[ErdigoClient], None]], extra_flags: Sequence[str] = ()) -> None:
    """Everything common to the writing commands. ``prepare`` reads its own options and returns the work to do."""
    options = Options(arguments, list(extra_flags) + ["keychain", "redact"])
    context = _make_context(options)
    label = options.optional("to")
    where = _target(options)
    retry = RetryPolicy.up_to(30)
    text = options.optional("retry-for")
    if text is not None:
        seconds = _parse_float(text)
        if seconds is None or seconds < 0 or seconds > 1e9:
            raise UsageError("--retry-for must be a number of seconds")
        retry = RetryPolicy.NONE if seconds == 0 else RetryPolicy.up_to(seconds)
    body = prepare(options)
    options.finish()

    pairing = _select_pairing(context, label)
    identity = context.store.load_or_create_identity()
    with ErdigoClient(identity, pairing, context.store, where, retry) as client:
        body(client)
    print("OK")


def run_serve(arguments: Sequence[str]) -> None:
    raise UsageError(
        "this edition has no mock receiver. Run the one from the .NET or Swift SDK in this repository, which this tool works with: "
        "'dotnet run --project src/erdigo-send -- serve --mock' in windows/, or 'swift run erdigo-send serve --mock'."
    )


def _prepare_pin(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    at = _parse_coordinate(options)
    title = options.optional("title")
    return lambda client: client.put_pin(item_id, at, title)


def _prepare_marker(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    at = _parse_coordinate(options)
    title = options.optional("title")
    color = None
    text = options.optional("color")
    if text is not None:
        color = MarkerColor.from_wire(text)
        if color is None:
            raise UsageError(f"--color must be one of: {', '.join(each.value for each in MarkerColor)}")
    return lambda client: client.put_marker(item_id, at, title, color)


def _prepare_note(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    at = _parse_coordinate(options)
    inline = options.optional("text")
    path = options.optional("text-file")
    if (inline is None) == (path is None):
        raise UsageError("give either --text or --text-file")
    # A note file holds the note as UTF-8, with nothing added: no byte order mark, and no trailing newline unless it
    # belongs to the note.
    text = inline if inline is not None else _decode_utf8(read_at_most(path, LIMITS_V1.note_bytes, "note", NOTE_TOO_LARGE), "note")
    return lambda client: client.put_note(item_id, at, text)


def _prepare_photo(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    at = _parse_coordinate(options)
    title = options.optional("title")
    # The format is checked by the SDK when the photo is sent, before it connects.
    data = read_at_most(options.required("file"), LIMITS_V1.photo_bytes, "photo", PHOTO_TOO_LARGE)
    return lambda client: client.put_photo(item_id, at, data, title)


def _prepare_track_add(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    at = _parse_coordinate(options)
    title = options.optional("title")
    timestamp = None
    text = options.optional("ts")
    if text is not None:
        stripped = text
        digits = stripped[1:] if stripped[:1] in "+-" else stripped
        if not digits or not digits.isascii() or not digits.isdigit() or not -(2**63) <= int(stripped) < 2**63:
            raise UsageError("--ts must be whole milliseconds")
        timestamp = int(stripped)
    altitude = None
    text = options.optional("alt")
    if text is not None:
        altitude = _parse_float(text)
        if altitude is None:
            raise UsageError("--alt must be a number")
    try:
        point = TrackPoint(at, timestamp, altitude)
    except WireError as error:
        raise UsageError(error.detail) from None
    return lambda client: client.append_track(item_id, [point], title)


def _prepare_track_finish(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    return lambda client: client.finish_track(item_id)


def _prepare_move(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    at = _parse_coordinate(options)
    return lambda client: client.move(item_id, at)


def _prepare_delete(options: Options) -> Callable[[ErdigoClient], None]:
    item_id = options.required("id")
    return lambda client: client.delete(item_id)


def _prepare_delete_all(options: Options) -> Callable[[ErdigoClient], None]:
    if not options.flag("yes"):
        raise UsageError("this deletes every item this device put on the receiver. Add --yes to confirm.")
    return lambda client: client.delete_all()


def run_command(command: str, arguments: Sequence[str]) -> None:
    simple = {"pair": run_pair, "list": run_list, "forget": run_forget, "identity": run_identity, "serve": run_serve}
    if command in simple:
        simple[command](arguments)
        return
    writers = {
        "ping": lambda options: (lambda client: client.ping()),
        "pin": _prepare_pin,
        "marker": _prepare_marker,
        "note": _prepare_note,
        "photo": _prepare_photo,
        "track-add": _prepare_track_add,
        "track-finish": _prepare_track_finish,
        "move": _prepare_move,
        "delete": _prepare_delete,
    }
    if command in writers:
        run_write(arguments, writers[command])
        return
    if command == "delete-all":
        run_write(arguments, _prepare_delete_all, ["yes"])
        return
    print(USAGE)
    raise UsageError("unknown command")


def main(argv: Optional[Sequence[str]] = None) -> int:
    arguments = list(sys.argv[1:] if argv is None else argv)
    if not arguments or arguments[0] in ("help", "--help", "-h"):
        print(USAGE)
        return 0
    try:
        run_command(arguments[0], arguments[1:])
        return 0
    except (UsageError, ErdigoError, WireError, OSError) as error:
        print(f"erdigo-send: {error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("erdigo-send: interrupted", file=sys.stderr)
        return 130


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
