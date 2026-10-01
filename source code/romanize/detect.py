"""Work out which writing system text is in.

Codes: 'ko' Korean, 'ja' Japanese, 'zh' Chinese, 'ar' Arabic-script
(Arabic/Persian/Urdu), 'ru' Cyrillic (Russian, Ukrainian, Bulgarian,
Serbian...), 'el' Greek, 'he' Hebrew, 'hi' Devanagari (Hindi/Marathi/Nepali).
Latin-script text (English, Spanish, Turkish, Vietnamese...) returns None —
it's already readable, so there's nothing to romanize.
"""

# (low, high, class) — 'han' and 'kana' are resolved to zh/ja per line.
_RANGES = (
    (0x1100, 0x11FF, "ko"), (0x3130, 0x318F, "ko"), (0xAC00, 0xD7A3, "ko"),
    (0x3040, 0x30FF, "kana"), (0x31F0, 0x31FF, "kana"), (0xFF66, 0xFF9F, "kana"),
    (0x3400, 0x4DBF, "han"), (0x4E00, 0x9FFF, "han"), (0xF900, 0xFAFF, "han"),
    (0x0600, 0x06FF, "ar"), (0x0750, 0x077F, "ar"), (0x08A0, 0x08FF, "ar"),
    (0xFB50, 0xFDFF, "ar"), (0xFE70, 0xFEFF, "ar"),
    (0x0400, 0x052F, "ru"),
    (0x0370, 0x03FF, "el"), (0x1F00, 0x1FFF, "el"),
    (0x0590, 0x05FF, "he"), (0xFB1D, 0xFB4F, "he"),
    (0x0900, 0x097F, "hi"),
)

def char_class(ch):
    """'ko' | 'kana' | 'han' | 'ar' | 'ru' | 'el' | 'he' | 'hi' | None."""
    o = ord(ch)
    if o < 0x0370:
        return None
    for lo, hi, cls in _RANGES:
        if lo <= o <= hi:
            return cls
    return None

def detect_script(text):
    """The dominant non-Latin script in `text` as a code (see module doc),
    or None if it's all Latin/neutral. Any kana means Japanese, since
    Japanese lyrics mix kanji + kana while Chinese lyrics use Han only."""
    if not text:
        return None
    counts = {}
    for ch in text:
        c = char_class(ch)
        if c:
            counts[c] = counts.get(c, 0) + 1
    if not counts:
        return None
    if counts.get("kana"):
        return "ja"
    han = counts.pop("han", 0)
    if han:
        counts["zh"] = han
    ko = counts.get("ko", 0)
    if ko and ko >= counts.get("zh", 0):
        return "ko"
    return max(counts, key=counts.get)
