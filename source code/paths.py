"""Where things live.

Frozen (the .exe): the program sits wherever it was installed (e.g. Program Files),
which is often read-only, so everything the app writes (settings, Spotify login,
lyrics cache, logs) goes to %LOCALAPPDATA%\\LycRomanise. Local rather than Roaming:
the cache and Spotify token are machine-specific and shouldn't sync between PCs.
Bundled resources (the icon) come from PyInstaller's temp unpack folder.
From source: both are the source folder, so development is unchanged.
"""
import os
import shutil
import sys

APP_NAME = "LycRomanise"
FROZEN = bool(getattr(sys, "frozen", False))

if FROZEN:
    APP_DIR = os.path.dirname(os.path.abspath(sys.executable))
    RESOURCE_DIR = getattr(sys, "_MEIPASS", APP_DIR)
    _base = os.environ.get("LOCALAPPDATA") or os.path.join(os.path.expanduser("~"), "AppData", "Local")
    DATA_DIR = os.path.join(_base, APP_NAME)
else:
    APP_DIR = RESOURCE_DIR = DATA_DIR = os.path.dirname(os.path.abspath(__file__))

try:
    os.makedirs(DATA_DIR, exist_ok=True)
except OSError:
    pass

# Older builds kept their files next to the .exe: move them across once.
_LEGACY = ("config.json", "appearance.json", "window_state.json", ".spotify_token_cache",
           "lyrics_cache.json", "provider_health.json")
if FROZEN and os.path.normcase(APP_DIR) != os.path.normcase(DATA_DIR):
    for _name in _LEGACY:
        _old, _new = os.path.join(APP_DIR, _name), os.path.join(DATA_DIR, _name)
        try:
            if os.path.isfile(_old) and not os.path.exists(_new):
                shutil.move(_old, _new)
        except OSError:
            pass
