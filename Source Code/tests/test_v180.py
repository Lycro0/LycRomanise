"""Regression tests for v1.8.0 (music note glyph, layered window retry, WinRT thread, OBS text file, lrcmux)."""
import ctypes
import http.server
import json
import os
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import alpha_window
import config
import lyrics_provider as lp
import media_client
import nowplaying

HERE = os.path.dirname(__file__)
DEJAVU = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
NOTE = "\u266a"


class _Meter:
    def prefix(self, font, text):
        return tuple(i * 12 for i in range(len(text) + 1))


class _Font:
    def cget(self, key):
        return {"weight": "bold", "size": 25}[key]


@unittest.skipUnless(alpha_window.Image is not None, "Pillow missing")
class MusicNoteGlyph(unittest.TestCase):
    """Segoe UI has no U+266A, so the strip drew an empty box for an instrumental break."""

    def _renderer(self, main, fallbacks):
        return alpha_window.StripRenderer(_Meter(), dpi=96, font_file=lambda bold: main,
                                          fallback_files=lambda: fallbacks)

    def _fonts(self):
        fonts = ["/usr/share/fonts/truetype/google-fonts/Poppins-Bold.ttf", DEJAVU]
        if not all(os.path.isfile(f) for f in fonts):
            self.skipTest("test fonts not installed")
        return fonts

    def test_missing_glyph_is_detected_and_a_fallback_font_is_used(self):
        main, fb = self._fonts()
        r = self._renderer(main, [fb])
        key, pf = r._pil_font(_Font())
        self.assertTrue(r._has_glyph(pf, key, "A"))
        self.assertFalse(r._has_glyph(pf, key, NOTE))
        self.assertEqual(r._font_for(key, pf, NOTE).path, fb)
        self.assertIs(r._font_for(key, pf, "A"), pf)

    def test_note_is_not_the_missing_glyph_box(self):
        main, fb = self._fonts()
        with_fb = self._renderer(main, [fb])
        without = self._renderer(main, [])
        out = []
        for r in (with_fb, without):
            r.begin(80, 60, hit_testable=False)
            r.create_text(10, 5, text=NOTE, font=_Font(), fill="#ffffff")
            out.append(r.img.tobytes())
        self.assertNotEqual(out[0], out[1])       # with a fallback the picture differs from the empty box

    def test_outline_tile_uses_the_same_fallback(self):
        main, fb = self._fonts()
        r = self._renderer(main, [fb])
        r.begin(80, 60, hit_testable=False)
        r.draw_outline(10, 5, NOTE, _Font(), 1.0)
        self.assertIsNotNone(r.img.getbbox())


class LayeredRetry(unittest.TestCase):
    """UpdateLayeredWindow answers error 87 on a window that SetLayeredWindowAttributes touched."""

    def _strip(self, answers):
        from PIL import Image
        s = object.__new__(alpha_window.AlphaStrip)
        w, h = 8, 4
        s.failed, s.img, s._hwnd, s._errors, s._reset_tried, s._last_error = False, Image.new("RGBA", (w, h)), 1, 0, False, 0
        buf = ctypes.create_string_buffer(w * h * 4)
        s._keep, s._bits = buf, ctypes.addressof(buf)
        s._ensure_dib = lambda *_a: None
        s._ct = ctypes
        s._wt = mock.Mock(SIZE=lambda *_a: object(), POINT=lambda *_a: object())
        s._BLEND = lambda *_a: object()
        calls = {"push": 0, "reset": 0}
        it = iter(answers)

        def push(*_a):
            calls["push"] += 1
            ok, err = next(it)
            s._last_error = err
            return ok
        s._push = push
        s._reset_layered = lambda: calls.__setitem__("reset", calls["reset"] + 1)
        return s, calls

    def test_error_87_resets_the_layered_style_and_retries(self):
        s, calls = self._strip([(False, 87), (True, 0)])
        s.commit()
        self.assertEqual((calls["push"], calls["reset"]), (2, 1))
        self.assertEqual(s._errors, 0)
        self.assertFalse(s.failed)

    def test_other_errors_are_not_retried(self):
        s, calls = self._strip([(False, 5)] * 5)
        for _ in range(5):
            s.commit()
        self.assertEqual(calls["reset"], 0)
        self.assertTrue(s.failed)


class WinRtThread(unittest.TestCase):
    def test_all_calls_run_on_one_thread_and_it_is_replaced_after_an_error(self):
        c = media_client.MediaClient()

        async def who():
            return threading.current_thread().name, threading.get_ident()

        names = {c._run(who()) for _ in range(3)}
        self.assertEqual(len(names), 1)
        self.assertEqual(names.pop()[0], "winrt")
        first = c._loop

        async def bad():
            raise ValueError("x")
        with self.assertRaises(ValueError):
            c._run(bad())
        self.assertEqual(c._run(who())[0], "winrt")
        self.assertIsNot(c._loop, first)

    def test_a_stuck_call_times_out(self):
        c = media_client.MediaClient()
        import asyncio

        async def forever():
            await asyncio.sleep(60)
        with mock.patch.object(media_client, "SMTC_CALL_TIMEOUT_S", 0.2):
            with self.assertRaises(Exception):
                c._run(forever())
        self.assertIsNone(c._rt)


def _read(path, mode="rb"):
    with open(path, mode, **({} if "b" in mode else {"encoding": "utf-8"})) as f:
        return f.read()


class NowPlayingFile(unittest.TestCase):
    def test_formats(self):
        f = nowplaying.format_track
        self.assertEqual(f("artist_title", "Steve Lacy", "Love 2 Fast"), "Steve Lacy - Love 2 Fast")
        self.assertEqual(f("title_artist", "Steve Lacy", "Love 2 Fast"), "Love 2 Fast - Steve Lacy")
        self.assertEqual(f("title", "Steve Lacy", "Love 2 Fast"), "Love 2 Fast")
        self.assertEqual(f("artist_title", "", "Solo"), "Solo")
        self.assertEqual(f("artist_title", "Only Artist", ""), "Only Artist")
        self.assertEqual(f("artist_title", "", ""), "")
        self.assertEqual(f("nonsense", "A", "B"), "A - B")
        self.assertEqual(f("artist_title", "A  \n b", "C"), "A b - C")           # one clean line
        self.assertLessEqual(len(f("artist_title", "x" * 500, "y")), nowplaying.MAX_CHARS)

    def test_writes_utf8_only_when_changed(self):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "LycRomanise.txt")
            n = nowplaying.NowPlayingFile(lambda: path)
            self.assertTrue(n.write("Steve Lacy - Love 2 Fast"))
            self.assertEqual(_read(path), b"Steve Lacy - Love 2 Fast")      # no newline, no BOM
            m = os.stat(path).st_mtime_ns
            n.write("Steve Lacy - Love 2 Fast")
            self.assertEqual(os.stat(path).st_mtime_ns, m)                               # unchanged: not rewritten
            n.write("宇多田ヒカル - Automatic")
            self.assertEqual(_read(path, "r"), "宇多田ヒカル - Automatic")
            n.write("")
            self.assertEqual(_read(path), b"")
            self.assertEqual([x for x in os.listdir(d) if x.endswith(".tmp")], [])

    def test_unwritable_path_never_raises(self):
        with tempfile.TemporaryDirectory() as d:
            blocker = os.path.join(d, "file")
            open(blocker, "w").close()
            n = nowplaying.NowPlayingFile(lambda: os.path.join(blocker, "sub", "x.txt"))
            self.assertFalse(n.write("x"))

    def test_settings_and_path(self):
        st = config.sanitize({"now_playing_format": "bogus", "now_playing_file": "yes"})
        self.assertEqual(st["now_playing_format"], "artist_title")
        self.assertIs(st["now_playing_file"], True)
        self.assertEqual(config.sanitize({"now_playing_format": "title"})["now_playing_format"], "title")
        self.assertTrue(config.DEFAULT_APPEARANCE["now_playing_file"])
        with tempfile.TemporaryDirectory() as d:
            cfg = os.path.join(d, "config.json")
            with open(cfg, "w") as f:
                json.dump({"now_playing_path": d}, f)
            with mock.patch.object(config, "CONFIG_PATH", cfg):
                self.assertEqual(config.now_playing_path(), os.path.join(d, "LycRomanise.txt"))
            with open(cfg, "w") as f:
                json.dump({"now_playing_path": os.path.join(d, "song.txt")}, f)
            with mock.patch.object(config, "CONFIG_PATH", cfg):
                self.assertEqual(config.now_playing_path(), os.path.join(d, "song.txt"))
        self.assertTrue(config.now_playing_path().endswith("LycRomanise.txt"))


class _Handler(http.server.BaseHTTPRequestHandler):
    hits = []

    def log_message(self, *_a):
        pass

    def do_GET(self):
        _Handler.hits.append(self.path)
        if self.path.startswith("/api/get"):
            self.send_response(404)
            self.end_headers()
            return
        body = json.dumps([{"id": 7, "trackName": "Song", "artistName": "Band", "duration": 200,
                            "syncedLyrics": "[00:01.00]one\n[00:05.00]two", "plainLyrics": "one\ntwo"}]).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class SecondSource(unittest.TestCase):
    def test_compatible_server_is_asked_like_lrclib(self):
        srv = http.server.HTTPServer(("127.0.0.1", 0), _Handler)
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        try:
            _Handler.hits.clear()
            base = "http://127.0.0.1:%d/api" % srv.server_address[1]
            synced, plain = lp._fetch_from_lrclib("Song", "Band", None, 200000, source="mirror", base=base)
        finally:
            srv.shutdown()
        self.assertEqual([t for _t, t in synced], ["one", "two"])
        self.assertTrue(any(h.startswith("/api/get") for h in _Handler.hits))
        self.assertTrue(any(h.startswith("/api/search") for h in _Handler.hits))

    def test_source_order_and_extras(self):
        names = [n for n, _f in lp._sources()]
        self.assertEqual(names[:2], ["lrclib", "lrcmux"])
        with tempfile.TemporaryDirectory() as d:
            cfg = os.path.join(d, "config.json")
            with open(cfg, "w") as f:
                json.dump({"extra_lyrics_apis": ["https://a.example/api/", "http://insecure.example/api", "https://a.example/api"]}, f)
            with mock.patch.object(config, "CONFIG_PATH", cfg):
                self.assertEqual(lp.extra_api_bases(), ["https://a.example/api"])
                self.assertEqual([n for n, _f in lp._sources()][-1], "extra1")

    def test_second_source_is_used_when_the_first_has_nothing(self):
        asked = []

        def first(*_a):
            asked.append("lrclib")
            return None, None

        def second(*_a):
            asked.append("lrcmux")
            return [(1.0, "one"), (5.0, "two"), (9.0, "three")], None

        with tempfile.TemporaryDirectory() as d, \
                mock.patch.object(lp, "SOURCES", (("lrclib", first), ("lrcmux", second))), \
                mock.patch.object(lp, "CACHE_PATH", os.path.join(d, "c.json")), \
                mock.patch.object(lp, "MISSING_PATH", os.path.join(d, "m.json")), \
                mock.patch.object(lp, "HEALTH_PATH", os.path.join(d, "h.json")), \
                mock.patch.object(lp, "_cache", None), mock.patch.object(lp, "_missing", None), \
                mock.patch.object(lp, "_health", None), mock.patch.object(lp, "_schedule_flush"), \
                mock.patch.object(lp, "_try_local", return_value=None), \
                mock.patch.object(lp.lyrics_check, "check", side_effect=lambda s, p, *a: (s, p, [])):
            synced, _plain = lp.fetch_lyrics("Some Song", "Some Band", None, 20000)
        self.assertEqual(asked, ["lrclib", "lrcmux"])
        self.assertEqual(len(synced), 3)


class InstallerKeepsSettings(unittest.TestCase):
    def test_installer_does_not_delete_settings(self):
        path = os.path.join(HERE, "..", "..", "builder", "installer.iss")
        if not os.path.exists(path):
            self.skipTest("builder folder not next to the source")
        text = _read(path, "r")
        for line in text.splitlines():
            if line.strip().startswith(";"):
                continue
            self.assertNotIn("appearance.json", line)
            self.assertNotIn("settings_reset", line)
            self.assertNotIn("[InstallDelete]", line)


if __name__ == "__main__":
    unittest.main()


class TrayMenuTests(unittest.TestCase):
    def test_menu_has_only_the_wanted_items(self):
        import tray
        labels = [e[0] for e in tray.menu_spec(True, False, False) if e]
        self.assertEqual(labels[1:], ["Unlock position", "Settings…", "Start with Windows", "Web view", "Show web view",
                                      "Wrong lyrics? Pick another...", "Check for updates", "Open logs folder", "Quit"])
        self.assertTrue(labels[0].startswith("LycRomanise v"))
