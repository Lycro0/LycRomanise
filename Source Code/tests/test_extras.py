"""Tests for the old-name migration, local .lrc files, the "wrong lyrics" picker back end, the mobile
page's push rule and the persistent event loop."""
import asyncio
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
os.environ.setdefault("LOCALAPPDATA", tempfile.mkdtemp())

import lyrics_provider as lp
import media_client
import mobile_server
import winsys


class _Key:
    def __enter__(self): return self
    def __exit__(self, *a): pass


class _Reg:
    HKEY_CURRENT_USER, KEY_READ, KEY_SET_VALUE, REG_SZ = 1, 1, 2, 1
    def __init__(self, d): self.d = d
    def OpenKey(self, *a): return _Key()
    def QueryValueEx(self, key, name):
        if name not in self.d:
            raise FileNotFoundError(name)
        return self.d[name], 1
    def DeleteValue(self, key, name): del self.d[name]
    def SetValueEx(self, key, name, _r, _t, value): self.d[name] = value


class OldNameMigration(unittest.TestCase):
    def test_old_entry_replaced_by_new(self):
        reg = _Reg({"SpotiLyricsOverlay": "old.exe"})
        self.assertTrue(winsys.migrate_legacy_autostart(reg))
        self.assertEqual(list(reg.d), [winsys.RUN_VALUE])

    def test_nothing_to_do(self):
        reg = _Reg({})
        self.assertFalse(winsys.migrate_legacy_autostart(reg))
        self.assertEqual(reg.d, {})

    def test_keeps_existing_new_entry(self):
        reg = _Reg({"SpotiLyricsOverlay": "old", winsys.RUN_VALUE: "new"})
        winsys.migrate_legacy_autostart(reg)
        self.assertEqual(reg.d, {winsys.RUN_VALUE: "new"})


class LocalLrc(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        for name, text in (("IU - Blueming.lrc", "[00:01.00]hello\n[00:05.00]world\n"),
                           ("IU - Other.lrc", "[00:01.00]no\n"), ("Solo.lrc", "[00:02.00]solo\n")):
            with open(os.path.join(self.dir, name), "w", encoding="utf-8") as f:
                f.write(text)
        self._old = lp.lyrics_folders
        lp.lyrics_folders = lambda: [self.dir]

    def tearDown(self):
        lp.lyrics_folders = self._old

    def test_matches_artist_dash_title_only(self):
        self.assertEqual(lp._fetch_from_local("Blueming", "IU")[0], [(1.0, "hello"), (5.0, "world")])
        self.assertEqual(lp._fetch_from_local("Solo", "Anyone")[0], [(2.0, "solo")])
        self.assertEqual(lp._fetch_from_local("Missing", "IU"), (None, None))
        self.assertEqual(lp._fetch_from_local("Other", "Someone Else"), (None, None))


class Picker(unittest.TestCase):
    def test_use_alternative_is_remembered(self):
        # Own temporary cache files: this test must never touch the real lyrics cache next to the app.
        import tempfile
        from unittest import mock
        d = tempfile.mkdtemp()
        with mock.patch.object(lp, "CACHE_PATH", os.path.join(d, "c.json")), \
                mock.patch.object(lp, "MISSING_PATH", os.path.join(d, "m.json")), \
                mock.patch.object(lp, "_cache", None), mock.patch.object(lp, "_missing", None):
            alt = {"id": 7, "synced": "[00:01.00]a\n[00:03.00]b", "plain": "a b"}
            self.assertEqual(lp.use_alternative("Pick Test", "Nobody", 180000, alt)[0], [(1.0, "a"), (3.0, "b")])
            self.assertIsNotNone(lp._cache_lookup(lp._cache_key("Pick Test", "Nobody", 180000)))
            self.assertEqual(lp.use_alternative("X", "Y", 1000, {"synced": ""}), (None, None))
            lp.forget("Pick Test", "Nobody", 180000)
            lp.flush_cache()      # write to the temporary files now, not at exit


class MobilePush(unittest.TestCase):
    def test_worth_sending(self):
        s = {"id": 1, "playing": True, "fraction": 0.1, "rate": 0.1}
        self.assertTrue(mobile_server.worth_sending(None, s, 0))
        self.assertFalse(mobile_server.worth_sending(s, dict(s, fraction=0.11), 0.1))     # on course
        self.assertTrue(mobile_server.worth_sending(s, dict(s, id=2), 0.1))              # new line
        self.assertTrue(mobile_server.worth_sending(s, dict(s, fraction=0.6), 0.1))      # seek / drift
        self.assertTrue(mobile_server.worth_sending(s, dict(s), 5.0))                    # refresh


class EventLoop(unittest.TestCase):
    def test_loop_reused_and_replaced_after_error(self):
        c = media_client.MediaClient()
        async def ok(): return 1
        async def bad(): raise ValueError("x")
        self.assertEqual(c._run(ok()), 1)
        first = c._loop
        self.assertEqual(c._run(ok()), 1)
        self.assertIs(c._loop, first)
        with self.assertRaises(ValueError):
            c._run(bad())
        self.assertEqual(c._run(ok()), 1)
        self.assertIsNot(c._loop, first)

    def test_extra_players(self):
        self.assertIn("tidal", media_client.player_matches())


if __name__ == "__main__":
    unittest.main()
