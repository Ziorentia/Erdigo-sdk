"""Helpers for the tests of the cloud relay and gateway examples in examples/cloud_relay."""

from __future__ import annotations

import base64
import http.client
import importlib.util
import json
import secrets
import socket
import sys
import threading
from dataclasses import dataclass
from types import ModuleType
from typing import Dict, List, Optional
from urllib.parse import urlencode

from .support import PYTHON_ROOT, REPOSITORY

EXAMPLES = PYTHON_ROOT / "examples" / "cloud_relay"
CASES = json.loads((REPOSITORY / "docs" / "cloud-relay-cases.json").read_text(encoding="utf-8"))


def load(name: str) -> ModuleType:
    """Imports one of the example programs, which are not part of the installed package."""
    module_name = f"erdigo_cloud_relay_example_{name}"
    if module_name not in sys.modules:
        spec = importlib.util.spec_from_file_location(module_name, EXAMPLES / f"{name}.py")
        assert spec is not None and spec.loader is not None
        module = importlib.util.module_from_spec(spec)
        sys.modules[module_name] = module
        spec.loader.exec_module(module)
    return sys.modules[module_name]


relay = load("relay")

PNG_1X1_BASE64 = next(case["op"]["data"] for case in CASES["valid_ops"] if case["name"] == "photo, a 1x1 PNG")
PNG_1X1 = base64.b64decode(PNG_1X1_BASE64)


def new_token() -> str:
    return secrets.token_urlsafe(32)


class FakeClock:
    """A clock the tests move forward, so that expiry needs no waiting."""

    def __init__(self) -> None:
        self.now = 1000.0

    def monotonic(self) -> float:
        return self.now

    def wall_ms(self) -> int:
        return 1_700_000_000_000 + int(self.now * 1000)

    def advance(self, seconds: float) -> None:
        self.now += seconds


@dataclass
class Reply:
    status: int
    headers: Dict[str, str]
    body: bytes

    @property
    def json(self) -> dict:
        return json.loads(self.body.decode("utf-8"))


class RunningRelay:
    """The example relay, in this process, on a free loopback port."""

    def __init__(
        self,
        limits: Optional[object] = None,
        clock: Optional[object] = None,
        keep_seconds: float = 24 * 3600.0,
        port: int = 0,
        tls: Optional[object] = None,
        client_tls: Optional[object] = None,
        source: Optional[str] = None,
        gateway: Optional[str] = None,
    ) -> None:
        self.source = source or new_token()
        self.gateway = gateway or new_token()
        self.queue = relay.OpsQueue(keep_seconds, limits or relay.Limits(), clock)
        self.logs: List[str] = []
        self.server = relay.RelayServer(("127.0.0.1", port), socket.AF_INET, self.queue, relay.Tokens([self.source], self.gateway), self.logs.append)
        if tls is not None:
            self.server.socket = tls.wrap_socket(self.server.socket, server_side=True)  # type: ignore[attr-defined]
        self.client_tls = client_tls
        self.port = self.server.server_address[1]
        self.url = self.server.url
        self._thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
        self._thread.start()

    def close(self) -> None:
        self.queue.stop()
        self.server.shutdown()
        self.server.server_close()

    def request(self, method: str, path: str, body: Optional[bytes] = None, headers: Optional[Dict[str, str]] = None, auth: Optional[str] = None) -> Reply:
        if self.client_tls is not None:
            connection: http.client.HTTPConnection = http.client.HTTPSConnection("localhost", self.port, timeout=60, context=self.client_tls)  # type: ignore[arg-type]
        else:
            connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=60)
        try:
            sent = dict(headers or {})
            if auth is not None:
                sent["Authorization"] = f"Bearer {self.source if auth == 'source' else self.gateway if auth == 'gateway' else auth}"
            connection.request(method, path, body=body, headers=sent)
            response = connection.getresponse()
            return Reply(response.status, {name.lower(): value for name, value in response.getheaders()}, response.read())
        finally:
            connection.close()

    def post_body(self, body: bytes, content_type: Optional[str] = "application/json", auth: Optional[str] = "source") -> Reply:
        headers = {} if content_type is None else {"Content-Type": content_type}
        return self.request("POST", "/v1/ops", body, headers, auth)

    def post(self, *ops: dict, auth: Optional[str] = "source") -> Reply:
        return self.post_body(json.dumps({"ops": list(ops)}).encode("utf-8"), auth=auth)

    def fetch(self, auth: Optional[str] = "gateway", **query: object) -> Reply:
        path = "/v1/ops" + (f"?{urlencode(query)}" if query else "")
        return self.request("GET", path, auth=auth)

    def held(self) -> List[int]:
        """The sequence numbers waiting, without making the relay forget any."""
        reply = self.fetch()
        assert reply.status == 200 and reply.json["reset"] is True
        return [entry["seq"] for entry in reply.json["ops"]]


def raw_exchange(port: int, data: bytes, timeout: float = 10.0) -> bytes:
    """Sends bytes as they are and reads until the relay closes the connection or goes quiet."""
    with socket.create_connection(("127.0.0.1", port), timeout=timeout) as connection:
        connection.sendall(data)
        received = b""
        while True:
            try:
                chunk = connection.recv(65536)
            except OSError:
                return received
            if not chunk:
                return received
            received += chunk


def pin(item_id: str = "p1", **fields: object) -> dict:
    return {"op": "pin", "id": item_id, "lat": 37.5663, "lon": 126.9779, **fields}
