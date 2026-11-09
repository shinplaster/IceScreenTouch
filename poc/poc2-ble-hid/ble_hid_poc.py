# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""PoC 2: Windows PC as a BLE HID peripheral that injects input into an iPhone.

The PC advertises HID-over-GATT (service 0x1812) with two input reports:
  * Report ID 1: relative mouse   (works with iOS AssistiveTouch)
  * Report ID 2: absolute 2-finger touch digitizer (0..10000 per axis)

Goal of the PoC: find out which of the two paths iOS actually accepts.
Run `python ble_hid_poc.py --help` for usage.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
import time
import uuid

from winrt.windows.devices.bluetooth import BluetoothAdapter
from winrt.windows.devices.bluetooth.genericattributeprofile import (
    GattCharacteristicProperties,
    GattLocalCharacteristic,
    GattLocalCharacteristicParameters,
    GattLocalDescriptorParameters,
    GattProtectionLevel,
    GattServiceProvider,
    GattServiceProviderAdvertisingParameters,
    GattServiceProviderAdvertisementStatus,
    GattWriteOption,
)
from winrt.windows.storage.streams import DataReader, DataWriter

from probe_server import ProbeServer

# ---------------------------------------------------------------------------
# GATT UUIDs
# ---------------------------------------------------------------------------


def u16(short: int) -> uuid.UUID:
    return uuid.UUID(f"0000{short:04x}-0000-1000-8000-00805f9b34fb")


SVC_HID = u16(0x1812)
SVC_BATTERY = u16(0x180F)
SVC_DEVINFO = u16(0x180A)

CHR_HID_INFO = u16(0x2A4A)
CHR_REPORT_MAP = u16(0x2A4B)
CHR_HID_CONTROL_POINT = u16(0x2A4C)
CHR_REPORT = u16(0x2A4D)
CHR_PROTOCOL_MODE = u16(0x2A4E)
CHR_BATTERY_LEVEL = u16(0x2A19)
CHR_MANUFACTURER = u16(0x2A29)
CHR_PNP_ID = u16(0x2A50)
DSC_REPORT_REFERENCE = u16(0x2908)

# ---------------------------------------------------------------------------
# HID report descriptor
# ---------------------------------------------------------------------------

AXIS_MAX = 10000  # logical range of the absolute touch axes (0x2710)
MAX_CONTACTS = 2

# Report ID 1: relative mouse (3 buttons + X/Y/wheel as int8).
MOUSE_DESC = bytes(
    [
        0x05, 0x01,        # Usage Page (Generic Desktop)
        0x09, 0x02,        # Usage (Mouse)
        0xA1, 0x01,        # Collection (Application)
        0x85, 0x01,        #   Report ID (1)
        0x09, 0x01,        #   Usage (Pointer)
        0xA1, 0x00,        #   Collection (Physical)
        0x05, 0x09,        #     Usage Page (Button)
        0x19, 0x01,        #     Usage Minimum (1)
        0x29, 0x03,        #     Usage Maximum (3)
        0x15, 0x00,        #     Logical Minimum (0)
        0x25, 0x01,        #     Logical Maximum (1)
        0x95, 0x03,        #     Report Count (3)
        0x75, 0x01,        #     Report Size (1)
        0x81, 0x02,        #     Input (Data,Var,Abs)
        0x95, 0x01,        #     Report Count (1)
        0x75, 0x05,        #     Report Size (5)
        0x81, 0x03,        #     Input (Const,Var,Abs) - padding
        0x05, 0x01,        #     Usage Page (Generic Desktop)
        0x09, 0x30,        #     Usage (X)
        0x09, 0x31,        #     Usage (Y)
        0x09, 0x38,        #     Usage (Wheel)
        0x15, 0x81,        #     Logical Minimum (-127)
        0x25, 0x7F,        #     Logical Maximum (127)
        0x75, 0x08,        #     Report Size (8)
        0x95, 0x03,        #     Report Count (3)
        0x81, 0x06,        #     Input (Data,Var,Rel)
        0xC0,              #   End Collection
        0xC0,              # End Collection
    ]
)


def _finger_desc() -> bytes:
    lo, hi = AXIS_MAX & 0xFF, (AXIS_MAX >> 8) & 0xFF
    return bytes(
        [
            0x05, 0x0D,        #   Usage Page (Digitizer)
            0x09, 0x22,        #   Usage (Finger)
            0xA1, 0x02,        #   Collection (Logical)
            0x09, 0x42,        #     Usage (Tip Switch)
            0x15, 0x00,        #     Logical Minimum (0)
            0x25, 0x01,        #     Logical Maximum (1)
            0x75, 0x01,        #     Report Size (1)
            0x95, 0x01,        #     Report Count (1)
            0x81, 0x02,        #     Input (Data,Var,Abs)
            0x75, 0x07,        #     Report Size (7)
            0x95, 0x01,        #     Report Count (1)
            0x81, 0x03,        #     Input (Const) - padding
            0x09, 0x51,        #     Usage (Contact Identifier)
            0x75, 0x08,        #     Report Size (8)
            0x95, 0x01,        #     Report Count (1)
            0x15, 0x00,        #     Logical Minimum (0)
            0x26, 0xFF, 0x00,  #     Logical Maximum (255)
            0x81, 0x02,        #     Input (Data,Var,Abs)
            0x05, 0x01,        #     Usage Page (Generic Desktop)
            0x09, 0x30,        #     Usage (X)
            0x15, 0x00,        #     Logical Minimum (0)
            0x26, lo, hi,      #     Logical Maximum (AXIS_MAX)
            0x75, 0x10,        #     Report Size (16)
            0x95, 0x01,        #     Report Count (1)
            0x81, 0x02,        #     Input (Data,Var,Abs)
            0x09, 0x31,        #     Usage (Y)
            0x81, 0x02,        #     Input (Data,Var,Abs)
            0xC0,              #   End Collection
        ]
    )


# Report ID 2: absolute touch screen, MAX_CONTACTS fingers + contact count.
TOUCH_DESC = (
    bytes(
        [
            0x05, 0x0D,        # Usage Page (Digitizer)
            0x09, 0x04,        # Usage (Touch Screen)
            0xA1, 0x01,        # Collection (Application)
            0x85, 0x02,        #   Report ID (2)
        ]
    )
    + _finger_desc() * MAX_CONTACTS
    + bytes(
        [
            0x05, 0x0D,        #   Usage Page (Digitizer)
            0x09, 0x54,        #   Usage (Contact Count)
            0x15, 0x00,        #   Logical Minimum (0)
            0x25, MAX_CONTACTS,  # Logical Maximum
            0x75, 0x08,        #   Report Size (8)
            0x95, 0x01,        #   Report Count (1)
            0x81, 0x02,        #   Input (Data,Var,Abs)
            0xC0,              # End Collection
        ]
    )
)

REPORT_MAP = MOUSE_DESC + TOUCH_DESC


def mouse_report(dx: int = 0, dy: int = 0, wheel: int = 0, buttons: int = 0) -> bytes:
    def s8(v: int) -> int:
        return max(-127, min(127, v)) & 0xFF

    return bytes([buttons & 0x07, s8(dx), s8(dy), s8(wheel)])


def touch_report(contacts: list[tuple]) -> bytes:
    """contacts: list of (contact_id, x, y[, tip]); tip defaults to True (finger down).

    A lift must be reported as tip=False at the LAST position. Sending (0, 0) for a released
    finger makes iOS see the finger jump to the top-left corner before it is lifted.
    """
    out = bytearray()
    for i in range(MAX_CONTACTS):
        if i < len(contacts):
            cid, x, y = contacts[i][:3]
            tip = contacts[i][3] if len(contacts[i]) > 3 else True
            x = max(0, min(AXIS_MAX, x))
            y = max(0, min(AXIS_MAX, y))
            out += bytes([0x01 if tip else 0x00, cid & 0xFF]) + x.to_bytes(2, "little") + y.to_bytes(2, "little")
        else:
            out += bytes([0x00, 0x00, 0, 0, 0, 0])
    out.append(len(contacts))
    return bytes(out)


# ---------------------------------------------------------------------------
# WinRT helpers
# ---------------------------------------------------------------------------


def to_buffer(data: bytes):
    writer = DataWriter()
    writer.write_bytes(data)
    return writer.detach_buffer()


def from_buffer(buf) -> bytes:
    reader = DataReader.from_buffer(buf)
    out = bytearray(buf.length)
    reader.read_bytes(out)
    return bytes(out)


class HidPeripheral:
    """Publishes a HID-over-GATT service and sends input reports by notification."""

    def __init__(self, loop: asyncio.AbstractEventLoop, name: str = "iScrMirror PoC") -> None:
        self.loop = loop
        self.name = name
        self.providers: list[GattServiceProvider] = []
        self.report_chars: dict[int, GattLocalCharacteristic] = {}
        self.last_report: dict[int, bytes] = {
            1: mouse_report(),
            2: touch_report([]),
        }
        self.protocol_mode = 1  # Report protocol
        self.contacts: list[tuple[int, int, int]] = []  # fingers currently down

    # -- construction -------------------------------------------------------

    async def _provider(self, svc: uuid.UUID) -> GattServiceProvider:
        result = await GattServiceProvider.create_async(svc)
        if int(result.error) != 0:  # BluetoothError.SUCCESS == 0
            raise RuntimeError(f"create service {svc}: BluetoothError={result.error}")
        self.providers.append(result.service_provider)
        return result.service_provider

    @staticmethod
    async def _static_char(provider, chr_uuid, props, value: bytes, encrypted: bool = False):
        params = GattLocalCharacteristicParameters()
        params.characteristic_properties = props
        params.static_value = to_buffer(value)
        if encrypted:
            params.read_protection_level = GattProtectionLevel.ENCRYPTION_REQUIRED
        result = await provider.service.create_characteristic_async(chr_uuid, params)
        if int(result.error) != 0:
            raise RuntimeError(f"create characteristic {chr_uuid}: BluetoothError={result.error}")
        return result.characteristic

    async def build(self) -> None:
        # Device Information: manufacturer + PnP ID (vendor source 2 = USB-IF, Apple-agnostic).
        # Windows reserves 0x180A (DisabledByPolicy), so this is best-effort only.
        try:
            di = await self._provider(SVC_DEVINFO)
            await self._static_char(di, CHR_MANUFACTURER, GattCharacteristicProperties.READ, b"iScrMirror")
            pnp = bytes([0x02, 0x6D, 0x04, 0x01, 0x00, 0x01, 0x00])
            await self._static_char(di, CHR_PNP_ID, GattCharacteristicProperties.READ, pnp)
        except RuntimeError as exc:
            print("note: Device Information service skipped:", exc)

        # Battery: required by the HOGP spec for most hosts (best-effort as well).
        try:
            bat = await self._provider(SVC_BATTERY)
            await self._static_char(bat, CHR_BATTERY_LEVEL, GattCharacteristicProperties.READ, bytes([100]))
        except RuntimeError as exc:
            print("note: Battery service skipped:", exc)

        # HID service.
        hid = await self._provider(SVC_HID)
        enc = True
        # HID Information: bcdHID 1.11, country 0, flags = normally connectable.
        await self._static_char(
            hid, CHR_HID_INFO, GattCharacteristicProperties.READ, bytes([0x11, 0x01, 0x00, 0x02]), enc
        )
        await self._static_char(
            hid, CHR_REPORT_MAP, GattCharacteristicProperties.READ, REPORT_MAP, enc
        )

        # HID Control Point (write without response).
        cp_params = GattLocalCharacteristicParameters()
        cp_params.characteristic_properties = GattCharacteristicProperties.WRITE_WITHOUT_RESPONSE
        cp_params.write_protection_level = GattProtectionLevel.ENCRYPTION_REQUIRED
        cp = await hid.service.create_characteristic_async(CHR_HID_CONTROL_POINT, cp_params)
        self._check(cp, "control point")

        # Protocol Mode (read / write without response).
        pm_params = GattLocalCharacteristicParameters()
        pm_params.characteristic_properties = (
            GattCharacteristicProperties.READ | GattCharacteristicProperties.WRITE_WITHOUT_RESPONSE
        )
        pm_params.read_protection_level = GattProtectionLevel.ENCRYPTION_REQUIRED
        pm_params.write_protection_level = GattProtectionLevel.ENCRYPTION_REQUIRED
        pm = await hid.service.create_characteristic_async(CHR_PROTOCOL_MODE, pm_params)
        self._check(pm, "protocol mode")
        pm.characteristic.add_read_requested(self._make_read_handler(lambda: bytes([self.protocol_mode])))
        pm.characteristic.add_write_requested(self._write_handler)

        # One input Report characteristic per report ID, each with a Report Reference descriptor.
        for report_id in (1, 2):
            params = GattLocalCharacteristicParameters()
            params.characteristic_properties = (
                GattCharacteristicProperties.READ | GattCharacteristicProperties.NOTIFY
            )
            params.read_protection_level = GattProtectionLevel.ENCRYPTION_REQUIRED
            res = await hid.service.create_characteristic_async(CHR_REPORT, params)
            self._check(res, f"report {report_id}")
            ch = res.characteristic
            ch.add_read_requested(
                self._make_read_handler(lambda rid=report_id: self.last_report[rid])
            )
            dparams = GattLocalDescriptorParameters()
            dparams.static_value = to_buffer(bytes([report_id, 0x01]))  # (ID, Input)
            dparams.read_protection_level = GattProtectionLevel.ENCRYPTION_REQUIRED
            dres = await ch.create_descriptor_async(DSC_REPORT_REFERENCE, dparams)
            self._check(dres, f"report reference {report_id}")
            self.report_chars[report_id] = ch

    @staticmethod
    def _check(result, what: str) -> None:
        if int(result.error) != 0:
            raise RuntimeError(f"{what}: BluetoothError={result.error}")

    # -- request handlers ---------------------------------------------------

    def _make_read_handler(self, getter):
        def handler(_sender, args):
            deferral = args.get_deferral()

            async def respond():
                try:
                    req = await args.get_request_async()
                    req.respond_with_value(to_buffer(getter()))
                finally:
                    deferral.complete()

            asyncio.run_coroutine_threadsafe(respond(), self.loop)

        return handler

    def _write_handler(self, _sender, args):
        deferral = args.get_deferral()

        async def respond():
            try:
                req = await args.get_request_async()
                data = from_buffer(req.value)
                if data:
                    self.protocol_mode = data[0]
                if req.option == GattWriteOption.WRITE_WITH_RESPONSE:
                    req.respond()
            finally:
                deferral.complete()

        asyncio.run_coroutine_threadsafe(respond(), self.loop)

    # -- advertising --------------------------------------------------------

    async def start(self) -> None:
        for p in self.providers:
            adv = GattServiceProviderAdvertisingParameters()
            adv.is_connectable = True
            adv.is_discoverable = True
            p.start_advertising(adv)

    def status(self) -> list[tuple[str, str]]:
        out = []
        for p in self.providers:
            out.append((str(p.service.uuid), str(p.advertisement_status)))
        return out

    def subscribed(self, report_id: int) -> int:
        return len(self.report_chars[report_id].subscribed_clients)

    # -- sending ------------------------------------------------------------

    async def send(self, report_id: int, payload: bytes) -> int:
        self.last_report[report_id] = payload
        ch = self.report_chars[report_id]
        if len(ch.subscribed_clients) == 0:
            return 0
        res = await ch.notify_value_async(to_buffer(payload))
        return len(res)

    # High-level gestures --------------------------------------------------

    async def _touch(self, contacts: list[tuple[int, int, int]]) -> None:
        """Send a frame with the given fingers down and remember it for the lift frame."""
        self.contacts = contacts
        await self.send(2, touch_report(contacts))

    async def touch_down(self, x: int, y: int, cid: int = 0) -> None:
        await self._touch([(cid, x, y)])

    async def touch_up(self) -> None:
        """Lift all fingers at their last positions, then clear the state."""
        if self.contacts:
            await self.send(2, touch_report([(c, x, y, False) for c, x, y in self.contacts]))
        self.contacts = []
        await self.send(2, touch_report([]))

    async def tap(self, x: int, y: int, hold_ms: int = 60) -> None:
        await self.touch_down(x, y)
        await asyncio.sleep(hold_ms / 1000)
        await self.touch_up()

    async def swipe(self, x1: int, y1: int, x2: int, y2: int, ms: int = 300, steps: int = 20) -> None:
        for i in range(steps + 1):
            t = i / steps
            await self.touch_down(int(x1 + (x2 - x1) * t), int(y1 + (y2 - y1) * t))
            await asyncio.sleep(ms / 1000 / steps)
        await self.touch_up()

    async def pinch(self, cx: int, cy: int, r1: int, r2: int, ms: int = 400, steps: int = 20) -> None:
        for i in range(steps + 1):
            r = int(r1 + (r2 - r1) * i / steps)
            await self._touch([(0, cx - r, cy), (1, cx + r, cy)])
            await asyncio.sleep(ms / 1000 / steps)
        await self.touch_up()

    async def mouse_move(self, dx: int, dy: int) -> None:
        # Split into int8-sized steps.
        while dx or dy:
            sx = max(-127, min(127, dx))
            sy = max(-127, min(127, dy))
            await self.send(1, mouse_report(sx, sy))
            dx -= sx
            dy -= sy
            await asyncio.sleep(0.005)

    async def mouse_click(self, buttons: int = 1) -> None:
        await self.send(1, mouse_report(buttons=buttons))
        await asyncio.sleep(0.05)
        await self.send(1, mouse_report())

    async def mouse_home(self) -> None:
        """Push the pointer into the top-left corner (iOS clamps it to the screen)."""
        for _ in range(40):
            await self.send(1, mouse_report(-127, -127))
            await asyncio.sleep(0.005)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

HELP = """commands (touch coordinates are 0..10000, (0,0) = top-left):
  status                         show advertising / subscription state
  tap X Y [hold_ms]              absolute tap
  swipe X1 Y1 X2 Y2 [ms]         absolute swipe
  pinch CX CY R1 R2 [ms]         two-finger pinch (radius R1 -> R2)
  down X Y / move X Y / up       manual single-finger drag
  m DX DY                        relative mouse move (pixels-ish)
  home                           relative mouse: pin pointer to top-left
  click                          relative mouse: left click
  grid                           tap a 3x3 grid of points (blind, no measurement)
  probe                          3x3 absolute taps, compared with what the Safari probe page received
  events [N]                     print the last N events the probe page received (default 20)
  clear                          forget collected probe events
  help / quit

Measurement: open the URL printed at startup in iPhone Safari (same Wi-Fi as this PC).
"""

NO_ARG_COMMANDS = ("status", "help", "quit", "exit", "grid", "up", "home", "click", "probe", "clear")


def _linfit(xs: list[float], ys: list[float]) -> tuple[float, float] | None:
    """Least squares y = a*x + b; None if x has no spread."""
    n = len(xs)
    if n < 2:
        return None
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx == 0:
        return None
    a = sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx
    return a, my - a * mx


def orientation_report(matched: list[tuple[int, int, dict]]) -> str:
    """Tell how the digitizer axes map onto the page: swapped? flipped? (rotation of the screen).

    Compares the two pairings sent_x/sent_y -> page_x/page_y by least squares and reads the slope
    signs. Rotation names match poc/poc3-integrated (r0 = portrait, r90/r270 = the two landscapes):
      r0   page_x ~ +sent_x, page_y ~ +sent_y        r180 page_x ~ -sent_x, page_y ~ -sent_y
      r90  page_x ~ +sent_y, page_y ~ -sent_x        r270 page_x ~ -sent_y, page_y ~ +sent_x
    """
    sx = [float(m[0]) for m in matched]
    sy = [float(m[1]) for m in matched]
    px = [m[2]["cx"] for m in matched]
    py = [m[2]["cy"] for m in matched]

    def sse(xs, ys):
        fit = _linfit(xs, ys)
        if not fit:
            return None
        a, b = fit
        return sum((a * x + b - y) ** 2 for x, y in zip(xs, ys)), a

    straight = (sse(sx, px), sse(sy, py))
    swapped = (sse(sy, px), sse(sx, py))
    if None in straight and None in swapped:
        return "orientation: not enough spread to tell"
    cost = lambda pair: float("inf") if None in pair else pair[0][0] + pair[1][0]  # noqa: E731
    is_swapped = cost(swapped) < cost(straight)
    (_, ap), (_, aq) = swapped if is_swapped else straight
    sp, sq = ("+" if ap > 0 else "-"), ("+" if aq > 0 else "-")
    names = {(False, "+", "+"): "r0", (True, "+", "-"): "r90", (False, "-", "-"): "r180", (True, "-", "+"): "r270"}
    name = names.get((is_swapped, sp, sq))
    src_p, src_q = ("sent_y", "sent_x") if is_swapped else ("sent_x", "sent_y")
    text = f"orientation: page_x ~ {sp}{src_p}, page_y ~ {sq}{src_q}  ->  axes {'SWAPPED' if is_swapped else 'not swapped'}"
    return text + (f", matches rotation {name}" if name else ", mirrored (unexpected)")


async def run_probe(hid: HidPeripheral, web: ProbeServer) -> None:
    web.clear()
    # Keep both axes away from the edges: Safari's address bar / toolbars swallow the extremes, and
    # in landscape the axes may swap, so an x that looks safe could land on the thin vertical axis.
    points = [(gx, gy) for gy in (3000, 5000, 7000) for gx in (3000, 5000, 7000)]
    sent: list[tuple[int, int, float]] = []
    for gx, gy in points:
        t0 = time.time()
        await hid.tap(gx, gy, hold_ms=120)
        sent.append((gx, gy, t0))
        await asyncio.sleep(0.7)
    await asyncio.sleep(0.5)

    events = web.snapshot()
    downs = [e for e in events if e.get("type") == "pointerdown"]
    print(f"probe page received {len(events)} events ({len(downs)} pointerdown)")
    if not events:
        print("  -> nothing arrived. Is the probe page open in Safari and in the foreground?")
        return

    matched: list[tuple[int, int, dict]] = []
    print(" sent(x,y)      -> observed norm(x,y)  client(x,y)    type   latency")
    for i, (gx, gy, t0) in enumerate(sent):
        t1 = sent[i + 1][2] if i + 1 < len(sent) else t0 + 1.2
        hit = next((e for e in downs if t0 <= e["t"] < t1), None)
        if hit is None:
            print(f" ({gx:5d},{gy:5d}) -> (no pointerdown in window)")
            continue
        matched.append((gx, gy, hit))
        print(
            f" ({gx:5d},{gy:5d}) -> ({hit['nx']:6d},{hit['ny']:6d})   "
            f"({hit['cx']:6.1f},{hit['cy']:6.1f})  {hit['ptype']:<6} {(hit['t'] - t0) * 1000:5.0f} ms"
        )
    print(f"matched {len(matched)}/{len(sent)} points; extra pointerdowns: {len(downs) - len(matched)}")
    fx = _linfit([m[0] for m in matched], [m[2]["nx"] for m in matched])
    fy = _linfit([m[1] for m in matched], [m[2]["ny"] for m in matched])
    if fx and fy:
        print(
            f"fit: observed_x = {fx[0]:.3f}*sent_x + {fx[1]:.0f}   observed_y = {fy[0]:.3f}*sent_y + {fy[1]:.0f}"
            "   (relative to the Safari viewport)"
        )
    if matched:
        # If the digitizer spans the whole screen 1:1, the viewport origin implied by every point is the same.
        lefts = [gx / 10000 * h["sw"] - h["cx"] for gx, gy, h in matched]
        tops = [gy / 10000 * h["sh"] - h["cy"] for gx, gy, h in matched]
        sw, sh = matched[0][2]["sw"], matched[0][2]["sh"]
        print(
            f"implied viewport origin if 0..10000 spans the {sw}x{sh} screen: "
            f"left={sum(lefts) / len(lefts):.1f}px (spread {max(lefts) - min(lefts):.1f}) "
            f"top={sum(tops) / len(tops):.1f}px (spread {max(tops) - min(tops):.1f})"
        )
        print("  -> a spread of a few px or less means the mapping is linear and 1:1 over the whole screen")

    if len(matched) >= 4:
        print("  " + orientation_report(matched))

    def lift_check(ev: list[dict]) -> str:
        """Each finger must be lifted where it last was; a jump means a bogus (0,0) lift frame."""
        bad = []
        for pid in sorted({e["id"] for e in ev}):
            mine = [e for e in ev if e["id"] == pid]
            ups = [e for e in mine if e["type"] == "pointerup"]
            before = [e for e in mine if e["type"] != "pointerup"]
            if not ups or not before:
                bad.append(f"id {pid}: no pointerup" if not ups else f"id {pid}: no move before up")
                continue
            d = ((ups[-1]["cx"] - before[-1]["cx"]) ** 2 + (ups[-1]["cy"] - before[-1]["cy"]) ** 2) ** 0.5
            if d > 20:
                bad.append(f"id {pid}: lifted {d:.0f}px away from its last position")
        return "lift OK (each finger lifted at its last position)" if not bad else "LIFT JUMP: " + "; ".join(bad)

    # Phase 2: single-finger swipe (expects pointermove events).
    t_swipe = time.time()
    await hid.swipe(5000, 7500, 5000, 2500, ms=600)
    await asyncio.sleep(0.6)
    ev = [e for e in web.snapshot() if e["t"] >= t_swipe and e.get("type") in ("pointerdown", "pointermove", "pointerup")]
    moves = [e for e in ev if e["type"] == "pointermove"]
    ys = [e["cy"] for e in ev]
    print(
        f"swipe: {len(ev)} events, {len(moves)} pointermove, "
        f"client y range {min(ys):.0f}..{max(ys):.0f}" if ys else "swipe: NO events reached the page"
    )
    if ev:
        print("  ", lift_check(ev))

    # Phase 3: two-finger pinch (expects two distinct pointer ids).
    t_pinch = time.time()
    await hid.pinch(5000, 5000, 800, 2800, ms=600)
    await asyncio.sleep(0.6)
    ev = [e for e in web.snapshot() if e["t"] >= t_pinch and e.get("type") in ("pointerdown", "pointermove", "pointerup")]
    ids = sorted({e["id"] for e in ev})
    xs_by_id = {i: [e["cx"] for e in ev if e["id"] == i] for i in ids}
    spans = ", ".join(f"id {i}: x {min(v):.0f}..{max(v):.0f}" for i, v in xs_by_id.items())
    print(f"pinch: {len(ev)} events, distinct pointer ids = {len(ids)}  ({spans})" if ev else "pinch: NO events reached the page")
    print("  -> 2 distinct ids that move apart means multi-touch works")
    if ev:
        print("  ", lift_check(ev))


async def run_command(hid: HidPeripheral, web: ProbeServer, line: str) -> bool:
    parts = line.split()
    if not parts:
        return True
    cmd = parts[0].lower()
    args = [int(a) for a in parts[1:]] if cmd not in NO_ARG_COMMANDS else []
    if cmd in ("quit", "exit"):
        return False
    if cmd == "probe":
        await run_probe(hid, web)
        return True
    if cmd == "clear":
        web.clear()
        return True
    if cmd == "events":
        for e in web.snapshot()[-(args[0] if args else 20):]:
            print(
                f"  {e['type']:<13} {e['ptype']:<6} id={e['id']} norm=({e['nx']},{e['ny']}) "
                f"buttons={e.get('buttons', 0)} viewport={e['vw']}x{e['vh']}"
            )
        return True
    if cmd == "help":
        print(HELP)
    elif cmd == "status":
        for s in hid.status():
            print("  ", s)
        print("   subscribed: mouse=%d touch=%d" % (hid.subscribed(1), hid.subscribed(2)))
    elif cmd == "tap":
        await hid.tap(*args)
    elif cmd == "swipe":
        await hid.swipe(*args)
    elif cmd == "pinch":
        await hid.pinch(*args)
    elif cmd in ("down", "move"):
        await hid.touch_down(*args)
    elif cmd == "up":
        await hid.touch_up()
    elif cmd == "m":
        await hid.mouse_move(*args)
    elif cmd == "home":
        await hid.mouse_home()
    elif cmd == "click":
        await hid.mouse_click()
    elif cmd == "grid":
        for gy in (1000, 5000, 9000):
            for gx in (1000, 5000, 9000):
                await hid.tap(gx, gy)
                await asyncio.sleep(0.4)
    else:
        print("unknown command; type 'help'")
    return True


async def amain(ns: argparse.Namespace) -> int:
    adapter = await BluetoothAdapter.get_default_async()
    if adapter is None or not adapter.is_peripheral_role_supported:
        print("ERROR: no Bluetooth LE adapter with peripheral role support", file=sys.stderr)
        return 2
    print(f"adapter: LE peripheral role supported = {adapter.is_peripheral_role_supported}")

    loop = asyncio.get_running_loop()
    hid = HidPeripheral(loop)
    await hid.build()
    await hid.start()
    await asyncio.sleep(1.0)
    states = hid.status()
    for s in states:
        print("service", s[0], "->", s[1])
    started = all(s[1].upper().endswith("STARTED") or s[1].endswith("2") for s in states)
    print("report map: %d bytes" % len(REPORT_MAP))

    if ns.selftest:
        await asyncio.sleep(ns.selftest)
        print("SELFTEST", "OK" if started else "FAILED")
        return 0 if started else 1

    web = ProbeServer(ns.web_port)
    try:
        web.start()
        for ip in ProbeServer.lan_ips():
            print(f"probe page: open http://{ip}:{ns.web_port}/ in iPhone Safari")
    except OSError as exc:
        print("warning: probe web server not started:", exc)

    print("\nOn the iPhone: Settings > Bluetooth > pair with this PC, and for the touch path")
    print("enable Settings > Accessibility > Zoom. For the mouse path enable AssistiveTouch.")
    print("Waiting for the iPhone to subscribe; type 'help' for commands.\n")
    while True:
        line = await loop.run_in_executor(None, sys.stdin.readline)
        if not line:
            break
        try:
            if not await run_command(hid, web, line.strip()):
                break
        except Exception as exc:  # keep the prompt alive on bad input
            print("error:", exc)
    return 0


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--selftest", type=float, metavar="SECONDS",
                    help="publish the service, wait, and exit (no phone needed)")
    ap.add_argument("--web-port", type=int, default=8000, help="port of the touch-probe page (default 8000)")
    ns = ap.parse_args()
    sys.exit(asyncio.run(amain(ns)))


if __name__ == "__main__":
    main()
