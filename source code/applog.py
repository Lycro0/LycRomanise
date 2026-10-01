"""Logging + crash capture for Spoti-Lyrics Overlay.

Everything the app prints, and every error it hits (including ones inside
Tk callbacks and background threads), ends up in the logs/ folder next to the
app, so when something goes wrong there's a file to look at even though no
console window is showing. Nothing is ever echoed to a console: the log file is
the single destination for log records, print() and tracebacks.

  logs/spoti-lyrics.log        the current log (rotates daily, 14 days kept)
  logs/crash-native.log        hard crashes of the Python interpreter itself
"""

import faulthandler
import logging
import os
import sys
import threading
from logging.handlers import TimedRotatingFileHandler

from paths import DATA_DIR as BASE_DIR
LOG_DIR = os.path.join(BASE_DIR, "logs")
LOG_FILE = None  # derived from LOG_DIR in setup()

_done = False
_native_crash_file = None  # kept referenced so faulthandler's fd stays open

_reentry = threading.local()


def _fallback_write(text):
    """Last-resort log destination when logs/spoti-lyrics.log can't be written: a file in the
    temp folder (and next to the app if that fails). Never raises."""
    import tempfile
    import time
    line = "%s %s\n" % (time.strftime("%Y-%m-%d %H:%M:%S"), text)
    for folder in (tempfile.gettempdir(), BASE_DIR, os.path.expanduser("~")):
        try:
            with open(os.path.join(folder, "spoti-lyrics-fallback.log"), "a", encoding="utf-8") as fh:
                fh.write(line)
            return
        except Exception:
            continue


class _SafeFileHandler(TimedRotatingFileHandler):
    """A logging failure must never turn into more logging: the default
    handleError prints to sys.stderr, which is itself routed back into the
    logger here, so one bad write would recurse forever.

    On Windows the midnight rotation renames the log, which fails while another copy of the
    app (or an editor) has it open; that used to lose every record. Now the rotation is
    skipped and appending goes on, and any other write failure goes to a fallback file."""

    def doRollover(self):
        try:
            super().doRollover()
        except OSError as exc:
            _fallback_write("log rotation skipped (%s); appending to the current file" % exc)
            try:
                if self.stream is None:
                    self.stream = self._open()
            except OSError:
                pass
            import time
            self.rolloverAt = int(time.time()) + 3600      # try again in an hour

    def handleError(self, record):
        try:
            _fallback_write("logging failed (%s): %s" % (sys.exc_info()[1], record.getMessage()))
        except Exception:
            pass


class _LogStream:
    """File-like object that forwards whatever is written to it (print(),
    stray tracebacks) into the logger, line by line."""

    def __init__(self, logger, level):
        self._logger = logger
        self._level = level
        self._buf = ""

    def write(self, text):
        if not text:
            return 0
        if getattr(_reentry, "busy", False):
            return len(text)          # output produced while logging: drop it
        self._buf += text
        while "\n" in self._buf:
            line, self._buf = self._buf.split("\n", 1)
            if line.strip():
                self._emit(line.rstrip())
        return len(text)

    def _emit(self, line):
        _reentry.busy = True
        try:
            self._logger.log(self._level, line)
        finally:
            _reentry.busy = False

    def flush(self):
        if self._buf.strip() and not getattr(_reentry, "busy", False):
            self._emit(self._buf.rstrip())
        self._buf = ""

    def isatty(self):
        return False

def setup():
    """Idempotent. Call once, as early as possible. File logging only."""
    global _done, _native_crash_file
    if _done:
        return logging.getLogger("spoti")
    _done = True

    logger = logging.getLogger("spoti")
    logger.setLevel(logging.INFO)
    logger.propagate = False   # never fall through to the root logger / a console
    fmt = logging.Formatter("%(asctime)s [%(levelname)s] %(name)s: %(message)s")

    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        handler = _SafeFileHandler(
            os.path.join(LOG_DIR, "spoti-lyrics.log"), when="midnight", backupCount=14, encoding="utf-8", delay=True
        )
        handler.setFormatter(fmt)
        logger.addHandler(handler)
    except OSError as exc:
        # read-only folder etc. - never write to a console; use the fallback file instead.
        _fallback_write("couldn't create the log folder %s: %s" % (LOG_DIR, exc))
        try:
            fb = logging.FileHandler(os.path.join(__import__("tempfile").gettempdir(), "spoti-lyrics-fallback.log"),
                                     encoding="utf-8")
            fb.setFormatter(fmt)
            logger.addHandler(fb)
        except OSError:
            logger.addHandler(logging.NullHandler())

    # print() and uncaught-traceback output -> log file. Under pythonw these
    # streams are None, which would otherwise make any print() raise.
    sys.stdout = _LogStream(logging.getLogger("spoti.stdout"), logging.INFO)
    sys.stderr = _LogStream(logging.getLogger("spoti.stderr"), logging.ERROR)

    def _excepthook(exc_type, exc, tb):
        logger.critical("Uncaught exception", exc_info=(exc_type, exc, tb))

    sys.excepthook = _excepthook

    def _thread_hook(args):
        logger.error(
            "Uncaught exception in thread %s",
            getattr(args.thread, "name", "?"),
            exc_info=(args.exc_type, args.exc_value, args.exc_traceback),
        )

    threading.excepthook = _thread_hook

    try:
        _native_crash_file = open(os.path.join(LOG_DIR, "crash-native.log"), "a", encoding="utf-8")
        faulthandler.enable(file=_native_crash_file)
    except Exception:
        pass

    logger.info("---- Spoti-Lyrics Overlay starting (python %s) ----", sys.version.split()[0])
    return logger

def install_tk_handler(root):
    """Errors inside Tk callbacks (after(), button clicks...) go to the log
    instead of vanishing into a console nobody can see."""
    log = logging.getLogger("spoti.tk")

    def _report(exc_type, exc, tb):
        log.error("Error in Tk callback", exc_info=(exc_type, exc, tb))

    root.report_callback_exception = _report
