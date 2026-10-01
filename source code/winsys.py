"""Windows integration for Spoti-Lyrics Overlay: running as its own console-less
app, a single-instance guard, and the optional "start with Windows" entry.

Everything here is a safe no-op on non-Windows systems, and every OS call can
be swapped out (the tests do) so the logic is checkable without Windows.
"""

import logging
import os
import subprocess
import sys

log = logging.getLogger("spoti.winsys")

from paths import APP_DIR as BASE_DIR
LAUNCHER = os.path.join(BASE_DIR, "LyricsOverlay.pyw")
MUTEX_NAME = "Local\\SpotiLyricsOverlay.SingleInstance"
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
RUN_VALUE = "SpotiLyricsOverlay"

DETACHED_PROCESS = 0x00000008
CREATE_NEW_PROCESS_GROUP = 0x00000200
CREATE_NO_WINDOW = 0x08000000

_mutex_handle = None   # kept referenced for the life of the process


def is_windows():
    return sys.platform.startswith("win")


def pythonw_path(executable=None):
    """The console-less twin of the running interpreter (pythonw.exe), or the
    interpreter itself if there isn't one next to it."""
    exe = executable or sys.executable
    folder, name = os.path.split(exe)
    if name.lower() == "python.exe":
        candidate = os.path.join(folder, "pythonw.exe")
        if os.path.exists(candidate):
            return candidate
    return exe


def has_console():
    """True when this process is attached to a console window."""
    if not is_windows():
        return False
    try:
        import ctypes
        return bool(ctypes.windll.kernel32.GetConsoleWindow())
    except Exception:
        return False


def detach_from_console(spawn=subprocess.Popen, console=None):
    if getattr(sys, "frozen", False):
        return False   # the windowed .exe never has a console
    """If the app was started from a console (python app.py, a
    terminal), start a fresh copy with pythonw and no console at all, and
    return True so the caller exits. The console can then be closed without
    touching the strip. Returns False when already console-less."""
    if not is_windows() or os.environ.get("SPOTI_NO_DETACH"):
        return False
    if not (has_console() if console is None else console):
        return False
    exe = pythonw_path()
    if os.path.basename(exe).lower() != "pythonw.exe":
        log.warning("pythonw.exe not found next to %s; staying attached to the console", sys.executable)
        return False
    env = dict(os.environ, SPOTI_NO_DETACH="1")   # the child must not detach again
    try:
        spawn(
            [exe, LAUNCHER], cwd=BASE_DIR, env=env, close_fds=True,
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW,
        )
    except OSError:
        log.exception("couldn't relaunch without a console")
        return False
    log.info("started a console-less copy (%s); this console copy exits", exe)
    return True


def acquire_single_instance(create_mutex=None):
    """True if we're the only running copy. A second launch returns False and
    should just exit (the strip and tray icon of the first one are already
    there)."""
    global _mutex_handle
    if not is_windows() and create_mutex is None:
        return True
    try:
        if create_mutex is None:
            import ctypes
            from ctypes import wintypes
            # use_last_error: ctypes keeps its own copy of GetLastError() taken right
            # after the call, so nothing Python does in between can clobber it.
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.CreateMutexW.restype = wintypes.HANDLE
            kernel32.CreateMutexW.argtypes = (wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR)
            ctypes.set_last_error(0)
            _mutex_handle = kernel32.CreateMutexW(None, False, MUTEX_NAME)
            return ctypes.get_last_error() != 183   # ERROR_ALREADY_EXISTS
        handle, already = create_mutex(MUTEX_NAME)
        _mutex_handle = handle
        return not already
    except Exception:
        log.exception("single-instance check failed; continuing anyway")
        return True


def autostart_command():
    if getattr(sys, "frozen", False):
        return '"%s"' % sys.executable
    return '"%s" "%s"' % (pythonw_path(), LAUNCHER)


def _autostart_value(reg):
    with reg.OpenKey(reg.HKEY_CURRENT_USER, RUN_KEY, 0, reg.KEY_READ) as key:
        value, _ = reg.QueryValueEx(key, RUN_VALUE)
    return value


def refresh_autostart(reg=None):
    """If "Start with Windows" is on but its entry points somewhere else (the app
    folder or the Python install moved), rewrite it to the current location.
    Returns True when it had to fix it. Never raises."""
    if not is_windows() and reg is None:
        return False
    try:
        reg = reg or _winreg()
        try:
            value = _autostart_value(reg)
        except OSError:
            return False          # not enabled: leave it alone
        if value == autostart_command():
            return False
        log.info("start-with-Windows entry was stale (%s); updating it", value)
        return set_autostart(True, reg=reg)
    except Exception:
        log.exception("couldn't refresh the start-with-Windows entry")
        return False


def _winreg():
    import winreg
    return winreg


def autostart_enabled(reg=None):
    if not is_windows() and reg is None:
        return False
    try:
        reg = reg or _winreg()
        with reg.OpenKey(reg.HKEY_CURRENT_USER, RUN_KEY, 0, reg.KEY_READ) as key:
            value, _ = reg.QueryValueEx(key, RUN_VALUE)
        return bool(value)
    except OSError:
        return False
    except Exception:
        log.exception("couldn't read the start-with-Windows setting")
        return False


def set_autostart(enabled, reg=None):
    """Add/remove the HKCU Run entry (no admin rights needed). Returns True on
    success."""
    if not is_windows() and reg is None:
        return False
    try:
        reg = reg or _winreg()
        with reg.OpenKey(reg.HKEY_CURRENT_USER, RUN_KEY, 0, reg.KEY_SET_VALUE) as key:
            if enabled:
                reg.SetValueEx(key, RUN_VALUE, 0, reg.REG_SZ, autostart_command())
            else:
                try:
                    reg.DeleteValue(key, RUN_VALUE)
                except FileNotFoundError:
                    pass
        log.info("start with Windows: %s", "on" if enabled else "off")
        return True
    except Exception:
        log.exception("couldn't change the start-with-Windows setting")
        return False


# ------------------------------------------------------------- DPI / windows --

def enable_dpi_awareness(shcore=None, user32=None):
    """Tell Windows this process draws at the real screen resolution, so the strip is
    crisp on scaled (125%/150%/...) displays instead of being bitmap-stretched (soft).
    Must run before the first Tk window exists. Returns a label of what took effect
    ("system", "legacy") or None.

    "System DPI aware" (not per-monitor) on purpose: Tk 8.6 does not re-layout when a
    window crosses to a monitor with a different scale, so per-monitor awareness would
    make the strip change size mid-drag. With system awareness it is sharp on the main
    display's scale and merely Windows-stretched (a little soft) on monitors with a
    different scale factor."""
    if not is_windows() and shcore is None and user32 is None:
        return None
    try:
        import ctypes
        if shcore is None:
            shcore = ctypes.windll.shcore
        if user32 is None:
            user32 = ctypes.windll.user32
    except Exception:
        return None
    try:
        # 1 = PROCESS_SYSTEM_DPI_AWARE. S_OK = 0; E_ACCESSDENIED (already set, e.g. by a
        # manifest) is fine too: the process is aware either way.
        hr = shcore.SetProcessDpiAwareness(1)
        if hr in (0, -2147024891):
            return "system"
    except Exception:
        pass                       # Windows 7 / 8.0: no shcore function
    try:
        if user32.SetProcessDPIAware():
            return "legacy"
    except Exception:
        pass
    return None


_api_cache = None


def _hwnd_api():
    """user32 with proper 64-bit-safe prototypes (built once, then cached)."""
    global _api_cache
    if _api_cache is not None:
        return _api_cache
    import ctypes
    from ctypes import wintypes
    u = ctypes.WinDLL("user32", use_last_error=True)
    u.GetParent.restype = wintypes.HWND
    u.GetParent.argtypes = (wintypes.HWND,)
    u.GetAncestor.restype = wintypes.HWND
    u.GetAncestor.argtypes = (wintypes.HWND, wintypes.UINT)
    u.GetWindowLongW.restype = wintypes.LONG
    u.GetWindowLongW.argtypes = (wintypes.HWND, ctypes.c_int)
    u.SetWindowLongW.restype = wintypes.LONG
    u.SetWindowLongW.argtypes = (wintypes.HWND, ctypes.c_int, wintypes.LONG)
    u.InvalidateRect.restype = wintypes.BOOL
    u.InvalidateRect.argtypes = (wintypes.HWND, wintypes.LPVOID, wintypes.BOOL)
    u.RedrawWindow.restype = wintypes.BOOL
    u.RedrawWindow.argtypes = (wintypes.HWND, wintypes.LPVOID, wintypes.HANDLE, wintypes.UINT)
    u.IsWindowVisible.restype = wintypes.BOOL
    u.IsWindowVisible.argtypes = (wintypes.HWND,)
    u.GetLayeredWindowAttributes.restype = wintypes.BOOL
    u.GetLayeredWindowAttributes.argtypes = (
        wintypes.HWND, ctypes.POINTER(wintypes.DWORD), ctypes.POINTER(ctypes.c_ubyte),
        ctypes.POINTER(wintypes.DWORD))
    u.GetWindowRect.restype = wintypes.BOOL
    u.GetWindowRect.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.RECT))
    _api_cache = u
    return u


GWL_EXSTYLE = -20
WS_EX_LAYERED = 0x00080000
WS_EX_TRANSPARENT = 0x00000020
GA_ROOT = 2
RDW_INVALIDATE = 0x0001
RDW_ALLCHILDREN = 0x0080
LWA_COLORKEY = 0x1
LWA_ALPHA = 0x2


def top_level_hwnd(tk_window_id, api=None):
    """Tk's winfo_id() is the inner client window; the wrapper Windows actually
    composites (and which carries -transparentcolor) is its root ancestor.
    GetAncestor(GA_ROOT) is used because it also copes with a not-yet-wrapped
    window (it then returns the window itself)."""
    api = api or _hwnd_api()
    getter = getattr(api, "GetAncestor", None)
    if getter is not None:
        return getter(tk_window_id, GA_ROOT) or tk_window_id
    return api.GetParent(tk_window_id) or tk_window_id


def exstyle_for_clickthrough(style, enabled):
    """The new extended window style. Enabling click-through needs layered +
    transparent. Disabling only clears WS_EX_TRANSPARENT: it must NOT add
    WS_EX_LAYERED, because a layered window that never had
    SetLayeredWindowAttributes called paints nothing at all."""
    if enabled:
        return style | WS_EX_LAYERED | WS_EX_TRANSPARENT
    return style & ~WS_EX_TRANSPARENT


def set_clickthrough(tk_window_id, enabled, api=None):
    """Make the window ignore the mouse (clicks fall through to what's underneath).
    Returns (hwnd, style_before, style_after); the style is only written if it
    actually changes."""
    api = api or _hwnd_api()
    hwnd = top_level_hwnd(tk_window_id, api)
    before = api.GetWindowLongW(hwnd, GWL_EXSTYLE)
    after = exstyle_for_clickthrough(before, enabled)
    if after != before:
        api.SetWindowLongW(hwnd, GWL_EXSTYLE, after)
    return hwnd, before, after


def invalidate(tk_window_id, api=None):
    """Repaint request without erasing the background (erasing every time
    flickers and, on a chroma-keyed window, can blank it)."""
    api = api or _hwnd_api()
    hwnd = top_level_hwnd(tk_window_id, api)
    redraw = getattr(api, "RedrawWindow", None)
    if redraw is not None:
        ok = redraw(hwnd, None, None, RDW_INVALIDATE | RDW_ALLCHILDREN)
    else:
        ok = api.InvalidateRect(hwnd, None, False)
    if not ok:
        raise OSError("RedrawWindow/InvalidateRect returned 0")


def describe_window(tk_window_id, api=None):
    """Facts about the real Win32 window for the diagnostics log."""
    import ctypes
    from ctypes import wintypes
    api = api or _hwnd_api()
    inner = tk_window_id
    parent = api.GetParent(inner)
    root = top_level_hwnd(inner, api)
    info = {"inner": inner, "parent": parent, "root": root}
    for label, h in (("inner", inner), ("root", root)):
        try:
            info["exstyle_" + label] = hex(api.GetWindowLongW(h, GWL_EXSTYLE) & 0xFFFFFFFF)
        except Exception as exc:
            info["exstyle_" + label] = "err:%s" % exc
    try:
        flags, colorkey, alpha = wintypes.DWORD(0), wintypes.DWORD(0), ctypes.c_ubyte(0)
        ok = api.GetLayeredWindowAttributes(root, ctypes.byref(colorkey), ctypes.byref(alpha), ctypes.byref(flags))
        info["layered_attrs"] = ("flags=%#x colorkey=%#08x alpha=%d" % (flags.value, colorkey.value, alpha.value)) \
            if ok else "not set (GetLayeredWindowAttributes failed, err %s)" % ctypes.get_last_error()
    except Exception as exc:
        info["layered_attrs"] = "err:%s" % exc
    try:
        info["visible_root"] = bool(api.IsWindowVisible(root))
        rect = wintypes.RECT()
        if api.GetWindowRect(root, ctypes.byref(rect)):
            info["rect"] = (rect.left, rect.top, rect.right, rect.bottom)
    except Exception as exc:
        info["visible_root"] = "err:%s" % exc
    return info
