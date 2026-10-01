"""Loads Spotify app credentials, and loads/saves the appearance settings and
the remembered window position used by Spoti-Lyrics Overlay.

Credentials priority:
  1. Environment variables SPOTIPY_CLIENT_ID / SPOTIPY_CLIENT_SECRET
     (these are spotipy's own conventional names)
  2. A local config.json (written by Settings in the app)

Everything the app writes (logs/, lyrics_cache.json, window_state.json,
appearance.json, the Spotify token cache) lives next to these files, so it
doesn't matter which folder you launch the app from.
"""

import json
import logging
import os

log = logging.getLogger("spoti.config")

from paths import DATA_DIR as BASE_DIR
CONFIG_PATH = os.path.join(BASE_DIR, "config.json")
APPEARANCE_PATH = os.path.join(BASE_DIR, "appearance.json")
WINDOW_STATE_PATH = os.path.join(BASE_DIR, "window_state.json")
TOKEN_CACHE_PATH = os.path.join(BASE_DIR, ".spotify_token_cache")
from paths import RESOURCE_DIR as _RES_DIR
ICON_PATH = os.path.join(_RES_DIR, "assets", "lyrics-overlay.ico")

# Appearance/behaviour settings live in their own file, separate from
# credentials, so this one is safe to share/back up without leaking anything.
# Edit it by hand, or with the config_editor.py GUI.
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

ENV_ID, ENV_SECRET = "SPOTIPY_CLIENT_ID", "SPOTIPY_CLIENT_SECRET"

# The Spotify app everyone logs in through ("Log in with Spotify" - no secret needed, it uses
# the PKCE flow). Left empty in the public source: running from source needs your own Client ID,
# entered in the app's login window / Settings. Release builds ship with one built in.
BUILTIN_CLIENT_ID = ""


def env_overrides_credentials():
    """True when both environment variables are set: they win over config.json."""
    return bool(os.environ.get(ENV_ID) and os.environ.get(ENV_SECRET))


def load_credentials():
    """(client_id, client_secret_or_None). Order: environment, config.json, the built-in app.
    No secret means the "Log in with Spotify" (PKCE) flow."""
    client_id = os.environ.get(ENV_ID)
    if client_id:
        return client_id.strip(), (os.environ.get(ENV_SECRET) or "").strip() or None
    saved_id, saved_secret = load_saved_credentials()
    if saved_id and saved_id.strip():
        return saved_id.strip(), (saved_secret or "").strip() or None
    if BUILTIN_CLIENT_ID.strip():
        return BUILTIN_CLIENT_ID.strip(), None
    return None, None


def load_saved_credentials():
    """(client_id, client_secret) from config.json only (ignores the environment).
    Missing file -> (None, None). A file that exists but can't be parsed raises."""
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            return data.get("client_id"), data.get("client_secret")

    return None, None


def save_credentials(client_id, client_secret):
    """Write the Spotify credentials into config.json (other keys are kept).
    Pasted values often carry stray spaces/newlines, so they are stripped."""
    data = {}
    if os.path.exists(CONFIG_PATH):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict):
                data = loaded
        except (ValueError, OSError):
            log.warning("config.json was unreadable; replacing it")
    data["client_id"] = (client_id or "").strip()
    data["client_secret"] = (client_secret or "").strip()
    tmp = CONFIG_PATH + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=4)
    os.replace(tmp, CONFIG_PATH)     # never leaves a half-written file behind
    log.info("Spotify credentials saved to config.json")

def clear_credentials():
    """Forget a custom client ID/secret so the built-in app is used again."""
    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict):
            data.pop("client_id", None)
            data.pop("client_secret", None)
            with open(CONFIG_PATH, "w", encoding="utf-8") as f:
                json.dump(data, f, indent=4)
    except (OSError, ValueError):
        pass


def logged_in():
    """True when a saved Spotify login (token) exists."""
    return os.path.exists(TOKEN_CACHE_PATH)


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
