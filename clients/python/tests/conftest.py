# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Fake servers that answer like the real API (shapes copied from src/IceScreenTouch.Api and its tests).

The fakes hold the client to the contract: every REST request body is validated against docs/api/openapi.yaml (see
openapi_check.py) and so is every JSON answer a test makes the fake send. A violation is recorded in ``schema_errors``
(the fake answers 400 ``invalid_request`` to a bad request, like the real server) and fails the test at teardown, even
when the test swallowed the resulting exception.
"""

import json
import threading
import time
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Callable, Dict, List, Optional, Tuple

import pytest

import openapi_check
from icescreentouch import Client, _winnet

TOKEN = "T" * 43  # the real token is 43 Base64url characters
OTHER_TOKEN = "O" * 43

STATUS = {
    "version": "0.3.0",
    "apiVersion": 1,
    "running": True,
    "bleState": "connected",
    "devices": [{"id": "iphone-1", "name": "iPhone", "model": "iPhone15,2", "subscribed": True}],
    "targetDeviceId": "iphone-1",
    "receiver": "UxPlay",
    "picture": {"width": 390, "height": 844, "isLandscape": False, "orientation": "portrait", "aspect": 0.462},
    "rotation": {"landscape": 90, "portrait": 0},
    "operating": False,
}


def problem(status: int, code: str, detail: str = "something went wrong", *, check: bool = True) -> "Reply":
    titles = {400: "Bad Request", 401: "Unauthorized", 403: "Forbidden", 404: "Not Found", 409: "Conflict",
              429: "Too Many Requests", 499: "Client Closed Request", 500: "Internal Server Error"}
    body = {"type": "about:blank", "title": titles.get(status, "Error"), "status": status, "detail": detail, "code": code}
    return Reply(status, "application/problem+json; charset=utf-8", json.dumps(body).encode(), validate=check)


@dataclass
class Reply:
    status: int = 200
    content_type: str = "application/json; charset=utf-8"
    body: bytes = b"{}"
    headers: Dict[str, str] = field(default_factory=dict)
    validate: bool = True  # False for an answer the real server never sends (a proxy's page, a future error code)


@dataclass
class Received:
    method: str
    path: str
    headers: Dict[str, str]  # lower-case names
    body: bytes

    @property
    def json(self) -> Any:
        return json.loads(self.body.decode("utf-8"))


class FakeApi:
    """An HTTP server on 127.0.0.1 with the real server's checks (Origin, Host, bearer token, problem+json)."""

    def __init__(self, token: str = TOKEN) -> None:
        self.token = token
        self.requests: List[Received] = []
        self.routes: Dict[Tuple[str, str], Callable[[Received], Any]] = {}
        self.schema_errors: List[str] = []
        api = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args: Any) -> None:  # silence
                pass

            def _handle(self) -> None:
                length = int(self.headers.get("Content-Length") or 0)
                body = self.rfile.read(length) if length else b""
                request = Received(self.command, self.path, {k.lower(): v for k, v in self.headers.items()}, body)
                api.requests.append(request)
                reply = api._answer(request)
                if not isinstance(reply, Reply):
                    reply = Reply(200, body=json.dumps(reply).encode())
                if reply.validate:
                    path = request.path.split("?")[0]
                    for error in openapi_check.response_errors(request.method, path, reply.status, reply.content_type,
                                                              reply.body):
                        api.schema_errors.append(f"answer to {request.method} {path}: {error}")
                self.send_response(reply.status)
                self.send_header("Content-Type", reply.content_type)
                self.send_header("Content-Length", str(len(reply.body)))
                for name, value in reply.headers.items():
                    self.send_header(name, value)
                self.end_headers()
                self.wfile.write(reply.body)

            do_GET = do_POST = do_PUT = _handle

        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self._server.daemon_threads = True
        self.port = self._server.server_address[1]
        self._thread = threading.Thread(target=self._server.serve_forever, kwargs={"poll_interval": 0.02}, daemon=True)

    def _answer(self, request: Received) -> Any:
        if "origin" in request.headers:
            return problem(403, "forbidden_origin")
        if request.headers.get("host") not in (f"127.0.0.1:{self.port}", f"localhost:{self.port}"):
            return problem(403, "forbidden_origin")
        path = request.path.split("?")[0]
        anonymous = path == "/v1/openapi.json"
        if not anonymous and request.headers.get("authorization") != f"Bearer {self.token}":
            return problem(401, "unauthorized", "a valid bearer token is required")
        route = self.routes.get((request.method, path))
        if route is None:
            return problem(404, "not_found", "no such endpoint")
        errors = openapi_check.request_errors(request.method, path, request.body, request.headers.get("content-type"))
        if request.method == "POST" and path == "/v1/keys/press" and not errors:
            body = request.json
            errors = openapi_check.key_errors(body.get("key"), body.get("modifiers"))
        if errors:
            self.schema_errors.extend(f"{request.method} {path}: {error}" for error in errors)
            return problem(400, "invalid_request", "the request does not match the API specification")
        return route(request) if callable(route) else route

    def route(self, method: str, path: str, reply: Any) -> None:
        self.routes[(method, path)] = reply

    @property
    def url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    def client(self, **kwargs: Any) -> Client:
        return Client(base_url=self.url, token=self.token, **kwargs)

    def start(self) -> "FakeApi":
        self._thread.start()
        return self

    def stop(self) -> None:
        self._server.shutdown()
        self._server.server_close()


@pytest.fixture
def api():
    fake = FakeApi().start()
    # The real server's answers for the plain operations.
    fake.route("GET", "/v1/status", STATUS)
    fake.route("GET", "/v1/rotation", {"landscape": 90, "portrait": 0})
    for path in ("/v1/touch/tap", "/v1/touch/long-press", "/v1/touch/swipe", "/v1/touch/path", "/v1/touch/pinch",
                 "/v1/keys/press", "/v1/home"):
        fake.route("POST", path, {})
    fake.route("PUT", "/v1/target", {})
    fake.route("POST", "/v1/keys/type", {"skipped": ""})
    fake.route("POST", "/v1/cancel", {"cancelled": False})
    yield fake
    fake.stop()
    assert fake.schema_errors == [], "the client and the fake disagree with docs/api/openapi.yaml"


@pytest.fixture(autouse=True)
def no_real_tcp_table(monkeypatch):
    """By default the owner check cannot run (as on a non-Windows machine); tests that need it set their own."""
    monkeypatch.setattr(_winnet, "listening_pids", lambda port: None)
    monkeypatch.setattr(_winnet, "process_identity", lambda pid: None)


def wait_until(condition: Callable[[], bool], timeout: float = 3.0) -> bool:
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if condition():
            return True
        time.sleep(0.01)
    return condition()
