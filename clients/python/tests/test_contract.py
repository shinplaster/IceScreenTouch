# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""The fakes are held to docs/api/openapi.yaml: these tests show that the checks themselves bite."""

import json

import openapi_check
import pytest
from conftest import STATUS, TOKEN, problem

import icescreentouch as ist


def test_a_request_the_real_server_would_reject_is_caught(api):
    api.route("POST", "/v1/touch/tap", {})
    # Bypass the client's own checks: send a body with a member the schema does not have.
    with pytest.raises(ist.InvalidRequestError):
        api.client()._json("POST", "/v1/touch/tap", {"x": 0.5, "y": 0.5, "pressure": 1})
    assert any("pressure" in e for e in api.schema_errors)
    api.schema_errors.clear()  # expected here: the fixture would fail the test otherwise


def test_a_pixel_request_without_ref_is_caught(api):
    with pytest.raises(ist.InvalidRequestError):
        api.client()._json("POST", "/v1/touch/tap", {"x": 1, "y": 1, "space": "pixel"})
    assert api.schema_errors
    api.schema_errors.clear()


def test_an_invalid_answer_of_the_fake_is_caught(api):
    api.route("GET", "/v1/status", {"version": "x"})
    try:
        api.client().get_status()  # the client may or may not cope with it; the fake must have noticed
    except ist.IceScreenTouchError:
        pass
    assert any("required" in e for e in api.schema_errors)
    api.schema_errors.clear()


def test_every_valid_answer_passes():
    assert openapi_check.response_errors("GET", "/v1/status", 200, "application/json", json.dumps(STATUS).encode()) == []
    assert openapi_check.response_errors("POST", "/v1/home", 409, "application/problem+json",
                                         problem(409, "busy").body) == []


@pytest.mark.parametrize("key", ["a", "Enter", "ArrowUp", "F5", "5", ";", " ", "escape"])
def test_real_key_names_pass(key):
    assert openapi_check.key_errors(key) == []


@pytest.mark.parametrize("key", ["Shift", "A", "!", "Ctrl", "F13", "", "ab", "é"])
def test_names_the_real_server_refuses_are_caught(key):
    assert openapi_check.key_errors(key)


def test_a_stream_message_is_checked_against_the_schema():
    good = {"op": "down", "seq": 1, "space": "pixel", "ref": {"width": 390, "height": 844},
            "contacts": [{"id": 0, "x": 1, "y": 2}]}
    assert openapi_check.stream_message_errors(good) == []
    del good["ref"]
    assert openapi_check.stream_message_errors(good)  # pixel needs ref
    assert openapi_check.stream_message_errors({"op": "auth", "token": TOKEN}) == []
    assert openapi_check.stream_message_errors({"op": "up"})  # seq is required
    assert openapi_check.stream_message_errors({"op": "key", "seq": 1, "key": "Shift", "down": True})
