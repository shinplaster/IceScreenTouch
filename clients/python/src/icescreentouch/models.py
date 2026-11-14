# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Result types of the client."""

from __future__ import annotations

import struct
from dataclasses import dataclass, field
from typing import Any, Dict, Mapping, Optional, Tuple


@dataclass(frozen=True)
class Device:
    """A phone or tablet that connected over BLE."""

    id: str
    name: str
    model: Optional[str]
    subscribed: bool


@dataclass(frozen=True)
class Picture:
    """The picture of the device as the receiver window shows it (PC pixels)."""

    width: int
    height: int
    orientation: str  # "portrait" or "landscape"

    @property
    def size(self) -> Tuple[int, int]:
        return (self.width, self.height)


@dataclass(frozen=True)
class RotationInfo:
    """The app's assumption of the device rotation in degrees: landscape 90 or 270, portrait 0 or 180."""

    landscape: int
    portrait: int


@dataclass(frozen=True)
class Status:
    """``GET /v1/status``."""

    version: str
    api_version: int
    running: bool
    ble_state: str
    devices: Tuple[Device, ...]
    target_device_id: Optional[str]
    receiver: Optional[str]
    picture: Optional[Picture]
    rotation: RotationInfo
    operating: bool
    raw: Mapping[str, Any] = field(repr=False, default_factory=dict)

    @classmethod
    def from_json(cls, data: Mapping[str, Any]) -> "Status":
        picture = data.get("picture")
        rotation = data.get("rotation") or {}
        return cls(
            version=str(data.get("version", "")),
            api_version=int(data.get("apiVersion", 0)),
            running=bool(data.get("running", False)),
            ble_state=str(data.get("bleState", "")),
            devices=tuple(
                Device(id=str(d.get("id", "")), name=str(d.get("name", "")), model=d.get("model"),
                       subscribed=bool(d.get("subscribed", False)))
                for d in (data.get("devices") or [])),
            target_device_id=data.get("targetDeviceId"),
            receiver=data.get("receiver"),
            picture=Picture(int(picture["width"]), int(picture["height"]),
                            str(picture.get("orientation") or ("landscape" if picture["width"] > picture["height"] else "portrait")))
            if isinstance(picture, Mapping) else None,
            rotation=RotationInfo(int(rotation.get("landscape", 90)), int(rotation.get("portrait", 0))),
            operating=bool(data.get("operating", False)),
            raw=dict(data),
        )

    def to_dict(self) -> Dict[str, Any]:
        """A plain, JSON-ready dict (snake_case)."""
        return {
            "version": self.version,
            "api_version": self.api_version,
            "running": self.running,
            "ble_state": self.ble_state,
            "devices": [{"id": d.id, "name": d.name, "model": d.model, "subscribed": d.subscribed} for d in self.devices],
            "target_device_id": self.target_device_id,
            "receiver": self.receiver,
            "picture": None if self.picture is None else {
                "width": self.picture.width, "height": self.picture.height, "orientation": self.picture.orientation},
            "rotation": {"landscape": self.rotation.landscape, "portrait": self.rotation.portrait},
            "operating": self.operating,
        }


@dataclass(frozen=True)
class Screenshot:
    """A screenshot of the device picture.

    Attributes:
        data: The encoded image (PNG or JPEG).
        format: ``"png"`` or ``"jpeg"``.
        size: ``(width, height)`` in pixels of ``data`` (what a ``pixel`` position must use as ``ref``).
        orientation: ``"portrait"`` or ``"landscape"`` (as the app sees the picture), or None.
        rotation: The rotation (0, 90, 180, 270) the app assumes, or None.
        source_size: The size before the app shrank it (``maxSize``), or None.
    """

    data: bytes = field(repr=False)
    format: str
    size: Tuple[int, int]
    orientation: Optional[str] = None
    rotation: Optional[int] = None
    source_size: Optional[Tuple[int, int]] = None

    @property
    def width(self) -> int:
        return self.size[0]

    @property
    def height(self) -> int:
        return self.size[1]

    @property
    def content_type(self) -> str:
        return "image/png" if self.format == "png" else "image/jpeg"

    def save(self, path: str) -> None:
        """Writes the image file."""
        with open(path, "wb") as handle:
            handle.write(self.data)

    def to_pil(self) -> Any:
        """The screenshot as a ``PIL.Image`` (needs the ``image`` extra: ``pip install icescreentouch[image]``)."""
        try:
            from PIL import Image
        except ImportError as exc:  # pragma: no cover - depends on the environment
            raise ImportError("Screenshot.to_pil() needs Pillow: pip install icescreentouch[image]") from exc
        import io

        image = Image.open(io.BytesIO(self.data))
        image.load()
        return image


def image_size(data: bytes) -> Optional[Tuple[int, int]]:
    """The (width, height) of a PNG or JPEG from its header (no image library needed), or None."""
    if data[:8] == b"\x89PNG\r\n\x1a\n" and len(data) >= 24 and data[12:16] == b"IHDR":
        width, height = struct.unpack(">II", data[16:24])
        return (width, height)
    if data[:2] == b"\xff\xd8":
        index = 2
        length = len(data)
        while index + 4 <= length:
            if data[index] != 0xFF:
                index += 1
                continue
            marker = data[index + 1]
            if marker == 0xFF:  # fill byte
                index += 1
                continue
            if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:  # markers without a length
                index += 2
                continue
            (segment,) = struct.unpack(">H", data[index + 2:index + 4])
            # SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC) carry the frame size.
            if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
                if index + 9 > length:
                    return None
                height, width = struct.unpack(">HH", data[index + 5:index + 9])
                return (width, height)
            index += 2 + segment
    return None


def parse_source_size(value: Optional[str]) -> Optional[Tuple[int, int]]:
    """``"1179x2556"`` to ``(1179, 2556)``."""
    if not value:
        return None
    try:
        width, height = value.lower().split("x")
        return (int(width), int(height))
    except ValueError:
        return None

