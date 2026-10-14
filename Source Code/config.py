"""Loads and saves the appearance settings, the remembered window position and the optional
config.json settings used by LycRomanise.

Everything the app writes (logs/, lyrics_cache.json, window_state.json, appearance.json) lives in
one data folder (see paths.py), so it doesn't matter which folder you launch the app from.
"""

import json
import logging
import os

log = logging.getLogger("lycro.config")

from paths import DATA_DIR as BASE_DIR
CONFIG_PATH = os.path.join(BASE_DIR, "config.json")
APPEARANCE_PATH = os.path.join(BASE_DIR, "appearance.json")
WINDOW_STATE_PATH = os.path.join(BASE_DIR, "window_state.json")
from paths import RESOURCE_DIR as _RES_DIR
ICON_PATH = os.path.join(_RES_DIR, "assets", "lyrics-overlay.ico")

from paths import write_json_atomic

# Appearance/behaviour settings live in appearance.json. Edit it by hand, or with the Settings window.
DEFAULT_APPEARANCE = {
    "karaoke_unsung_rgb": [255, 225, 77],   # #ffe14d yellow — not-yet-sung text
    "karaoke_sung_rgb": [255, 61, 154],     # #ff3d9a pink — already-sung text
    "next_line_rgb": [255, 225, 77],        # #ffe14d — next-line preview
    "desktop_bg": "#000000",
    "curr_font_size": 26,
    "next_font_size": 19,
    "curr_font_min_size": 13,
    "next_font_min_size": 12,
    "desktop_width": 720,
    "desktop_height": 150,
    "lyric_offset_ms": 0,                   # +ve = lyrics appear earlier, -ve = later
    "outline_size": 1.5,                    # text border, px (0 = none)
    "outline_rgb": [0, 0, 0],               # text border colour (also used for the title card and next line)
    "spawn_y_percent": 18,                  # first-launch height of the strip, % down the screen
    "max_line_chars": 0,                    # split lines longer than this many characters (0 = never: lines stay whole, as timed by the lyrics site)
    "show_title_card": True,                # "Artist - Song" card (about 10 s, or until the first lyric starts)
    "hide_lock_icon": True,                 # True: no padlock on the strip; lock/unlock from the tray menu only
    "hide_desktop_strip": False,            # True: no lyrics on this PC (they are only served to the OBS / phone page); needs mobile_enabled
    "mobile_enabled": False,                # True: also show the lyrics on phones/tablets on the same Wi-Fi (built-in web page)
    "mobile_port": 8765,                    # port of that web page
    "show_in_taskbar": False,               # True: the strip also has a taskbar button (so OBS and other tools can list it); applies live
}

# key -> (type, minimum, maximum) for numbers; colours are checked by _rgb / _hex
_RANGES = {
    "curr_font_size": (int, 8, 120), "next_font_size": (int, 8, 120),
    "curr_font_min_size": (int, 6, 120), "next_font_min_size": (int, 6, 120),
    "desktop_width": (int, 200, 4000), "desktop_height": (int, 60, 1500),
    "lyric_offset_ms": (float, -30000, 30000), "outline_size": (float, 0, 4),
    "spawn_y_percent": (float, 0, 90), "max_line_chars": (int, 0, 200), "mobile_port": (int, 1024, 65535),
}
_RGB_KEYS = ("karaoke_unsung_rgb", "karaoke_sung_rgb", "next_line_rgb", "outline_rgb")
_BOOL_KEYS = ("show_title_card", "hide_lock_icon", "hide_desktop_strip", "mobile_enabled", "show_in_taskbar")


def _rgb(value, default):
    try:
        rgb = [int(c) for c in value]
        if len(rgb) >= 3 and all(0 <= c <= 255 for c in rgb[:3]):
            return rgb[:3]
    except (TypeError, ValueError):
        pass
    return list(default)


def _hex(value, default):
    if isinstance(value, str) and len(value) == 7 and value[0] == "#":
        try:
            int(value[1:], 16)
            return value.lower()
        except ValueError:
            pass
    return default


def sanitize(settings):
    """Return a complete, valid settings dict: unknown keys dropped, wrong types / out-of-range
    values replaced by the default (numbers are clamped). Never raises."""
    src = settings if isinstance(settings, dict) else {}
    out = {}
    for key, default in DEFAULT_APPEARANCE.items():
        value = src.get(key, default)
        if key in _RGB_KEYS:
            out[key] = _rgb(value, default)
        elif key == "desktop_bg":
            out[key] = _hex(value, default)
        elif key in _BOOL_KEYS:
            out[key] = value if isinstance(value, bool) else default
        elif key in _RANGES:
            cast, lo, hi = _RANGES[key]
            try:
                if isinstance(value, bool):
                    raise ValueError
                num = cast(float(value)) if cast is int else float(value)
                out[key] = max(lo, min(hi, num))
            except (TypeError, ValueError, OverflowError):
                out[key] = default
        else:
            out[key] = default
    if out["next_font_min_size"] > out["next_font_size"]:
        out["next_font_min_size"] = out["next_font_size"]
    if out["curr_font_min_size"] > out["curr_font_size"]:
        out["curr_font_min_size"] = out["curr_font_size"]
    return out


def config_str_list(key):
    """A list of non-empty strings from config.json (e.g. "extra_players": ["tidal"]); [] if the
    key is missing or isn't a list of strings. Never raises."""
    try:
        if os.path.exists(CONFIG_PATH):
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                data = json.load(f)
            val = data.get(key) if isinstance(data, dict) else None
            if isinstance(val, list):
                return [v.strip() for v in val if isinstance(v, str) and v.strip()]
    except (OSError, ValueError):
        pass
    return []


def check_updates_enabled():
    """Set "check_updates": false in config.json to turn the GitHub update check off."""
    try:
        if os.path.exists(CONFIG_PATH):
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                data = json.load(f)
            if isinstance(data, dict) and data.get("check_updates") is False:
                return False
    except (OSError, ValueError):
        pass
    return True


def load_appearance():
    """Return the (sanitized) appearance settings: defaults merged with appearance.json.
    Never raises: a missing, corrupt or hand-edited-wrong file can't stop the app from starting."""
    loaded = {}
    try:
        if os.path.exists(APPEARANCE_PATH):
            with open(APPEARANCE_PATH, "r", encoding="utf-8") as f:
                loaded = json.load(f)
    except (ValueError, OSError) as exc:
        log.warning("ignoring unreadable appearance.json: %s", exc)
    try:
        return sanitize(loaded)
    except Exception as exc:
        log.warning("settings could not be checked, using defaults: %s", exc)
        return dict(DEFAULT_APPEARANCE)


def save_appearance(data):
    """Write out appearance.json (sanitized, only known keys, atomically)."""
    write_json_atomic(APPEARANCE_PATH, sanitize(data), indent=2)


def update_appearance(**changes):
    """Load, change a few keys, save. Used by the tray/Settings so
    toggling something there sticks for the next launch."""
    try:
        settings = load_appearance()
        settings.update(changes)
        save_appearance(settings)
    except Exception as exc:  # cosmetics must never crash the app
        log.warning("couldn't save settings %s: %s", list(changes), exc)


def reset_window_position():
    """Forget the saved strip position (keeps the lock state), so the next start uses the default place."""
    state = load_window_state()
    state.pop("x", None)
    state.pop("y", None)
    save_window_state(state)

def load_window_state():
    """{'x': int, 'y': int, 'locked': bool} from the last session, or {}."""
    try:
        with open(WINDOW_STATE_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}

def save_window_state(state):
    try:
        write_json_atomic(WINDOW_STATE_PATH, state)
    except OSError as exc:
        log.warning("couldn't save window position: %s", exc)
