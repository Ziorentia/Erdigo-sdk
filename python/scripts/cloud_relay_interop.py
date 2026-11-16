"""Runs the cloud relay example end to end against another edition's mock receiver.

Usage, from the python folder, with the mock's command after ``--``:

    python scripts/cloud_relay_interop.py --cwd ../dotnet -- dotnet run --no-build -c Release --project src/erdigo-send --
    python scripts/cloud_relay_interop.py --cwd .. -- swift run erdigo-send

It starts examples/cloud_relay/relay.py on a free loopback port and the mock with a code file, pairs the gateway with
the QR text from that file, sends every kind of operation to the relay with a source token, runs the gateway with
--once, and checks with the mock's own ``items`` command what it holds. Then: a second run applies nothing; a gateway
paired for pins only refuses the rest with not_permitted; a restarted relay is followed; a wrong token, a plain http://
address that is not loopback and a missing token are refused; and with the mock stopped, --once gives up with exit code
3 after about 30 seconds and the operations stay on the relay, while at the same time another run given a relay address
where nothing listens gives up with exit code 3 in about 30 seconds too.

It exits 0 only if every step passed. Each step is printed, so a CI log shows how far it got.
"""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request
from pathlib import Path
from typing import List, Optional

from interop import JPEG, Mock, png

EXAMPLES = Path(__file__).resolve().parents[1] / "examples" / "cloud_relay"


class Relay:
    def __init__(self, work: Path, source: str, gateway: str, listen: str = "127.0.0.1:0") -> None:
        self.source = source
        self.gateway = gateway
        self.log = work / f"relay-{secrets.token_hex(4)}.log"
        environment = dict(os.environ, ERDIGO_RELAY_SOURCE_TOKENS=source, ERDIGO_RELAY_GATEWAY_TOKEN=gateway, PYTHONUNBUFFERED="1")
        self._log_file = open(self.log, "w", encoding="utf-8")
        self.process = subprocess.Popen(
            [sys.executable, str(EXAMPLES / "relay.py"), "--listen", listen],
            env=environment,
            stdout=subprocess.PIPE,
            stderr=self._log_file,
            text=True,
            encoding="utf-8",
        )
        line = self.process.stdout.readline()  # type: ignore[union-attr]
        found = re.fullmatch(r"Relay listening on (http://127\.0\.0\.1:(\d+))\n", line)
        if not found:
            raise AssertionError(f"the relay did not say where it listens: {line!r}")
        self.url = found.group(1)
        self.port = int(found.group(2))
        print(f"  relay listening on {self.url}")

    def post(self, *ops: dict) -> dict:
        request = urllib.request.Request(
            f"{self.url}/v1/ops",
            data=json.dumps({"ops": list(ops)}).encode("utf-8"),
            headers={"Authorization": f"Bearer {self.source}", "Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(request, timeout=30) as response:
            answer = json.loads(response.read())
        print(f"  relay took {answer['accepted']} operations, up to {answer['last_seq']}")
        return answer

    def held(self) -> List[int]:
        """The sequence numbers waiting, without making the relay forget any."""
        request = urllib.request.Request(f"{self.url}/v1/ops", headers={"Authorization": f"Bearer {self.gateway}"})
        with urllib.request.urlopen(request, timeout=30) as response:
            return [entry["seq"] for entry in json.loads(response.read())["ops"]]

    def stop(self) -> None:
        self.process.terminate()
        try:
            self.process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.process.kill()
        self._log_file.close()
        text = self.log.read_text(encoding="utf-8")
        for secret in (self.source, self.gateway):
            if secret in text:
                raise AssertionError("the relay's log holds a token")
        print(f"  relay stopped after {len([line for line in text.splitlines() if ' /v1/' in line])} logged requests")


class GatewayRun:
    """One run of the gateway, started at once. finish() waits for it and checks how it ended."""

    def __init__(self, *arguments: str, token: Optional[str]) -> None:
        print(f"  gateway {' '.join(arguments)}")
        self.arguments = arguments
        self.token = token
        environment = dict(os.environ, PYTHONUNBUFFERED="1")
        environment.pop("ERDIGO_RELAY_TOKEN", None)
        if token is not None:
            environment["ERDIGO_RELAY_TOKEN"] = token
        self.started = time.monotonic()
        self.process = subprocess.Popen(
            [sys.executable, str(EXAMPLES / "gateway.py"), *arguments],
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
        )
        self._ended: List[subprocess.CompletedProcess] = []
        #: Seconds from the start to the end of the run, measured as it ends, even while another run is waited for.
        self.elapsed = 0.0
        self._waiter = threading.Thread(target=self._wait, daemon=True)
        self._waiter.start()

    def _wait(self) -> None:
        stdout, stderr = self.process.communicate()
        self.elapsed = time.monotonic() - self.started
        self._ended.append(subprocess.CompletedProcess(self.process.args, self.process.returncode, stdout, stderr))

    def finish(self, expect: Optional[int] = 0, timeout: float = 180, again: bool = False) -> subprocess.CompletedProcess:
        self._waiter.join(max(0.0, self.started + timeout - time.monotonic()))
        if not self._ended:
            self.process.kill()
            self._waiter.join()
            raise AssertionError(f"the gateway did not end within {timeout:.0f} seconds")
        result = self._ended[0]
        if again:
            # Another run printed in between, so say again which one this is.
            print(f"  gateway {' '.join(self.arguments)}")
        for line in (result.stdout + result.stderr).splitlines():
            print(f"    {line}")
        print(f"    (exit code {result.returncode} after {self.elapsed:.1f} s)")
        if expect is not None and result.returncode != expect:
            raise AssertionError(f"the gateway exited with {result.returncode}, not {expect}")
        if self.token is not None and self.token in result.stdout + result.stderr:
            raise AssertionError("the gateway printed its token")
        return result


def gateway(*arguments: str, token: Optional[str], expect: Optional[int] = 0, timeout: float = 180) -> subprocess.CompletedProcess:
    return GatewayRun(*arguments, token=token).finish(expect, timeout)


def free_port() -> int:
    """A loopback port nothing listens on."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def items(mock: Mock, name: str) -> List[str]:
    """What the mock holds from the device paired under this name. The mock numbers its devices in its own order."""
    mock.drain(0.5)
    mock.command("devices")
    numbers = {found.group(2): found.group(1) for found in (re.match(r"^(\d+)\. '(.*)' ", line) for line in mock.drain(2.0)) if found}
    if name not in numbers:
        raise AssertionError(f"the mock does not list a device named {name!r}")
    mock.command(f"items {numbers[name]}")
    return [line for line in mock.drain(2.0) if " at " in line]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--cwd", required=True, help="where to run the mock's command")
    parser.add_argument("--startup-timeout", type=float, default=600.0, help="how long the mock may take to start")
    parser.add_argument("mock", nargs=argparse.REMAINDER, help="the mock's command, after --")
    options = parser.parse_args()
    command = options.mock[1:] if options.mock[:1] == ["--"] else options.mock
    if not command:
        parser.error("give the mock's command after --")

    work = Path(tempfile.mkdtemp(prefix="erdigo-cloud-relay-"))
    os.chmod(work, 0o700)
    code_file = work / "pairing.txt"
    state = work / "gateway"
    source, token = secrets.token_urlsafe(32), secrets.token_urlsafe(32)
    print("Starting the relay")
    relay = Relay(work, source, token)
    print(f"Starting the mock: {' '.join(command)}")
    mock = Mock(command, options.cwd, code_file)
    mock_running = True
    try:
        port = int(mock.wait_for(r"Listening on 127\.0\.0\.1, port (\d+)", options.startup_timeout).group(1))
        mock_key = mock.wait_for(r"Receiver key: ([0-9a-f-]{19})").group(1)
        mock.wait_for(r"Pairing text written to the code file")
        address = ["--host", "127.0.0.1", "--port", str(port)]

        print("Pairing the gateway with the QR text from the mock's code file")
        paired = gateway("pair", "--name", "Cloud gateway", "--code-file", str(code_file), *address, "--state-dir", str(state), token=None)
        if f"Receiver key: {mock_key}" not in paired.stdout or "Allowed: pin, marker, note, photo, track." not in paired.stdout:
            raise AssertionError("pairing did not report the mock's key and every kind")
        mock.wait_for(r"\[mock\] paired ")

        print("Sending every kind of operation to the relay")
        big_png = png(160, 120, seed=1)
        if len(big_png) <= 48000:
            raise AssertionError("the large PNG should need several chunks")
        relay.post(
            {"op": "pin", "id": "city-hall", "lat": 37.5663, "lon": 126.9779, "title": "Seoul City Hall"},
            {"op": "marker", "id": "tower", "lat": 37.5512, "lon": 126.9882, "title": "Tower", "color": "blue"},
            {"op": "note", "id": "memo", "lat": 37.57, "lon": 126.98, "text": "First line\nSecond line, with a link https://example.com"},
            {"op": "photo", "id": "view", "lat": 37.5665, "lon": 126.978, "type": "image/png", "data": base64.b64encode(big_png).decode(), "title": "View"},
            {"op": "photo", "id": "small", "lat": 37.5666, "lon": 126.9781, "type": "image/jpeg", "data": base64.b64encode(JPEG).decode()},
            {"op": "track", "id": "walk", "title": "Morning walk", "points": [
                {"lat": 37.5663, "lon": 126.9779, "ts": 1700000000000, "alt": 38.5},
                {"lat": 37.5665, "lon": 126.9782, "ts": 1700000005000},
            ]},
            {"op": "finish_track", "id": "walk"},
            {"op": "move", "id": "city-hall", "lat": 37.5664, "lon": 126.978},
            {"op": "pin", "id": "temporary", "lat": 1, "lon": 2},
            {"op": "delete", "id": "temporary"},
        )
        run = ["run", "--relay", relay.url, *address, "--once", "--state-dir", str(state)]
        result = gateway(*run, token=token)
        if result.stdout.splitlines() != ["Done with 10 operations, up to 10"]:
            raise AssertionError("the gateway did not report the ten operations")

        print("Checking what the mock holds")
        held = items(mock, "Cloud gateway")
        expected = {
            "pin city-hall at 37.5664,126.978 title=Seoul City Hall points=0",
            "marker tower at 37.5512,126.9882 title=Tower points=0",
            "note memo at 37.57,126.98 title=- points=0",
            "photo view at 37.5665,126.978 title=View points=0",
            "photo small at 37.5666,126.9781 title=- points=0",
            "track walk at -,- title=Morning walk points=2",
        }
        if set(held) != expected:
            raise AssertionError(f"the mock holds {sorted(held)}, not {sorted(expected)}")
        if relay.held():
            raise AssertionError("the relay still holds operations the gateway finished")

        print("Running again applies nothing twice")
        mock.drain(0.5)
        result = gateway(*run, token=token)
        if result.stdout.strip():
            raise AssertionError("a second run did something")
        if any(re.match(r"\[mock\] #\d+ ", line) for line in mock.drain(1.0)):
            raise AssertionError("a second run wrote to the mock")

        print("A gateway paired for pins only refuses the rest")
        mock.command("code")
        mock.wait_for(r"Pairing text written to the code file")
        pins_only = work / "pins-only"
        gateway("pair", "--name", "Pins only", "--kinds", "pin", "--code-file", str(code_file), *address, "--state-dir", str(pins_only), token=None)
        relay.post({"op": "marker", "id": "refused", "lat": 1, "lon": 2}, {"op": "pin", "id": "taken", "lat": 3.5, "lon": 4.5})
        result = gateway("run", "--relay", relay.url, *address, "--once", "--state-dir", str(pins_only), token=token)
        if result.stdout.splitlines() != ["Refused operation 11 (marker): not_permitted", "Done with 2 operations, up to 12 (1 refused)"]:
            raise AssertionError("the marker was not refused with not_permitted")
        if items(mock, "Pins only") != ["pin taken at 3.5,4.5 title=- points=0"]:
            raise AssertionError("the pin did not reach the mock")

        print("A relay that starts again is followed")
        relay.stop()
        relay = Relay(work, source, token, f"127.0.0.1:{relay.port}")
        relay.post({"op": "pin", "id": "after-restart", "lat": 5.5, "lon": 6.5})
        result = gateway(*run, token=token)
        if result.stdout.splitlines() != ["The relay started a new numbering; starting from its oldest operation.", "Done with 1 operation, up to 1"]:
            raise AssertionError("the new numbering was not followed")
        if "pin after-restart at 5.5,6.5 title=- points=0" not in items(mock, "Cloud gateway"):
            raise AssertionError("the pin sent after the restart did not reach the mock")

        print("Mistakes are refused")
        if "refused the token" not in gateway(*run, token=secrets.token_urlsafe(32), expect=2).stderr:
            raise AssertionError("a wrong token was not refused")
        gateway("run", "--relay", f"http://relay.example.com:{relay.port}", *address, "--once", "--state-dir", str(state), token=token, expect=1)
        gateway(*run, token=None, expect=1)

        print("With the mock stopped, --once gives up and the operations stay on the relay. At the same time, with a relay")
        print("address where nothing listens, --once gives up too")
        mock.stop()
        mock_running = False
        relay.post({"op": "pin", "id": "waiting", "lat": 7, "lon": 8})
        no_relay = GatewayRun("run", "--relay", f"http://127.0.0.1:{free_port()}", *address, "--once", "--state-dir", str(pins_only), token=token)
        no_app = GatewayRun(*run, token=token)
        result = no_app.finish(expect=3, again=True)
        if not 25 <= no_app.elapsed <= 90 or "The Erdigo app could not be reached. The operations stay on the relay." not in result.stdout:
            raise AssertionError("the gateway did not give up after about 30 seconds")
        if relay.held() != [2]:
            raise AssertionError("the operation did not stay on the relay")
        result = no_relay.finish(expect=3, again=True)
        if not 25 <= no_relay.elapsed <= 90 or result.stdout.splitlines()[-1:] != ["The relay could not be reached."]:
            raise AssertionError("the gateway did not give up on the relay after about 30 seconds")

        print("Cloud relay interop passed.")
        return 0
    finally:
        if mock_running:
            mock.stop()
        relay.stop()


if __name__ == "__main__":
    sys.exit(main())
