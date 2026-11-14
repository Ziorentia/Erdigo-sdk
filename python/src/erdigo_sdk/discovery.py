"""Where to find a receiver: a given address, the last one that worked, or the local network.

Looking around the network uses the optional ``zeroconf`` package (``pip install "erdigo-sdk[discovery]"``). Without it,
give an address. An advertisement proves nothing about who is behind it: only the handshake does.
"""

from __future__ import annotations

import ipaddress
import threading
import time
from dataclasses import dataclass
from typing import List, Optional

from . import protocol
from .errors import ErdigoError, ErrorKind

_SERVICE = protocol.SERVICE_TYPE + ".local."


@dataclass(frozen=True)
class ReceiverTarget:
    """Where to look for a receiver. Use :data:`DISCOVER` or :meth:`address`."""

    host: Optional[str] = None
    port: Optional[int] = None

    @classmethod
    def address(cls, host: str, port: int) -> "ReceiverTarget":
        """Try this address only."""
        if not isinstance(host, str) or not host:
            raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "give a host name or address")
        if isinstance(port, bool) or not isinstance(port, int) or not 0 < port < 65536:
            raise ErdigoError(ErrorKind.INVALID_ARGUMENT, "the port must be between 1 and 65535")
        return cls(host, port)

    @property
    def is_discover(self) -> bool:
        return self.host is None


#: Try the remembered address, then up to eight receivers advertised on the local network. A receiver this device is
#: not paired with stays silent. An advertisement proves nothing about who is behind it, so use
#: :meth:`ReceiverTarget.address` where that matters. See the threat model.
DISCOVER = ReceiverTarget()


@dataclass(frozen=True)
class Endpoint:
    host: str
    port: int


def known_endpoints(target: ReceiverTarget, last_host: Optional[str] = None, last_port: Optional[int] = None) -> List[Endpoint]:
    if not target.is_discover:
        return [Endpoint(target.host, target.port)]  # type: ignore[arg-type]
    if last_host is not None and last_port is not None:
        return [Endpoint(last_host, last_port)]
    return []


def discovery_available() -> bool:
    try:
        import zeroconf  # noqa: F401
    except ImportError:
        return False
    return True


def _local_networks() -> list:
    """The networks of this computer's interfaces, so that an answer from beyond them can be ignored."""
    import ifaddr  # A dependency of zeroconf.

    networks = []
    for adapter in ifaddr.get_adapters():
        for entry in adapter.ips:
            address = entry.ip[0] if isinstance(entry.ip, tuple) else entry.ip
            try:
                networks.append(ipaddress.ip_network(f"{address.split('%')[0]}/{entry.network_prefix}", strict=False))
            except ValueError:
                continue
    return networks


def is_local(address: str, networks: Optional[list] = None) -> bool:
    try:
        parsed = ipaddress.ip_address(address.split("%", 1)[0])
    except ValueError:
        return False
    if isinstance(parsed, ipaddress.IPv6Address) and parsed.ipv4_mapped is not None:
        parsed = parsed.ipv4_mapped
    if parsed.is_loopback or parsed.is_link_local:
        return True
    return any(parsed.version == network.version and parsed in network for network in (networks if networks is not None else _local_networks()))


def browse(duration: float = 2.0, limit: int = 8, deadline: Optional[float] = None) -> List[Endpoint]:
    """Listens for receivers for a short time, and returns up to ``limit`` of them. Returns nothing if the optional
    ``zeroconf`` package is not installed."""
    try:
        from zeroconf import ServiceBrowser, ServiceListener, Zeroconf
    except ImportError:
        return []
    if deadline is not None:
        duration = min(duration, max(0.0, deadline - time.monotonic()))

    names: List[str] = []
    lock = threading.Lock()

    class Listener(ServiceListener):
        def add_service(self, zc: "Zeroconf", type_: str, name: str) -> None:
            with lock:
                if len(names) < limit and name not in names:
                    names.append(name)

        def update_service(self, zc: "Zeroconf", type_: str, name: str) -> None:
            pass

        def remove_service(self, zc: "Zeroconf", type_: str, name: str) -> None:
            pass

    zeroconf = Zeroconf()
    try:
        browser = ServiceBrowser(zeroconf, _SERVICE, Listener())
        time.sleep(duration)
        browser.cancel()
        networks = _local_networks()
        endpoints: List[Endpoint] = []
        with lock:
            found = list(names)
        for name in found:
            info = zeroconf.get_service_info(_SERVICE, name, timeout=1000)
            if info is None or not info.port:
                continue
            addresses = sorted(info.parsed_addresses(), key=lambda text: ":" in text)
            for address in addresses:
                if is_local(address, networks):
                    endpoints.append(Endpoint(address.split("%", 1)[0], info.port))
                    break
        return endpoints
    finally:
        zeroconf.close()


def candidates(target: ReceiverTarget, deadline: Optional[float] = None) -> List[Endpoint]:
    """Every endpoint to try when pairing, where no address is remembered."""
    if not target.is_discover:
        return known_endpoints(target)
    if not discovery_available():
        raise ErdigoError(
            ErrorKind.INVALID_ARGUMENT,
            "finding a receiver on the network needs the optional zeroconf package (pip install \"erdigo-sdk[discovery]\"). "
            "Otherwise give the receiver's address.",
        )
    return browse(deadline=deadline)
