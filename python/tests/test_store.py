"""The file store: a private folder, a key that is never replaced, and counters that only grow."""

from __future__ import annotations

import os
import stat
import sys

import pytest

from erdigo_sdk import ErdigoError, ErrorKind, FileDeviceStore, ItemKind, ReceiverPairing, StorageProblem, noise
from erdigo_sdk.store import default_directory

POSIX = os.name == "posix"
WINDOWS = sys.platform == "win32"
KEY_FILE = "identity.dpapi" if WINDOWS else "identity.key"


def _pairing(label: str = "home") -> ReceiverPairing:
    return ReceiverPairing(label, noise.public_key(noise.new_private_key()), (ItemKind.PIN, ItemKind.TRACK), "127.0.0.1", 4000, counter_floor=7)


def test_the_key_is_made_once_and_read_back(tmp_path) -> None:
    folder = tmp_path / "state"
    first = FileDeviceStore(str(folder)).load_or_create_identity()
    second = FileDeviceStore(str(folder)).load_or_create_identity()
    assert first.private_key == second.private_key
    assert (folder / KEY_FILE).exists()


@pytest.mark.skipif(not POSIX, reason="file modes")
def test_folder_and_files_are_private(tmp_path) -> None:
    folder = tmp_path / "state"
    store = FileDeviceStore(str(folder))
    store.load_or_create_identity()
    store.save(_pairing())
    assert stat.S_IMODE(os.stat(folder).st_mode) == 0o700
    for name in ("identity.key", "pairings.json"):
        assert stat.S_IMODE(os.stat(folder / name).st_mode) == 0o600


@pytest.mark.skipif(not POSIX, reason="file modes")
def test_a_folder_others_can_read_is_refused(tmp_path) -> None:
    folder = tmp_path / "state"
    folder.mkdir()
    os.chmod(folder, 0o755)
    with pytest.raises(ErdigoError) as raised:
        FileDeviceStore(str(folder))
    assert raised.value.storage_problem is StorageProblem.INSECURE


@pytest.mark.skipif(not POSIX, reason="file modes")
def test_a_key_file_others_can_read_is_refused_and_left_alone(tmp_path) -> None:
    folder = tmp_path / "state"
    store = FileDeviceStore(str(folder))
    store.load_or_create_identity()
    key = folder / "identity.key"
    before = key.read_bytes()
    os.chmod(key, 0o644)
    with pytest.raises(ErdigoError) as raised:
        store.load_or_create_identity()
    assert raised.value.storage_problem is StorageProblem.INSECURE
    assert key.read_bytes() == before


@pytest.mark.skipif(not POSIX, reason="symbolic links")
def test_a_linked_key_file_is_refused(tmp_path) -> None:
    folder = tmp_path / "state"
    store = FileDeviceStore(str(folder))
    target = tmp_path / "elsewhere"
    target.write_bytes(b"EDK0" + bytes(32))
    os.chmod(target, 0o600)
    os.symlink(target, folder / "identity.key")
    with pytest.raises(ErdigoError) as raised:
        store.load_or_create_identity()
    assert raised.value.storage_problem is StorageProblem.INSECURE


def test_a_damaged_key_is_reported_and_never_replaced(tmp_path) -> None:
    folder = tmp_path / "state"
    store = FileDeviceStore(str(folder))
    store.load_or_create_identity()
    key = folder / KEY_FILE
    key.write_bytes(b"not a key")
    with pytest.raises(ErdigoError) as raised:
        store.load_or_create_identity()
    assert raised.value.kind is ErrorKind.STORAGE
    assert raised.value.storage_problem is StorageProblem.IDENTITY_DAMAGED
    assert key.read_bytes() == b"not a key"


def test_pairings_and_counters_survive_a_restart(tmp_path) -> None:
    folder = str(tmp_path / "state")
    store = FileDeviceStore(folder)
    pairing = _pairing()
    store.save(pairing)
    # The counter floor from the receiver takes effect when the pairing is saved.
    assert store.next_connect_counter(pairing.fingerprint, 1) == 8
    store.update_address(pairing.fingerprint, "192.168.1.20", 5000)
    again = FileDeviceStore(folder)
    [loaded] = again.pairings()
    assert loaded.receiver_key == pairing.receiver_key and loaded.kinds == pairing.kinds and loaded.label == "home"
    assert (loaded.last_host, loaded.last_port) == ("192.168.1.20", 5000)
    assert loaded.paired_at.replace(microsecond=0) == pairing.paired_at.replace(microsecond=0)
    assert again.next_connect_counter(pairing.fingerprint, 1) == 9
    assert again.next_connect_counter(pairing.fingerprint, 1_700_000_000_000) == 1_700_000_000_000
    again.remove_pairing(pairing.fingerprint)
    assert FileDeviceStore(folder).pairings() == []


def test_a_damaged_pairing_file_is_reported(tmp_path) -> None:
    folder = tmp_path / "state"
    store = FileDeviceStore(str(folder))
    store.save(_pairing())
    (folder / "pairings.json").write_bytes(b'{"version": 1, "pairings": [{"receiverKey": "zz"}], "counters": {}}')
    with pytest.raises(ErdigoError) as raised:
        store.pairings()
    assert raised.value.storage_problem is StorageProblem.STATE_DAMAGED


def test_each_profile_has_a_folder_of_its_own_apart_from_the_other_editions() -> None:
    first = default_directory("default")
    second = default_directory("other")
    assert first != second
    assert "ErdigoSDK.Python" in first
    assert "ErdigoSDK.NET" not in first


@pytest.mark.skipif(not WINDOWS, reason="DPAPI and access lists are Windows only")
def test_on_windows_the_key_is_encrypted_and_the_folder_private(tmp_path) -> None:
    from erdigo_sdk import _windows

    folder = tmp_path / "state"
    store = FileDeviceStore(str(folder))
    identity = store.load_or_create_identity()
    stored = (folder / "identity.dpapi").read_bytes()
    assert stored.startswith(b"EDK1")
    assert identity.private_key not in stored
    assert _windows.unprotect(stored[4:]) == identity.private_key
    assert _windows.has_private_access_list(str(folder))
    assert _windows.has_private_access_list(str(folder / "identity.dpapi"))


@pytest.mark.skipif(not WINDOWS, reason="access lists are Windows only")
def test_on_windows_a_folder_everyone_can_read_is_refused(tmp_path) -> None:
    import subprocess

    folder = tmp_path / "shared"
    folder.mkdir()
    subprocess.run(["icacls", str(folder), "/grant", "*S-1-1-0:(OI)(CI)R"], check=True, capture_output=True)
    with pytest.raises(ErdigoError) as raised:
        FileDeviceStore(str(folder))
    assert raised.value.storage_problem is StorageProblem.INSECURE
