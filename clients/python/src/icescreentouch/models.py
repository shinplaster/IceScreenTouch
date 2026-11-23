# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Result types of the client."""

from __future__ import annotations

import re
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
    ocr: Optional["OcrInfo"] = None  # None: an app version without text recognition

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
            ocr=OcrInfo.from_json(data.get("ocr")),
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
            "ocr": None if self.ocr is None else self.ocr.to_dict(),
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


_SOURCE_SIZE = re.compile(r"([0-9]{1,6})x([0-9]{1,6})")


def parse_source_size(value: Optional[str]) -> Optional[Tuple[int, int]]:
    """``"1179x2556"`` to ``(1179, 2556)``."""
    if not value:
        return None
    match = _SOURCE_SIZE.fullmatch(value.strip().lower())
    return (int(match.group(1)), int(match.group(2))) if match else None



# ---- Text (OCR) ----------------------------------------------------------------------------------------------

Point = Tuple[float, float]


@dataclass(frozen=True)
class TextBox:
    """A rectangle in the space of the request (``screen`` 0..1 or ``pixel`` of ``ref``): top left and size."""

    x: float
    y: float
    width: float
    height: float

    @property
    def center(self) -> Point:
        return (self.x + self.width / 2, self.y + self.height / 2)

    @classmethod
    def from_json(cls, data: Mapping[str, Any]) -> "TextBox":
        return cls(float(data.get("x", 0)), float(data.get("y", 0)), float(data.get("width", 0)), float(data.get("height", 0)))


def _point(data: Any) -> Point:
    return (float(data.get("x", 0)), float(data.get("y", 0))) if isinstance(data, Mapping) else (0.0, 0.0)


def _confidence(data: Mapping[str, Any]) -> Optional[float]:
    value = data.get("confidence")
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else None


@dataclass(frozen=True)
class TextWord:
    """A word of a read line (only when ``read_text(words=True)``)."""

    text: str
    box: TextBox
    confidence: Optional[float] = None  # None: the engine gives none (Windows.Media.Ocr)


@dataclass(frozen=True)
class TextLine:
    """A line of text on the screen, in reading order."""

    text: str
    box: TextBox
    center: Point
    words: Tuple[TextWord, ...] = ()
    confidence: Optional[float] = None

    @classmethod
    def from_json(cls, data: Mapping[str, Any]) -> "TextLine":
        return cls(
            text=str(data.get("text", "")),
            box=TextBox.from_json(data.get("box") or {}),
            center=_point(data.get("center")),
            words=tuple(TextWord(str(w.get("text", "")), TextBox.from_json(w.get("box") or {}), _confidence(w))
                        for w in (data.get("words") or []) if isinstance(w, Mapping)),
            confidence=_confidence(data),
        )


@dataclass(frozen=True)
class TextMatch:
    """A place where the searched text is.

    Attributes:
        text: The words that matched.
        line_text: The whole line they are in.
        box: Their box; ``center`` is where ``tap_text`` taps (before ``offset``).
        box_level: ``"word"``, or ``"line"`` when the engine has no word boxes (the box is then the whole line's).
    """

    text: str
    line_text: str
    box: TextBox
    center: Point
    box_level: str = "word"
    confidence: Optional[float] = None

    @classmethod
    def from_json(cls, data: Mapping[str, Any]) -> "TextMatch":
        return cls(
            text=str(data.get("text", "")),
            line_text=str(data.get("lineText", "")),
            box=TextBox.from_json(data.get("box") or {}),
            center=_point(data.get("center")),
            box_level=str(data.get("boxLevel", "word")),
            confidence=_confidence(data),
        )


@dataclass(frozen=True)
class OcrEngineInfo:
    """The OCR engine that read the text (``id`` is ``"windows"``: Windows.Media.Ocr).

    ``kind``, ``word_boxes`` and ``confidence`` are known only in ``Status.ocr`` (None in a text result).
    """

    id: str
    name: str
    version: str
    kind: Optional[str] = None
    word_boxes: Optional[bool] = None
    confidence: Optional[bool] = None

    @classmethod
    def from_json(cls, data: Any) -> Optional["OcrEngineInfo"]:
        if not isinstance(data, Mapping):
            return None
        capabilities = data.get("capabilities") if isinstance(data.get("capabilities"), Mapping) else {}
        return cls(
            id=str(data.get("id", "")),
            name=str(data.get("name", "")),
            version=str(data.get("version", "")),
            kind=data.get("kind") if isinstance(data.get("kind"), str) else None,
            word_boxes=capabilities.get("wordBoxes") if isinstance(capabilities.get("wordBoxes"), bool) else None,
            confidence=capabilities.get("confidence") if isinstance(capabilities.get("confidence"), bool) else None,
        )


@dataclass(frozen=True)
class OcrInfo:
    """``Status.ocr``: whether text can be read, the engine and the OCR languages Windows has installed."""

    available: bool
    engine: Optional[OcrEngineInfo]
    engines: Tuple[str, ...]
    languages: Tuple[str, ...]
    default_language: Optional[str]

    @classmethod
    def from_json(cls, data: Any) -> Optional["OcrInfo"]:
        if not isinstance(data, Mapping):
            return None
        return cls(
            available=bool(data.get("available", False)),
            engine=OcrEngineInfo.from_json(data.get("engine")),
            engines=tuple(str(e.get("id", "")) for e in (data.get("engines") or []) if isinstance(e, Mapping) and e.get("available")),
            languages=tuple(str(lang) for lang in (data.get("languages") or [])),
            default_language=data.get("defaultLanguage") if isinstance(data.get("defaultLanguage"), str) else None,
        )

    def to_dict(self) -> Dict[str, Any]:
        return {
            "available": self.available,
            "engine": None if self.engine is None else {"id": self.engine.id, "name": self.engine.name, "version": self.engine.version},
            "engines": list(self.engines),
            "languages": list(self.languages),
            "default_language": self.default_language,
        }


@dataclass(frozen=True)
class TextPicture:
    """The picture the text was read from (as the headers of a screenshot describe it)."""

    width: int
    height: int
    orientation: str
    rotation: int

    @classmethod
    def from_json(cls, data: Any) -> "TextPicture":
        data = data if isinstance(data, Mapping) else {}
        return cls(int(data.get("width", 0)), int(data.get("height", 0)), str(data.get("orientation", "portrait")),
                   int(data.get("rotation", 0)))


@dataclass(frozen=True)
class TextResult:
    """``read_text()``: the lines of the screen in reading order.

    Attributes:
        language: The language the recognizer used (check it when nothing was found).
        space: ``"screen"`` or ``"pixel"``: the space of every box and centre.
        duration_ms: Capture and recognition.
    """

    language: str
    engine: Optional[OcrEngineInfo]
    picture: TextPicture
    space: str
    duration_ms: int
    lines: Tuple[TextLine, ...]
    raw: Mapping[str, Any] = field(repr=False, default_factory=dict)

    @classmethod
    def from_json(cls, data: Mapping[str, Any]) -> "TextResult":
        return cls(
            language=str(data.get("language", "")),
            engine=OcrEngineInfo.from_json(data.get("engine")),
            picture=TextPicture.from_json(data.get("picture")),
            space=str(data.get("space", "screen")),
            duration_ms=int(data.get("durationMs", 0)),
            lines=tuple(TextLine.from_json(line) for line in (data.get("lines") or []) if isinstance(line, Mapping)),
            raw=dict(data),
        )


@dataclass(frozen=True)
class TapTextResult:
    """``tap_text()``: what was tapped.

    Attributes:
        tapped: The match that was tapped.
        at: Where the finger went down (``tapped.center`` plus the offset), in ``space``.
        match_count: The matches on the picture the tap was decided on.
        attempts: The readings made (with the one that confirmed the match).
    """

    language: str
    engine: Optional[OcrEngineInfo]
    picture: TextPicture
    space: str
    duration_ms: int
    attempts: int
    tapped: TextMatch
    at: Point
    match_count: int
    raw: Mapping[str, Any] = field(repr=False, default_factory=dict)

    @classmethod
    def from_json(cls, data: Mapping[str, Any]) -> "TapTextResult":
        tapped = data.get("tapped") if isinstance(data.get("tapped"), Mapping) else {}
        return cls(
            language=str(data.get("language", "")),
            engine=OcrEngineInfo.from_json(data.get("engine")),
            picture=TextPicture.from_json(data.get("picture")),
            space=str(data.get("space", "screen")),
            duration_ms=int(data.get("durationMs", 0)),
            attempts=int(data.get("attempts", 0)),
            tapped=TextMatch.from_json(tapped),
            at=_point(tapped.get("at")),
            match_count=int(data.get("matchCount", 0)),
            raw=dict(data),
        )
