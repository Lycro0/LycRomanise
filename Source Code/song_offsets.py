"""Remembers a lyric-timing adjustment per song (so a song whose lyrics run early or late is fixed once).

Stored in song_offsets.json as {"artist|title": milliseconds}. The global offset from Settings
still applies on top of this one. Never raises: a missing or damaged file just means "no offsets".
"""
import json
import logging
import os
import threading

from paths import DATA_DIR, write_json_atomic

log = logging.getLogger("lycro.offsets")

PATH = os.path.join(DATA_DIR, "song_offsets.json")
MAX_ENTRIES = 500
LIMIT_MS = 30000
_lock = threading.Lock()
_data = None


def make_key(artist, title):
    return "%s|%s" % (" ".join((artist or "").casefold().split()), " ".join((title or "").casefold().split()))


def _load():
    global _data
    if _data is None:
        try:
            with open(PATH, "r", encoding="utf-8") as f:
                raw = json.load(f)
        except (OSError, ValueError):
            raw = {}
        _data = {k: int(v) for k, v in (raw.items() if isinstance(raw, dict) else [])
                 if isinstance(k, str) and isinstance(v, (int, float)) and not isinstance(v, bool)
                 and abs(v) <= LIMIT_MS}
    return _data


def get_ms(key):
    with _lock:
        return _load().get(key, 0)


def set_ms(key, ms):
    """Store the offset for a song (0 removes it). Returns the stored value."""
    ms = max(-LIMIT_MS, min(LIMIT_MS, int(round(ms))))
    with _lock:
        data = _load()
        data.pop(key, None)
        if ms:
            data[key] = ms          # re-inserted last, so the oldest entries are dropped first
        while len(data) > MAX_ENTRIES:
            data.pop(next(iter(data)))
        try:
            write_json_atomic(PATH, data, ensure_ascii=False)
        except OSError as exc:
            log.warning("couldn't write song offsets: %s", exc)
    return ms
