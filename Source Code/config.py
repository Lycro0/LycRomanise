"""Loads and saves the appearance settings, the remembered window position and the optional
config.json settings used by LycRomanise.

Everything the app writes (logs/, lyrics_cache.json, window_state.json, appearance.json) lives in
one data folder (see paths.py), so it doesn't matter which folder you launch the app from.
"""

import json
import logging
import os

log = logging.getLogger("spoti.config")

from paths import DATA_DIR as BASE_DIR
CONFIG_PATH = os.path.join(BASE_DIR, "config.json")
APPEARANCE_PATH = os.path.join(BASE_DIR, "appearance.json")
WINDOW_STATE_PATH = os.path.join(BASE_DIR, "window_state.json")
from paths import RESOURCE_DIR as _RES_DIR
ICON_PATH = os.path.join(_RES_DIR, "assets", "lyrics-overlay.ico")

# Appearance/behaviour settings live in appearance.json. Edit it by hand, or with the Settings window.
TITLE_CARD_MARKER = "_title_card_off_v2"   # set once appearance.json was saved by this version
DEFAULT_APPEARANCE = {
    "karaoke_unsung_rgb": [255, 225, 77],   # #ffe14d yellow — not-yet-sung text
    "karaoke_sung_rgb": [255, 61, 154],     # #ff3d9a pink — already-sung text
    "next_line_rgb": [255, 225, 77],        # #ffe14d — next-line preview
    "desktop_bg": "#000000",
    "curr_font_size": 26,
    "next_font_size": 17,
    "curr_font_min_size": 13,
    "next_font_min_size": 11,
    "desktop_width": 720,
    "desktop_height": 150,
    "lyric_offset_ms": 0,                   # +ve = lyrics appear earlier, -ve = later
    "outline_size": 1.5,                    # black text border, px (0 = none)
    "spawn_y_percent": 18,                  # first-launch height of the strip, % down the screen
    "max_line_chars": 0,                    # split lines longer than this (0 = automatic)
    "show_title_card": False,               # "Artist - Song" before the first lyric (off by default)
}



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
    """Return the appearance settings dict, defaults merged with whatever
    appearance.json overrides. Never raises — a missing or corrupt file
    just falls back to defaults, since cosmetic settings shouldn't be
    able to stop the app from starting.
    """
    data = dict(DEFAULT_APPEARANCE)
    if os.path.exists(APPEARANCE_PATH):
        try:
            with open(APPEARANCE_PATH, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict):
                data.update(loaded)
                if not loaded.get(TITLE_CARD_MARKER):
                    # One-time reset: the title card is now off for everyone.
                    # Turning it back on in Settings sticks (save adds the marker).
                    data["show_title_card"] = False
        except (json.JSONDecodeError, OSError) as exc:
            log.warning("ignoring unreadable appearance.json: %s", exc)
    return data

def save_appearance(data):
    """Write out appearance.json. Only known keys are saved, so a typo'd
    or stale key from an older version doesn't get carried forward
    forever."""
    clean = {k: data[k] for k in DEFAULT_APPEARANCE if k in data}
    clean[TITLE_CARD_MARKER] = True
    with open(APPEARANCE_PATH, "w", encoding="utf-8") as f:
        json.dump(clean, f, indent=2)

def update_appearance(**changes):
    """Load, change a few keys, save. Used by the tray/Settings so
    toggling something there sticks for the next launch."""
    try:
        settings = load_appearance()
        settings.update(changes)
        save_appearance(settings)
    except Exception as exc:  # cosmetics must never crash the app
        log.warning("couldn't save settings %s: %s", list(changes), exc)

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
        with open(WINDOW_STATE_PATH, "w", encoding="utf-8") as f:
            json.dump(state, f)
    except OSError as exc:
        log.warning("couldn't save window position: %s", exc)
