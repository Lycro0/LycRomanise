"""Thin wrapper around spotipy for reading the user's currently-playing track.

Only uses read-only scopes (user-read-currently-playing, user-read-playback-state).
This app never modifies playback or any of the user's data.
"""

import logging
import os
import time
import webbrowser

import requests
import spotipy
from spotipy.exceptions import SpotifyException
from spotipy.oauth2 import SpotifyOAuth, SpotifyPKCE

try:
    from spotipy.oauth2 import SpotifyOauthError
except ImportError:      # very old spotipy
    SpotifyOauthError = None

import config

log = logging.getLogger("spoti.spotify")

SCOPES = "user-read-currently-playing user-read-playback-state"
DEFAULT_REDIRECT_URI = "http://127.0.0.1:8888/callback"

# Spotify doesn't publish an exact rate-limit number for this endpoint. When a
# 429 does come back, Spotify's own Retry-After header says how long to wait —
# this is just the fallback for the rare case that header is missing.
DEFAULT_RETRY_AFTER_S = 30
MAX_RETRY_AFTER_S = 300   # sanity cap in case a header ever comes back absurd
MAX_BACKOFF_S = 60        # cap for the plain-error backoff below
MAX_TIMEOUT_BACKOFF_S = 8  # a slow response is transient - don't sit out for long

# (connect, read) seconds. The old single 6s value was too tight: Spotify's API
# occasionally takes longer than that to answer, which showed up as
# "Read timed out. (read timeout=6)" even though nothing was wrong.
REQUEST_TIMEOUT = (5, 15)

def _retry_after_seconds(exc, default=DEFAULT_RETRY_AFTER_S):
    """Pull Retry-After out of a SpotifyException's response headers, if
    it's there. Header casing/availability isn't guaranteed across
    spotipy versions, so this is deliberately tolerant."""
    headers = getattr(exc, "headers", None) or {}
    for key, value in headers.items():
        if key.lower() == "retry-after":
            try:
                return max(1, min(int(float(value)), MAX_RETRY_AFTER_S))
            except (TypeError, ValueError):
                break
    return default

def _is_auth_error(exc):
    """Bad client id/secret, revoked token, declined login, etc."""
    if SpotifyOauthError is not None and isinstance(exc, SpotifyOauthError):
        return True
    text = str(exc).lower()
    return any(k in text for k in ("invalid_client", "invalid client", "invalid_grant",
                                   "invalid_request", "unauthorized", "access_denied"))

def _track_dict(item, progress_ms=0, is_playing=False):
    return {
        "id": item.get("id") or item.get("uri") or item.get("name"),
        "name": item.get("name", "Unknown track"),
        "artist": ", ".join(a["name"] for a in item.get("artists", []) if a.get("name"))
                  or (item.get("show", {}) or {}).get("name", ""),
        "album": (item.get("album") or {}).get("name", ""),
        "duration_ms": item.get("duration_ms", 0),
        "progress_ms": progress_ms,
        "is_playing": is_playing,
    }

def open_in_browser(url):
    """Open `url` in the default browser. webbrowser.open can quietly return False inside a
    packaged .exe, so on Windows fall back to os.startfile, then to `start` via the shell.
    Returns True if something was launched."""
    log.info("opening Spotify login page: %s", url)
    try:
        if webbrowser.open(url, new=2):
            return True
    except Exception:
        log.warning("webbrowser.open failed", exc_info=True)
    if hasattr(os, "startfile"):
        try:
            os.startfile(url)
            return True
        except Exception:
            log.warning("os.startfile failed", exc_info=True)
    return False


class _BrowserMixin:
    """Replaces spotipy's own browser launch with open_in_browser, and remembers the login
    URL so the UI can show it for copy/paste if no browser opened."""
    last_auth_url = None
    browser_opened = False

    def _open_auth_url(self):
        url = self.get_authorize_url()
        self.last_auth_url = url
        self.browser_opened = open_in_browser(url)


class _OAuth(_BrowserMixin, SpotifyOAuth):
    pass


class _PKCE(_BrowserMixin, SpotifyPKCE):
    pass


class SpotifyClient:
    def __init__(self, client_id, client_secret=None, redirect_uri=DEFAULT_REDIRECT_URI,
                 cache_path=config.TOKEN_CACHE_PATH):
        # cache_path is absolute (next to the app), NOT relative to whatever
        # folder the app happened to be launched from - otherwise launching
        # from a shortcut or a different folder looks like a fresh install and
        # sends you back through the browser login.
        if client_secret:       # your own app with a secret: classic authorization-code flow
            auth_manager = _OAuth(
                client_id=client_id, client_secret=client_secret, redirect_uri=redirect_uri,
                scope=SCOPES, cache_path=cache_path, open_browser=True,
            )
        else:                   # "Log in with Spotify": PKCE, no secret needed
            auth_manager = _PKCE(
                client_id=client_id, redirect_uri=redirect_uri,
                scope=SCOPES, cache_path=cache_path, open_browser=True,
            )
        # retries=0 / status_retries=0: spotipy's built-in behavior on a 429 is
        # to *sleep* for the whole Retry-After period inside the call (and
        # retry, which counts against the limit again). Polling runs on a
        # background thread, so we handle 429 ourselves with a cooldown.
        self.sp = spotipy.Spotify(
            auth_manager=auth_manager, retries=0, status_retries=0,
            requests_timeout=REQUEST_TIMEOUT,
        )
        self._cooldown_until = 0.0
        self._consecutive_errors = 0
        self.last_auth_status = None     # HTTP status of the last auth_error (403 = account not allowed)

    def login(self):
        """Run the browser login right now: opens Spotify's page and blocks until the user
        clicks Agree (or it fails). Returns True once a token is saved. Meant for a
        background thread - never call it on the UI thread."""
        manager = self.sp.auth_manager
        try:
            token = manager.get_access_token(as_dict=False)   # SpotifyOAuth
        except TypeError:
            token = manager.get_access_token()                # SpotifyPKCE takes no as_dict
        return bool(token)

    def poll(self):
        """Ask Spotify what's playing. Returns (status, track):

          ("ok", track_dict)   something is loaded (check track["is_playing"])
          ("idle", None)       Spotify says nothing is playing at all
          ("auth_error", None) Spotify sign-in / credentials problem (shown on
                               the strip; retried with backoff)
          ("error", None)      couldn't find out (network error, rate limit,
                               cooldown) - callers should keep showing what
                               they had rather than treat it as "stopped"
        """
        now = time.perf_counter()
        if now < self._cooldown_until:
            return "error", None

        request_start = now
        try:
            current = self.sp.current_user_playing_track()
        except SpotifyException as exc:
            if exc.http_status in (401, 403):
                self.last_auth_status = exc.http_status
                log.error("Spotify rejected our credentials/permission (HTTP %s): %s", exc.http_status, exc)
                self._register_failure()
                return "auth_error", None
            if exc.http_status == 429:
                retry_after = _retry_after_seconds(exc)
                self._cooldown_until = time.perf_counter() + retry_after
                log.warning("rate limited (429) - pausing polling for %ss", retry_after)
            else:
                log.warning("error fetching playback state: %s", exc)
                self._register_failure()
            return "error", None
        except (requests.exceptions.Timeout, requests.exceptions.ConnectionError) as exc:
            # Slow or dropped connection: transient, retry soon.
            log.warning("Spotify didn't answer in time (%s) - will retry shortly", type(exc).__name__)
            self._register_failure(transient=True)
            return "error", None
        except Exception as exc:  # expired token mid-refresh, etc.
            if _is_auth_error(exc):
                log.error("Spotify sign-in failed: %s", exc)
                self._register_failure()
                return "auth_error", None
            log.warning("error fetching playback state: %s", exc)
            self._register_failure()
            return "error", None

        self._consecutive_errors = 0
        self.last_auth_status = None
        request_elapsed_s = time.perf_counter() - request_start

        if not current or not current.get("item"):
            return "idle", None

        progress_ms = current.get("progress_ms") or 0
        is_playing = bool(current.get("is_playing", False))
        if is_playing:
            # progress_ms is the position when Spotify's server handled the
            # request, not when we got the response - add back about half the
            # round trip so lyrics don't fire late on a slow connection.
            progress_ms += request_elapsed_s * 1000 / 2
        return "ok", _track_dict(current["item"], progress_ms, is_playing)

    def get_current_track(self):
        """Backwards-compatible: the track dict or None."""
        status, track = self.poll()
        return track if status == "ok" else None

    def get_upcoming_tracks(self, n=2):
        """The next `n` tracks in the queue (fewer if the queue is shorter),
        used to fetch their lyrics ahead of time."""
        if time.perf_counter() < self._cooldown_until:
            return []
        out, seen = [], set()
        try:
            data = self.sp._get("me/player/queue")
            queue = (data or {}).get("queue") if isinstance(data, dict) else None
            # The queue can be empty (end of the playlist, nothing queued), shorter than n,
            # hold podcast episodes/ads (skipped), or list a song twice (repeat/shuffle):
            # return only real, distinct tracks, at most n.
            for item in queue if isinstance(queue, list) else []:
                if not isinstance(item, dict) or item.get("type", "track") != "track":
                    continue
                track = _track_dict(item)
                if track["id"] in seen:
                    continue
                seen.add(track["id"])
                out.append(track)
                if len(out) >= n:
                    break
        except SpotifyException as exc:
            if exc.http_status == 429:      # honour Spotify's Retry-After for the queue call too
                retry_after = _retry_after_seconds(exc)
                self._cooldown_until = max(self._cooldown_until, time.perf_counter() + retry_after)
                log.warning("rate limited (429) on the queue - pausing polling for %ss", retry_after)
            else:
                log.warning("couldn't read the queue: %s", exc)
        except Exception as exc:
            log.warning("couldn't read the queue: %s", exc)
        return out

    def get_next_track(self):
        """The single next track, or None."""
        upcoming = self.get_upcoming_tracks(1)
        return upcoming[0] if upcoming else None

    def _register_failure(self, transient=False):
        """Errors get a short, escalating backoff - 2s, 4s, 8s... - rather
        than being retried every poll. Timeouts are capped much lower since
        they usually clear on the next try."""
        self._consecutive_errors = min(self._consecutive_errors + 1, 6)
        cap = MAX_TIMEOUT_BACKOFF_S if transient else MAX_BACKOFF_S
        backoff = min(2 ** self._consecutive_errors if not transient else self._consecutive_errors * 2, cap)
        self._cooldown_until = max(self._cooldown_until, time.perf_counter() + backoff)
