"""LycRomanise — a small always-on-top synced-lyrics strip for Spotify and Apple Music,
with romanization for lyrics in many scripts.

Run: double-click LyricsOverlay.pyw (or LyricsOverlay.bat) - no console window.
`python app.py` also works: it relaunches itself without a console and exits.
Quit from the tray icon.
"""

import applog

applog.setup()   # first thing: file logging + crash capture (nothing goes to a console)

import logging
import math
import os
import queue
import sys
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
from functools import lru_cache

import config
import layout
import tray
import winddiag
import winsys
from drawing import (OUTLINE_RGB, _draw_outline_only, _draw_outlined_text, _ease_out_cubic,
                     _lerp_rgb, _outline_offsets, _rgb_to_hex)
from selftest import _selftest_report, run_selftest
from lyrics_provider import fetch_lyrics, is_non_song, outage_since
from romanize import detect_script, needs_romanization, romanize_line
from media_client import MediaClient
import updater
import update_dialog
from textnorm import normalize

log = logging.getLogger("spoti.app")

from version import __version__, RELEASES_URL


def _safe(fn, *args):
    """Call a dialog method that may run after the window was closed."""
    try:
        fn(*args)
    except tk.TclError:
        pass
APP_NAME = "LycRomanise v%s" % __version__

# --- polling ---------------------------------------------------------------
# The song is read from the Windows media controls, which is local (no network, no rate limit), so
# it can be polled quickly. Polling speeds up for a few seconds after a song changes (to lock the
# position in) and as a song is about to end (to spot the next one right away).
UPDATE_BANNER_S = 12.0
VERSION_BANNER_S = 5.0        # how long "LycRomanise v1.x.x" shows at startup
POLL_INTERVAL_MS = 500          # playing or paused (quick skip detection)
POLL_INTERVAL_IDLE_MS = 1000    # nothing playing at all
FAST_POLL_MS = 250
FAST_POLL_AFTER_CHANGE_S = 8.0
END_OF_TRACK_WINDOW_S = 8.0
RESULT_DRAIN_MS = 20          # how often the UI thread picks up finished background work
# A new poll only *nudges* the interpolated position by this fraction of the
# disagreement (the reported position is jittery), unless the gap is big
# enough to be a real seek, in which case it snaps.
POLL_CORRECTION_GAIN = 0.35
POLL_SEEK_THRESHOLD_S = 1.0
LINE_TICK_MS = 30              # how often the current line is re-checked, independent of polling
DESKTOP_TICK_MS = 20           # redraw rate for the strip
LINE_ANIM_SECONDS = 0.28       # slide transition between lines

TITLE_CARD_MAX_S = 8.0         # the "Artist - Song" card never shows longer than this
NO_LYRICS_NOTICE_S = 6.0

# --- desktop strip defaults (config.DEFAULT_APPEARANCE / appearance.json override) ---
DESKTOP_BG = "#000000"
DESKTOP_NEXT_RGB = (255, 225, 77)
DESKTOP_WIDTH = 720
DESKTOP_HEIGHT = 150
DESKTOP_CURR_SIZE = 26
DESKTOP_NEXT_SIZE = 17
DESKTOP_CURR_MIN_SIZE = 13
DESKTOP_NEXT_MIN_SIZE = 11
DESKTOP_TEXT_MARGIN = 40     # side margin (both sides combined) text must fit within
DESKTOP_MAX_LINES = 2        # last-resort wrap of the top line (long lines are normally split first)
OUTLINE_SIZE = 1.5
SPAWN_Y_PERCENT = 18
MAX_LINE_CHARS = 0
SHOW_TITLE_CARD = False

# Karaoke fill: the current line starts yellow (not-yet-sung) and turns pink
# left to right as playback reaches it, blended across a band scaled to the
# line's own average character width so it sweeps instead of snapping.
KARAOKE_UNSUNG_RGB = (255, 225, 77)
KARAOKE_SUNG_RGB = (255, 61, 154)
KARAOKE_GRADIENT_MIN_PX = 20
KARAOKE_GRADIENT_CHAR_SPAN = 2.2
LYRIC_OFFSET_S = 0.0   # +ve = lyrics run earlier; set via Settings / appearance.json

# Sources without per-word timing only give each line's *start*; assuming it
# finishes when the next line starts makes the fill crawl through pauses, so
# the fill is capped to a plausible sung pace for the line's length.
KARAOKE_CHARS_PER_SECOND = 6.0
KARAOKE_MIN_FILL_SECONDS = 0.6

LOCK_ICON_BG = "#1a1a1a"
LOCK_ICON_FG = "#b8b8b8"
LOCK_ICON_SIZE = (22, 22)
HOVER_BORDER_RGB = "#2c2c2c"   # faint frame, only while the pointer is over an unlocked strip
HOVER_HIDE_DELAY_S = 0.6       # chrome lingers briefly so the pointer can reach the lock icon
NOTICE_ERROR_FG = "#ff8a8a"
NOTICE_INFO_FG = "#cfcfcf"
APPEARANCE_WATCH_MS = 250
UI_CALL_DRAIN_MS = 20
REDRAW_KEEPALIVE_S = 0.5       # Windows full-repaint at most this often unless the scene changed

def _load_appearance():
    """Pull appearance.json (if any) in over the defaults above. A bad or
    missing file can never stop the app from starting."""
    try:
        s = config.load_appearance()
    except Exception as exc:
        log.warning("failed to load appearance settings, using defaults: %s", exc)
        return

    global DESKTOP_BG, DESKTOP_NEXT_RGB, DESKTOP_WIDTH, DESKTOP_HEIGHT
    global DESKTOP_CURR_SIZE, DESKTOP_NEXT_SIZE, DESKTOP_CURR_MIN_SIZE, DESKTOP_NEXT_MIN_SIZE
    global KARAOKE_UNSUNG_RGB, KARAOKE_SUNG_RGB, LYRIC_OFFSET_S, OUTLINE_SIZE, SPAWN_Y_PERCENT
    global MAX_LINE_CHARS, SHOW_TITLE_CARD

    def num(key, cast, default):
        try:
            return cast(s.get(key, default))
        except (TypeError, ValueError):
            return default

    DESKTOP_BG = s.get("desktop_bg", DESKTOP_BG)
    DESKTOP_NEXT_RGB = tuple(s.get("next_line_rgb", DESKTOP_NEXT_RGB))
    DESKTOP_WIDTH = num("desktop_width", int, DESKTOP_WIDTH)
    DESKTOP_HEIGHT = num("desktop_height", int, DESKTOP_HEIGHT)
    DESKTOP_CURR_SIZE = num("curr_font_size", int, DESKTOP_CURR_SIZE)
    DESKTOP_NEXT_SIZE = num("next_font_size", int, DESKTOP_NEXT_SIZE)
    DESKTOP_CURR_MIN_SIZE = num("curr_font_min_size", int, DESKTOP_CURR_MIN_SIZE)
    DESKTOP_NEXT_MIN_SIZE = num("next_font_min_size", int, DESKTOP_NEXT_MIN_SIZE)
    KARAOKE_UNSUNG_RGB = tuple(s.get("karaoke_unsung_rgb", KARAOKE_UNSUNG_RGB))
    KARAOKE_SUNG_RGB = tuple(s.get("karaoke_sung_rgb", KARAOKE_SUNG_RGB))
    LYRIC_OFFSET_S = num("lyric_offset_ms", float, 0.0) / 1000.0
    OUTLINE_SIZE = max(0.0, min(4.0, num("outline_size", float, OUTLINE_SIZE)))
    SPAWN_Y_PERCENT = max(0, min(90, num("spawn_y_percent", float, SPAWN_Y_PERCENT)))
    MAX_LINE_CHARS = max(0, num("max_line_chars", int, 0))
    SHOW_TITLE_CARD = bool(s.get("show_title_card", False))

_load_appearance()

class LyricsApp:
    LAST_LINE_ASSUMED_SPAN = 4.0  # seconds — used only when there's no next line to time against

    def __init__(self, root):
        self.root = root
        self.root.title(APP_NAME)
        applog.install_tk_handler(root)
        self.scale = self._detect_scale()   # 1.0 at 100% display scaling, 1.5 at 150%, ...

        # --- current track / lyrics state
        self.current_track_id = None
        self._lookup_lengths = {}        # track id -> track length (ms) its lyrics were looked up with
        self._relookups = 0
        self._relookup_busy = False
        self._len_candidate = None
        self.track = None
        self.title_text = ""
        self.duration_s = 0.0
        self.lyrics_state = "none"     # none | pending | ready | missing
        self.lyrics = []               # [(timestamp_s, raw_text)]
        self.norm_lines = []           # lyrics with numbers/symbols spelled out
        self.song_hint = None          # 'ko' | 'ja' | 'zh' | ... for the song as a whole
        self.segments = []             # displayed lines (long lyric lines are split into several)
        self.seg_starts = []
        self.line_infos = []
        self.current_index = -1        # index into self.segments

        # --- desktop strip state
        self.desktop_mode = False
        self.desktop_curr_size = DESKTOP_CURR_SIZE
        self.desktop_next_size = DESKTOP_NEXT_SIZE
        self._drag_offset = (0, 0)
        self.desktop_locked = False
        self.lock_window = None
        self.lock_icon_label = None
        self._lock_icon_visible = None
        self.desktop_canvas = None
        self._anim_start_perf = None

        # --- playback timing (interpolated between polls)
        self._last_position_s = 0.0
        self._last_poll_perf = time.perf_counter()
        self._is_playing = False
        self._fast_poll_until = 0.0
        self._results = queue.Queue()
        self._ui_calls = queue.Queue()   # callables posted from other threads (tray) to run on the Tk thread
        self._notice = None              # {"text", "kind", "until"} shown on the strip
        self._banner = None              # {"text", "until"}: version / update line, drawn over the lyrics too
        self._hover_since = 0.0
        self._hover_last = None
        self._show_border = False
        self._last_scene_sig = None
        self._last_full_redraw = 0.0
        self._settings_window = None
        self._appearance_mtime = self._appearance_stamp()
        self.tray = None
        self._tray_state = (False, False)
        self.player = None
        self._polling = False
        self._poll_busy = False
        self._poll_started = 0.0

        # The strip itself always comes up, even when the player can't be read,
        # so problems are shown *on it* (and in the log) instead of nowhere.
        self._build_desktop_ui(restore_lock=True)
        self.root.after(UI_CALL_DRAIN_MS, self._drain_ui_calls)
        self.root.after(APPEARANCE_WATCH_MS, self._watch_appearance)
        self.root.after(RESULT_DRAIN_MS, self._drain_results)
        self.root.after(LINE_TICK_MS, self._line_tick)

        self._connect_player()
        self._update_url = None
        self._update_release = None
        self._update_win = None
        if not os.environ.get("SPOTI_SELFTEST"):
            self._set_banner("LycRomanise v%s" % __version__, VERSION_BANNER_S)
            if config.check_updates_enabled():
                self.root.after(1500, lambda: self.check_for_update(manual=False))

    # -------------------------------------------------------- media reader --

    def _connect_player(self):
        """Start reading what is playing from the Windows media controls.
        Returns True when the reader is running."""
        if os.environ.get("SPOTI_SELFTEST"):
            # --selftest feeds the strip a demo song; a real poll would replace it with
            # whatever is (or is not) playing and empty the strip.
            log.info("selftest: polling is off")
            return False
        try:
            self.player = MediaClient()
        except Exception as exc:
            log.exception("couldn't start the Windows media reader")
            self._set_notice("Couldn't read what's playing: %s" % exc, "error")
            return False
        if not self._polling:
            self._polling = True
            self.root.after(0, self._poll)
        return True

    # ----------------------------------------------------------------- DPI --

    def _detect_scale(self):
        """Display scale factor as Tk sees it. Once the process is DPI aware
        (winsys.enable_dpi_awareness, called in main()) Tk reports the real DPI;
        an unaware process always sees 96 and so gets 1.0 (Windows stretches it).
        Font sizes are in points and follow this automatically; every size given
        in pixels (strip size, slots, outline, padlock) is multiplied by it."""
        try:
            value = float(self.root.winfo_fpixels("1i")) / 96.0
        except Exception:
            return 1.0
        scale = max(1.0, min(4.0, round(value, 2)))
        if scale != 1.0:
            log.info("display scale %.0f%%", scale * 100)
        return scale

    def _px(self, logical):
        return int(round(logical * self.scale))

    @property
    def strip_w(self):
        return self._px(DESKTOP_WIDTH)

    @property
    def strip_h(self):
        return self._px(DESKTOP_HEIGHT)

    # ------------------------------------------------------------ notices --

    def _set_notice(self, text, kind="error", seconds=None):
        """A small message on the strip (a problem reading the player etc.). Errors
        stay until the problem clears; info notices can time out."""
        until = time.perf_counter() + seconds if seconds else None
        if self._notice and self._notice["text"] == text:
            self._notice["until"] = until
            return
        self._notice = {"text": text, "kind": kind, "until": until}
        log.info("strip notice (%s): %s", kind, text)

    def _set_banner(self, text, seconds):
        self._banner = {"text": text, "until": time.perf_counter() + seconds}
        log.info("strip banner (%.0fs): %s", seconds, text)

    def _active_banner(self):
        b = self._banner
        if b and time.perf_counter() > b["until"]:
            self._banner = b = None
        return b

    def _clear_notice(self, kind=None):
        if self._notice and (kind is None or self._notice["kind"] == kind):
            self._notice = None

    def _active_notice(self):
        n = self._notice
        if n and n["until"] is not None and time.perf_counter() > n["until"]:
            self._notice = n = None
        return n

    # ----------------------------------------- cross-thread calls (tray etc.) --

    def post(self, fn):
        """Thread-safe: run fn on the Tk thread soon."""
        self._ui_calls.put(fn)

    def _drain_ui_calls(self):
        try:
            while True:
                fn = self._ui_calls.get_nowait()
                try:
                    fn()
                except Exception:
                    log.exception("error in posted UI call")
        except queue.Empty:
            pass
        finally:
            self.root.after(UI_CALL_DRAIN_MS, self._drain_ui_calls)

    # ------------------------------------------------ live appearance reload --

    @staticmethod
    def _appearance_stamp():
        try:
            st = os.stat(config.APPEARANCE_PATH)
            return (st.st_mtime_ns, st.st_size)
        except OSError:
            return None

    def _watch_appearance(self):
        """appearance.json changed (settings window or a hand edit)? Apply it."""
        try:
            stamp = self._appearance_stamp()
            if stamp != self._appearance_mtime:
                self._appearance_mtime = stamp
                self.reload_appearance()
        except Exception:
            log.exception("appearance watch error (recovering)")
        finally:
            self.root.after(APPEARANCE_WATCH_MS, self._watch_appearance)

    def reload_appearance(self):
        """Re-read appearance.json and apply it to the running strip: colours,
        font sizes, strip size, outline, offset and title card, with
        no restart."""
        old_bg, old_w, old_h = DESKTOP_BG, DESKTOP_WIDTH, DESKTOP_HEIGHT
        _load_appearance()
        self._refresh_tray()
        self.desktop_curr_size = DESKTOP_CURR_SIZE
        self.desktop_next_size = DESKTOP_NEXT_SIZE
        self._desktop_top_font.configure(size=self.desktop_curr_size)
        self._desktop_next_font.configure(size=self.desktop_next_size)
        self._measure_font.configure(size=self.desktop_curr_size)
        if (DESKTOP_WIDTH, DESKTOP_HEIGHT) != (old_w, old_h):
            self.root.geometry(f"{self.strip_w}x{self.strip_h}+{self.root.winfo_x()}+{self.root.winfo_y()}")
            self.desktop_canvas.configure(width=self.strip_w, height=self.strip_h)
        if DESKTOP_BG != old_bg:
            self.root.configure(bg=DESKTOP_BG)
            self.desktop_canvas.configure(bg=DESKTOP_BG)
            self._enable_transparency()
        self._rebuild_segments()
        self.title_text = self._make_title(self.track) if self.track else ""
        self._reposition_lock_icon()
        log.info("appearance settings applied live")

    # ---------------------------------------------------------- desktop UI --

    def _virtual_screen(self):
        """(x, y, w, h) of the whole desktop across monitors."""
        if sys.platform.startswith("win"):
            try:
                import ctypes

                u = ctypes.windll.user32
                x, y, w, h = (u.GetSystemMetrics(i) for i in (76, 77, 78, 79))
                if w > 0 and h > 0:
                    return x, y, w, h
            except Exception:
                pass
        return 0, 0, self.root.winfo_screenwidth(), self.root.winfo_screenheight()

    def _initial_position(self):
        """Where the strip starts: the spot it was last left in if there is
        one (kept on-screen even if monitors changed), otherwise centred
        horizontally and high up the screen."""
        vx, vy, vw, vh = self._virtual_screen()
        state = config.load_window_state()
        try:
            x, y = int(state["x"]), int(state["y"])
            x = max(vx - self.strip_w + 80, min(x, vx + vw - 80))
            y = max(vy, min(y, vy + vh - 60))
            return x, y, bool(state.get("locked", False))
        except (KeyError, TypeError, ValueError):
            pass
        sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
        return (sw - self.strip_w) // 2, int(sh * SPAWN_Y_PERCENT / 100), False

    def _build_desktop_ui(self, restore_lock=True):
        """A frameless, draggable, always-on-top strip showing the current
        line (karaoke-coloured) with the next line underneath.

        - Left-click and drag to move it (only while unlocked). Where you leave
          it is remembered for next time.
        - No right-click menu: use the tray icon (lock, settings, quit).
        - The small padlock pins it in place; once locked, on Windows it also
          becomes click-through. Whether it was locked is remembered too.
        """
        self.desktop_mode = True
        self._clear_root()
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.root.configure(bg=DESKTOP_BG)
        self.root.protocol("WM_DELETE_WINDOW", self._quit)

        x, y, was_locked = self._initial_position()
        self.root.geometry(f"{self.strip_w}x{self.strip_h}+{x}+{y}")

        self.desktop_canvas = tk.Canvas(
            self.root, width=self.strip_w, height=self.strip_h,
            bg=DESKTOP_BG, highlightthickness=0, bd=0,
        )
        self.desktop_canvas.pack(fill="both", expand=True)
        self.desktop_canvas.bind("<ButtonPress-1>", self._start_drag)
        self.desktop_canvas.bind("<B1-Motion>", self._do_drag)
        self.desktop_canvas.bind("<ButtonRelease-1>", self._end_drag)
        self.root.bind("<Configure>", self._on_desktop_configure)

        # Fonts are built once and resized in place: recreating Font objects
        # on every ~40ms redraw would leak Tcl font resources.
        self._desktop_top_font = tkfont.Font(family="Segoe UI", size=self.desktop_curr_size, weight="bold")
        self._desktop_next_font = tkfont.Font(family="Segoe UI", size=self.desktop_next_size)
        self._measure_font = tkfont.Font(family="Segoe UI", size=self.desktop_curr_size, weight="bold")

        self._enable_transparency()
        self._build_lock_icon()
        self.desktop_locked = bool(was_locked and restore_lock)
        # No window-style changes here: the window isn't mapped yet, so Tk hasn't
        # built its real Win32 wrapper. Styles are applied once it is mapped.
        self._apply_lock_state(restyle=False)
        self._schedule_startup_window_work()

        self._anim_start_perf = None
        if self.lyrics:
            self._rebuild_segments()
        self._redraw_desktop_canvas()
        self._desktop_tick()

    def _start_drag(self, event):
        if self.desktop_locked:
            return
        self._drag_offset = (event.x, event.y)
        self._raise_lock_icon()

    def _do_drag(self, event):
        if self.desktop_locked:
            return
        x = self.root.winfo_pointerx() - self._drag_offset[0]
        y = self.root.winfo_pointery() - self._drag_offset[1]
        self.root.geometry(f"+{x}+{y}")

    def _end_drag(self, _event=None):
        self._save_window_state()
        self._raise_lock_icon()

    def _raise_lock_icon(self):
        """Clicking the strip lifts it above the padlock window (both are topmost;
        the last one clicked wins). Put the padlock back on top if it's showing."""
        if self.lock_window is not None and self._lock_icon_visible:
            try:
                self.lock_window.attributes("-topmost", True)
                self.lock_window.lift()
            except tk.TclError:
                pass

    def _on_desktop_configure(self, _event):
        self._reposition_lock_icon()

    def _save_window_state(self):
        if not self.desktop_mode:
            return
        try:
            config.save_window_state({
                "x": self.root.winfo_x(), "y": self.root.winfo_y(),
                "locked": bool(self.desktop_locked),
            })
        except tk.TclError:
            pass

    def _quit(self):
        self._save_window_state()
        log.info("quitting")
        if self.tray is not None:
            self.tray.stop()
            self.tray = None
        self.root.destroy()

    # ------------------------------------------- tray / settings / autostart --

    def start_tray(self):
        """Create the tray icon. Its actions are posted to the Tk thread."""
        self._snapshot_tray_state()
        self.tray = tray.TrayIcon(
            actions={
                "toggle_lock": self._toggle_lock,
                "settings": self.open_settings,
                "toggle_autostart": self._toggle_autostart,
                "about": lambda: winsys.open_url(RELEASES_URL),
                "check_update": self.check_for_update,
                "open_logs": self._open_logs_folder,
                "quit": self._quit,
            },
            state=lambda: self._tray_state,
            post=self.post,
            can_autostart=winsys.is_windows(),
        )
        if not self.tray.start():
            self.tray = None
        return self.tray is not None

    def _snapshot_tray_state(self):
        """(locked, autostart) as plain values. The tray runs on its own
        thread and must never touch Tk variables or the registry itself, so it reads
        this snapshot, which is only ever rebuilt here on the Tk thread."""
        self._tray_state = (bool(self.desktop_locked), bool(winsys.autostart_enabled()))
        return self._tray_state

    def _refresh_tray(self):
        self._snapshot_tray_state()
        if self.tray is not None:
            self.tray.refresh()

    def _toggle_autostart(self):
        want = not winsys.autostart_enabled()
        if not winsys.set_autostart(want):
            self._set_notice("Couldn't change 'Start with Windows' - see the log", "error", seconds=8)
        self._refresh_tray()

    def check_for_update(self, manual=True):
        """Ask GitHub for the latest release (background thread). Automatic on startup;
        tray > Check for updates does it on demand and always answers."""
        if manual and self._update_release:
            self._offer_update(self._update_release)
            return

        def work():
            found = updater.check()
            self.post(lambda: self._update_checked(found, manual))
        threading.Thread(target=work, daemon=True, name="update-check").start()

    def _update_checked(self, found, manual):
        if not found:
            if manual:
                text = "You're on the latest version (v%s)" % updater.__version__
                self._banner_after_current(lambda: self._set_banner(text, 5.0))
            return
        self._update_release = found
        self._update_url = found["url"]
        if manual or updater.skipped_tag() != found["tag"]:
            self._offer_update(found)
        else:
            log.info("update %s was skipped by the user", found["tag"])

    def _banner_after_current(self, show):
        # Let the "LycRomanise v1.x.x" line finish its 5 s first, then show the result.
        b = self._active_banner()
        wait_ms = int(max(0.0, b["until"] - time.perf_counter()) * 1000) + 100 if b else 0
        self.root.after(wait_ms, show)

    def _offer_update(self, release):
        """The "update now?" window. Copies that can't update themselves (running from source, no
        installer file on the release, read-only folder) get the old behaviour: the release page."""
        if not updater.can_self_update(release):
            text = "New version %s available: opening the download page" % release["tag"]
            self._banner_after_current(lambda: self._set_banner(text, UPDATE_BANNER_S))
            winsys.open_url(release["url"])
            return
        try:
            if self._update_win is not None:
                try:
                    self._update_win.deiconify()
                    self._update_win.lift()
                    return
                except tk.TclError:
                    self._update_win = None
            win = tk.Toplevel(self.root)
            win.attributes("-topmost", True)
            tag = release["tag"]
            dlg = update_dialog.UpdateDialog(
                win, tag, updater.__version__, release.get("notes"),
                on_update=lambda: self._run_update(release, dlg),
                on_later=lambda: self._close_update_win(),
                on_skip=lambda: (updater.skip_version(tag), self._close_update_win()),
                on_open_page=lambda: winsys.open_url(release["url"]))
            win.protocol("WM_DELETE_WINDOW", self._close_update_win)
            self._update_win = win
            # centre on the screen the strip is on
            win.update_idletasks()
            x = max(0, (win.winfo_screenwidth() - win.winfo_reqwidth()) // 2)
            y = max(0, (win.winfo_screenheight() - win.winfo_reqheight()) // 3)
            win.geometry("+%d+%d" % (x, y))
            win.focus_force()
        except Exception:
            log.exception("couldn't open the update window")
            self._set_notice("Couldn't open the update window - see the log", "error", seconds=8)
            winsys.open_url(release["url"])

    def _close_update_win(self):
        win, self._update_win = self._update_win, None
        try:
            if win is not None:
                win.destroy()
        except tk.TclError:
            pass

    def _run_update(self, release, dlg):
        """Download on a worker thread, then install and quit."""
        def work():
            try:
                path, kind = updater.download(
                    release, progress=lambda d, t: self.post(lambda: _safe(dlg.progress, d, t)))
            except RuntimeError as exc:
                log.warning("update download failed: %s", exc)
                self.post(lambda: _safe(dlg.failed, str(exc)))
                return
            except Exception:
                log.exception("update download crashed")
                self.post(lambda: _safe(dlg.failed, "Something went wrong while downloading the update."))
                return
            self.post(lambda: self._finish_update(path, kind, dlg))
        threading.Thread(target=work, daemon=True, name="update-download").start()

    def _finish_update(self, path, kind, dlg):
        _safe(dlg.installing)
        if updater.apply(path, kind):
            self.root.after(900, self._quit)      # the installer / swap script takes over from here
        else:
            _safe(dlg.failed, "Couldn't start the installer. The file is in %s" % updater.UPDATE_DIR)

    def _open_logs_folder(self):
        try:
            os.makedirs(applog.LOG_DIR, exist_ok=True)
            if winsys.is_windows():
                os.startfile(applog.LOG_DIR)   # noqa: S606 - opens Explorer on our own log folder
        except Exception:
            log.exception("couldn't open the logs folder")

    def open_settings(self):
        """The appearance editor, in-process: edits are saved as you make
        them and picked up by the running strip within half a second."""
        try:
            if self._settings_window is not None:
                try:
                    self._settings_window.deiconify()
                    self._settings_window.lift()
                    self._settings_window.focus_force()
                    return
                except tk.TclError:
                    self._settings_window = None
            import config_editor
            win = tk.Toplevel(self.root)
            win.attributes("-topmost", True)   # the strip is topmost too; stay above it
            config_editor.ConfigEditor(win, on_apply=self.reload_appearance)
            win.protocol("WM_DELETE_WINDOW", lambda: self._close_settings(win))
            self._settings_window = win
            win.focus_force()
        except Exception:
            log.exception("couldn't open settings")
            self._set_notice("Couldn't open settings - see the log", "error", seconds=8)

    def _close_settings(self, win):
        self._settings_window = None
        try:
            win.destroy()
        except tk.TclError:
            pass

    def _nudge_offset(self, delta_s):
        global LYRIC_OFFSET_S
        LYRIC_OFFSET_S = round(LYRIC_OFFSET_S + delta_s, 3)
        config.update_appearance(lyric_offset_ms=int(round(LYRIC_OFFSET_S * 1000)))
        log.info("lyric offset now %+.0f ms", LYRIC_OFFSET_S * 1000)

    def _resize_desktop_text(self, delta):
        self.desktop_curr_size = max(DESKTOP_CURR_MIN_SIZE, self.desktop_curr_size + delta)
        self.desktop_next_size = max(DESKTOP_NEXT_MIN_SIZE, self.desktop_next_size + delta)
        self._desktop_top_font.configure(size=self.desktop_curr_size)
        self._desktop_next_font.configure(size=self.desktop_next_size)
        self._measure_font.configure(size=self.desktop_curr_size)
        self._rebuild_segments()   # how many characters fit on a line just changed

    # ------------------------------------------------------- lock / pin UI --

    def _build_lock_icon(self):
        """A tiny always-clickable Toplevel floating over the strip's top
        edge, showing a drawn padlock. It is never made click-through, so
        it's there to unlock the strip again - but it only appears while the
        pointer is over the strip (see _update_lock_icon_visibility)."""
        self._teardown_lock_icon()
        w, h = self._px(LOCK_ICON_SIZE[0]), self._px(LOCK_ICON_SIZE[1])
        self.lock_window = tk.Toplevel(self.root)
        self.lock_window.overrideredirect(True)
        self.lock_window.attributes("-topmost", True)
        self.lock_window.configure(bg=LOCK_ICON_BG)
        self.lock_icon_label = tk.Canvas(
            self.lock_window, width=w, height=h, bg=LOCK_ICON_BG,
            highlightthickness=0, bd=0, cursor="hand2",
        )
        self.lock_icon_label.pack()
        self.lock_icon_label.bind("<Button-1>", lambda _e: self._toggle_lock())
        self._draw_lock_glyph()
        self._lock_icon_visible = False
        self.lock_window.withdraw()          # hidden until the pointer is over the strip
        self._reposition_lock_icon()

    def _draw_lock_glyph(self):
        """Closed padlock when locked, open shackle when unlocked (drawn with
        lines and a rectangle, so no font/emoji support is needed)."""
        c = self.lock_icon_label
        if c is None:
            return
        k = self.scale
        w, h = self._px(LOCK_ICON_SIZE[0]), self._px(LOCK_ICON_SIZE[1])
        c.delete("all")
        cx = w / 2
        body_top = h * 0.48
        c.create_rectangle(cx - 6 * k, body_top, cx + 6 * k, h - 4 * k, fill=LOCK_ICON_FG, outline=LOCK_ICON_FG)
        width = max(1, round(2 * k))
        if self.desktop_locked:
            end_y = body_top
        else:
            end_y = 10 * k        # open shackle: the right leg stops short of the body
        c.create_line(cx - 4 * k, body_top, cx - 4 * k, 8 * k, cx - 2 * k, 5 * k, cx + 2 * k, 5 * k,
                      cx + 4 * k, 8 * k, cx + 4 * k, end_y, fill=LOCK_ICON_FG, width=width)

    def _teardown_lock_icon(self):
        if self.lock_window is not None:
            try:
                self.lock_window.destroy()
            except tk.TclError:
                pass
        self.lock_window = None
        self.lock_icon_label = None

    def _reposition_lock_icon(self):
        if self.lock_window is None:
            return
        try:
            icon_w = self._px(LOCK_ICON_SIZE[0])
            x = self.root.winfo_x() + (self.strip_w - icon_w) // 2
            y = self.root.winfo_y() + self._px(4)
            self.lock_window.geometry(f"+{x}+{y}")
        except tk.TclError:
            pass

    def _toggle_lock(self):
        self.desktop_locked = not self.desktop_locked
        self._apply_lock_state()
        self._save_window_state()

    def _apply_lock_state(self, restyle=True):
        self._draw_lock_glyph()
        self._show_border = False      # the frame only ever shows on hover while unlocked
        if restyle:
            self._set_windows_clickthrough(self.desktop_locked)
        self._reposition_lock_icon()
        self._refresh_tray()
        if restyle and sys.platform.startswith("win") and winsys.env_flag("SPOTI_DEBUG_DIAGNOSTICS"):
            self._log_window_diagnostics("lock %s" % ("on" if self.desktop_locked else "off"))

    def _schedule_startup_window_work(self):
        """Once the window is really mapped: apply a restored lock, then log the
        window diagnostics once (~2 s; more with SPOTI_DEBUG_DIAGNOSTICS=1)."""
        self._startup_styled = False
        self.root.after(200, self._startup_when_mapped)
        self.root.after(2000, lambda: self._log_window_diagnostics("2 s after start"))
        if winsys.env_flag("SPOTI_DEBUG_DIAGNOSTICS"):
            self.root.after(6000, lambda: self._log_window_diagnostics("6 s after start"))

    def _startup_when_mapped(self, tries=0):
        if self._startup_styled:
            return
        try:
            mapped = bool(self.root.winfo_ismapped())
        except tk.TclError:
            return
        if not mapped and tries < 50:            # give it up to ~10 s
            self.root.after(200, lambda: self._startup_when_mapped(tries + 1))
            return
        self._startup_styled = True
        if not mapped:
            log.warning("strip window still not mapped after 10 s; applying window styles anyway")
        if self.desktop_locked:                  # only a restored lock needs a style change
            self._set_windows_clickthrough(True)

    def _set_windows_clickthrough(self, enabled):
        if not sys.platform.startswith("win"):
            return
        if winsys.env_flag("SPOTI_DEBUG_NO_CLICKTHROUGH"):
            if not getattr(self, "_warned_no_ct", False):
                self._warned_no_ct = True
                log.warning("SPOTI_DEBUG_NO_CLICKTHROUGH set: window styles are left alone")
            return
        try:
            self.root.update_idletasks()
            info = winsys.set_clickthrough(self.root.winfo_id(), enabled)
            if info:
                hwnd, before, after = info
                log.info("click-through %s: hwnd %s exstyle %#x -> %#x",
                         "on" if enabled else "off", hwnd, before & 0xFFFFFFFF, after & 0xFFFFFFFF)
            # A style change can drop the colour key: put it back and read it back.
            self._enable_transparency()
        except Exception as exc:
            log.warning("click-through toggle failed: %s", exc)

    def _force_windows_redraw(self):
        """Ask Windows for a repaint (no erase). -transparentcolor is a hard
        chroma key, not real alpha, so the compositor can otherwise leave
        stale glyph pixels behind when text changes or disappears."""
        if not sys.platform.startswith("win") or winsys.env_flag("SPOTI_DEBUG_NO_INVALIDATE"):
            return
        try:
            winsys.invalidate(self.root.winfo_id())
        except Exception as exc:
            if not getattr(self, "_warned_redraw", False):
                self._warned_redraw = True
                log.warning("window repaint request failed (logged once): %s", exc)

    def _lyric_colour_targets(self):
        return [tuple(KARAOKE_UNSUNG_RGB), tuple(KARAOKE_SUNG_RGB), tuple(DESKTOP_NEXT_RGB)]

    def _count_lyric_pixels(self):
        return winddiag.count_lyric_pixels(self, self._lyric_colour_targets())

    def _log_window_diagnostics(self, reason):
        winddiag.log_window_diagnostics(self, reason, self._lyric_colour_targets())

    def _enable_transparency(self):
        """See-through background is a Windows-only tkinter trick; elsewhere
        this is a no-op and the strip just has a solid dark background."""
        if sys.platform.startswith("win"):
            try:
                self.root.attributes("-transparentcolor", DESKTOP_BG)
            except tk.TclError:
                pass

    def _disable_transparency(self):
        if sys.platform.startswith("win"):
            try:
                self.root.attributes("-transparentcolor", "")
            except tk.TclError:
                pass

    def _clear_root(self):
        self.root.unbind("<Configure>")
        for widget in self.root.winfo_children():
            widget.destroy()

    # ------------------------------------------------------------ polling --

    def _next_poll_delay_ms(self):
        now = time.perf_counter()
        if now < self._fast_poll_until:
            return FAST_POLL_MS
        if self.current_track_id is None:
            return POLL_INTERVAL_IDLE_MS
        if self._is_playing and self.duration_s:
            remaining = self.duration_s - self._raw_position_s()
            if remaining < END_OF_TRACK_WINDOW_S:
                # Aim just past the end of the song so the next one is
                # noticed almost as soon as it starts.
                return int(max(150, min(POLL_INTERVAL_MS, (remaining + 0.15) * 1000)))
        return POLL_INTERVAL_MS

    def _poll(self):
        """Kick off a background fetch and reschedule. The network never runs
        on the UI thread, so a slow response can't freeze the strip."""
        try:
            self._start_poll_worker()
        finally:
            self.root.after(self._next_poll_delay_ms(), self._poll)

    def _start_poll_worker(self):
        try:
            now = time.perf_counter()
            if self.player is None:
                return
            if not self._poll_busy:
                self._poll_busy = True
                self._poll_started = now
                threading.Thread(
                    target=self._poll_worker, args=(self.current_track_id,),
                    daemon=True, name="media-poll",
                ).start()
            elif now - self._poll_started > 45:
                log.warning("previous poll never finished; starting a new one")
                self._poll_busy = False
        except Exception:
            self._poll_busy = False
            log.exception("unexpected poll error (recovering)")

    def _poll_worker(self, known_track_id):
        try:
            player = self.player
            status, track = player.poll()
            received = time.perf_counter()
            # Hand the track over immediately so the title card can appear
            # while the lyrics are still being looked up.
            self._results.put(("track", status, track, received))
            if status == "ok" and track and track["id"] != known_track_id:
                track = self._settle_length(player, track)
                self._lookup_lengths = {track["id"]: track.get("duration_ms") or 0}
                log.info("looking up lyrics for %r (track length %.0fs)", track["name"],
                         (track.get("duration_ms") or 0) / 1000.0)
                asked = time.monotonic()
                result = fetch_lyrics(track["name"], track["artist"], track["album"], track["duration_ms"])
                if not (result and result[0]) and outage_since(asked):
                    result = self._retry_after_outage(track, result)
                self._results.put(("lyrics", track["id"], result))
        except Exception:
            log.exception("poll worker error (recovering)")
            self._results.put(("track", "error", None, time.perf_counter()))

    def _settle_length(self, player, track):
        """Right after a skip Windows can report the previous song's length for a moment, and a
        wrong length picks the wrong edition of the lyrics. Ask again until it changes."""
        for _ in range(3):
            if not track.get("duration_suspect"):
                break
            time.sleep(0.7)
            status, fresh = player.poll()
            if status != "ok" or not fresh or fresh["id"] != track["id"]:
                break
            track = fresh
        return track

    def _maybe_relookup(self, track):
        """The song's length changed after its lyrics were looked up (a late correction from
        Windows): look up again with the right length if it moved by more than 3 s."""
        want = track.get("duration_ms") or 0
        used = self._lookup_lengths.get(self.current_track_id)
        if not want or not used or abs(want - used) <= 3000 or track.get("duration_suspect"):
            self._len_candidate = None
            return
        if self._relookups >= 2 or self._relookup_busy or self.lyrics_state == "pending":
            return
        # Windows can report the next song's length a moment before its title: only act when the
        # new length is still there, for the same song, on the next poll.
        key = round(want / 1000.0)
        if self._len_candidate is None or self._len_candidate[0] != key:
            self._len_candidate = (key, 1)
            return
        self._len_candidate = None
        tid, self._relookups, self._relookup_busy = self.current_track_id, self._relookups + 1, True
        self._lookup_lengths = {tid: want}
        log.info("track length is now %.0fs (was %.0fs when looked up): looking up lyrics again for %r",
                 want / 1000.0, used / 1000.0, track["name"])

        def work():
            asked = time.monotonic()
            try:
                result = fetch_lyrics(track["name"], track["artist"], track["album"], want)
            except Exception:
                log.exception("lyrics re-lookup failed")
                result = None
            outage = not (result and result[0]) and outage_since(asked)
            self.post(lambda: self._relookup_done(tid, result, outage))
        threading.Thread(target=work, daemon=True, name="lyrics-relookup").start()

    def _relookup_done(self, tid, result, outage):
        self._relookup_busy = False
        if outage or tid != self.current_track_id:
            return          # the site was down (keep what we have) or the song changed
        if result and result[0] and list(result[0]) == self.lyrics:
            return          # same lyrics: nothing to redraw
        self._handle_lyrics(tid, result)

    RETRY_DELAYS_S = (15, 30, 60)

    def _retry_after_outage(self, track, result):
        """The lyrics site was down or rate-limiting (HTTP 503 while skipping songs quickly): "no
        lyrics" would be wrong, so ask again a few times while this song is still playing."""
        for delay in self.RETRY_DELAYS_S:
            time.sleep(delay)
            if self.current_track_id != track["id"]:
                return result      # song changed: the new one does its own lookup
            asked = time.monotonic()
            log.info("retrying lyrics for %r after a lyrics-site outage", track["name"])
            result = fetch_lyrics(track["name"], track["artist"], track["album"], track["duration_ms"])
            if result and result[0]:
                return result
            if not outage_since(asked):
                break              # the site answered normally: really not there
        return result

    def _drain_results(self):
        try:
            while True:
                msg = self._results.get_nowait()
                try:
                    if msg[0] == "track":
                        self._handle_track(*msg[1:])
                    elif msg[0] == "lyrics":
                        self._handle_lyrics(*msg[1:])
                except Exception:
                    log.exception("error applying %s result (recovering)", msg[0])
        except queue.Empty:
            pass
        finally:
            self.root.after(RESULT_DRAIN_MS, self._drain_results)

    # ------------------------------------------------------ track / lyrics --

    def _handle_track(self, status, track, received):
        self._poll_busy = False
        if status == "error":
            return   # couldn't find out: keep showing what we had
        self._clear_notice()   # the player answered: any earlier notice is out of date

        if status == "idle" or track is None:
            if self.current_track_id is not None:
                log.info("nothing playing")
            self._reset_track_state()
            self._is_playing = False
            self._refresh_current_labels()
            return

        changed = track["id"] != self.current_track_id
        if changed:
            self._begin_track(track)

        polled = track["progress_ms"] / 1000.0
        was_playing = self._is_playing
        if not changed and was_playing and track["is_playing"]:
            # Nudge the running estimate toward the player's jittery figure
            # instead of snapping, unless the gap is a real seek.
            est = self._last_position_s + (received - self._last_poll_perf)
            diff = polled - est
            position = est + diff * POLL_CORRECTION_GAIN if abs(diff) < POLL_SEEK_THRESHOLD_S else polled
        else:
            position = polled
        self._last_position_s = position
        self._last_poll_perf = received
        self._is_playing = bool(track["is_playing"])
        self.duration_s = (track.get("duration_ms") or 0) / 1000.0
        self._maybe_relookup(track)
        if not was_playing and self._is_playing and not changed:
            self._anim_start_perf = None   # resumed: show the line in place, no slide

        self._update_display(self._estimate_position_s())
        self._refresh_current_labels()

    def _reset_track_state(self):
        self.current_track_id = None
        self.track = None
        self.title_text = ""
        self.duration_s = 0.0
        self.lyrics_state = "none"
        self.lyrics, self.norm_lines = [], []
        self.segments, self.seg_starts, self.line_infos = [], [], []
        self.current_index = -1
        self._anim_start_perf = None

    def _begin_track(self, track):
        log.info("now playing: %s - %s", track["artist"], track["name"])
        self._reset_track_state()
        self.current_track_id = track["id"]
        self.track = track
        self._relookups = 0
        self._relookup_busy = False
        self._len_candidate = None
        self.lyrics_state = "pending"
        self.title_text = self._make_title(track)
        self._fast_poll_until = time.perf_counter() + FAST_POLL_AFTER_CHANGE_S

    def _make_title(self, track):
        """'Artist - Song', with numbers/symbols spelled out and non-Latin
        text romanized."""
        if not track or is_non_song(track.get("name"), track.get("artist")):
            return ""          # the Spotify DJ ("Up next" / DJ X) and ads get no title card
        artist = normalize(track.get("artist", ""))
        name = normalize(track.get("name", ""))
        text = f"{artist} - {name}" if artist and name else (artist or name)
        if needs_romanization(text):
            text = romanize_line(text, detect_script(text))
        return text

    def _handle_lyrics(self, track_id, result):
        if track_id != self.current_track_id:
            return   # the song changed while we were fetching
        synced = None
        if result:
            synced, _plain = result
        if not synced:
            self.lyrics_state = "missing"
            self.lyrics, self.segments = [], []
            return

        self.lyrics = list(synced)
        # Numbers and symbols -> the words a singer would say, before anything
        # else (romanizing, splitting) sees the text.
        self.norm_lines = [normalize(text) for _ts, text in self.lyrics]
        self.song_hint = detect_script(" ".join(self.norm_lines))

        self.lyrics_state = "ready"
        self._rebuild_segments()
        log.info("lyrics ready: %d lines, first at %.1fs, %d displayed segments, song position %.1fs; "
                 "first line %r, last at %.0fs", len(self.lyrics), self.lyrics[0][0], len(self.segments),
                 self._raw_position_s(), self.lyrics[0][1][:40], self.lyrics[-1][0])

    # ---------------------------------------------------- segments / layout --

    def _max_chars(self):
        """How many characters fit on the top line before it gets split."""
        if MAX_LINE_CHARS > 0:
            return MAX_LINE_CHARS
        font = getattr(self, "_measure_font", None)
        if font is None or not self.desktop_mode:
            return 42
        try:
            sample = "The quick brown fox jumps over"
            avg = font.measure(sample) / len(sample)
            usable = self.strip_w - self._px(DESKTOP_TEXT_MARGIN)
            return max(20, min(80, int(usable / max(avg, 1) * 0.95)))
        except tk.TclError:
            return 42

    def _line_display(self, i):
        """Text of lyric line i as displayed (romanized when it needs it)."""
        norm = self.norm_lines[i]
        return romanize_line(norm, self.song_hint) if needs_romanization(norm) else norm

    def _rebuild_segments(self):
        """Recompute the displayed lines (called when the lyrics arrive or a
        display setting changes). Long lines are split here."""
        if not self.lyrics:
            self.segments, self.seg_starts, self.line_infos = [], [], []
            self.current_index = -1
            self._refresh_current_labels()
            return
        displays = [self._line_display(i) for i in range(len(self.lyrics))]
        self.segments, self.line_infos = layout.build_segments(
            self.lyrics, displays, self._max_chars(), self.LAST_LINE_ASSUMED_SPAN,
            KARAOKE_CHARS_PER_SECOND, KARAOKE_MIN_FILL_SECONDS,
        )
        self.seg_starts = [s.start for s in self.segments]
        self.current_index = layout.index_for_position(self.seg_starts, self._estimate_position_s())
        self._anim_start_perf = None
        self._refresh_current_labels()

    # ------------------------------------------------------------- timing --

    def _raw_position_s(self):
        """Playback position in the song, without the lyric-offset tweak."""
        if not self._is_playing:
            return self._last_position_s
        return self._last_position_s + (time.perf_counter() - self._last_poll_perf)

    def _estimate_position_s(self):
        """Playback position for lyric timing (raw position plus the user's
        offset), so lines and the fill move smoothly between polls."""
        return self._raw_position_s() + LYRIC_OFFSET_S

    def _line_tick(self):
        """Re-check which line is current on a much shorter cadence than the
        player poll, so lines never show up late or get skipped."""
        try:
            if self.segments and self._is_playing:
                self._update_display(self._estimate_position_s())
        except Exception:
            log.exception("line tick error (recovering)")
        finally:
            self.root.after(LINE_TICK_MS, self._line_tick)

    def _update_display(self, position_s):
        if not self.segments:
            return
        idx = layout.index_for_position(self.seg_starts, position_s)
        if idx == self.current_index:
            return
        self.current_index = idx
        self._anim_start_perf = time.perf_counter()
        self._refresh_current_labels()

    # ---------------------------------------------------- what to show (scene) --

    def _scene(self):
        """What the strip should show right now: (kind, top, next) or None for a completely clear screen. Paused, stopped, or
        nothing playing all mean None - the lyrics vanish."""
        if not self._is_playing or self.current_track_id is None:
            return None
        pos = self._raw_position_s()
        state = self.lyrics_state

        if state == "ready" and self.segments:
            idx = self.current_index
            if idx >= 0:
                seg = self.segments[idx]
                nxt = self.segments[idx + 1].text if idx + 1 < len(self.segments) else ""
                return "lyric", seg.text, nxt
            # Before the first line: the title card, with the first line as a preview.
            if SHOW_TITLE_CARD and self.title_text and pos < TITLE_CARD_MAX_S:
                return "title", self.title_text, self.segments[0].text
            return None

        if SHOW_TITLE_CARD and self.title_text:
            if state == "pending" and pos < TITLE_CARD_MAX_S:
                return "title", self.title_text, ""
            if state == "missing" and pos < NO_LYRICS_NOTICE_S:
                return "title", self.title_text, "No synced lyrics found for this track"
        return None

    def _refresh_current_labels(self):
        """Repaint the strip."""
        if self.desktop_mode:
            self._redraw_desktop_canvas()

    # ------------------------------------------------- desktop lyrics draw --

    def _desktop_tick(self):
        if not self.desktop_mode:
            return
        try:
            self._update_lock_icon_visibility()
            self._redraw_desktop_canvas()
            # Full Windows repaint only when what's shown changed (or every
            # REDRAW_KEEPALIVE_S): invalidating with erase every 40 ms flickers.
            sig = (self._scene(), bool(self._show_border), (self._active_notice() or {}).get("text"),
                   (self._active_banner() or {}).get("text"))
            now = time.perf_counter()
            if sig != self._last_scene_sig or now - self._last_full_redraw > REDRAW_KEEPALIVE_S:
                self._last_scene_sig = sig
                self._last_full_redraw = now
                self._force_windows_redraw()
        except Exception:
            # canvas.delete("all") has already wiped the old frame, so an
            # uncaught error here would freeze the strip on stale content.
            # Log it and try again next tick.
            log.exception("desktop tick error (recovering)")
        finally:
            if self.desktop_mode:
                self.root.after(DESKTOP_TICK_MS, self._desktop_tick)

    def _pointer_over_strip(self):
        try:
            px, py = self.root.winfo_pointerx(), self.root.winfo_pointery()
            x0, y0 = self.root.winfo_x(), self.root.winfo_y()
        except tk.TclError:
            return None
        return x0 <= px < x0 + self.strip_w and y0 <= py < y0 + self.strip_h

    def _update_lock_icon_visibility(self):
        """Idle strip = only the lyrics: no frame, no lock icon. While the
        pointer is over the strip the small padlock appears (locked or not),
        and, when unlocked, a faint frame shows that it can be dragged. The
        global cursor position is used because a locked strip is
        click-through and never gets real hover events. The chrome lingers
        for HOVER_HIDE_DELAY_S so the pointer can reach the icon."""
        if self.lock_window is None:
            return
        over = self._pointer_over_strip()
        if over is None:
            return
        now = time.perf_counter()
        if over:
            self._hover_since = now
        show = over or (now - self._hover_since) < HOVER_HIDE_DELAY_S
        self._show_border = bool(show and not self.desktop_locked)

        if show and self._lock_icon_visible is not True:
            self._reposition_lock_icon()
            self.lock_window.deiconify()
            self.lock_window.attributes("-topmost", True)   # deiconify can drop it behind other windows
            self._lock_icon_visible = True
        elif not show and self._lock_icon_visible is not False:
            self.lock_window.withdraw()
            self._lock_icon_visible = False

    def _fit_font_size(self, font, text, base_size, min_size, max_width):
        """Shrink `font` (in place) from base_size to whatever fits `text`
        in max_width, stopping at min_size."""
        if not text:
            font.configure(size=base_size)
            return base_size
        size = base_size
        font.configure(size=size)
        while size > min_size and font.measure(text) > max_width:
            size -= 1
            font.configure(size=size)
        return size

    @staticmethod
    def _ellipsize(font, text, max_width):
        if font.measure(text) <= max_width:
            return text
        while len(text) > 1 and font.measure(text + "...") > max_width:
            text = text[:-1]
        return text.rstrip() + "..."

    def _wrap_top_text(self, font, text, max_width, max_lines=DESKTOP_MAX_LINES):
        """Last-resort wrap for a top line that still doesn't fit at the
        smallest allowed size (normally long lines are split into separate
        lines long before this)."""
        if max_lines <= 1 or font.measure(text) <= max_width:
            return [text]

        words = text.split(" ")
        lines = []
        current = ""
        i = 0
        while i < len(words) and len(lines) < max_lines - 1:
            word = words[i]
            candidate = f"{current} {word}".strip()
            if current and font.measure(candidate) > max_width:
                lines.append(current)
                current = ""
                continue
            current = candidate
            i += 1
        remainder = " ".join(words[i:])
        current = f"{current} {remainder}".strip() if remainder else current
        if current:
            lines.append(current)

        final = []
        for line in lines:
            if font.measure(line) <= max_width or " " in line:
                final.append(line)
            else:
                final.extend(self._hard_split_word(font, line, max_width))
        if len(final) > max_lines:
            head = final[: max_lines - 1]
            head.append("".join(final[max_lines - 1:]))
            final = head
        return final

    @staticmethod
    def _hard_split_word(font, word, max_width):
        chunks = []
        current = ""
        for ch in word:
            candidate = current + ch
            if current and font.measure(candidate) > max_width:
                chunks.append(current)
                current = ch
            else:
                current = candidate
        if current:
            chunks.append(current)
        return chunks or [word]

    def _current_fill_fraction(self):
        if not self.segments or not (0 <= self.current_index < len(self.segments)):
            return 0.0
        seg = self.segments[self.current_index]
        return layout.seg_fraction(seg, self.line_infos[seg.line], self._estimate_position_s())

    def _redraw_desktop_canvas(self):
        canvas = self.desktop_canvas
        if canvas is None:
            return
        canvas.delete("all")

        if self._show_border:
            canvas.create_rectangle(
                0, 0, self.strip_w - 1, self.strip_h - 1, outline=HOVER_BORDER_RGB, width=1,
            )

        scene = self._scene()
        notice = self._active_notice()
        if notice is not None and scene is None:
            self._draw_notice(canvas, notice)
        banner = self._active_banner()
        if banner is not None and scene is None and notice is None:
            self._draw_notice(canvas, {"text": banner["text"], "kind": "info"})
        if scene is None:
            return   # paused / stopped / nothing to show: a clear screen
        kind, top_text, next_text = scene
        if banner is not None:
            self._draw_banner_line(canvas, banner["text"])

        center_x = self.strip_w / 2
        max_text_width = self.strip_w - self._px(DESKTOP_TEXT_MARGIN)
        top_slot_y = self._px(46)
        bottom_slot_y = self.strip_h - self._px(28)
        outline = OUTLINE_SIZE * self.scale
        next_outline = min(OUTLINE_SIZE, 1.0) * self.scale

        # line-change animation progress (0 = just changed, 1 = settled)
        anim_t = 1.0
        if kind == "lyric" and self._anim_start_perf is not None:
            anim_t = min(1.0, (time.perf_counter() - self._anim_start_perf) / LINE_ANIM_SECONDS)
        eased_t = _ease_out_cubic(anim_t)

        target_size = self._fit_font_size(
            self._desktop_top_font, top_text, self.desktop_curr_size,
            DESKTOP_CURR_MIN_SIZE, max_text_width,
        )
        self._fit_font_size(
            self._desktop_next_font, next_text, self.desktop_next_size,
            DESKTOP_NEXT_MIN_SIZE, max_text_width,
        )
        # The title card is drawn entirely in the "sung" colour (pink). A fraction of 0 would leave
        # its first character or two mid-gradient while the rest is yellow.
        fraction = 1.0 if kind == "title" else self._current_fill_fraction()

        # The finished line is simply gone the instant the next one starts:
        # fading toward black over a chroma-key window just leaves a dark
        # ghost. The new line rises from the next-line slot into the top slot.
        top_lines = self._wrap_top_text(self._desktop_top_font, top_text, max_text_width)
        if len(top_lines) > 1:
            line_height = self._px(DESKTOP_CURR_MIN_SIZE + 6)
            y = top_slot_y - (len(top_lines) - 1) * line_height / 2
            for line in top_lines:
                self._draw_karaoke_line(canvas, line, self._desktop_top_font, center_x, y, fraction, outline)
                y += line_height
        elif top_text:
            incoming_y = bottom_slot_y + (top_slot_y - bottom_slot_y) * eased_t
            incoming_size = round(self.desktop_next_size + (target_size - self.desktop_next_size) * eased_t)
            self._desktop_top_font.configure(size=max(DESKTOP_NEXT_MIN_SIZE, incoming_size))
            self._draw_karaoke_line(canvas, top_text, self._desktop_top_font, center_x, incoming_y, fraction, outline)
            self._desktop_top_font.configure(size=target_size)

        # The next line appears once the rising line has cleared its slot.
        reveal = max(0.0, min(1.0, (anim_t - 0.35) / 0.65))
        if next_text and reveal > 0.0:
            y = bottom_slot_y + (1.0 - _ease_out_cubic(reveal)) * self._px(12)
            shown = self._ellipsize(self._desktop_next_font, next_text, max_text_width)
            _draw_outlined_text(
                canvas, center_x, y, shown, self._desktop_next_font,
                _rgb_to_hex(DESKTOP_NEXT_RGB), next_outline, anchor="center", justify="center",
            )

    def _draw_banner_line(self, canvas, text):
        """Small line along the top edge, over the lyrics, for the version / update messages."""
        font = self._desktop_next_font
        text = self._ellipsize(font, text, self.strip_w - self._px(DESKTOP_TEXT_MARGIN))
        _draw_outlined_text(canvas, self.strip_w / 2, self._px(12), text, font, NOTICE_INFO_FG,
                            1.0 * self.scale, anchor="center", justify="center")

    def _draw_notice(self, canvas, notice):
        fg = NOTICE_ERROR_FG if notice["kind"] == "error" else NOTICE_INFO_FG
        font = self._desktop_next_font
        text = self._ellipsize(font, notice["text"], self.strip_w - self._px(DESKTOP_TEXT_MARGIN))
        _draw_outlined_text(
            canvas, self.strip_w / 2, self.strip_h / 2, text, font, fg, 1.0 * self.scale,
            anchor="center", justify="center",
        )

    def _draw_karaoke_line(self, canvas, text, font, center_x, y, fraction, outline_size):
        """Draw `text` centered at (center_x, y) with a dark outline, turning
        from yellow to pink left-to-right as `fraction` (0..1) of it is sung.
        The fill edge blends over a band scaled to the average character width
        so it sweeps instead of snapping per character."""
        if not text:
            return

        # Cumulative prefix widths: positions taken this way match how the
        # whole string is laid out, so the outline lines up with the fill.
        prefix = [font.measure(text[:i]) for i in range(len(text) + 1)]
        total_width = prefix[-1]
        start_x = center_x - total_width / 2
        fill_x = fraction * total_width

        avg_char_width = total_width / max(1, len(text))
        gradient_px = max(self._px(KARAOKE_GRADIENT_MIN_PX), avg_char_width * KARAOKE_GRADIENT_CHAR_SPAN)

        _draw_outline_only(canvas, start_x, y, text, font, outline_size)

        for i, ch in enumerate(text):
            if ch == " ":
                continue
            ch_width = prefix[i + 1] - prefix[i]
            x = start_x + prefix[i]
            t = (prefix[i] + ch_width / 2 - fill_x) / gradient_px
            t = max(0.0, min(1.0, t))
            color = _rgb_to_hex(_lerp_rgb(KARAOKE_SUNG_RGB, KARAOKE_UNSUNG_RGB, t))
            canvas.create_text(x, y, text=ch, font=font, fill=color, anchor="nw")

def main():
    if "--selftest" in sys.argv[1:]:
        sys.exit(run_selftest())
    # Started from a console (python app.py, run.bat, a terminal)? Hand over
    # to a console-less copy and let this one exit, so closing that console
    # can never take the strip down with it.
    if winsys.detach_from_console():
        return
    if not winsys.acquire_single_instance():
        log.info("another copy is already running; exiting")
        return
    winsys.refresh_autostart()          # keep the Run entry pointing at where the app is now
    if winsys.env_flag("SPOTI_DEBUG_NO_DPI"):
        dpi = None
        log.warning("SPOTI_DEBUG_NO_DPI set: not enabling DPI awareness")
    else:
        dpi = winsys.enable_dpi_awareness()  # before the first Tk window, or it has no effect
    if dpi:
        log.info("DPI awareness: %s", dpi)
    root = tk.Tk()
    application = None
    try:
        application = LyricsApp(root)
        application.start_tray()
        root.mainloop()
    except KeyboardInterrupt:
        sys.exit(0)
    except Exception:
        log.exception("fatal error")
        raise
    finally:
        try:
            if application is not None and application.tray is not None:
                application.tray.stop()
        except Exception:
            pass

if __name__ == "__main__":
    main()
