# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

import io

import pytest

import icescreentouch as ist
from icescreentouch import errors
from icescreentouch.models import image_size, parse_source_size


def encode(width, height, fmt, **options):
    from PIL import Image

    out = io.BytesIO()
    Image.new("RGB", (width, height), (200, 100, 50)).save(out, format=fmt, **options)
    return out.getvalue()


def test_image_size_png():
    assert image_size(encode(123, 456, "PNG")) == (123, 456)


@pytest.mark.parametrize("options", [{}, {"quality": 50}, {"progressive": True}, {"optimize": True}])
def test_image_size_jpeg(options):
    assert image_size(encode(300, 700, "JPEG", **options)) == (300, 700)


def test_image_size_unknown():
    assert image_size(b"") is None and image_size(b"GIF89a....") is None and image_size(b"\xff\xd8\xff") is None


def test_source_size():
    assert parse_source_size("1179x2556") == (1179, 2556)
    assert parse_source_size("bad") is None and parse_source_size(None) is None


def test_every_code_maps_to_one_class_with_the_right_status():
    seen = set()
    for code, status in errors._STATUS_BY_CODE.items():
        exc = errors.error_for_code(code, "detail")
        assert exc.code == code and exc.status == status and exc.detail == "detail"
        seen.add(type(exc))
    assert len(seen) == len(errors._STATUS_BY_CODE)  # no two codes share a class


def test_the_exception_texts_carry_the_code_and_detail():
    exc = errors.error_for_code("busy", "another operation is running")
    assert str(exc) == "busy: another operation is running"


def test_hierarchy():
    assert issubclass(ist.BusyError, ist.ApiError) and issubclass(ist.ApiError, ist.IceScreenTouchError)
    assert issubclass(ist.StaleConnectionError, ist.ConnectionDiscoveryError)
    assert issubclass(ist.ApiTimeoutError, TimeoutError) and issubclass(ist.ApiTimeoutError, ist.ApiUnavailableError)
    assert issubclass(ist.StreamInterruptedError, ist.IceScreenTouchError)


def test_closed_error_prefers_the_named_cause():
    cause = errors.error_for_code("busy", "x")
    assert errors.closed_error(1013, "busy", cause) is cause
    plain = errors.closed_error(1011, "internal error", None)
    assert isinstance(plain, ist.StreamClosedError) and plain.close_code == 1011 and "internal" in str(plain)
    assert errors.closed_error(None, "", None).close_code is None
