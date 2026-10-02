"""Romanization for song lyrics: Korean, Japanese, Chinese (pinyin), Arabic-
script, Cyrillic, Greek, Hebrew and Devanagari. Latin-script text is left as is.

A single lyric line can mix scripts (a Japanese song with an Arabic line, or
K-pop with English), so text is split into runs by script and each run is
romanized by its own engine.
"""

from .detect import char_class, detect_script

__all__ = ["romanize_line", "needs_romanization", "detect_script", "char_class"]

SUPPORTED_SCRIPTS = {
    "ko": "Korean (Revised Romanization)",
    "ja": "Japanese (Hepburn romaji)",
    "zh": "Chinese (Mandarin pinyin with tone marks)",
    "ar": "Arabic / Persian / Urdu",
    "ru": "Cyrillic: Russian, Ukrainian, Bulgarian, Serbian...",
    "el": "Greek",
    "he": "Hebrew",
    "hi": "Devanagari: Hindi, Marathi, Nepali",
}

def needs_romanization(text):
    """True if `text` contains any character from a supported non-Latin script."""
    return any(char_class(ch) for ch in text or "")

def _engine(cls):
    # Imported lazily so a missing optional library (pykakasi/pypinyin) only
    # affects its own language rather than stopping the whole app.
    if cls == "ko":
        from . import korean
        return korean.romanize
    if cls == "ja":
        from . import japanese
        return japanese.romanize
    if cls == "zh":
        from . import chinese
        return chinese.romanize
    from . import others
    return {
        "ar": others.arabic, "ru": others.cyrillic, "el": others.greek,
        "he": others.hebrew, "hi": others.devanagari,
    }[cls]

def _runs(text, han_as):
    """Split into [(class_or_None, text)] with each script one run. Neutral
    characters (spaces, punctuation, Latin, digits) that sit between two runs
    of the same script stay inside that run, so a sentence isn't cut up."""
    raw = []
    for ch in text:
        cls = char_class(ch)
        if cls in ("han", "kana"):
            cls = han_as
        if raw and raw[-1][0] == cls:
            raw[-1][1].append(ch)
        else:
            raw.append([cls, [ch]])
    merged = []
    for idx, (cls, chars) in enumerate(raw):
        if (
            cls is None and merged and merged[-1][0] is not None
            and idx + 1 < len(raw) and raw[idx + 1][0] == merged[-1][0]
        ):
            merged[-1][1].extend(chars)
            continue
        if merged and merged[-1][0] == cls:
            merged[-1][1].extend(chars)
        else:
            merged.append([cls, list(chars)])
    return [(cls, "".join(chars)) for cls, chars in merged]

def romanize_line(text, hint=None):
    """Return `text` with every non-Latin run replaced by its Latin reading.

    `hint` (a code from detect_script, usually for the whole song) only
    matters for Han characters that appear without kana: they're read as
    Japanese if the song is Japanese, else Chinese.
    """
    if not text or not needs_romanization(text):
        return text
    line_script = detect_script(text)
    han_as = "ja" if (line_script == "ja" or (line_script is None and hint == "ja")
                      or (line_script == "zh" and hint == "ja")) else "zh"
    out = []
    for cls, chunk in _runs(text, han_as):
        if cls is None:
            out.append(chunk)
            continue
        try:
            reading = _engine(cls)(chunk)
        except ImportError:
            reading = chunk   # library for that language isn't installed
        if out and reading and out[-1] and not out[-1].endswith(" ") and not reading.startswith(" "):
            out.append(" ")
        out.append(reading)
    return "".join(out).replace("  ", " ")
