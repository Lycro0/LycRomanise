"""Mandarin Chinese -> pinyin with tone marks, using the pypinyin library.

Compared with a plain pinyin() call this also
  * applies the usual tone changes (不 + 4th tone -> bú, 一 -> yí / yì, 3rd + 3rd -> 2nd) when the
    installed pypinyin supports it,
  * turns Chinese punctuation (，。！？、：；「」…) into ordinary punctuation, and glues it to the
    word before it ("wǒ ài nǐ," not "wǒ ài nǐ ,"),
  * keeps Latin words and numbers that sit inside a line as they are.
"""

import re
import unicodedata

from pypinyin import pinyin, Style

_PUNCT_MAP = {
    "。": ".", "、": ",", "，": ",", "；": ";", "：": ":", "！": "!", "？": "?",
    "「": "", "」": "", "『": "", "』": "", "《": "", "》": "", "“": "", "”": "",
    "‘": "'", "’": "'", "（": "(", "）": ")", "…": "...", "⋯": "...", "～": "~", "·": " ",
}
_CLOSING_RE = re.compile(r"^[.,;:!?)\]\"'~]+$")
_PUNCT_ONLY_RE = re.compile(r"^[^\w]+$", re.UNICODE)


def _pinyin(text):
    try:
        return pinyin(text, style=Style.TONE, heteronym=False, errors="default", tone_sandhi=True)
    except TypeError:                           # an older pypinyin without tone_sandhi
        return pinyin(text, style=Style.TONE, heteronym=False, errors="default")


def _assemble(syllables):
    """Join pinyin()'s [[syllable], ...] into one string with sensible spacing."""
    out = []
    for item in syllables:
        if not item:
            continue
        piece = item[0]
        if not piece or not piece.strip():
            continue
        piece = "".join(_PUNCT_MAP.get(ch, ch) for ch in piece).strip()
        if not piece:
            continue
        if out and _PUNCT_ONLY_RE.match(piece) and _CLOSING_RE.match(piece):
            out[-1] += piece                    # closing punctuation hugs the previous word
        else:
            out.append(piece)
    return " ".join(out)


def romanize(text: str) -> str:
    return _assemble(_pinyin(unicodedata.normalize("NFKC", text)))
