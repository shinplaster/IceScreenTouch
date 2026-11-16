# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Finding the API: port, token and (when known) the id of the process that listens.

Order (docs/API-DESIGN.md, chapter 4):

1. an explicit ``base_url`` / ``token`` given by the caller;
2. the environment: ``ICESCREENTOUCH_API`` (``http://127.0.0.1:47821``) and ``ICESCREENTOUCH_TOKEN``;
3. ``api.json`` of the Microsoft Store package (the MSIX redirects the app's %LOCALAPPDATA% writes into
   ``%LOCALAPPDATA%\\Packages\\<family>\\LocalCache\\Local\\IceScreenTouch``);
4. ``%ICESCREENTOUCH_DATA_DIR%\\api.json`` when that variable is set;
5. ``%LOCALAPPDATA%\\IceScreenTouch\\api.json``.

The client always talks to ``127.0.0.1`` (never ``localhost``, which may resolve to ``::1``; the server listens on IPv4
loopback only) and never sends an ``Origin`` header.

Owner check. Before the token is handed to a port, this module asks Windows (``GetExtendedTcpTable``) which processes
listen there, and about each of them (``OpenProcess``, ``OpenProcessToken``, ``QueryFullProcessImageNameW``) who runs it:

* every listener on the port must run as the **current Windows user** and be ``IceScreenTouch.exe``;
* for a connection from api.json, whose ``pid`` names the app, the only listener must also be exactly that pid (else the
  file is stale: a crash left it behind, or another program holds the port), and nobody listening means stale too;
* for an environment variable or an explicit ``base_url``, nobody listening is not an error here (the call itself then
  fails with "cannot be reached"); any listener must pass the checks above.

If the token were sent to a process that does not pass, whoever took the port would get it, so it is NOT sent
(:class:`~icescreentouch.errors.StaleConnectionError`). On Windows the check **fails closed**: if the table or a process
cannot be read (for example access to the process is denied) the token is not sent either. Off Windows nothing can be
asked and the check is skipped. ``Client(..., verify_owner=False)`` turns the check off, for a deliberate setup such as
a test server or a development build with another file name. A race between the check and the connection (the process
exits and another takes the port in between) is not closed here, but the check is repeated before every request.
"""

from __future__ import annotations

import dataclasses
import glob
import json
import os
import re
from dataclasses import dataclass, field
from typing import List, Mapping, Optional, Tuple
from urllib.parse import urlsplit

from . import _winnet
from .errors import ConnectionDiscoveryError, StaleConnectionError

DEFAULT_PORT = 47821
HOST = "127.0.0.1"
ENV_API = "ICESCREENTOUCH_API"
ENV_TOKEN = "ICESCREENTOUCH_TOKEN"
ENV_DATA_DIR = "ICESCREENTOUCH_DATA_DIR"
FILE_NAME = "api.json"

# The package family of the Microsoft Store build (publisher id hash included), and the pattern that tolerates
# another publisher hash (a sideloaded or re-signed build).
STORE_FAMILY = "Shin-plaster.IceScreenTouch_5c159ja95e6hm"
_STORE_FAMILY_GLOB = "Shin-plaster.IceScreenTouch_*"
_STORE_SUBPATH = os.path.join("LocalCache", "Local", "IceScreenTouch", FILE_NAME)

# The server only accepts the tokens its own generator makes: 43 characters of the Base64url alphabet. A looser check
# keeps this client usable if that changes, but still refuses anything that could break an HTTP header.
_TOKEN_RE = re.compile(r"^[A-Za-z0-9_\-]{16,512}$")


@dataclass(frozen=True)
class Connection:
    """Where the API is and how to authenticate. The token is kept out of ``repr``."""

    port: int
    token: str = field(repr=False)
    source: str = "explicit"
    pid: Optional[int] = None
    from_file: bool = False  # read from an api.json (the app wrote it, so a port nobody listens on means it is stale)

    @property
    def base_url(self) -> str:
        return f"http://{HOST}:{self.port}"


def check_token(token: str, origin: str) -> str:
    """Returns ``token`` when it looks like a token; raises ConnectionDiscoveryError (without the token) otherwise."""
    if not isinstance(token, str) or not _TOKEN_RE.match(token):
        raise ConnectionDiscoveryError(f"the token from {origin} is not a valid API token")
    return token


def parse_base_url(base_url: str) -> int:
    """The port of ``http://127.0.0.1:47821``. ``localhost`` is accepted and means 127.0.0.1; anything else is refused."""
    try:
        parts = urlsplit(base_url if "//" in base_url else f"//{base_url}", scheme="http")
        host = (parts.hostname or "").lower()
        port = parts.port
    except ValueError as exc:
        raise ConnectionDiscoveryError(f"the API address {base_url!r} is not valid") from exc
    if parts.scheme != "http":
        raise ConnectionDiscoveryError("the API address must use http:// (the API listens on 127.0.0.1 without TLS)")
    if host not in (HOST, "localhost"):
        # The token would go to another machine: refuse instead of trusting the caller.
        raise ConnectionDiscoveryError("the API address must be 127.0.0.1 (the API listens on the local machine only)")
    if parts.path not in ("", "/") or parts.query or parts.fragment or parts.username or parts.password:
        raise ConnectionDiscoveryError("the API address must be just http://127.0.0.1:<port>")
    return port if port is not None else DEFAULT_PORT


def candidate_files(env: Optional[Mapping[str, str]] = None) -> List[Tuple[str, str]]:
    """The api.json paths to try, in order, as ``(path, label)``. Only paths that exist are worth reading."""
    env = os.environ if env is None else env
    local = env.get("LOCALAPPDATA")
    found: List[Tuple[str, str]] = []
    if local:
        store = os.path.join(local, "Packages", STORE_FAMILY, _STORE_SUBPATH)
        found.append((store, "Microsoft Store package"))
        pattern = os.path.join(glob.escape(os.path.join(local, "Packages")), _STORE_FAMILY_GLOB, _STORE_SUBPATH)
        for path in sorted(glob.glob(pattern)):
            if os.path.normcase(path) != os.path.normcase(store):
                found.append((path, "Microsoft Store package"))
    data_dir = env.get(ENV_DATA_DIR)
    if data_dir:
        found.append((os.path.join(data_dir, FILE_NAME), f"{ENV_DATA_DIR}"))
    if local:
        found.append((os.path.join(local, "IceScreenTouch", FILE_NAME), "the app data folder"))
    return found


def read_connection_file(path: str, label: str = "api.json") -> Connection:
    """Parses one api.json (``{"port": ..., "token": ..., "pid": ...}``)."""
    try:
        with open(path, "r", encoding="utf-8-sig") as handle:
            data = json.load(handle)
    except (OSError, ValueError) as exc:
        raise ConnectionDiscoveryError(f"{path} could not be read ({type(exc).__name__})") from None
    if not isinstance(data, dict):
        raise ConnectionDiscoveryError(f"{path} is not a valid connection file")
    port, token, pid = data.get("port"), data.get("token"), data.get("pid")
    if isinstance(port, bool) or not isinstance(port, int) or not 0 < port < 65536:
        raise ConnectionDiscoveryError(f"{path} has no valid port")
    if pid is not None and (isinstance(pid, bool) or not isinstance(pid, int) or pid <= 0):
        raise ConnectionDiscoveryError(f"{path} has an invalid pid")
    return Connection(port=port, token=check_token(token, path), source=f"{label} ({path})", pid=pid, from_file=True)


IMAGE_NAME = "IceScreenTouch.exe"


def check_owner(connection: Connection) -> None:
    """Refuses (StaleConnectionError) a port that does not belong to IceScreen Touch of this user. See the module text.

    Nothing is checked off Windows. On Windows every failure to find out refuses.
    """
    port = connection.port
    try:
        owners = _winnet.listening_pids(port)
    except _winnet.OwnerCheckError as exc:
        raise StaleConnectionError(
            f"could not verify the owner of port {port} ({exc}); the token was not sent") from None
    if owners is None:
        return  # not Windows: cannot be checked here
    if not owners:
        if connection.from_file or connection.pid is not None:
            raise StaleConnectionError(
                f"api.json ({connection.source}) names a port nobody listens on: IceScreen Touch is not running "
                "or its automation API is off (the file is stale)")
        return  # the call itself will say that the API cannot be reached
    if connection.pid is not None and owners != {connection.pid}:
        raise StaleConnectionError(
            f"the process that listens on port {port} is not the IceScreen Touch process named in "
            f"api.json ({connection.source}); the token was not sent")
    for pid in sorted(owners):
        try:
            identity = _winnet.process_identity(pid)
        except _winnet.OwnerCheckError as exc:
            raise StaleConnectionError(
                f"could not verify the owner of port {port} ({exc}); the token was not sent") from None
        if identity is None:
            return
        if not identity.same_user:
            raise StaleConnectionError(
                f"the process that listens on port {port} does not run as the current Windows user; "
                "the token was not sent")
        if os.path.basename(identity.image.replace("\\", "/")).lower() != IMAGE_NAME.lower():
            raise StaleConnectionError(
                f"the process that listens on port {port} is not {IMAGE_NAME}; the token was not sent")


def discover(base_url: Optional[str] = None, token: Optional[str] = None,
             env: Optional[Mapping[str, str]] = None, verify_owner: bool = True) -> Connection:
    """Finds the connection (see the module text for the order). Raises ConnectionDiscoveryError.

    ``verify_owner`` (default True) runs :func:`check_owner` on the result; with False nothing is checked, and a stale
    api.json is no longer skipped in favor of the next one.
    """
    env = os.environ if env is None else env
    if base_url is not None:
        explicit_token = token if token is not None else env.get(ENV_TOKEN)
        if not explicit_token:
            raise ConnectionDiscoveryError(
                f"base_url was given without a token (pass token=... or set {ENV_TOKEN})")
        connection = Connection(parse_base_url(base_url), check_token(explicit_token, "the caller"), "explicit")
        if verify_owner:
            check_owner(connection)
        return connection

    env_url = env.get(ENV_API)
    if env_url:
        env_token = token if token is not None else env.get(ENV_TOKEN)
        if not env_token:
            raise ConnectionDiscoveryError(f"{ENV_API} is set but {ENV_TOKEN} is not")
        connection = Connection(parse_base_url(env_url), check_token(env_token, "the environment"), "environment")
        if verify_owner:
            check_owner(connection)
        return connection

    first_error: Optional[Exception] = None
    for path, label in candidate_files(env):
        if not os.path.isfile(path):
            continue
        try:
            connection = read_connection_file(path, label)
            if token is not None:
                connection = dataclasses.replace(connection, token=check_token(token, "the caller"))
            if verify_owner:
                check_owner(connection)
            return connection
        except ConnectionDiscoveryError as exc:
            # A leftover file of another install must not hide a working one: try the next.
            if first_error is None:
                first_error = exc
    if first_error is not None:
        raise first_error
    raise ConnectionDiscoveryError(
        "IceScreen Touch's connection file (api.json) was not found. Start the app and switch on "
        "Settings > Automation API, or set ICESCREENTOUCH_API and ICESCREENTOUCH_TOKEN.")
