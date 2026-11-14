# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Who owns a local TCP port (Windows, standard library only).

Used to check, before the bearer token is sent, that the process behind the port is IceScreen Touch running as the
current Windows user: which processes listen on the port (``GetExtendedTcpTable``), and for a process its image name and
the user it runs as (``OpenProcess``, ``QueryFullProcessImageNameW``, ``OpenProcessToken``, ``GetTokenInformation``,
``EqualSid``).

Off Windows nothing can be asked and both public functions return None (the caller skips the check). **On Windows a
failure is not "cannot tell": it raises** :class:`OwnerCheckError`, so the caller refuses to send the token.
"""

from __future__ import annotations

import socket
import struct
import sys
from dataclasses import dataclass
from typing import Any, Callable, Optional, Set

_AF_INET = 2
_AF_INET6 = 23
_TCP_TABLE_OWNER_PID_LISTENER = 3
_ERROR_INSUFFICIENT_BUFFER = 122
_NO_ERROR = 0

_ROW4_SIZE = 24  # MIB_TCPROW_OWNER_PID
_ROW6_SIZE = 56  # MIB_TCP6ROW_OWNER_PID
_TABLE_ATTEMPTS = 5
_SLACK_ROWS = 64  # the table can grow between the size query and the read

# Addresses a listener that accepts connections to 127.0.0.1 can have, as the little-endian DWORD the table holds.
_LOOPBACK = 0x0100007F  # 127.0.0.1
_ANY = 0  # 0.0.0.0

_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
_TOKEN_QUERY = 0x0008
_TOKEN_USER = 1


class OwnerCheckError(Exception):
    """On Windows: the owner of a port could not be determined. The message never contains a token."""


@dataclass(frozen=True)
class ProcessIdentity:
    """What the owner check needs to know about a process."""

    pid: int
    image: str  # the full path of the executable
    same_user: bool  # runs as the same user as this process


def listening_pids(port: int) -> Optional[Set[int]]:
    """The process ids of the listeners that accept connections to ``127.0.0.1:port``.

    That is every IPv4 listener on 127.0.0.1 or 0.0.0.0, and every IPv6 listener on ``::`` (a dual-stack socket takes
    IPv4 connections too). Returns an empty set when nobody listens. Returns None off Windows (the caller skips the
    check) and raises :class:`OwnerCheckError` on Windows when the table cannot be read.
    """
    if sys.platform != "win32":
        return None
    try:
        return _listening_pids_windows(port)
    except OwnerCheckError:
        raise
    except Exception as exc:  # noqa: BLE001 - any failure of the system call must refuse, not pass
        raise OwnerCheckError(f"the TCP table could not be read ({type(exc).__name__})") from None


def process_identity(pid: int) -> Optional[ProcessIdentity]:
    """The image path of process ``pid`` and whether it runs as the current user.

    Returns None off Windows. On Windows any failure (the process is gone, access is denied, an elevated or protected
    process) raises :class:`OwnerCheckError`.
    """
    if sys.platform != "win32":
        return None
    try:
        return _process_identity_windows(pid)
    except OwnerCheckError:
        raise
    except Exception as exc:  # noqa: BLE001
        raise OwnerCheckError(f"process {pid} could not be inspected ({type(exc).__name__})") from None


# ---- The TCP table ---------------------------------------------------------------------------------------------

def _listening_pids_windows(port: int, get_table: Optional[Callable[..., int]] = None) -> Set[int]:
    owners: Set[int] = set()
    for family in (_AF_INET, _AF_INET6):
        owners |= parse_listener_table(_read_table(family, get_table), port, family)
    return owners


def _read_table(family: int, get_table: Optional[Callable[..., int]] = None) -> bytes:
    """The raw MIB_TCPTABLE_OWNER_PID (or the IPv6 form). ``get_table`` is injectable for tests."""
    import ctypes
    from ctypes import wintypes

    if get_table is None:
        iphlpapi = ctypes.WinDLL("iphlpapi", use_last_error=True)
        get_table = iphlpapi.GetExtendedTcpTable
        get_table.argtypes = [wintypes.LPVOID, ctypes.POINTER(wintypes.DWORD), wintypes.BOOL, wintypes.ULONG,
                              ctypes.c_int, wintypes.ULONG]
        get_table.restype = wintypes.DWORD

    row_size = _ROW4_SIZE if family == _AF_INET else _ROW6_SIZE
    size = wintypes.DWORD(0)
    buffer = None
    # The table can grow between the call that asks for its size and the one that reads it: leave slack and retry.
    for _ in range(_TABLE_ATTEMPTS):
        result = get_table(buffer, ctypes.byref(size), False, family, _TCP_TABLE_OWNER_PID_LISTENER, 0)
        if result == _NO_ERROR and buffer is not None:
            return bytes(buffer.raw[:size.value])
        if result not in (_NO_ERROR, _ERROR_INSUFFICIENT_BUFFER):
            raise OwnerCheckError(f"GetExtendedTcpTable failed ({result})")
        buffer = ctypes.create_string_buffer(size.value + _SLACK_ROWS * row_size)
        size.value = len(buffer)
    raise OwnerCheckError("the TCP table kept changing while it was read")


def parse_listener_table(table: bytes, port: int, family: int = _AF_INET) -> Set[int]:
    """Reads a MIB_TCPTABLE_OWNER_PID (a count, then rows) and returns the owners of ``127.0.0.1:port``.

    IPv4 rows are 24 bytes and count when the local address is 127.0.0.1 or 0.0.0.0; IPv6 rows are 56 bytes and count
    when the local address is ``::``. A table that is shorter than its count says raises ValueError.
    """
    (count,) = struct.unpack_from("<I", table, 0)
    row_size = _ROW4_SIZE if family == _AF_INET else _ROW6_SIZE
    if 4 + count * row_size > len(table):
        raise ValueError("the TCP table is shorter than its row count")
    owners: Set[int] = set()
    for index in range(count):
        offset = 4 + row_size * index
        if family == _AF_INET:
            # dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid
            _state, local_addr, local_port, _raddr, _rport, pid = struct.unpack_from("<6I", table, offset)
            matches = local_addr in (_LOOPBACK, _ANY)
        else:
            # ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort,
            # dwState, dwOwningPid
            local_addr6 = table[offset:offset + 16]
            (local_port,) = struct.unpack_from("<I", table, offset + 20)
            (pid,) = struct.unpack_from("<I", table, offset + 52)
            matches = local_addr6 == bytes(16)
        if matches and socket.ntohs(local_port & 0xFFFF) == port:
            owners.add(pid)
    return owners


# ---- Processes -------------------------------------------------------------------------------------------------

def _process_identity_windows(pid: int) -> ProcessIdentity:
    import ctypes
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    HANDLE, DWORD, BOOL = wintypes.HANDLE, wintypes.DWORD, wintypes.BOOL

    kernel32.OpenProcess.argtypes = [DWORD, BOOL, DWORD]
    kernel32.OpenProcess.restype = HANDLE
    kernel32.GetCurrentProcess.argtypes = []
    kernel32.GetCurrentProcess.restype = HANDLE
    kernel32.CloseHandle.argtypes = [HANDLE]
    kernel32.CloseHandle.restype = BOOL
    kernel32.QueryFullProcessImageNameW.argtypes = [HANDLE, DWORD, wintypes.LPWSTR, ctypes.POINTER(DWORD)]
    kernel32.QueryFullProcessImageNameW.restype = BOOL
    advapi32.OpenProcessToken.argtypes = [HANDLE, DWORD, ctypes.POINTER(HANDLE)]
    advapi32.OpenProcessToken.restype = BOOL
    advapi32.GetTokenInformation.argtypes = [HANDLE, ctypes.c_int, wintypes.LPVOID, DWORD, ctypes.POINTER(DWORD)]
    advapi32.GetTokenInformation.restype = BOOL
    advapi32.EqualSid.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
    advapi32.EqualSid.restype = BOOL

    def fail(what: str) -> OwnerCheckError:
        return OwnerCheckError(f"{what} failed for process {pid} (Windows error {ctypes.get_last_error()})")

    def user_of(process: Any) -> Any:
        """The TOKEN_USER buffer of ``process``; its first member is the pointer to the user's SID."""
        token = HANDLE()
        if not advapi32.OpenProcessToken(process, _TOKEN_QUERY, ctypes.byref(token)):
            raise fail("OpenProcessToken")
        try:
            needed = DWORD(0)
            advapi32.GetTokenInformation(token, _TOKEN_USER, None, 0, ctypes.byref(needed))  # asks for the size
            if needed.value == 0:
                raise fail("GetTokenInformation")
            buffer = ctypes.create_string_buffer(needed.value)
            if not advapi32.GetTokenInformation(token, _TOKEN_USER, buffer, needed, ctypes.byref(needed)):
                raise fail("GetTokenInformation")
            return buffer
        finally:
            kernel32.CloseHandle(token)

    handle = kernel32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        raise fail("OpenProcess")
    try:
        length = DWORD(32768)
        path = ctypes.create_unicode_buffer(length.value)
        if not kernel32.QueryFullProcessImageNameW(handle, 0, path, ctypes.byref(length)):
            raise fail("QueryFullProcessImageNameW")
        theirs = user_of(handle)
        mine = user_of(kernel32.GetCurrentProcess())
        their_sid = ctypes.c_void_p.from_buffer(theirs).value
        my_sid = ctypes.c_void_p.from_buffer(mine).value
        if not their_sid or not my_sid:
            raise OwnerCheckError(f"no user SID for process {pid}")
        same_user = bool(advapi32.EqualSid(their_sid, my_sid))
        return ProcessIdentity(pid=pid, image=path.value, same_user=same_user)
    finally:
        kernel32.CloseHandle(handle)
