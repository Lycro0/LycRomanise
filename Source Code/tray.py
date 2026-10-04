"""System-tray icon for LycRomanise (needs `pystray` + `pillow`).

The tray icon is what makes the strip a proper app: it's there while the
strip is running, and has Lock / Settings / Start with Windows / Quit.

pystray runs its own message loop on a background thread, and Tk is not
thread-safe, so a menu click never touches Tk itself: it only calls
`post(callable)`, which the app drains on the Tk thread.
"""

import logging
import os

import config

log = logging.getLogger("spoti.tray")

try:
    import pystray
    from PIL import Image, ImageDraw
except Exception as _exc:          # not installed: the app still works, just without a tray
    pystray = None
    Image = ImageDraw = None
    _IMPORT_ERROR = _exc
else:
    _IMPORT_ERROR = None


def available():
    return pystray is not None


def make_icon_image(size=64):
    """Fallback icon (used only if assets/lyrics-overlay.ico is missing)."""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((2, 2, size - 3, size - 3), radius=size // 5, fill=(29, 185, 84, 255))
    for i, frac in enumerate((0.70, 0.50, 0.62)):
        y = size * (0.30 + 0.17 * i)
        d.rounded_rectangle((size * 0.20, y, size * (0.20 + 0.60 * frac), y + size * 0.07),
                            radius=size // 30 + 1, fill=(255, 255, 255, 255))
    return img


def load_icon_image(path=None, size=64):
    """The tray image: assets/lyrics-overlay.ico if it can be read, otherwise the
    generated fallback, so a missing/corrupt file never costs the tray icon."""
    path = path or config.ICON_PATH
    if os.path.exists(path):
        try:
            img = Image.open(path)
            sizes = sorted(img.info.get("sizes") or [img.size])
            best = min((s for s in sizes if s[0] >= size), default=sizes[-1])   # smallest frame >= size
            img.size = best
            img.load()
            img = img.convert("RGBA")
            return img if img.size == (size, size) else img.resize((size, size), Image.LANCZOS)
        except Exception:
            log.warning("couldn't read %s; using the generated tray icon", path, exc_info=True)
    else:
        log.warning("tray icon file %s not found; using the generated icon", path)
    return make_icon_image(size)


def menu_spec(is_locked, autostart_on, can_autostart=True):
    """Pure description of the tray menu: [(label, action_name, checked)] with
    None as a separator. Kept separate from pystray so it can be tested."""
    from version import __version__
    spec = [
        ("LycRomanise v%s" % __version__, "about", None),
        None,
        (("Unlock position" if is_locked else "Lock position"), "toggle_lock", None),
    ]
    spec.append(("Settings…", "settings", None))
    if can_autostart:
        spec.append(("Start with Windows", "toggle_autostart", bool(autostart_on)))
    spec += [None, ("Check for updates", "check_update", None), ("Open logs folder", "open_logs", None), ("Quit", "quit", None)]
    return spec


class TrayIcon:
    """`actions` maps action names from menu_spec to zero-arg callables (run on
    the Tk thread via `post`); `state` returns (locked, autostart)."""

    def __init__(self, actions, state, post, can_autostart=True, title=None):
        self._actions, self._state, self._post = actions, state, post
        self._can_autostart = can_autostart
        from version import __version__
        self._title = title or "LycRomanise v%s" % __version__
        self._icon = None

    def _build_menu(self):
        items = []
        for entry in menu_spec(*self._state(), can_autostart=self._can_autostart):
            if entry is None:
                items.append(pystray.Menu.SEPARATOR)
                continue
            label, action, checked = entry
            kwargs = {}
            if checked is not None:
                kwargs["checked"] = (lambda _i, a=action: self._is_checked(a))
            items.append(pystray.MenuItem(label, self._make_handler(action), **kwargs))
        return pystray.Menu(*items)

    def _is_checked(self, action):
        _locked, autostart = self._state()
        return {"toggle_autostart": autostart}.get(action, False)

    def _make_handler(self, action):
        def handler(_icon=None, _item=None):
            fn = self._actions.get(action)
            if fn is not None:
                self._post(fn)
        return handler

    def start(self):
        """Start the tray on its own thread. Returns True if it's running."""
        if not available():
            log.warning("no tray icon (pystray/pillow not installed: %s). "
                        "Quit from Task Manager; run `pip install -r requirements.txt`.",
                        _IMPORT_ERROR)
            return False
        try:
            self._icon = pystray.Icon("spoti-lyrics", load_icon_image(), self._title, menu=self._build_menu())
            self._icon.run_detached()
            log.info("tray icon started")
            return True
        except Exception:
            log.exception("couldn't start the tray icon")
            self._icon = None
            return False

    def refresh(self):
        """Rebuild the menu so labels/checks follow the current state."""
        if self._icon is not None:
            try:
                self._icon.menu = self._build_menu()
                self._icon.update_menu()
            except Exception:
                log.warning("tray refresh failed", exc_info=True)

    def notify(self, message):
        if self._icon is not None:
            try:
                self._icon.notify(message, self._title)
            except Exception:
                log.warning("tray notify failed", exc_info=True)

    def stop(self):
        if self._icon is not None:
            try:
                self._icon.stop()
            except Exception:
                log.warning("tray stop failed", exc_info=True)
            self._icon = None
