"""Tiny HTTP server that serves a touch-probe page and collects the events it reports.

Open http://<PC-IP>:<port>/ in iPhone Safari. Every pointer event the page receives is
POSTed back, so the PC can compare what it *sent* over BLE HID with what iOS *delivered*.
"""

from __future__ import annotations

import json
import socket
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PAGE = """<!doctype html>
<html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover">
<title>touch probe</title>
<style>
  html,body{margin:0;height:100%;overflow:hidden;background:#101418;color:#cfd8dc;
    font:12px/1.4 ui-monospace,Menlo,monospace;touch-action:none;-webkit-user-select:none;user-select:none}
  canvas{position:fixed;inset:0;width:100%;height:100%}
  #hud{position:fixed;left:8px;top:8px;right:8px;pointer-events:none;white-space:pre-wrap}
</style></head>
<body>
<canvas id="c"></canvas>
<div id="hud">touch probe ready - waiting for input</div>
<script>
const cv = document.getElementById('c'), ctx = cv.getContext('2d'), hud = document.getElementById('hud');
let W = 0, H = 0, dots = [], n = 0;
function resize(){ const r = devicePixelRatio || 1; W = innerWidth; H = innerHeight;
  cv.width = W*r; cv.height = H*r; ctx.setTransform(r,0,0,r,0,0); }
addEventListener('resize', resize); resize();
function send(o){ try{ fetch('/e', {method:'POST', body: JSON.stringify(o), keepalive:true}); }catch(_){} }
function onPtr(e){
  e.preventDefault();
  const o = {type:e.type, id:e.pointerId, ptype:e.pointerType, cx:e.clientX, cy:e.clientY,
             nx: Math.round(e.clientX/W*10000), ny: Math.round(e.clientY/H*10000),
             vw: W, vh: H, sw: screen.width, sh: screen.height, buttons: e.buttons, seq: ++n};
  send(o);
  if (e.type === 'pointerdown' || e.type === 'pointermove') dots.push({x:e.clientX, y:e.clientY, t:performance.now(), c:e.pointerType==='mouse'?'#ffb74d':'#4fc3f7'});
  hud.textContent = e.type + ' ' + e.pointerType + ' id=' + e.pointerId + '\\nclient=(' + Math.round(e.clientX) + ',' + Math.round(e.clientY) + ')'
    + ' norm=(' + o.nx + ',' + o.ny + ')\\nviewport=' + W + 'x' + H + ' screen=' + screen.width + 'x' + screen.height + ' #' + n;
}
['pointerdown','pointermove','pointerup','pointercancel'].forEach(t => addEventListener(t, onPtr, {passive:false}));
['touchstart','touchmove','gesturestart'].forEach(t => addEventListener(t, e => e.preventDefault(), {passive:false}));
(function draw(){ ctx.clearRect(0,0,W,H); const now = performance.now();
  dots = dots.filter(d => now - d.t < 4000);
  for (const d of dots){ ctx.globalAlpha = 1 - (now-d.t)/4000; ctx.fillStyle = d.c; ctx.beginPath(); ctx.arc(d.x,d.y,14,0,7); ctx.fill(); }
  ctx.globalAlpha = 1; requestAnimationFrame(draw); })();
</script></body></html>
"""


class ProbeServer:
    def __init__(self, port: int = 8000) -> None:
        self.port = port
        self._events: list[dict] = []
        self._lock = threading.Lock()
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):  # noqa: N802
                body = PAGE.encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(body)

            def do_POST(self):  # noqa: N802
                length = int(self.headers.get("Content-Length", "0"))
                try:
                    ev = json.loads(self.rfile.read(length) or b"{}")
                    ev["t"] = time.time()
                    with outer._lock:
                        outer._events.append(ev)
                except json.JSONDecodeError:
                    pass
                self.send_response(204)
                self.end_headers()

            def log_message(self, *_args):  # keep the interactive console clean
                pass

        self._httpd = ThreadingHTTPServer(("0.0.0.0", port), Handler)
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)

    def start(self) -> None:
        self._thread.start()

    def stop(self) -> None:
        self._httpd.shutdown()

    def clear(self) -> None:
        with self._lock:
            self._events.clear()

    def snapshot(self) -> list[dict]:
        with self._lock:
            return list(self._events)

    @staticmethod
    def lan_ips() -> list[str]:
        ips: set[str] = set()
        try:
            for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
                ip = info[4][0]
                if not ip.startswith("127."):
                    ips.add(ip)
        except OSError:
            pass
        try:  # the interface used for the default route
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect(("192.0.2.1", 9))
            ips.add(s.getsockname()[0])
            s.close()
        except OSError:
            pass
        return sorted(ips)
