"""LycRomanise.txt: the song that is playing right now, as plain text for OBS.

In OBS add a "Text (GDI+)" source, tick "Read from file" and pick LycRomanise.txt (Settings > Phone & OBS
shows the full path and has a button that opens its folder). The file holds one line, for example
"Steve Lacy - Love 2 Fast", and is rewritten only when the song changes, so OBS picks the change up
by itself. It keeps showing the song while it is paused, and is emptied when nothing is playing any more
and when LycRomanise quits.
"""
import logging
import os
import tempfile
import threading

log = logging.getLogger("lycro.nowplaying")

FILE_NAME = "LycRomanise.txt"
FORMATS = {
    "artist_title": "{artist} - {title}",
    "title_artist": "{title} - {artist}",
    "title": "{title}",
}
DEFAULT_FORMAT = "artist_title"
FORMAT_LABELS = {
    "artist_title": "Artist - Song",
    "title_artist": "Song - Artist",
    "title": "Song only",
}
MAX_CHARS = 300


def format_track(fmt, artist, title):
    """One line of text for a song. A missing artist or title never leaves a dangling ' - '."""
    artist = " ".join((artist or "").split())
    title = " ".join((title or "").split())
    if not artist and not title:
        return ""
    if not artist or not title or fmt == "title":
        return (title or artist)[:MAX_CHARS]
    template = FORMATS.get(fmt, FORMATS[DEFAULT_FORMAT])
    return template.format(artist=artist, title=title)[:MAX_CHARS]


class NowPlayingFile:
    """Writes the text to the file, only when it changed. Never raises: a locked or unwritable file
    is logged once and tried again with the next song."""

    def __init__(self, path_fn):
        self._path_fn = path_fn
        self._last = None               # (path, text) last written
        self._lock = threading.Lock()
        self._warned = False

    @property
    def path(self):
        return self._path_fn()

    def write(self, text):
        text = text or ""
        path = self.path
        with self._lock:
            if self._last == (path, text):
                return True
            ok = self._write(path, text)
            if ok:
                self._last = (path, text)
                log.info("wrote %s: %r", os.path.basename(path), text)
            return ok

    def _write(self, path, text):
        data = text.encode("utf-8")
        try:
            folder = os.path.dirname(os.path.abspath(path))
            os.makedirs(folder, exist_ok=True)
            fd, tmp = tempfile.mkstemp(prefix=".lycromanise.", suffix=".tmp", dir=folder)
            try:
                with os.fdopen(fd, "wb") as f:
                    f.write(data)
                os.replace(tmp, path)
            except BaseException:
                try:
                    os.remove(tmp)
                except OSError:
                    pass
                raise
            self._warned = False
            return True
        except OSError:
            pass
        try:                            # OBS may have the file open: write in place instead of swapping it
            with open(path, "wb") as f:
                f.write(data)
            self._warned = False
            return True
        except OSError as exc:
            if not self._warned:
                self._warned = True
                log.warning("couldn't write %s: %s", path, exc)
            return False
