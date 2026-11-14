# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

import json
import os
import struct
import sys

import pytest
from conftest import OTHER_TOKEN, TOKEN

import icescreentouch as ist
from icescreentouch import _winnet, discovery
from icescreentouch.discovery import STORE_FAMILY, Connection

REAL_LISTENING_PIDS = _winnet.listening_pids
REAL_PROCESS_IDENTITY = _winnet.process_identity


def write_file(directory, port, token=TOKEN, pid=None):
    os.makedirs(directory, exist_ok=True)
    data = {"port": port, "token": token}
    if pid is not None:
        data["pid"] = pid
    path = os.path.join(directory, "api.json")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(data, handle)
    return path


def store_dir(local, family=STORE_FAMILY):
    return os.path.join(local, "Packages", family, "LocalCache", "Local", "IceScreenTouch")


@pytest.fixture
def local(tmp_path):
    return str(tmp_path / "Local")


# ---- Explicit and environment --------------------------------------------------------------------------------

def test_explicit_overrides_everything(local):
    write_file(os.path.join(local, "IceScreenTouch"), 1111)
    env = {"LOCALAPPDATA": local, "ICESCREENTOUCH_API": "http://127.0.0.1:2222", "ICESCREENTOUCH_TOKEN": OTHER_TOKEN}
    connection = discovery.discover("http://127.0.0.1:3333", TOKEN, env)
    assert (connection.port, connection.token, connection.source) == (3333, TOKEN, "explicit")


def test_explicit_url_without_token_is_an_error_unless_the_environment_has_one():
    with pytest.raises(ist.ConnectionDiscoveryError):
        discovery.discover("http://127.0.0.1:3333", None, {})
    assert discovery.discover("http://127.0.0.1:3333", None, {"ICESCREENTOUCH_TOKEN": TOKEN}).token == TOKEN


def test_environment_variables(local):
    write_file(os.path.join(local, "IceScreenTouch"), 1111)
    env = {"LOCALAPPDATA": local, "ICESCREENTOUCH_API": "http://127.0.0.1:2222", "ICESCREENTOUCH_TOKEN": OTHER_TOKEN}
    connection = discovery.discover(env=env)
    assert (connection.port, connection.token, connection.source) == (2222, OTHER_TOKEN, "environment")
    assert connection.base_url == "http://127.0.0.1:2222"


def test_api_variable_without_token_is_an_error():
    with pytest.raises(ist.ConnectionDiscoveryError, match="ICESCREENTOUCH_TOKEN"):
        discovery.discover(env={"ICESCREENTOUCH_API": "http://127.0.0.1:2222"})


@pytest.mark.parametrize("url", ["http://evil.example:47821", "https://127.0.0.1:47821", "http://127.0.0.1:47821/x",
                                 "http://user:pw@127.0.0.1:47821", "http://[::1]:47821", "ftp://127.0.0.1:1"])
def test_only_plain_loopback_urls(url):
    with pytest.raises(ist.ConnectionDiscoveryError):
        discovery.parse_base_url(url)


def test_default_port():
    assert discovery.parse_base_url("http://127.0.0.1") == 47821
    assert discovery.parse_base_url("localhost:5000") == 5000


@pytest.mark.parametrize("token", ["", "short", "has space " + "a" * 40, "new\nline" + "a" * 40, "ü" * 43])
def test_odd_tokens_are_refused_without_echoing_them(token):
    with pytest.raises(ist.ConnectionDiscoveryError) as info:
        discovery.check_token(token, "the test")
    assert token not in str(info.value) or token == ""


# ---- Files ---------------------------------------------------------------------------------------------------

def test_search_order(local, tmp_path):
    data = str(tmp_path / "data")
    env = {"LOCALAPPDATA": local, "ICESCREENTOUCH_DATA_DIR": data}
    paths = [p for p, _ in discovery.candidate_files(env)]
    assert paths[0] == os.path.join(store_dir(local), "api.json")
    assert paths[1:] == [os.path.join(data, "api.json"), os.path.join(local, "IceScreenTouch", "api.json")]


def test_order_without_data_dir(local):
    paths = [p for p, _ in discovery.candidate_files({"LOCALAPPDATA": local})]
    assert paths == [os.path.join(store_dir(local), "api.json"), os.path.join(local, "IceScreenTouch", "api.json")]


def test_store_package_wins_over_the_plain_folder(local, tmp_path):
    write_file(store_dir(local), 1111)
    write_file(os.path.join(local, "IceScreenTouch"), 2222)
    write_file(str(tmp_path / "data"), 3333)
    env = {"LOCALAPPDATA": local, "ICESCREENTOUCH_DATA_DIR": str(tmp_path / "data")}
    assert discovery.discover(env=env).port == 1111


def test_data_dir_wins_over_the_plain_folder(local, tmp_path):
    write_file(os.path.join(local, "IceScreenTouch"), 2222)
    write_file(str(tmp_path / "data"), 3333)
    env = {"LOCALAPPDATA": local, "ICESCREENTOUCH_DATA_DIR": str(tmp_path / "data")}
    assert discovery.discover(env=env).port == 3333


def test_plain_folder(local):
    write_file(os.path.join(local, "IceScreenTouch"), 2222)
    assert discovery.discover(env={"LOCALAPPDATA": local}).port == 2222


def test_another_publisher_hash_is_found_by_glob(local):
    write_file(store_dir(local, "Shin-plaster.IceScreenTouch_abcdefghij123"), 4444)
    connection = discovery.discover(env={"LOCALAPPDATA": local})
    assert connection.port == 4444 and "Microsoft Store" in connection.source


def test_nothing_found(local):
    with pytest.raises(ist.ConnectionDiscoveryError, match="not found"):
        discovery.discover(env={"LOCALAPPDATA": local})


def test_malformed_files(local):
    directory = os.path.join(local, "IceScreenTouch")
    os.makedirs(directory)
    bad = [
        "not json", "[]", json.dumps({"port": "47821", "token": TOKEN}), json.dumps({"port": 0, "token": TOKEN}),
        json.dumps({"port": 70000, "token": TOKEN}), json.dumps({"port": 1, "token": "x"}),
        json.dumps({"port": 1, "token": TOKEN, "pid": "7"}), json.dumps({"port": True, "token": TOKEN}),
    ]
    for text in bad:
        with open(os.path.join(directory, "api.json"), "w", encoding="utf-8") as handle:
            handle.write(text)
        with pytest.raises(ist.ConnectionDiscoveryError) as info:
            discovery.discover(env={"LOCALAPPDATA": local})
        assert TOKEN not in str(info.value)


def test_utf8_bom_is_tolerated(local):
    directory = os.path.join(local, "IceScreenTouch")
    os.makedirs(directory)
    with open(os.path.join(directory, "api.json"), "w", encoding="utf-8-sig") as handle:
        json.dump({"port": 5, "token": TOKEN}, handle)
    assert discovery.discover(env={"LOCALAPPDATA": local}).port == 5


def test_connection_repr_hides_the_token():
    connection = Connection(port=1, token=TOKEN)
    assert TOKEN not in repr(connection) and TOKEN not in str(connection)


# ---- The owner check -----------------------------------------------------------------------------------------

APP = r"C:\Program Files\WindowsApps\Shin-plaster.IceScreenTouch_1.0.0.0_x64__5c159ja95e6hm\IceScreenTouch.exe"
OWN_PORT = 5000


def identity(pid, *, same_user=True, image=APP):
    return _winnet.ProcessIdentity(pid=pid, image=image, same_user=same_user)


def fake_system(monkeypatch, owners, identities=None):
    """What Windows would say: ``owners`` listen on the port; ``identities`` maps pid -> ProcessIdentity (default: the app
    as the current user). A value that is an exception is raised."""
    calls = {"table": 0, "process": []}

    def listening(port):
        calls["table"] += 1
        if isinstance(owners, Exception):
            raise owners
        return owners

    def process(pid):
        calls["process"].append(pid)
        found = (identities or {}).get(pid, identity(pid))
        if isinstance(found, Exception):
            raise found
        return found

    monkeypatch.setattr(_winnet, "listening_pids", listening)
    monkeypatch.setattr(_winnet, "process_identity", process)
    return calls


def file_env(local, pid=4242, port=OWN_PORT):
    write_file(os.path.join(local, "IceScreenTouch"), port, pid=pid)
    return {"LOCALAPPDATA": local}


def env_vars(port=OWN_PORT):
    return {"ICESCREENTOUCH_API": f"http://127.0.0.1:{port}", "ICESCREENTOUCH_TOKEN": TOKEN}


# -- api.json: the pid must match AND be the same user running IceScreenTouch.exe

def test_matching_pid_passes(local, monkeypatch):
    calls = fake_system(monkeypatch, {4242})
    assert discovery.discover(env=file_env(local)).pid == 4242
    assert calls["process"] == [4242]


def test_other_process_on_the_port_is_refused(local, monkeypatch):
    fake_system(monkeypatch, {9999})
    with pytest.raises(ist.StaleConnectionError) as info:
        discovery.discover(env=file_env(local))
    assert TOKEN not in str(info.value)


def test_extra_listener_on_the_port_is_refused(local, monkeypatch):
    fake_system(monkeypatch, {4242, 9999})
    with pytest.raises(ist.StaleConnectionError):
        discovery.discover(env=file_env(local))


def test_nobody_listening_means_stale(local, monkeypatch):
    fake_system(monkeypatch, set())
    with pytest.raises(ist.StaleConnectionError, match="not running"):
        discovery.discover(env=file_env(local))


def test_file_pid_of_another_user_is_refused(local, monkeypatch):
    fake_system(monkeypatch, {4242}, {4242: identity(4242, same_user=False)})
    with pytest.raises(ist.StaleConnectionError, match="current Windows user"):
        discovery.discover(env=file_env(local))


@pytest.mark.parametrize("image", [r"C:\Python313\python.exe", r"C:\Tools\NotIceScreenTouch.exe",
                                   r"C:\Tools\IceScreenTouch.exe.bak", r"C:\IceScreenTouch.exe\evil.exe", ""])
def test_file_pid_with_another_image_is_refused(local, monkeypatch, image):
    fake_system(monkeypatch, {4242}, {4242: identity(4242, image=image)})
    with pytest.raises(ist.StaleConnectionError, match="IceScreenTouch.exe"):
        discovery.discover(env=file_env(local))


@pytest.mark.parametrize("image", [APP, r"C:\build\bin\icescreentouch.EXE", "D:/x/IceScreenTouch.exe"])
def test_the_image_name_is_compared_case_insensitively_on_the_file_name(local, monkeypatch, image):
    fake_system(monkeypatch, {4242}, {4242: identity(4242, image=image)})
    assert discovery.discover(env=file_env(local)).port == OWN_PORT


def test_file_without_a_pid_checks_every_listener(local, monkeypatch):
    write_file(os.path.join(local, "IceScreenTouch"), OWN_PORT)  # no pid
    env = {"LOCALAPPDATA": local}
    fake_system(monkeypatch, {1, 2}, {2: identity(2, image=r"C:\x\other.exe")})
    with pytest.raises(ist.StaleConnectionError):
        discovery.discover(env=env)
    fake_system(monkeypatch, {1})
    assert discovery.discover(env=env).port == OWN_PORT
    fake_system(monkeypatch, set())
    with pytest.raises(ist.StaleConnectionError, match="not running"):
        discovery.discover(env=env)  # a file the app wrote with nobody listening is stale


def test_check_is_skipped_when_it_cannot_run_here(local, monkeypatch):
    env = file_env(local)
    monkeypatch.setattr(_winnet, "listening_pids", lambda port: None)  # not Windows
    assert discovery.discover(env=env).port == OWN_PORT


# -- ICESCREENTOUCH_API / ICESCREENTOUCH_TOKEN and an explicit base_url: every listener must be the current user's app

@pytest.mark.parametrize("how", ["environment", "explicit"])
def test_caller_chosen_address_every_listener_is_checked(monkeypatch, how):
    def find():
        if how == "environment":
            return discovery.discover(env=env_vars())
        return discovery.discover(f"http://127.0.0.1:{OWN_PORT}", TOKEN, {})

    calls = fake_system(monkeypatch, {1, 2})
    assert find().port == OWN_PORT
    assert sorted(calls["process"]) == [1, 2]

    fake_system(monkeypatch, {1, 2}, {2: identity(2, same_user=False)})  # one bad listener is enough
    with pytest.raises(ist.StaleConnectionError, match="current Windows user"):
        find()
    fake_system(monkeypatch, {1}, {1: identity(1, image=r"C:\Python313\python.exe")})
    with pytest.raises(ist.StaleConnectionError, match="IceScreenTouch.exe"):
        find()


@pytest.mark.parametrize("how", ["environment", "explicit"])
def test_caller_chosen_address_with_nobody_listening_is_left_to_the_call(monkeypatch, how):
    fake_system(monkeypatch, set())
    connection = (discovery.discover(env=env_vars()) if how == "environment"
                  else discovery.discover(f"http://127.0.0.1:{OWN_PORT}", TOKEN, {}))
    assert connection.port == OWN_PORT  # the request will say that nobody answers


def test_verify_owner_false_checks_nothing(monkeypatch, local):
    calls = fake_system(monkeypatch, {9999}, {9999: identity(9999, same_user=False, image="x.exe")})
    assert discovery.discover(f"http://127.0.0.1:{OWN_PORT}", TOKEN, {}, verify_owner=False).port == OWN_PORT
    assert discovery.discover(env=env_vars(), verify_owner=False).port == OWN_PORT
    assert discovery.discover(env=file_env(local, pid=1), verify_owner=False).pid == 1
    assert calls == {"table": 0, "process": []}


# -- Windows: fail closed

def test_unreadable_tcp_table_refuses(local, monkeypatch):
    fake_system(monkeypatch, _winnet.OwnerCheckError("the TCP table could not be read (OSError)"))
    for find in (lambda: discovery.discover(env=file_env(local)), lambda: discovery.discover(env=env_vars()),
                 lambda: discovery.discover(f"http://127.0.0.1:{OWN_PORT}", TOKEN, {})):
        with pytest.raises(ist.StaleConnectionError, match="could not verify the owner") as info:
            find()
        assert TOKEN not in str(info.value)


def test_unreadable_process_refuses(local, monkeypatch):
    fake_system(monkeypatch, {4242}, {4242: _winnet.OwnerCheckError("OpenProcess failed for process 4242")})
    with pytest.raises(ist.StaleConnectionError, match="could not verify the owner"):
        discovery.discover(env=file_env(local))
    with pytest.raises(ist.StaleConnectionError, match="could not verify the owner"):
        discovery.discover(env=env_vars())


def test_a_stale_file_does_not_hide_a_working_one(local, monkeypatch):
    write_file(store_dir(local), 1111, pid=1)  # left behind by a crash
    write_file(os.path.join(local, "IceScreenTouch"), 2222, pid=2)
    monkeypatch.setattr(_winnet, "listening_pids", lambda port: {2} if port == 2222 else set())
    monkeypatch.setattr(_winnet, "process_identity", lambda pid: identity(pid))
    assert discovery.discover(env={"LOCALAPPDATA": local}).port == 2222


def test_all_stale_reports_the_first_problem(local, monkeypatch):
    write_file(store_dir(local), 1111, pid=1)
    write_file(os.path.join(local, "IceScreenTouch"), 2222, pid=2)
    fake_system(monkeypatch, set())
    with pytest.raises(ist.StaleConnectionError, match="Microsoft Store"):
        discovery.discover(env={"LOCALAPPDATA": local})


# -- The client: the token is never sent to a port that fails the check

def test_stale_file_never_sends_the_token(api, tmp_path, monkeypatch):
    directory = str(tmp_path / "data")
    write_file(directory, api.port, token=TOKEN, pid=4242)
    fake_system(monkeypatch, {9999})
    client = ist.Client(env={"ICESCREENTOUCH_DATA_DIR": directory})
    with pytest.raises(ist.StaleConnectionError):
        client.get_status()
    assert api.requests == []  # nothing was sent to the port


def test_matching_pid_then_the_call_works(api, tmp_path, monkeypatch):
    directory = str(tmp_path / "data")
    write_file(directory, api.port, token=TOKEN, pid=4242)
    fake_system(monkeypatch, {4242})
    assert ist.Client(env={"ICESCREENTOUCH_DATA_DIR": directory}).get_status().version == "0.3.0"


def test_explicit_client_refuses_a_foreign_listener_by_default(api, monkeypatch):
    fake_system(monkeypatch, {7}, {7: identity(7, image=r"C:\Python313\python.exe")})
    with pytest.raises(ist.StaleConnectionError):
        ist.Client(base_url=api.url, token=TOKEN)
    assert api.requests == []


def test_verify_owner_false_sends_the_token_anyway(api, monkeypatch):
    calls = fake_system(monkeypatch, {7}, {7: identity(7, image=r"C:\Python313\python.exe")})
    assert ist.Client(base_url=api.url, token=TOKEN, verify_owner=False).get_status().version == "0.3.0"
    assert calls == {"table": 0, "process": []}


def test_the_owner_is_checked_before_every_request(api, monkeypatch):
    calls = fake_system(monkeypatch, {7})
    client = ist.Client(base_url=api.url, token=TOKEN)
    client.get_status()
    client.get_status()
    assert calls["table"] == 3  # once when the client was made, once per request
    # The app crashed and another process took the port: the next request must not carry the token.
    fake_system(monkeypatch, {7}, {7: identity(7, same_user=False)})
    api.requests.clear()
    with pytest.raises(ist.StaleConnectionError):
        client.get_status()
    assert api.requests == []


def test_a_restarted_app_with_a_new_pid_is_found_again(api, tmp_path, monkeypatch):
    directory = tmp_path / "data"
    write_file(str(directory), api.port, token=TOKEN, pid=1)
    fake_system(monkeypatch, {1})
    client = ist.Client(env={"ICESCREENTOUCH_DATA_DIR": str(directory)})
    client.get_status()
    write_file(str(directory), api.port, token=TOKEN, pid=2)  # the app restarted: a new pid, the same port
    fake_system(monkeypatch, {2})
    client.get_status()
    assert client.connection.pid == 2


# ---- The Windows layer ---------------------------------------------------------------------------------------------

def row(addr, port, pid, state=2):
    return struct.pack("<6I", state, addr, int.from_bytes(struct.pack(">H", port), "little"), 0, 0, pid)


def row6(addr16, port, pid, state=2):
    return (addr16 + struct.pack("<II", 0, int.from_bytes(struct.pack(">H", port), "little")) + bytes(16)
            + struct.pack("<IIII", 0, 0, state, pid))


def test_table_parser():
    loopback = 0x0100007F
    table = struct.pack("<I", 4) + row(loopback, 47821, 100) + row(0, 47821, 200) + row(loopback, 8000, 300) + row(0x0200A8C0, 47821, 400)
    assert _winnet.parse_listener_table(table, 47821) == {100, 200}
    assert _winnet.parse_listener_table(table, 8000) == {300}
    assert _winnet.parse_listener_table(table, 1) == set()


def test_ipv6_table_parser_counts_dual_stack_wildcards_only():
    wildcard, loopback6, other = bytes(16), bytes(15) + b"\x01", bytes([0xFE, 0x80]) + bytes(14)
    table = struct.pack("<I", 3) + row6(wildcard, 47821, 10) + row6(loopback6, 47821, 20) + row6(other, 47821, 30)
    assert _winnet.parse_listener_table(table, 47821, _winnet._AF_INET6) == {10}  # ::1 and fe80:: do not take IPv4


def test_truncated_table_is_an_error():
    with pytest.raises(ValueError):
        _winnet.parse_listener_table(struct.pack("<I", 5) + row(0, 1, 1), 1)


class FakeTable:
    """GetExtendedTcpTable: ``sizes`` are the sizes it asks for on successive calls (it grows between the calls)."""

    def __init__(self, rows, sizes=None, fail_with=None):
        self.data = struct.pack("<I", len(rows)) + b"".join(rows)
        self.sizes = list(sizes or [len(self.data)])
        self.fail_with = fail_with
        self.buffers = []

    def __call__(self, buffer, size_ref, order, family, table_class, reserved):
        size = size_ref._obj
        if self.fail_with is not None:
            return self.fail_with
        needed = self.sizes[0] if len(self.sizes) == 1 else self.sizes.pop(0)
        capacity = 0 if buffer is None else len(buffer)
        self.buffers.append(capacity)
        if capacity < needed:
            size.value = needed
            return 122
        buffer[:len(self.data)] = self.data
        size.value = len(self.data)
        return 0


def test_table_read_leaves_slack_and_retries_while_it_grows():
    rows = [row(0x0100007F, 47821, 100)]
    exact = 4 + 24
    table = FakeTable(rows, sizes=[exact, exact + 24 * 3, exact + 24 * 100])  # still growing after the first retry
    raw = _winnet._read_table(_winnet._AF_INET, table)
    assert _winnet.parse_listener_table(raw, 47821) == {100}
    assert table.buffers[0] == 0 and table.buffers[1] >= exact + 24 * 64  # slack beyond the size it asked for


def test_table_that_never_settles_fails_closed():
    class Growing(FakeTable):
        def __call__(self, buffer, size_ref, *rest):
            size_ref._obj.value = (0 if buffer is None else len(buffer)) + 1000
            return 122

    with pytest.raises(_winnet.OwnerCheckError, match="kept changing"):
        _winnet._read_table(_winnet._AF_INET, Growing([]))


def test_table_call_failure_fails_closed():
    with pytest.raises(_winnet.OwnerCheckError, match="87"):
        _winnet._read_table(_winnet._AF_INET, FakeTable([], fail_with=87))


def test_on_windows_every_failure_raises_instead_of_skipping(monkeypatch):
    monkeypatch.setattr(sys, "platform", "win32")
    for broken in (OSError("x"), ValueError("short table"), struct.error("x"), AttributeError("x")):
        def boom(port, _e=broken):
            raise _e
        monkeypatch.setattr(_winnet, "_listening_pids_windows", boom)
        with pytest.raises(_winnet.OwnerCheckError):
            REAL_LISTENING_PIDS(80)

        def boom_process(pid, _e=broken):
            raise _e
        monkeypatch.setattr(_winnet, "_process_identity_windows", boom_process)
        with pytest.raises(_winnet.OwnerCheckError):
            REAL_PROCESS_IDENTITY(4242)


def test_not_windows_cannot_tell(monkeypatch):
    monkeypatch.setattr(sys, "platform", "linux")
    assert REAL_LISTENING_PIDS(80) is None
    assert REAL_PROCESS_IDENTITY(4242) is None


windows_only = pytest.mark.skipif(sys.platform != "win32", reason="the owner check is Windows only")


@windows_only
def test_real_table_finds_this_process():
    import socket

    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        port = listener.getsockname()[1]
        assert REAL_LISTENING_PIDS(port) == {os.getpid()}


@windows_only
def test_real_dual_stack_listener_is_seen():
    import socket

    with socket.socket(socket.AF_INET6) as listener:
        listener.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
        listener.bind(("::", 0))
        listener.listen()
        port = listener.getsockname()[1]
        assert REAL_LISTENING_PIDS(port) == {os.getpid()}


@windows_only
def test_real_process_identity_of_this_process():
    found = REAL_PROCESS_IDENTITY(os.getpid())
    assert found.pid == os.getpid() and found.same_user
    assert os.path.basename(found.image).lower().startswith("python")


@windows_only
def test_real_process_that_does_not_exist_fails_closed():
    with pytest.raises(_winnet.OwnerCheckError):
        REAL_PROCESS_IDENTITY(0x7FFFFFF0)


@windows_only
def test_real_listener_that_is_not_the_app_is_refused(monkeypatch):
    """A real local listener (this python.exe) on the port: same user, but not IceScreenTouch.exe, so no token."""
    import socket

    monkeypatch.setattr(_winnet, "listening_pids", REAL_LISTENING_PIDS)
    monkeypatch.setattr(_winnet, "process_identity", REAL_PROCESS_IDENTITY)
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        port = listener.getsockname()[1]
        for find in (lambda: discovery.discover(f"http://127.0.0.1:{port}", TOKEN, {}),
                     lambda: discovery.discover(env=env_vars(port))):
            with pytest.raises(ist.StaleConnectionError, match="IceScreenTouch.exe"):
                find()
        assert discovery.discover(f"http://127.0.0.1:{port}", TOKEN, {}, verify_owner=False).port == port
