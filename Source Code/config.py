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
NEXT_SIZE_MARKER = "_next_size_v1_4_2"      # set once the next-line size default (17 -> 19) was applied
RESET_MARKER = "_defaults_reset_v1_3_2"    # set once every setting was reset to the defaults (v1.3.2)
defaults_were_reset = False                # True during the run in which that one-time reset happened
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
    global defaults_were_reset
    data = dict(DEFAULT_APPEARANCE)
    if os.path.exists(APPEARANCE_PATH):
        try:
            with open(APPEARANCE_PATH, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict) and not loaded.get(RESET_MARKER):
                # First start of v1.3.2: everyone's settings go back to the (new) defaults, once.
                log.info("first start of this version: resetting all settings to the defaults")
                defaults_were_reset = True
                try:
                    save_appearance(data)
                except OSError as exc:
                    log.warning("couldn't save the reset settings: %s", exc)
                return data
            if isinstance(loaded, dict):
                data.update(loaded)
                if not loaded.get(NEXT_SIZE_MARKER):
                    # v1.4.2: the next line is a little bigger. Only settings still on the old default move.
                    if loaded.get("next_font_size") == 17:
                        data["next_font_size"] = DEFAULT_APPEARANCE["next_font_size"]
                    if loaded.get("next_font_min_size") == 11:
                        data["next_font_min_size"] = DEFAULT_APPEARANCE["next_font_min_size"]
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
    clean[RESET_MARKER] = True
    clean[NEXT_SIZE_MARKER] = True
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
        with open(WINDOW_STATE_PATH, "w", encoding="utf-8") as f:
            json.dump(state, f)
    except OSError as exc:
        log.warning("couldn't save window position: %s", exc)
