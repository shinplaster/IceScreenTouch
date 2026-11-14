# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Checks what the client sends and what the fakes answer against docs/api/openapi.yaml (the contract of the real server).

OpenAPI 3.1 schemas are JSON Schema 2020-12, so ``jsonschema`` validates them directly; ``$ref`` pointers are resolved
against the whole document through a ``referencing`` registry. The fake servers of the tests call these functions for
every request and every stream message, so a client that sends something the real server rejects fails the tests.
"""

import json
import re
from pathlib import Path
from typing import Any, List, Optional
from urllib.parse import parse_qsl

import yaml
from jsonschema import Draft202012Validator
from referencing import Registry, Resource
from referencing.jsonschema import DRAFT202012

SPEC_PATH = Path(__file__).resolve().parents[3] / "docs" / "api" / "openapi.yaml"
_URI = "urn:icescreentouch:openapi"

with open(SPEC_PATH, encoding="utf-8") as _handle:
    SPEC = yaml.safe_load(_handle)

_REGISTRY = Registry().with_resource(_URI, Resource.from_contents(SPEC, default_specification=DRAFT202012))


def _escape(part: str) -> str:
    return part.replace("~", "~0").replace("/", "~1")


def _resolve(node: Any, walked: List[str]):
    """Follows ``$ref`` (local pointers only) from ``node``, which sits at ``walked``."""
    while isinstance(node, dict) and isinstance(node.get("$ref"), str):
        walked = [p.replace("~1", "/").replace("~0", "~") for p in node["$ref"][2:].split("/")]
        node = _node(walked)
    return node, walked


def _follow(parts: List[str]) -> Optional[List[str]]:
    """The pointer (as parts) to the node at ``parts``, with every ``$ref`` on the way followed. None when missing."""
    node: Any = SPEC
    walked: List[str] = []
    for part in parts:
        node, walked = _resolve(node, walked)
        if not isinstance(node, dict) or part not in node:
            return None
        node, walked = node[part], walked + [part]
    return _resolve(node, walked)[1]


def _node(parts: List[str]) -> Any:
    node: Any = SPEC
    for part in parts:
        node = node[part]
    return node


def _validator(parts: List[str]) -> Draft202012Validator:
    pointer = "/".join(_escape(p) for p in parts)
    return Draft202012Validator({"$ref": f"{_URI}#/{pointer}"}, registry=_REGISTRY)


def _errors(parts: List[str], instance: Any) -> List[str]:
    found = []
    for error in _validator(parts).iter_errors(instance):
        where = "/".join(str(p) for p in error.absolute_path) or "(root)"
        found.append(f"{where}: {error.message[:200]}")
    return found


def schema_errors(name: str, instance: Any) -> List[str]:
    """The problems of ``instance`` against ``components.schemas.<name>`` (an empty list when it is valid)."""
    return _errors(["components", "schemas", name], instance)


def request_errors(method: str, path: str, body: bytes, content_type: Optional[str]) -> List[str]:
    """The problems of a REST request body against the operation's ``requestBody`` in the spec."""
    operation = SPEC["paths"].get(path, {}).get(method.lower())
    if operation is None:
        return []  # not an operation of the spec (the fake answers 404 for it)
    request_body = operation.get("requestBody")
    if request_body is None:
        return ["the operation takes no body but one was sent"] if body else []
    if not body:
        return ["the body is required"] if request_body.get("required") else []
    if (content_type or "").split(";")[0].strip().lower() != "application/json":
        return [f"Content-Type must be application/json, not {content_type!r}"]
    try:
        instance = json.loads(body.decode("utf-8"))
    except ValueError as exc:
        return [f"the body is not JSON ({exc})"]
    return _errors(["paths", path, method.lower(), "requestBody", "content", "application/json", "schema"], instance)


def query_errors(method: str, path: str, query: str) -> List[str]:
    """The problems of a URL query string against the operation's ``parameters`` (``in: query``) in the spec.

    Names are case-sensitive; an undeclared or repeated name is refused; an integer parameter is plain decimal digits; every
    value must satisfy its schema. What the schema cannot say is left to the operation (for the screenshot: ``quality`` only
    with ``format=jpeg``).
    """
    operation = SPEC["paths"].get(path, {}).get(method.lower())
    if operation is None:
        return []
    declared = {p["name"]: p for p in operation.get("parameters", []) if p.get("in") == "query"}
    pairs = parse_qsl(query, keep_blank_values=True)
    problems: List[str] = []
    for name, value in pairs:
        parameter = declared.get(name)
        if parameter is None:
            problems.append(f"the query parameter {name!r} is not in the spec")
            continue
        if [n for n, _ in pairs].count(name) > 1:
            if not any(f"{name!r} is given more than once" in p for p in problems):
                problems.append(f"the query parameter {name!r} is given more than once")
            continue
        schema = parameter["schema"]
        instance: Any = value
        if schema.get("type") == "integer":
            if not re.fullmatch(r"[0-9]+", value):
                problems.append(f"{name}: {value!r} is not plain decimal digits")
                continue
            instance = int(value)
        for error in Draft202012Validator(schema).iter_errors(instance):
            problems.append(f"{name}: {error.message[:200]}")
    for name, parameter in declared.items():
        if parameter.get("required") and name not in [n for n, _ in pairs]:
            problems.append(f"the query parameter {name!r} is required")
    if path == "/v1/screenshot":
        values = dict(pairs)
        if "quality" in values and values.get("format", "png") != "jpeg":
            problems.append("quality only applies to format=jpeg")
    return problems


def response_errors(method: str, path: str, status: int, content_type: str, body: bytes) -> List[str]:
    """The problems of a JSON answer against the spec: a 200 against the operation's schema, an error against Problem."""
    if "json" not in content_type:
        return []
    try:
        instance = json.loads(body.decode("utf-8"))
    except ValueError as exc:
        return [f"the body is not JSON ({exc})"]
    if status >= 400:
        if "problem+json" not in content_type:
            return [f"an error must be application/problem+json, not {content_type!r}"]
        problems = schema_errors("Problem", instance)
        if isinstance(instance, dict) and instance.get("status") != status:
            problems.append("status: differs from the HTTP status")
        return problems
    parts = _follow(["paths", path, method.lower(), "responses", str(status), "content", "application/json", "schema"])
    if parts is None:
        return [] if path not in SPEC["paths"] else [f"{method} {path} documents no JSON answer for {status}"]
    return _errors(parts, instance)


def stream_message_errors(message: Any) -> List[str]:
    """The problems of a message the client sent on ``/v1/stream`` (also its key names)."""
    problems = schema_errors("StreamClientMessage", message)
    if isinstance(message, dict) and message.get("op") == "key":
        problems += key_errors(message.get("key"), message.get("modifiers"))
    return problems


def stream_event_errors(event: Any) -> List[str]:
    """The problems of an event a fake server sent on ``/v1/stream``."""
    return schema_errors("StreamEvent", event)


_SPECIAL_KEYS = {"enter", "escape", "backspace", "tab", "space", "delete", "home", "end", "arrowup", "arrowdown",
                 "arrowleft", "arrowright", "pageup", "pagedown"} | {f"f{i}" for i in range(1, 13)}
_BASIC_CHARACTERS = set("abcdefghijklmnopqrstuvwxyz0123456789`-=[]\\;',./ ")


def key_errors(key: Any, modifiers: Any = None) -> List[str]:
    """What the schema cannot say: the key names the real server accepts (``ApiKeys.TryParse``).

    A special name (case-insensitive), or one character of the unshifted US layout. A character that needs Shift (``A``,
    ``!``) and the name of a modifier (``Shift``) are refused: send the base key with the ``shift`` modifier.
    """
    if not isinstance(key, str) or not key:
        return ["key is required"]
    if key.lower() in _SPECIAL_KEYS or key in _BASIC_CHARACTERS:
        return []
    return [f"unknown key {key!r} (use a letter, digit, basic symbol or a name such as Enter, ArrowUp, F5)"]
