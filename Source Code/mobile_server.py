"""LycRomanise mobile view (v1.4.0).

A tiny built-in web server so a phone or tablet on the same Wi-Fi can show the lyrics: open
http://<this PC's address>:<port> in Safari / Chrome and the page draws the same karaoke line, next
line and artist/title card as the desktop strip. Nothing to install on the phone.

Design:
  * Pure standard library (http.server), read-only, GET only.
  * Off by default; only started when "Show lyrics on phones and tablets" is ticked in Settings.
  * The app (Tk thread) calls publish(state) a few times a second; the page polls /state and animates
    the karaoke fill between polls, so it stays smooth even though it is only updated ~8 times a second.
  * Only the lyrics currently on the strip are ever served (no files, no settings, no tokens).
"""

import json
import logging
import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

log = logging.getLogger("spoti.mobile")

DEFAULT_PORT = 8765


def lan_addresses():
    """This PC's IPv4 address(es) on the local network, best guess first."""
    found = []
    try:                                   # the address the OS would use to reach the internet / LAN
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.connect(("10.255.255.255", 1))     # UDP connect sends nothing
            found.append(s.getsockname()[0])
        finally:
            s.close()
    except OSError:
        pass
    try:
        for ip in socket.gethostbyname_ex(socket.gethostname())[2]:
            if ip not in found:
                found.append(ip)
    except OSError:
        pass
    found = [ip for ip in found if ip and not ip.startswith("127.") and not ip.startswith("169.254.")]
    return found


def mobile_url(port):
    ips = lan_addresses()
    return "http://%s:%d" % (ips[0] if ips else "<this-pc-address>", port)


class _Handler(BaseHTTPRequestHandler):
    server_version = "LycRomanise"
    protocol_version = "HTTP/1.0"

    def log_message(self, fmt, *args):         # nothing goes to a console; keep the log file quiet
        pass

    def _send(self, code, body, ctype):
        data = body if isinstance(body, bytes) else body.encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        try:
            if path in ("/", "/index.html", "/obs"):
                self._send(200, PAGE.replace("__APP__", self.server.app_name), "text/html; charset=utf-8")
            elif path == "/state":
                self._send(200, json.dumps(self.server.snapshot(), ensure_ascii=False),
                           "application/json; charset=utf-8")
            elif path == "/favicon.ico":
                self._send(204, b"", "image/x-icon")
            else:
                self._send(404, "Not found", "text/plain; charset=utf-8")
        except (BrokenPipeError, ConnectionError, OSError):
            pass                                # the phone went away mid-reply

    def do_HEAD(self):
        self.send_response(200)
        self.end_headers()


class _Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def handle_error(self, request, client_address):   # dropped connections are normal on Wi-Fi
        log.debug("mobile view: connection error from %s", client_address, exc_info=True)

    def snapshot(self):
        with self.lock:
            return self.state


class MobileServer:
    def __init__(self, app_name="LycRomanise"):
        self.app_name = app_name
        self._httpd = None
        self._thread = None
        self.port = None
        self.error = None

    @property
    def running(self):
        return self._httpd is not None

    def start(self, port=DEFAULT_PORT):
        """Start serving on all network interfaces. Returns True when it is running."""
        if self._httpd is not None and self.port == port:
            return True
        self.stop()
        try:
            httpd = _Server(("0.0.0.0", int(port)), _Handler)
        except OSError as exc:
            self.error = "port %s: %s" % (port, exc)
            log.warning("mobile view could not start on port %s: %s", port, exc)
            return False
        httpd.lock = threading.Lock()
        httpd.state = {"kind": "none", "playing": False}
        httpd.app_name = self.app_name
        self._httpd, self.port, self.error = httpd, int(port), None
        self._thread = threading.Thread(target=httpd.serve_forever, name="mobile-view", daemon=True)
        self._thread.start()
        log.info("mobile view on: %s", mobile_url(self.port))
        return True

    def publish(self, state):
        """Called from the app's UI thread with the dict the page should show."""
        httpd = self._httpd
        if httpd is not None:
            with httpd.lock:
                httpd.state = state

    def stop(self):
        httpd, self._httpd = self._httpd, None
        if httpd is None:
            return
        try:
            httpd.shutdown()
            httpd.server_close()
        except Exception:
            log.warning("mobile view: error while stopping", exc_info=True)
        self.port = None
        log.info("mobile view off")


PAGE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black">
<meta name="theme-color" content="#000000">
<title>__APP__</title>
<style>
  :root { --sung:#ff3d9a; --unsung:#ffe14d; --next:#ffe14d; --ol:#000; --olw:3px; --olwn:2px; }
  [hidden] { display:none !important; }
  html, body { margin:0; height:100%; background:#000; overflow:hidden; -webkit-user-select:none; user-select:none;
               -webkit-tap-highlight-color: transparent; }
  body { font-family: -apple-system, "SF Pro Text", "Segoe UI", Roboto, "Noto Sans", Arial, sans-serif; font-weight:700; }
  #stage { position:fixed; inset:0; display:flex; flex-direction:column; align-items:center; justify-content:center;
           padding: env(safe-area-inset-top) max(16px, env(safe-area-inset-right)) env(safe-area-inset-bottom)
                    max(16px, env(safe-area-inset-left)); text-align:center; box-sizing:border-box; }
  .ln { position:relative; display:inline-block; max-width:100%; line-height:1.25; overflow-wrap:anywhere; }
  .ln::before { content: attr(data-t); position:absolute; left:0; right:0; top:0; z-index:0; color:var(--ol);
                -webkit-text-stroke: var(--w) var(--ol); pointer-events:none; }
  .ln > span { position:relative; z-index:1; }
  html.obs, html.obs body { background: transparent; }
  html.obs #idle { display:none !important; }
  #top { font-size: calc(clamp(24px, 7.2vw, 48px) * var(--k, 1)); --w: var(--olw); }
  #next { font-size: calc(clamp(16px, 4.6vw, 30px) * var(--k, 1)); margin-top: 3.5vh; --w: var(--olwn); }
  #top > span { color:transparent; -webkit-text-fill-color:transparent; -webkit-background-clip:text; background-clip:text; }
  #next > span { color: var(--next); }
  .rise { animation: rise .28s cubic-bezier(.2,.7,.2,1); }
  @keyframes rise { from { transform: translateY(40%); opacity:.0; } to { transform:none; opacity:1; } }
  #idle { position:fixed; left:0; right:0; bottom: calc(14px + env(safe-area-inset-bottom)); text-align:center;
          color:#555; font-size:13px; font-weight:600; letter-spacing:.04em; }
</style>
</head>
<body>
<div id="stage">
  <div id="top" class="ln" hidden><span></span></div>
  <div id="next" class="ln" hidden><span></span></div>
</div>
<div id="idle">__APP__ &middot; connecting&hellip;</div>
<script>
(function () {
  var top = document.getElementById('top'), next = document.getElementById('next'), idle = document.getElementById('idle');
  var topSpan = top.firstElementChild, nextSpan = next.firstElementChild;
  // OBS mode: open /obs (or ?obs=1). Transparent background, no status text; ?scale=2 makes the text bigger.
  var OBS = /^\/obs/.test(location.pathname) || /[?&]obs(=|&|$)/.test(location.search);
  if (OBS) {
    document.documentElement.classList.add('obs');
    var m = /[?&]scale=([0-9.]+)/.exec(location.search);
    document.documentElement.style.setProperty('--k', String(Math.max(0.3, Math.min(6, m ? parseFloat(m[1]) : 1.5))));
  }
  var st = null, got = 0, shownId = null, shownNext = null, lastOk = 0, lastTop = '';

  function setText(el, span, text) { span.textContent = text; el.setAttribute('data-t', text); }

  function apply(s) {
    var root = document.documentElement.style;
    if (s.sung)    root.setProperty('--sung', s.sung);
    if (s.unsung)  root.setProperty('--unsung', s.unsung);
    if (s.next_c)  root.setProperty('--next', s.next_c);
    if (s.outline) root.setProperty('--ol', s.outline);
    if (typeof s.outline_px === 'number') {
      root.setProperty('--olw', (s.outline_px * 2) + 'px');
      root.setProperty('--olwn', (Math.min(s.outline_px, 1) * 2) + 'px');
    }
    if (s.kind === 'none' || !s.top) {
      top.hidden = next.hidden = true; shownId = shownNext = null; lastTop = '';
      idle.style.display = '';
      idle.textContent = s.playing ? 'Waiting for lyrics\u2026' : 'Nothing playing \u00b7 lyrics appear here';
      return;
    }
    idle.style.display = 'none';
    top.hidden = false;
    if (s.id !== shownId) {                 // a new line: swap the text and play the rise animation
      shownId = s.id; setText(top, topSpan, s.top); lastTop = s.top;
      top.classList.remove('rise'); void top.offsetWidth; top.classList.add('rise');
      shownNext = null;
    }
    if (s.next !== shownNext) {
      shownNext = s.next;
      next.hidden = !s.next;
      if (s.next) { setText(next, nextSpan, s.next); next.classList.remove('rise'); void next.offsetWidth; next.classList.add('rise'); }
    }
  }

  function paintFill() {                    // the karaoke sweep, smooth between polls
    requestAnimationFrame(paintFill);
    if (!st || st.kind === 'none' || !st.top) return;
    var f = st.fraction + (st.playing ? st.rate * (performance.now() - got) / 1000 : 0);
    f = Math.max(0, Math.min(1, f));
    var n = Math.max(1, lastTop.length);
    var band = Math.max(3, Math.min(40, 220 / n));         // blend width ~2.2 characters, in %
    var p = f * 100;
    topSpan.style.backgroundImage = 'linear-gradient(90deg, var(--sung) 0%, var(--sung) ' + p + '%, var(--unsung) ' +
                                    Math.min(100, p + band) + '%, var(--unsung) 100%)';
  }

  function poll() {
    var ctl = ('AbortController' in window) ? new AbortController() : null;
    var timer = setTimeout(function () { if (ctl) ctl.abort(); }, 2500);
    fetch('/state?t=' + Date.now(), { cache: 'no-store', signal: ctl ? ctl.signal : undefined })
      .then(function (r) { return r.json(); })
      .then(function (s) { clearTimeout(timer); st = s; got = performance.now(); lastOk = got; apply(s); })
      .catch(function () {
        clearTimeout(timer);
        if (performance.now() - lastOk > 4000) {
          top.hidden = next.hidden = true; shownId = shownNext = null;
          idle.style.display = ''; idle.textContent = 'Reconnecting\u2026';
        }
      })
      .then(function () { setTimeout(poll, 120); });
  }

  if (!OBS) document.addEventListener('click', function () {      // tap = full screen where the browser allows it
    var el = document.documentElement;
    try { if (!document.fullscreenElement && el.requestFullscreen) el.requestFullscreen(); } catch (e) {}
  });
  if (!OBS && navigator.wakeLock && window.isSecureContext) {
    var keepAwake = function () { navigator.wakeLock.request('screen').catch(function () {}); };
    keepAwake(); document.addEventListener('visibilitychange', function () { if (!document.hidden) keepAwake(); });
  }
  paintFill(); poll();
})();
</script>
</body>
</html>
"""
