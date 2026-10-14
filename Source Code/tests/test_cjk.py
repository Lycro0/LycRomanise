"""Tests for the Japanese and Chinese romanizer fixes. pykakasi / pypinyin are replaced by tiny
stand-ins when they aren't installed, so these check OUR rules (particles, kanji overrides, long
vowels, punctuation), not the libraries' dictionaries."""
import os
import sys
import types
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))

_HEP = dict(zip("あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをんがぎぐげござじずぜぞだでどばびぶべぼぱぴぷぺぽ",
                "a i u e o ka ki ku ke ko sa shi su se so ta chi tsu te to na ni nu ne no ha hi fu he ho ma mi mu me mo ya yu yo ra ri ru re ro wa wo n ga gi gu ge go za ji zu ze zo da de do ba bi bu be bo pa pi pu pe po".split()))
_HEP["コ"], _HEP["ヒ"], _HEP["ー"] = "ko", "hi", "-"


def _install_stubs():
    try:
        import pykakasi  # noqa: F401
        import pypinyin  # noqa: F401
        return False
    except ImportError:
        pass

    class _K:
        def convert(self, text):
            items, cur = [], ""
            def kind(c):
                return "h" if "\u3041" <= c <= "\u309f" else "k" if c in "コヒー" else "o"
            for ch in text:
                if cur and kind(ch) == kind(cur[-1]) and kind(ch) != "o":
                    cur += ch
                else:
                    if cur:
                        items.append(cur)
                    cur = ch
            if cur:
                items.append(cur)
            return [{"orig": t, "hepburn": "".join(_HEP.get(c, c) for c in t)} for t in items]

    pk = types.ModuleType("pykakasi")
    pk.kakasi = lambda: _K()
    sys.modules["pykakasi"] = pk
    pp = types.ModuleType("pypinyin")
    pp.pinyin = lambda *a, **k: [[c] for c in a[0]]
    pp.Style = types.SimpleNamespace(TONE=1)
    sys.modules["pypinyin"] = pp
    return True


STUBBED = _install_stubs()
from romanize import japanese, chinese  # noqa: E402


class JapaneseRules(unittest.TestCase):
    def test_particles(self):
        self.assertEqual(japanese._fix_run("わたしは", "watashiha"), "watashiwa")
        self.assertEqual(japanese._fix_run("は", "ha"), "wa")
        self.assertEqual(japanese._fix_run("へ", "he"), "e")
        self.assertEqual(japanese._fix_run("ここへ", "kokohe"), "kokoe")
        self.assertEqual(japanese._fix_run("あはは", "ahaha"), "ahaha")      # a laugh, not a particle
        self.assertEqual(japanese._fix_run("はは", "haha"), "haha")

    def test_wo_particle_anywhere(self):
        if STUBBED:
            self.assertEqual(japanese._fix_run("ゆめを", "yumewo"), "yume o")
            self.assertEqual(japanese._fix_run("ゆめをみて", "yumewomite"), "yume o mite")

    def test_kanji_overrides(self):
        r = japanese._apply_readings
        self.assertEqual(r("今日は"), "きょうは")
        self.assertEqual(r("君が"), "きみが")
        self.assertEqual(r("夢"), "ゆめ")
        self.assertEqual(r("光る"), "光る")        # verb: not a lone noun
        self.assertEqual(r("愛する"), "愛する")
        self.assertEqual(r("愛情"), "愛情")        # compound
        self.assertEqual(r("空気"), "空気")
        self.assertEqual(r("君、"), "きみ、")

    def test_long_vowel_and_romanize(self):
        self.assertEqual(japanese._long_vowels("コーヒー", "ko-hi-"), "koohii")
        self.assertEqual(japanese._long_vowels("あい", "ai"), "ai")
        if STUBBED:
            self.assertEqual(japanese.romanize("コーヒー"), "koohii")
            self.assertEqual(japanese.romanize("これは"), "korewa")


class ChineseAssembly(unittest.TestCase):
    def test_spacing_and_punctuation(self):
        self.assertEqual(chinese._assemble([["wǒ"], ["ài"], ["nǐ"], ["，"]]), "wǒ ài nǐ,")
        self.assertEqual(chinese._assemble([["wǒ"], ["love"], ["nǐ"], ["。"]]), "wǒ love nǐ.")
        self.assertEqual(chinese._assemble([["「"], ["wǒ"], ["」"]]), "wǒ")
        self.assertEqual(chinese._assemble([["ài"], [" "], ["nǐ"], ["…"]]), "ài nǐ...")
        self.assertEqual(chinese._assemble([]), "")


if __name__ == "__main__":
    unittest.main()
