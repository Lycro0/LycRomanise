"""Mandarin Chinese -> pinyin romanization, using the pypinyin library."""

from pypinyin import pinyin, Style


def romanize(text: str) -> str:
    syllables = pinyin(text, style=Style.TONE, heteronym=False, errors="default")
    return " ".join(s[0] for s in syllables if s)
