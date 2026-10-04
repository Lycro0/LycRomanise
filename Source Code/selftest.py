"""python app.py --selftest: builds the real strip with a demo song under real Tk and writes
SELFTEST PASS/FAIL to logs/selftest-result.txt. LyricsApp is imported lazily so this module
never imports app at load time (app imports this one)."""
import logging
import os
import sys
import time
import tkinter as tk

import winsys

log = logging.getLogger("spoti.app")


def _lyrics_app_class():
    """The LyricsApp of the module that is running (python app.py makes that __main__, and
    importing 'app' again would load a second copy), otherwise import app."""
    main = sys.modules.get("__main__")
    if main is not None and hasattr(main, "LyricsApp"):
        return main.LyricsApp
    import app
    return app.LyricsApp


def _selftest_report(text):
    """Write the selftest result straight to logs/selftest-result.txt (plain append + flush, no
    logging machinery), so a result exists even if the logger itself is what is broken."""
    for folder in (os.path.join(os.path.dirname(os.path.abspath(__file__)), "logs"),
                   os.path.dirname(os.path.abspath(__file__))):
        try:
            os.makedirs(folder, exist_ok=True)
            with open(os.path.join(folder, "selftest-result.txt"), "a", encoding="utf-8") as fh:
                fh.write("%s %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), text))
                fh.flush()
            return
        except OSError:
            continue


def run_selftest(wait_s=6.0):
    """Build the real strip with a demo song, wait until it is mapped, log the window
    diagnostics (mapped, styles, colour key, on-screen lyric pixels on Windows) and
    exit 0/1. The result goes to logs/spoti-lyrics.log and logs/selftest-result.txt.
    Run:  python app.py --selftest   (prints nothing; check those files).
    Uses real Tk, so unlike tests/ it can catch real-window bugs. The player is not read."""
    os.environ["SPOTI_SELFTEST"] = "1"
    _selftest_report("selftest start (python %s, %s)" % (sys.version.split()[0], sys.platform))
    ok = False
    root = None
    try:
        if not sys.platform.startswith("win"):
            os.environ.setdefault("SPOTI_SELFTEST_NO_PIXELS", "1")
        dpi = None if winsys.env_flag("SPOTI_DEBUG_NO_DPI") else winsys.enable_dpi_awareness()
        log.info("---- selftest start (dpi=%s) ----", dpi)
        LyricsApp = _lyrics_app_class()
        root = tk.Tk()
        application = LyricsApp(root)
        demo = [(0.0, "Selftest line one"), (4.0, "Selftest line two"), (8.0, "Selftest line three"), (12.0, "Selftest line four")]
        application._handle_track("ok", {"id": "selftest", "name": "Selftest", "artist": "Spoti-Lyrics",
                                         "album": "", "duration_ms": 60000, "progress_ms": 1000,
                                         "is_playing": True}, time.perf_counter())
        application._handle_lyrics("selftest", (demo, None, None))
        deadline = time.monotonic() + wait_s
        while time.monotonic() < deadline and not root.winfo_ismapped():
            root.update()
            time.sleep(0.05)
        for _ in range(20):                       # let the strip draw a few frames
            root.update()
            time.sleep(0.05)
        mapped = bool(root.winfo_ismapped())
        items = len(application.desktop_canvas.find_all()) if application.desktop_canvas is not None else 0
        application._log_window_diagnostics("selftest")
        px = None
        if sys.platform.startswith("win") and not os.environ.get("SPOTI_SELFTEST_NO_PIXELS"):
            px = application._count_lyric_pixels()
        ok = mapped and items > 0 and (px is None or px > 0)
        msg = ("SELFTEST %s: mapped=%s canvas_items=%s lyric_pixels_on_screen=%s "
               "[playing=%s track=%s lyrics_state=%s index=%s scene=%s]" % (
                   "PASS" if ok else "FAIL", mapped, items, px, application._is_playing,
                   application.current_track_id, application.lyrics_state,
                   application.current_index, application._scene()))
        log.info(msg)
        _selftest_report(msg)
    except BaseException as exc:                  # includes SystemExit and import-time surprises
        import traceback
        log.exception("SELFTEST FAIL: exception")
        _selftest_report("SELFTEST FAIL: exception %r\n%s" % (exc, traceback.format_exc()))
    finally:
        try:
            if root is not None:
                root.destroy()
        except Exception:
            pass
    return 0 if ok else 1
