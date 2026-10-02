"""Japanese -> Hepburn romaji romanization, using the pykakasi library."""

import re

import pykakasi

_kks = pykakasi.kakasi()

# pykakasi.convert() segments a line into one chunk per underlying
# word/particle. A chunk whose *original* text is pure punctuation
# (or whitespace already in the source) should stay glued to the
# previous word — "sky!" not "sky !" — everything else gets a space
# before it. That matches how romaji normally reads and gives long lines
# real word breaks to wrap / split at.
_PUNCT_ONLY_RE = re.compile(r"^[^\w]+$", re.UNICODE)

def romanize(text: str) -> str:
    result = _kks.convert(text)
    parts = []
    for item in result:
        piece = item.get("hepburn", "")
        if not piece:
            continue
        orig = item.get("orig", "")
        if parts and not _PUNCT_ONLY_RE.match(orig or ""):
            parts.append(" ")
        parts.append(piece)
    return "".join(parts)
