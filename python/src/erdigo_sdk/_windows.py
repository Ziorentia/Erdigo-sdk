"""DPAPI and file access lists on Windows, through ctypes. Nothing here is used on other systems."""

from __future__ import annotations

import ctypes
import sys
from ctypes import wintypes

if sys.platform != "win32":  # pragma: no cover - imported on Windows only
    raise ImportError("Windows only")

_crypt32 = ctypes.WinDLL("crypt32", use_last_error=True)
_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
_advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)

_CRYPTPROTECT_UI_FORBIDDEN = 0x1
_TOKEN_QUERY = 0x0008
_TOKEN_USER = 1
_SE_FILE_OBJECT = 1
_OWNER_SECURITY_INFORMATION = 0x1
_DACL_SECURITY_INFORMATION = 0x4
_ACL_SIZE_INFORMATION = 2
_ACCESS_ALLOWED_ACE_TYPE = 0x0
#: Every type of ACE that allows something. Only the plain one names a SID where this code reads it.
_ALLOWED_ACE_TYPES = {0x0, 0x4, 0x5, 0x9, 0xB}
_WIN_LOCAL_SYSTEM_SID = 22
_WIN_BUILTIN_ADMINISTRATORS_SID = 26
_SDDL_REVISION_1 = 1


class _DataBlob(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


class _AceHeader(ctypes.Structure):
    _fields_ = [("AceType", wintypes.BYTE), ("AceFlags", wintypes.BYTE), ("AceSize", wintypes.WORD)]


class _AclSizeInformation(ctypes.Structure):
    _fields_ = [("AceCount", wintypes.DWORD), ("AclBytesInUse", wintypes.DWORD), ("AclBytesFree", wintypes.DWORD)]


class _SecurityAttributes(ctypes.Structure):
    _fields_ = [("nLength", wintypes.DWORD), ("lpSecurityDescriptor", wintypes.LPVOID), ("bInheritHandle", wintypes.BOOL)]


_crypt32.CryptProtectData.argtypes = [
    ctypes.POINTER(_DataBlob), wintypes.LPCWSTR, ctypes.POINTER(_DataBlob), wintypes.LPVOID, wintypes.LPVOID, wintypes.DWORD,
    ctypes.POINTER(_DataBlob),
]
_crypt32.CryptProtectData.restype = wintypes.BOOL
_crypt32.CryptUnprotectData.argtypes = [
    ctypes.POINTER(_DataBlob), ctypes.POINTER(wintypes.LPWSTR), ctypes.POINTER(_DataBlob), wintypes.LPVOID, wintypes.LPVOID,
    wintypes.DWORD, ctypes.POINTER(_DataBlob),
]
_crypt32.CryptUnprotectData.restype = wintypes.BOOL
_kernel32.LocalFree.argtypes = [wintypes.HLOCAL]
_kernel32.LocalFree.restype = wintypes.HLOCAL
_kernel32.GetCurrentProcess.restype = wintypes.HANDLE
_kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
_kernel32.CreateDirectoryW.argtypes = [wintypes.LPCWSTR, ctypes.POINTER(_SecurityAttributes)]
_kernel32.CreateDirectoryW.restype = wintypes.BOOL
_advapi32.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
_advapi32.OpenProcessToken.restype = wintypes.BOOL
_advapi32.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.LPVOID, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
_advapi32.GetTokenInformation.restype = wintypes.BOOL
_advapi32.GetNamedSecurityInfoW.argtypes = [
    wintypes.LPCWSTR, ctypes.c_int, wintypes.DWORD, ctypes.POINTER(wintypes.LPVOID), ctypes.POINTER(wintypes.LPVOID),
    ctypes.POINTER(wintypes.LPVOID), ctypes.POINTER(wintypes.LPVOID), ctypes.POINTER(wintypes.LPVOID),
]
_advapi32.GetNamedSecurityInfoW.restype = wintypes.DWORD
_advapi32.GetAclInformation.argtypes = [wintypes.LPVOID, wintypes.LPVOID, wintypes.DWORD, ctypes.c_int]
_advapi32.GetAclInformation.restype = wintypes.BOOL
_advapi32.GetAce.argtypes = [wintypes.LPVOID, wintypes.DWORD, ctypes.POINTER(wintypes.LPVOID)]
_advapi32.GetAce.restype = wintypes.BOOL
_advapi32.EqualSid.argtypes = [wintypes.LPVOID, wintypes.LPVOID]
_advapi32.EqualSid.restype = wintypes.BOOL
_advapi32.IsWellKnownSid.argtypes = [wintypes.LPVOID, ctypes.c_int]
_advapi32.IsWellKnownSid.restype = wintypes.BOOL
_advapi32.IsValidSid.argtypes = [wintypes.LPVOID]
_advapi32.IsValidSid.restype = wintypes.BOOL
_advapi32.ConvertSidToStringSidW.argtypes = [wintypes.LPVOID, ctypes.POINTER(wintypes.LPWSTR)]
_advapi32.ConvertSidToStringSidW.restype = wintypes.BOOL
_advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, ctypes.POINTER(wintypes.LPVOID), ctypes.POINTER(wintypes.ULONG),
]
_advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW.restype = wintypes.BOOL

_ENTROPY = b"erdigo-integration-v1/device-key"


class DpapiError(OSError):
    """DPAPI refused. The key belongs to another Windows user or computer, or the data is damaged."""


def _blob(data: bytes) -> tuple:
    buffer = ctypes.create_string_buffer(data, len(data))
    return _DataBlob(len(data), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_char))), buffer


def _take(blob: _DataBlob) -> bytes:
    try:
        return ctypes.string_at(blob.pbData, blob.cbData)
    finally:
        _kernel32.LocalFree(ctypes.cast(blob.pbData, wintypes.HLOCAL))


def protect(data: bytes) -> bytes:
    """Encrypts with DPAPI for the current user, with entropy that ties the result to this purpose."""
    source, _source_buffer = _blob(data)
    entropy, _entropy_buffer = _blob(_ENTROPY)
    output = _DataBlob()
    if not _crypt32.CryptProtectData(ctypes.byref(source), None, ctypes.byref(entropy), None, None, _CRYPTPROTECT_UI_FORBIDDEN, ctypes.byref(output)):
        raise DpapiError(ctypes.get_last_error(), "Windows could not encrypt the device key")
    return _take(output)


def unprotect(data: bytes) -> bytes:
    source, _source_buffer = _blob(data)
    entropy, _entropy_buffer = _blob(_ENTROPY)
    output = _DataBlob()
    if not _crypt32.CryptUnprotectData(ctypes.byref(source), None, ctypes.byref(entropy), None, None, _CRYPTPROTECT_UI_FORBIDDEN, ctypes.byref(output)):
        raise DpapiError(ctypes.get_last_error(), "Windows could not decrypt the device key")
    return _take(output)


def _current_user_sid() -> ctypes.Array:
    """The current user's SID, copied into a buffer this module owns."""
    token = wintypes.HANDLE()
    if not _advapi32.OpenProcessToken(_kernel32.GetCurrentProcess(), _TOKEN_QUERY, ctypes.byref(token)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        needed = wintypes.DWORD()
        _advapi32.GetTokenInformation(token, _TOKEN_USER, None, 0, ctypes.byref(needed))
        buffer = ctypes.create_string_buffer(needed.value)
        if not _advapi32.GetTokenInformation(token, _TOKEN_USER, buffer, needed, ctypes.byref(needed)):
            raise ctypes.WinError(ctypes.get_last_error())
        # TOKEN_USER starts with SID_AND_ATTRIBUTES, which starts with a pointer to the SID inside this buffer.
        return buffer
    finally:
        _kernel32.CloseHandle(token)


def _sid_pointer(token_user: ctypes.Array) -> wintypes.LPVOID:
    return ctypes.cast(token_user, ctypes.POINTER(wintypes.LPVOID))[0]


def _current_user_sid_string() -> str:
    token_user = _current_user_sid()
    text = wintypes.LPWSTR()
    if not _advapi32.ConvertSidToStringSidW(_sid_pointer(token_user), ctypes.byref(text)):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return text.value
    finally:
        _kernel32.LocalFree(ctypes.cast(text, wintypes.HLOCAL))


def has_private_access_list(path: str) -> bool:
    """True if nobody but the current user, SYSTEM and the Administrators group is allowed anything on ``path``.

    A missing access list allows everyone, so it is refused. An allowing entry of a kind this code does not read is
    refused too, rather than guessed at.
    """
    token_user = _current_user_sid()
    user = _sid_pointer(token_user)
    owner = wintypes.LPVOID()
    dacl = wintypes.LPVOID()
    descriptor = wintypes.LPVOID()
    status = _advapi32.GetNamedSecurityInfoW(
        path, _SE_FILE_OBJECT, _OWNER_SECURITY_INFORMATION | _DACL_SECURITY_INFORMATION,
        ctypes.byref(owner), None, ctypes.byref(dacl), None, ctypes.byref(descriptor),
    )
    if status != 0:
        raise ctypes.WinError(status)
    try:
        if not dacl.value:
            return False
        information = _AclSizeInformation()
        if not _advapi32.GetAclInformation(dacl, ctypes.byref(information), ctypes.sizeof(information), _ACL_SIZE_INFORMATION):
            raise ctypes.WinError(ctypes.get_last_error())
        for index in range(information.AceCount):
            ace = wintypes.LPVOID()
            if not _advapi32.GetAce(dacl, index, ctypes.byref(ace)):
                raise ctypes.WinError(ctypes.get_last_error())
            header = ctypes.cast(ace, ctypes.POINTER(_AceHeader)).contents
            if header.AceType not in _ALLOWED_ACE_TYPES:
                continue
            if header.AceType != _ACCESS_ALLOWED_ACE_TYPE:
                return False
            # ACCESS_ALLOWED_ACE: the header, a 4-byte mask, then the SID.
            sid = wintypes.LPVOID(ace.value + ctypes.sizeof(_AceHeader) + 4)
            if not _advapi32.IsValidSid(sid):
                return False
            trusted = (
                _advapi32.EqualSid(sid, user)
                or _advapi32.IsWellKnownSid(sid, _WIN_LOCAL_SYSTEM_SID)
                or _advapi32.IsWellKnownSid(sid, _WIN_BUILTIN_ADMINISTRATORS_SID)
            )
            if not trusted:
                return False
        return True
    finally:
        _kernel32.LocalFree(ctypes.cast(descriptor, wintypes.HLOCAL))


def create_private_directory(path: str) -> None:
    """Creates a folder whose access list is protected from inheritance and grants the current user alone. Files made
    in it inherit that list. Raises FileExistsError if it is already there."""
    sddl = f"D:P(A;OICI;FA;;;{_current_user_sid_string()})"
    descriptor = wintypes.LPVOID()
    if not _advapi32.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, _SDDL_REVISION_1, ctypes.byref(descriptor), None):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        attributes = _SecurityAttributes(ctypes.sizeof(_SecurityAttributes), descriptor, False)
        if not _kernel32.CreateDirectoryW(path, ctypes.byref(attributes)):
            error = ctypes.get_last_error()
            if error == 183:  # ERROR_ALREADY_EXISTS
                raise FileExistsError(path)
            raise ctypes.WinError(error)
    finally:
        _kernel32.LocalFree(ctypes.cast(descriptor, wintypes.HLOCAL))
