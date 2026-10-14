"""Regression tests for v1.7.0 (fixed font sizes, 32-character line split, settings reset, OBS scaling)."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import config
import layout
import mobile_server


class SettingsTests(unittest.TestCase):
    def test_removed_settings_are_gone(self):
        for key in ("curr_font_size", "next_font_size", "curr_font_min_size", "next_font_min_size",
                    "outline_size", "max_line_chars"):
            self.assertNotIn(key, config.DEFAULT_APPEARANCE)
            self.assertNotIn(key, config.sanitize({key: 5}))

    def test_reset_happens_once(self):
        with tempfile.TemporaryDirectory() as d:
            old = (config.APPEARANCE_PATH, config.SETTINGS_RESET_MARKER)
            config.APPEARANCE_PATH = os.path.join(d, "appearance.json")
            config.SETTINGS_RESET_MARKER = os.path.join(d, "flag")
            try:
                config.save_appearance(dict(config.DEFAULT_APPEARANCE, lyric_offset_ms=900))
                self.assertTrue(config.reset_settings_once())
                self.assertEqual(config.load_appearance()["lyric_offset_ms"], 0)
                config.save_appearance(dict(config.DEFAULT_APPEARANCE, lyric_offset_ms=900))
                self.assertFalse(config.reset_settings_once())        # second start: the user's settings stay
                self.assertEqual(config.load_appearance()["lyric_offset_ms"], 900)
            finally:
                config.APPEARANCE_PATH, config.SETTINGS_RESET_MARKER = old


class SplitTests(unittest.TestCase):
    def test_long_line_splits_in_two_at_a_word(self):
        parts = layout.split_display("Something, something, something, man, we don't have a problem", 32)
        self.assertEqual([p[0] for p in parts], ["Something, something, something,", "man, we don't have a problem"])

    def test_short_line_stays_whole(self):
        self.assertEqual(len(layout.split_display("A line of thirty-two characters!!", 33)), 1)
        self.assertEqual(len(layout.split_display("Short line", 32)), 1)


class ObsPageTests(unittest.TestCase):
    def test_page_scales_a_fixed_strip(self):
        page = mobile_server.PAGE
        self.assertIn("Math.min(window.innerWidth / dw, window.innerHeight / dh)", page)
        self.assertIn("scale(var(--s, 1))", page)
        self.assertNotIn("--k", page)


if __name__ == "__main__":
    unittest.main()
