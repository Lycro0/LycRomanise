"""Transliteration for Arabic-script, Cyrillic, Greek, Hebrew and Devanagari
lyrics. Rule-based and dictionary-free, aiming for "readable while you
listen" rather than scholarly precision:

  * Cyrillic and Greek are close to exact (simple BGN/PCGN-style, no marks).
  * Devanagari (Hindi etc.) is fairly good; the inherent 'a' is dropped at the
    end of a word the way Hindi is spoken.
  * Arabic and Hebrew are normally written WITHOUT short vowels, so those are
    guessed (an 'a' goes between two consonants) unless the text carries
    vowel marks. Expect an approximation, not a dictionary reading.
"""

import re
import unicodedata


def _cased(src_char, out):
    """Carry capitalisation of the source letter over to its Latin output."""
    if out and src_char.isupper():
        return out[0].upper() + out[1:]
    return out


def _split_words(text, is_letter):
    """Yield (is_word, chunk) so words can be converted and the rest kept."""
    buf, mode = [], None
    for ch in text:
        m = is_letter(ch)
        if mode is None or m == mode:
            buf.append(ch)
        else:
            yield mode, "".join(buf)
            buf = [ch]
        mode = m
    if buf:
        yield mode, "".join(buf)


# ------------------------------------------------------------- Cyrillic --

_CYR = {
    "а": "a", "б": "b", "в": "v", "г": "g", "д": "d", "е": "e", "ё": "yo",
    "ж": "zh", "з": "z", "и": "i", "й": "y", "к": "k", "л": "l", "м": "m",
    "н": "n", "о": "o", "п": "p", "р": "r", "с": "s", "т": "t", "у": "u",
    "ф": "f", "х": "kh", "ц": "ts", "ч": "ch", "ш": "sh", "щ": "shch",
    "ъ": "", "ы": "y", "ь": "", "э": "e", "ю": "yu", "я": "ya",
    # Ukrainian / Belarusian
    "є": "ye", "і": "i", "ї": "yi", "ґ": "g", "ў": "u",
    # Serbian / Macedonian
    "ђ": "dj", "ј": "j", "љ": "lj", "њ": "nj", "ћ": "c", "џ": "dz", "ѓ": "gj", "ќ": "kj", "ѕ": "dz",
}


def cyrillic(text):
    out = []
    for ch in text:
        low = ch.lower()
        if low in _CYR:
            out.append(_cased(ch, _CYR[low]))
        else:
            out.append(ch)
    return "".join(out)


# ---------------------------------------------------------------- Greek --

_GREEK = {
    "α": "a", "β": "v", "γ": "g", "δ": "d", "ε": "e", "ζ": "z", "η": "i",
    "θ": "th", "ι": "i", "κ": "k", "λ": "l", "μ": "m", "ν": "n", "ξ": "x",
    "ο": "o", "π": "p", "ρ": "r", "σ": "s", "ς": "s", "τ": "t", "υ": "i",
    "φ": "f", "χ": "ch", "ψ": "ps", "ω": "o",
}
_GREEK_DIGRAPHS = {
    "ου": "ou", "ει": "i", "οι": "i", "αι": "e", "υι": "i",
    "μπ": "b", "ντ": "d", "γκ": "g", "γγ": "ng", "γχ": "nch", "τσ": "ts", "τζ": "tz",
}


def greek(text):
    # Drop tone marks / diaeresis but keep the base letters.
    decomposed = unicodedata.normalize("NFD", text)
    src = "".join(c for c in decomposed if not unicodedata.combining(c))
    out, i = [], 0
    while i < len(src):
        pair = src[i:i + 2]
        low_pair = pair.lower()
        if len(pair) == 2 and low_pair in _GREEK_DIGRAPHS:
            out.append(_cased(pair[0], _GREEK_DIGRAPHS[low_pair]))
            i += 2
            continue
        # au / eu -> af/av, ef/ev depending on what follows
        if len(pair) == 2 and low_pair in ("αυ", "ευ"):
            nxt = src[i + 2:i + 3].lower()
            voiceless = nxt in ("", "κ", "ξ", "π", "σ", "ς", "τ", "φ", "χ", "ψ", "θ") or not nxt.isalpha()
            out.append(_cased(pair[0], ("a" if low_pair == "αυ" else "e") + ("f" if voiceless else "v")))
            i += 2
            continue
        ch = src[i]
        low = ch.lower()
        out.append(_cased(ch, _GREEK[low]) if low in _GREEK else ch)
        i += 1
    return "".join(out)


# ------------------------------------------------- vowel guessing helper --

def _fill_vowels(tokens):
    """tokens: [('c', text) | ('v', text)]. Put an 'a' between two adjacent
    consonants (never after the last one) - the usual reading of an
    unvowelled word."""
    out = []
    just_inserted = False
    for idx, (kind, txt) in enumerate(tokens):
        out.append(txt)
        if kind == "c" and idx + 1 < len(tokens) and tokens[idx + 1][0] == "c":
            # Alternate so a run of consonants reads m-a-r-h-a-b-a, not m-a-r-a-h-a-b-a.
            if not just_inserted:
                out.append("a")
            just_inserted = not just_inserted
        else:
            just_inserted = False
    return "".join(out)


# --------------------------------------------------------------- Arabic --

_AR_CONS = {
    "ب": "b", "ت": "t", "ث": "th", "ج": "j", "ح": "h", "خ": "kh", "د": "d", "ذ": "dh",
    "ر": "r", "ز": "z", "س": "s", "ش": "sh", "ص": "s", "ض": "d", "ط": "t", "ظ": "z",
    "ع": "'", "غ": "gh", "ف": "f", "ق": "q", "ك": "k", "ل": "l", "م": "m", "ن": "n",
    "ه": "h",
    # Persian / Urdu extras
    "پ": "p", "چ": "ch", "ژ": "zh", "گ": "g", "ک": "k", "ٹ": "t", "ڈ": "d", "ڑ": "r",
    "ں": "n", "ھ": "h", "ہ": "h", "ﻩ": "h",
}
# Vowel-like letters. ة (ta marbuta) is the word-final "-a"/"-ah"; the hamza
# seats ؤ / ئ carry a "u" / "i" (they used to be dropped: سؤال read "sal").
_AR_ALEF = {"ا": "a", "أ": "a", "إ": "i", "آ": "aa", "ٱ": "a", "ى": "a", "ة": "a", "ؤ": "u", "ئ": "i"}
_AR_SILENT = {"ء", "ـ", "ٔ", "ٕ"}   # bare hamza, tatweel
_AR_SHORT = {"َ": "a", "ُ": "u", "ِ": "i", "ٰ": "a"}
_AR_TANWIN = {"ً": "an", "ٌ": "un", "ٍ": "in"}
_AR_GLIDES = {"و": ("w", "u"), "ي": ("y", "i"), "ی": ("y", "i"), "ے": ("y", "e")}
_AR_SUKUN, _AR_SHADDA = "ْ", "ّ"
_AR_ALL = (set(_AR_CONS) | set(_AR_ALEF) | _AR_SILENT | set(_AR_SHORT) | set(_AR_TANWIN)
           | set(_AR_GLIDES) | {_AR_SUKUN, _AR_SHADDA})


def _arabic_word(word):
    tokens, has_marks = [], False
    n = len(word)
    for i, ch in enumerate(word):
        nxt = word[i + 1] if i + 1 < n else ""
        if ch in _AR_CONS:
            tokens.append(["c", _AR_CONS[ch]])
        elif ch in _AR_ALEF:
            tokens.append(["v", _AR_ALEF[ch]])
        elif ch in _AR_GLIDES:
            cons, vow = _AR_GLIDES[ch]
            prev_is_cons = bool(tokens) and tokens[-1][0] == "c"
            if i == 0 or nxt in _AR_SHORT or nxt in _AR_ALEF or nxt in _AR_TANWIN or not prev_is_cons:
                tokens.append(["c", cons])
            else:
                tokens.append(["v", vow])
        elif ch in _AR_SHORT:
            if ch == "ٰ" and tokens and tokens[-1][0] == "v":
                continue
            tokens.append(["v", _AR_SHORT[ch]])
            has_marks = True
        elif ch in _AR_TANWIN:
            tokens.append(["v", _AR_TANWIN[ch]])
            has_marks = True
        elif ch == _AR_SHADDA:
            if tokens and tokens[-1][0] == "c":
                tokens[-1][1] = tokens[-1][1] * 2 if len(tokens[-1][1]) == 1 else tokens[-1][1]
            has_marks = True
        elif ch == _AR_SUKUN:
            has_marks = True
        # silent characters are skipped
    tokens = [tuple(t) for t in tokens]
    return "".join(t for _, t in tokens) if has_marks else _fill_vowels(tokens)


def arabic(text):
    return "".join(
        _arabic_word(chunk) if is_word else chunk
        for is_word, chunk in _split_words(text, lambda c: c in _AR_ALL)
    )


# --------------------------------------------------------------- Hebrew --

_HE_CONS = {
    "א": "", "ב": "v", "ג": "g", "ד": "d", "ה": "h", "ז": "z", "ח": "kh", "ט": "t",
    "י": "y", "כ": "kh", "ך": "kh", "ל": "l", "מ": "m", "ם": "m", "נ": "n", "ן": "n",
    "ס": "s", "ע": "", "פ": "f", "ף": "f", "צ": "ts", "ץ": "ts", "ק": "k", "ר": "r",
    "ש": "sh", "ת": "t", "װ": "v", "ײ": "ey",
}
_HE_NIQQUD = {"ַ": "a", "ָ": "a", "ֲ": "a", "ֳ": "o", "ֱ": "e", "ֵ": "e", "ֶ": "e",
              "ִ": "i", "ֹ": "o", "ֻ": "u"}
_HE_SHEVA, _HE_DAGESH, _HE_SHIN, _HE_SIN = "ְ", "ּ", "ׁ", "ׂ"
_HE_ALL = (set(_HE_CONS) | {"ו"} | set(_HE_NIQQUD) | {_HE_SHEVA, _HE_DAGESH, _HE_SHIN, _HE_SIN, "ׇ", "־", "׳"})
_HE_HARD = {"ב": "b", "כ": "k", "ך": "k", "פ": "p", "ף": "p"}


def _hebrew_word(word):
    word = word.replace("־", "")
    tokens, has_marks = [], False
    n = len(word)
    for i, ch in enumerate(word):
        nxt = word[i + 1] if i + 1 < n else ""
        if ch in _HE_NIQQUD:
            tokens.append(["v", _HE_NIQQUD[ch]])
            has_marks = True
        elif ch in (_HE_SHEVA, "ׇ"):
            has_marks = True
        elif ch == _HE_DAGESH:
            if tokens and tokens[-1][0] == "c":
                base = word[max(0, i - 1)]
                if base in _HE_HARD:
                    tokens[-1][1] = _HE_HARD[base]
            has_marks = True
        elif ch == _HE_SIN:
            if tokens and tokens[-1][0] == "c":
                tokens[-1][1] = "s"
        elif ch == _HE_SHIN:
            pass
        elif ch == "ו":
            if nxt == "ֹ" and i > 0:
                continue                  # holam male: the mark supplies the 'o'
            if i == 0 or nxt in _HE_NIQQUD:
                tokens.append(["c", "v"])
            else:
                tokens.append(["v", "o"])
        elif ch == "י" and i > 0 and tokens and tokens[-1][0] == "c" and nxt == "":
            tokens.append(["v", "i"])
        elif ch == "ה" and nxt == "" and i > 0:
            continue                      # final he is silent
        elif ch in ("׳", "'"):
            # geresh softens ג ז צ into j / zh / ch
            if tokens and tokens[-1][0] == "c":
                tokens[-1][1] = {"g": "j", "z": "zh", "ts": "ch"}.get(tokens[-1][1], tokens[-1][1])
        elif ch in _HE_CONS:
            if i == 0 and ch in _HE_HARD and _HE_CONS[ch]:
                # word-initial ב כ פ are b / k / p (the dagesh is usually not written)
                tokens.append(["c", _HE_HARD[ch]])
            elif _HE_CONS[ch] == "":
                # aleph / ayin have no sound of their own; at the start of a
                # word (without a vowel mark) they stand for an 'a'.
                if i == 0 and nxt not in _HE_NIQQUD:
                    tokens.append(["v", "a"])
            else:
                tokens.append(["c", _HE_CONS[ch]])
    tokens = [tuple(t) for t in tokens if t[1] != "" or t[0] == "v"]
    return "".join(t for _, t in tokens) if has_marks else _fill_vowels(tokens)


def hebrew(text):
    text = re.sub("([גזצ])'", "\\1׳", text)   # ASCII apostrophe used as geresh: ג'ינס
    return "".join(
        _hebrew_word(chunk) if is_word else chunk
        for is_word, chunk in _split_words(text, lambda c: c in _HE_ALL)
    )


# ----------------------------------------------------------- Devanagari --

_DEV_CONS = {
    "क": "k", "ख": "kh", "ग": "g", "घ": "gh", "ङ": "ng",
    "च": "ch", "छ": "chh", "ज": "j", "झ": "jh", "ञ": "ny",
    "ट": "t", "ठ": "th", "ड": "d", "ढ": "dh", "ण": "n",
    "त": "t", "थ": "th", "द": "d", "ध": "dh", "न": "n",
    "प": "p", "फ": "ph", "ब": "b", "भ": "bh", "म": "m",
    "य": "y", "र": "r", "ल": "l", "व": "v", "श": "sh", "ष": "sh", "स": "s", "ह": "h",
    "क्ष": "ksh", "ज्ञ": "gy", "ळ": "l",
}
_DEV_NUKTA = {"ड": "r", "ढ": "rh", "फ": "f", "ज": "z", "क": "q", "ख": "kh", "ग": "gh"}
_DEV_VOWELS = {
    "अ": "a", "आ": "aa", "इ": "i", "ई": "ee", "उ": "u", "ऊ": "oo", "ऋ": "ri",
    "ए": "e", "ऐ": "ai", "ओ": "o", "औ": "au", "ऑ": "o",
}
_DEV_MATRAS = {
    "ा": "aa", "ि": "i", "ी": "ee", "ु": "u", "ू": "oo", "ृ": "ri",
    "े": "e", "ै": "ai", "ो": "o", "ौ": "au", "ॉ": "o",
}
_DEV_MARKS = {"ं": "n", "ँ": "n", "ः": "h"}
_DEV_DIGITS = {c: str(i) for i, c in enumerate("०१२३४५६७८९")}
_VIRAMA, _NUKTA = "्", "़"


def _is_dev_letter(c):
    return (c in _DEV_CONS or c in _DEV_VOWELS or c in _DEV_MATRAS or c in _DEV_MARKS
            or c in (_VIRAMA, _NUKTA))


def _devanagari_word(word):
    out = []          # list of [text, inherent_a_pending]
    n = len(word)
    i = 0
    while i < n:
        ch = word[i]
        if ch in _DEV_CONS:
            reading = _DEV_CONS[ch]
            if i + 1 < n and word[i + 1] == _NUKTA:
                reading = _DEV_NUKTA.get(ch, reading)
                i += 1
            out.append([reading, True])
        elif ch == _VIRAMA:
            if out:
                out[-1][1] = False
        elif ch in _DEV_MATRAS:
            if out:
                out[-1][1] = False
            out.append([_DEV_MATRAS[ch], False])
        elif ch in _DEV_VOWELS:
            out.append([_DEV_VOWELS[ch], False])
        elif ch in _DEV_MARKS:
            if out and out[-1][1]:
                out[-1][1] = True
            out.append([_DEV_MARKS[ch], False])
        i += 1
    # Hindi drops the inherent 'a' at the end of a word.
    res = []
    for idx, (txt, pending) in enumerate(out):
        res.append(txt)
        if pending and idx != len(out) - 1:
            res.append("a")
    return "".join(res)


def devanagari(text):
    text = "".join(_DEV_DIGITS.get(c, c) for c in text)
    return "".join(
        _devanagari_word(chunk) if is_word else chunk
        for is_word, chunk in _split_words(text, _is_dev_letter)
    )
