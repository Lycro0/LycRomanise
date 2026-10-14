"""Run with:  python -m unittest discover tests   (from this folder). Pure logic only, no windows."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
os.environ.setdefault("LOCALAPPDATA", tempfile.mkdtemp())

import config
import lyrics_provider as lp
from romanize.korean import romanize as ko


class LrcParser(unittest.TestCase):
    def test_multi_stamp_offset_and_word_tags(self):
        lines = lp.parse_lrc("[offset:500]\n[00:10.00][00:50]Chorus <00:11.00>word\n[00:30.00]Next")
        self.assertEqual(lines, [(9.5, "Chorus word"), (29.5, "Next"), (49.5, "Chorus word")])

    def test_instrumental_gap_marker(self):
        lines = lp.parse_lrc("[00:05.00]a\n[00:08.00]\n[00:30.00]b\n[00:31.00]\n[00:32.00]c\n[00:40.00]")
        self.assertEqual([t for _s, t in lines], ["a", lp.INSTRUMENTAL_MARK, "b", "c"])


class Matching(unittest.TestCase):
    def test_artists(self):
        self.assertEqual(lp._split_artists("Tyler, The Creator"), ["tyler the creator"])
        self.assertEqual(lp._split_artists("AC/DC"), ["ac dc"])
        self.assertEqual(lp._split_artists("X Japan"), ["x japan"])

    def test_clean_title_keeps_edition_tags(self):
        self.assertEqual(lp._clean_title("99 Problems (Acoustic)"), "99 Problems (Acoustic)")
        self.assertEqual(lp._clean_title("Song (feat. Someone)"), "Song")


class Korean(unittest.TestCase):
    def test_rules(self):
        for text, want in {"합니다": "hamnida", "좋다": "jota", "국물": "gungmul", "신라": "silla",
                           "먹는다": "meongneunda", "같이": "gachi", "한국어": "hangugeo", "닭이": "dalgi"}.items():
            self.assertEqual(ko(text), want, text)


class Settings(unittest.TestCase):
    def test_sanitize_bad_values(self):
        s = config.sanitize({"karaoke_sung_rgb": 5, "curr_font_size": "x", "desktop_width": 99999, "desktop_bg": "red"})
        self.assertEqual(s["karaoke_sung_rgb"], config.DEFAULT_APPEARANCE["karaoke_sung_rgb"])
        self.assertEqual(s["curr_font_size"], config.DEFAULT_APPEARANCE["curr_font_size"])
        self.assertEqual(s["desktop_width"], 4000)
        self.assertEqual(s["desktop_bg"], "#000000")


class CacheAndOffsets(unittest.TestCase):
    def test_cache_negative_cache_and_forget(self):
        from unittest import mock
        d = tempfile.mkdtemp()
        calls = []

        def fake(track, artist, album, dur):
            calls.append(track)
            return (([(1.0, "hello world line"), (5.0, "second line here")], None)
                    if track == "Found" else (None, None))
        with mock.patch.object(lp, "CACHE_PATH", os.path.join(d, "c.json")), \
                mock.patch.object(lp, "MISSING_PATH", os.path.join(d, "m.json")), \
                mock.patch.object(lp, "_cache", None), mock.patch.object(lp, "_missing", None), \
                mock.patch.object(lp, "SOURCES", (("lrclib", fake),)), \
                mock.patch.object(lp.lyrics_check, "check", lambda s, p, t, a, du: (s, p, [])):
            self.assertTrue(lp.fetch_lyrics("Found", "A", None, 100000)[0])
            self.assertTrue(lp.fetch_lyrics("Found", "A", None, 100000)[0])
            self.assertEqual(calls, ["Found"])                  # second call came from memory
            self.assertEqual(lp.fetch_lyrics("Nope", "A", None, 100000), (None, None))
            self.assertEqual(lp.fetch_lyrics("Nope", "A", None, 100000), (None, None))
            self.assertEqual(calls, ["Found", "Nope"])          # the miss was remembered
            lp.forget("Nope", "A", 100000)
            lp.fetch_lyrics("Nope", "A", None, 100000)
            self.assertEqual(calls, ["Found", "Nope", "Nope"])  # forget() allows a fresh look-up
            lp.flush_cache()
            self.assertTrue(os.path.exists(os.path.join(d, "c.json")))
            self.assertTrue(os.path.exists(os.path.join(d, "m.json")))

    def test_song_offsets(self):
        from unittest import mock
        import song_offsets as so
        with mock.patch.object(so, "PATH", os.path.join(tempfile.mkdtemp(), "o.json")), \
                mock.patch.object(so, "_data", None):
            k = so.make_key("  AC/DC ", "Back In Black")
            self.assertEqual(so.get_ms(k), 0)
            self.assertEqual(so.set_ms(k, 250), 250)
            so._data = None                                     # reload from disk
            self.assertEqual(so.get_ms(k), 250)
            self.assertEqual(so.set_ms(k, 999999), so.LIMIT_MS)
            self.assertEqual(so.set_ms(k, 0), 0)
            self.assertEqual(so.get_ms(k), 0)


if __name__ == "__main__":
    unittest.main()
