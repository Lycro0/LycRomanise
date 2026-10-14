"""Regression tests for bugs found in the v1.5.0 review.
Run with:  python -m unittest discover tests   (from the source folder). Pure logic only, no windows."""
import ast
import os
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
os.environ.setdefault("LOCALAPPDATA", tempfile.mkdtemp())

import lyrics_provider as lp
import romanize
import winsys
from romanize import others

HERE = os.path.join(os.path.dirname(__file__), "..")


class PickCandidate(unittest.TestCase):
    def test_exact_length_beats_one_second_off(self):
        # 0.0 used to be falsy: an exact-length hit was scored like one 3 s off and lost to a 1 s one.
        cands = [{"id": 1, "name": "Song", "artists": ["Artist"], "dur_ms": 200000},
                 {"id": 2, "name": "Song", "artists": ["Artist"], "dur_ms": 201000}]
        self.assertEqual(lp._pick_candidate("t", cands, "Song", "Artist", 200000)["id"], 1)
        cands.reverse()
        self.assertEqual(lp._pick_candidate("t", cands, "Song", "Artist", 200000)["id"], 1)

    def test_unknown_length_still_works(self):
        cands = [{"id": 7, "name": "Song", "artists": ["Artist"], "dur_ms": 0}]
        self.assertEqual(lp._pick_candidate("t", cands, "Song", "Artist", 0)["id"], 7)


class LocalLyrics(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.lyrics_dir = os.path.join(self.dir, "lyrics")
        os.makedirs(self.lyrics_dir)
        self.patches = [
            mock.patch.object(lp, "CACHE_PATH", os.path.join(self.dir, "c.json")),
            mock.patch.object(lp, "MISSING_PATH", os.path.join(self.dir, "m.json")),
            mock.patch.object(lp, "_cache", None), mock.patch.object(lp, "_missing", None),
            mock.patch.object(lp, "lyrics_folders", lambda: [self.lyrics_dir]),
        ]
        for p in self.patches:
            p.start()
        self.addCleanup(lambda: [p.stop() for p in self.patches])

    def _write(self, name, text):
        with open(os.path.join(self.lyrics_dir, name), "w", encoding="utf-8") as fh:
            fh.write(text)

    def test_local_file_wins_over_cached_online_lyrics(self):
        online = [(1.0, "online line one"), (5.0, "online line two")]

        def fake(track, artist, album, dur):
            return online, None
        with mock.patch.object(lp, "SOURCES", (("lrclib", fake),)), \
                mock.patch.object(lp.lyrics_check, "check", lambda s, p, t, a, du: (s, p, [])):
            self.assertEqual(lp.fetch_lyrics("Song", "Artist", None, 100000)[0], online)   # now cached
            self._write("Artist - Song.lrc", "[00:02.00]my own line\n[00:06.00]my second line")
            got = lp.fetch_lyrics("Song", "Artist", None, 100000)[0]
            self.assertEqual([t for _s, t in got], ["my own line", "my second line"])
            os.remove(os.path.join(self.lyrics_dir, "Artist - Song.lrc"))
            # the file was never cached, so deleting it brings the online lyrics straight back
            self.assertEqual(lp.fetch_lyrics("Song", "Artist", None, 100000)[0], online)

    def test_local_file_is_trusted_for_length(self):
        # the last line is far past the end of a 100 s song; for an online hit that means "another
        # edition", for the user's own file it must not be thrown away
        self._write("Song.lrc", "[00:02.00]first line here\n[08:00.00]last line much later")
        got = lp.fetch_lyrics("Song", "Artist", None, 100000)[0]
        self.assertEqual(len(got), 2)

    def test_local_placeholder_is_ignored(self):
        self._write("Song.lrc", "[00:00.00]纯音乐，请欣赏")
        with mock.patch.object(lp, "SOURCES", ()):
            self.assertEqual(lp.fetch_lyrics("Song", "Artist", None, 100000), (None, None))


class CacheHit(unittest.TestCase):
    def test_replaying_a_song_does_not_rewrite_the_cache_file(self):
        d = tempfile.mkdtemp()
        calls = []

        def fake(track, artist, album, dur):
            calls.append(track)
            return [(1.0, "hello world line"), (5.0, "second line here")], None
        with mock.patch.object(lp, "CACHE_PATH", os.path.join(d, "c.json")), \
                mock.patch.object(lp, "MISSING_PATH", os.path.join(d, "m.json")), \
                mock.patch.object(lp, "_cache", None), mock.patch.object(lp, "_missing", None), \
                mock.patch.object(lp, "lyrics_folders", lambda: []), \
                mock.patch.object(lp, "SOURCES", (("lrclib", fake),)), \
                mock.patch.object(lp.lyrics_check, "check", lambda s, p, t, a, du: (s, p, [])):
            lp.fetch_lyrics("Found", "A", None, 100000)
            lp.flush_cache()
            with mock.patch.object(lp, "_schedule_flush") as sched:
                self.assertTrue(lp.fetch_lyrics("Found", "A", None, 100000)[0])
                sched.assert_not_called()
            self.assertTrue(lp._dirty)            # the "last used" order is still saved, at exit
            lp.flush_cache()
            self.assertFalse(lp._dirty)
            self.assertEqual(calls, ["Found"])


class Romanize(unittest.TestCase):
    def test_runs_of_spaces_collapse(self):
        self.assertEqual(romanize.romanize_line("привет   мир"), "privet mir")
        self.assertEqual(romanize.romanize_line("а     б"), "a b")

    def test_greek_diaeresis_keeps_vowels_apart(self):
        self.assertEqual(others.greek("παϊδάκια"), "paidakia")     # not "pedakia"
        self.assertEqual(others.greek("παιδί"), "pedi")            # no diaeresis: the digraph is still read as e
        self.assertEqual(others.greek("Ελλάδα"), "Ellada")
        self.assertEqual(others.greek("αυτό"), "afto")


class Misc(unittest.TestCase):
    def test_detach_docstring_is_a_real_docstring(self):
        self.assertTrue(winsys.detach_from_console.__doc__)

    def test_selftest_hands_handle_lyrics_a_pair(self):
        """_handle_lyrics unpacks (synced, plain); the selftest used to pass three values and crashed."""
        with open(os.path.join(HERE, "selftest.py"), encoding="utf-8") as fh:
            tree = ast.parse(fh.read())
        calls = [n for n in ast.walk(tree) if isinstance(n, ast.Call)
                 and getattr(n.func, "attr", "") == "_handle_lyrics"]
        self.assertTrue(calls)
        for call in calls:
            result = call.args[1]
            self.assertIsInstance(result, ast.Tuple)
            self.assertEqual(len(result.elts), 2)

    def test_update_script_escapes_percent_in_install_folder(self):
        import updater
        with mock.patch.object(updater, "APP_DIR", r"C:\Users\100%\LycRomanise"):
            line = [x for x in updater._update_script(r"C:\t\setup.exe").split("\r\n") if "/DIR=" in x][0]
        self.assertIn("100%%", line)
        self.assertNotIn("100%\\", line)


if __name__ == "__main__":
    unittest.main()
