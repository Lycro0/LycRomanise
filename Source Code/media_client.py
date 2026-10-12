"""Finds out what Spotify, Spotifast or Apple Music is playing by asking Windows (no login, no
account, no rate limit). These players publish the current song to the
Windows media controls (the volume-flyout / lock-screen player, "SMTC"): title,
artist, album, length, position and play/pause state. That is read locally.

If that isn't available (the `winrt` packages are missing, or Windows has no media
session for the player), it falls back to reading the player's window title ("Artist - Song").
The title has no length or position, so the position is counted from when the song
was first seen. Lyrics still work, but the timing can be a bit off after a seek.
"""

import asyncio
import ctypes
import logging
import sys
import time
from ctypes import wintypes
from datetime import datetime, timezone

log = logging.getLogger("spoti.media")

# SMTC session ids: Spotify desktop "Spotify.exe" / Store "SpotifyAB.SpotifyMusic_...!Spotify";
# Spotifast (open source, low-memory Spotify client, spotifast.rocks) "spotifast.exe", or
# "fastpotify.exe" for installs from before it was renamed;
# Apple Music for Windows "AppleInc.AppleMusicWin_...!App"; classic "iTunes.exe".
# Matching is by substring, and "spotify" is not inside "spotifast", so Spotifast is listed on its own.
APP_MATCHES = ("spotify", "spotifast", "fastpotify", "applemusic", "apple music", "itunes")
# Window title fallback: the player's process names (lower case).
PLAYER_EXES = ("spotify.exe", "spotifast.exe", "fastpotify.exe")
# These show up instead of a song while an ad plays or when paused / idle.
NOT_A_SONG = {"", "spotify", "spotify free", "spotify premium", "advertisement", "spotify - web player",
              "spotifast", "fastpotify"}
# Window titles that mean "paused" (the app is open but shows its own name).
PAUSED_TITLES = ("spotify", "spotify free", "spotify premium", "spotifast", "fastpotify")

PLAYING = 4          # GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
SMTC_MAX_FAILURES = 5        # in a row, then the media controls are left alone for a while
SMTC_RETRY_AFTER_S = 30.0    # ...and tried again after this long

try:                                    # winrt-* packages (maintained)
    from winrt.windows.media.control import GlobalSystemMediaTransportControlsSessionManager as _Manager
    _WINRT = "winrt"
except Exception:
    try:                                # older "winsdk" package
        from winsdk.windows.media.control import GlobalSystemMediaTransportControlsSessionManager as _Manager
        _WINRT = "winsdk"
    except Exception:
        _Manager = None
        _WINRT = None


def smtc_available():
    return _Manager is not None and sys.platform == "win32"


def _track_dict(title, artist, album, duration_ms, progress_ms, is_playing):
    return {
        # Not the length: Windows reports a song's length late and in steps right after a skip,
        # and a changing id would restart the lyrics lookup each time it moved.
        "id": "%s|%s" % (artist, title),
        "name": title,
        "artist": artist,
        "album": album,
        "duration_ms": duration_ms,
        "progress_ms": progress_ms,
        "is_playing": is_playing,
    }


def _td_ms(value):
    """timedelta (winrt 2.x) or 100ns ticks (older builds) -> milliseconds."""
    if value is None:
        return 0.0
    if hasattr(value, "total_seconds"):
        return value.total_seconds() * 1000.0
    try:
        return float(value) / 10000.0
    except (TypeError, ValueError):
        return 0.0


class MediaClient:
    """poll() -> ("ok", track) / ("idle", None) / ("error", None)."""

    def __init__(self):
        self._manager = None
        self._smtc_failures = 0
        self._smtc_retry_at = 0.0       # monotonic time the media controls are tried again after repeated failures
        self._logged_app = None         # media session id last logged as the one being read
        self._logged_unmatched = None   # media session ids last logged when none matched
        # Length bookkeeping: right after a skip Windows can still report the previous song's length.
        self._len_key = None            # (artist, title) being watched
        self._len_prev_ms = 0           # last length seen for the song before it
        self._len_last_ms = 0           # latest length seen for the current song
        self._len_first_ms = 0          # first length reported for this song
        self._len_seen_at = 0.0
        self._len_settled = True        # True once the length moved off the first/stale figure
        # window-title fallback state
        self._title_key = None
        self._title_started = 0.0
        self._title_last_track = None
        self._title_paused_at = None
        self._title_paused_total = 0.0
        self.source = "windows media controls" if smtc_available() else "window title"
        log.info("playback source: %s%s", self.source, " (%s)" % _WINRT if _WINRT else "")

    # ------------------------------------------------------------------ API --

    def poll(self):
        if smtc_available() and self._smtc_usable():
            try:
                result = asyncio.run(self._poll_smtc())
                self._smtc_failures = 0
                if result is not None:
                    return result
                # No supported media session: maybe the player isn't open - try the window title.
            except Exception as exc:
                self._smtc_failures += 1
                self._manager = None
                log.warning("Windows media controls failed (%s: %s)", type(exc).__name__, exc)
                if self._smtc_failures >= SMTC_MAX_FAILURES:
                    self._smtc_retry_at = time.monotonic() + SMTC_RETRY_AFTER_S
                    log.warning("media controls failed %d times in a row; using the window title for %.0f s",
                                self._smtc_failures, SMTC_RETRY_AFTER_S)
        return self._poll_window_title()

    def _smtc_usable(self):
        """False while the media controls are resting after repeated failures. After the rest one
        more failure puts them back to sleep, so a broken setup doesn't spam the log."""
        if self._smtc_failures < SMTC_MAX_FAILURES:
            return True
        if time.monotonic() < self._smtc_retry_at:
            return False
        self._smtc_failures = SMTC_MAX_FAILURES - 1
        return True

    # ----------------------------------------------------------------- SMTC --

    async def _poll_smtc(self):
        if self._manager is None:
            self._manager = await _Manager.request_async()
        session, session_playing, session_app = None, False, ""
        seen_ids = []
        for s in self._manager.get_sessions():
            try:
                app_id = (s.source_app_user_model_id or "").lower()
                seen_ids.append(app_id)
                if not any(m in app_id for m in APP_MATCHES):
                    continue
                playing = int(getattr(s.get_playback_info().playback_status, "value",
                                      s.get_playback_info().playback_status)) == PLAYING
                if session is None or (playing and not session_playing):
                    session, session_playing, session_app = s, playing, app_id    # prefer whichever is playing
            except Exception:
                continue
        if session is None:
            # Log what Windows does list (once per change), so a player that isn't recognised
            # can be added to APP_MATCHES from the log.
            key = tuple(sorted(seen_ids))
            if key != self._logged_unmatched:
                self._logged_unmatched = key
                log.info("no supported player among the Windows media sessions: %s", list(key) or "none")
            return None
        if session_app != self._logged_app:
            self._logged_app = session_app
            log.info("reading playback from media session %s", session_app)

        props = await session.try_get_media_properties_async()
        title = (props.title or "").strip()
        artist = (props.artist or "").strip()
        album = (props.album_title or "").strip()
        if title.lower() in NOT_A_SONG:
            return ("idle", None)
        # Apple Music puts "Artist \u2014 Album" in the artist field.
        if "\u2014" in artist:
            left, _, right = artist.partition("\u2014")
            artist, album = left.strip(), album or right.strip()

        info = session.get_playback_info()
        status = int(getattr(info.playback_status, "value", info.playback_status))
        is_playing = status == PLAYING

        tl = session.get_timeline_properties()
        start_ms = _td_ms(getattr(tl, "start_time", None))
        end_ms = _td_ms(getattr(tl, "end_time", None))
        duration_ms = max(0.0, end_ms - start_ms)
        position_ms = max(0.0, _td_ms(getattr(tl, "position", None)) - start_ms)
        if is_playing:
            # `position` is the value at `last_updated_time`; add the time since.
            stamp = getattr(tl, "last_updated_time", None)
            try:
                if stamp is not None:
                    if stamp.tzinfo is None:
                        stamp = stamp.replace(tzinfo=timezone.utc)
                    age_ms = (datetime.now(timezone.utc) - stamp).total_seconds() * 1000.0
                    if 0 <= age_ms < 6 * 3600 * 1000:
                        position_ms += age_ms
            except Exception:
                pass
        if duration_ms:
            position_ms = min(position_ms, duration_ms)
        track = _track_dict(title, artist, album, int(duration_ms), position_ms, is_playing)
        track["duration_suspect"] = self._length_is_suspect(artist, title, duration_ms)
        return ("ok", track)

    def _length_is_suspect(self, artist, title, duration_ms):
        """True while a new song's length is still the previous song's (Windows hasn't updated it
        yet). The app waits a moment before looking up lyrics by length."""
        now = time.monotonic()
        key = (artist, title)
        if key != self._len_key:
            self._len_prev_ms = self._len_last_ms if self._len_key else 0
            self._len_key, self._len_first_ms, self._len_seen_at = key, duration_ms, now
            self._len_settled = False
        elif not self._len_settled and abs(duration_ms - self._len_first_ms) > 1500:
            self._len_settled = True        # the figure changed: the real one has arrived
        self._len_last_ms = duration_ms
        if self._len_settled or not duration_ms or not self._len_prev_ms or now - self._len_seen_at > 8.0:
            return False
        return abs(duration_ms - self._len_prev_ms) <= 1500

    # --------------------------------------------------------- window title --

    def _poll_window_title(self):
        title = spotify_window_title()
        if title is None:
            return ("idle", None)          # Spotify not running / no window
        now = time.perf_counter()
        low = title.strip().lower()
        if " - " not in title or low in NOT_A_SONG:
            # Paused (title goes back to "Spotify Free/Premium") or an ad.
            last = self._title_last_track
            if last is not None and low in PAUSED_TITLES:
                if self._title_paused_at is None:
                    self._title_paused_at = now
                return ("ok", self._title_track(last, now, playing=False))
            return ("idle", None)
        artist, name = [p.strip() for p in title.split(" - ", 1)]
        key = (artist, name)
        if key != self._title_key:
            self._title_key = key
            self._title_started = now
            self._title_paused_at = None
            self._title_paused_total = 0.0
            self._title_last_track = (name, artist)
        elif self._title_paused_at is not None:
            self._title_paused_total += now - self._title_paused_at
            self._title_paused_at = None
        return ("ok", self._title_track(self._title_last_track, now, playing=True))

    def _title_track(self, last, now, playing):
        name, artist = last
        end = now if playing or self._title_paused_at is None else self._title_paused_at
        elapsed = max(0.0, end - self._title_started - self._title_paused_total)
        return _track_dict(name, artist, "", 0, elapsed * 1000.0, playing)


# ---------------------------------------------------------- window helpers ---

def spotify_window_title():
    """Title of the Spotify (or Spotifast) desktop window, or None if there isn't one."""
    if sys.platform != "win32":
        return None
    user32, kernel32 = ctypes.windll.user32, ctypes.windll.kernel32
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def visit(hwnd, _lparam):
        try:
            length = user32.GetWindowTextLengthW(hwnd)
            if length <= 0:
                return True
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            handle = kernel32.OpenProcess(0x1000, False, pid.value)      # PROCESS_QUERY_LIMITED_INFORMATION
            if not handle:
                return True
            try:
                buf = ctypes.create_unicode_buffer(520)
                size = wintypes.DWORD(520)
                if kernel32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
                    if buf.value.lower().endswith(PLAYER_EXES):
                        text = ctypes.create_unicode_buffer(length + 1)
                        user32.GetWindowTextW(hwnd, text, length + 1)
                        # The player can have several windows; the real one has a visible, non-empty title.
                        if text.value:
                            found.append(text.value)
            finally:
                kernel32.CloseHandle(handle)
        except Exception:
            pass
        return True

    user32.EnumWindows(visit, 0)
    # Prefer a "Artist - Song" title over "Spotify Free".
    for t in found:
        if " - " in t and t.strip().lower() not in NOT_A_SONG:
            return t
    return found[0] if found else None
