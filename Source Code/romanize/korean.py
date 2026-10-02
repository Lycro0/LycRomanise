"""
Korean -> Latin romanization (Revised Romanization of Korean, RR 2000).

This is a rule-based, dictionary-free implementation. It handles:
  - Standard Hangul syllable decomposition (initial / vowel / final)
  - Liaison (연음): a syllable-final consonant is carried over as the
    onset of the next syllable when that next syllable starts with a
    "null" initial (i.e. it begins with a vowel sound), e.g.
    한국어 -> "hangugeo", not "hangug-eo"
  - Consonant clusters (겹받침) such as ㄳ, ㄵ, ㄺ, ㅄ splitting correctly
    during liaison (e.g. 닭이 -> "dalgi", not "dakgi")

What it deliberately does NOT do, because these require a pronunciation
dictionary rather than character rules, and would need per-word exceptions:
  - Palatalization (구개음화), e.g. 굳이 is technically pronounced "guji"
    rather than the "gud-i" this module would produce
  - Tensification (경음화) after certain finals, e.g. 없어 is often heard
    as "eopsseo" rather than the "eopseo" this module produces
  - Word-specific irregular batchim readings (e.g. 밟다 vs 넓다, which
    read the same 래 cluster two different ways)

For the vast majority of song lyrics this still reads very close to how
a native speaker would actually pronounce the line, which is the goal
for a "romanized lyrics" display.
"""

SBASE = 0xAC00
LCOUNT = 19
VCOUNT = 21
TCOUNT = 28
NCOUNT = VCOUNT * TCOUNT  # 588

# Choseong (initial consonant) -> Revised Romanization, in Unicode order
CHO = [
    "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s",
    "ss", "", "j", "jj", "ch", "k", "t", "p", "h",
]

# Jungseong (vowel) -> Revised Romanization, in Unicode order
JUNG = [
    "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa",
    "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i",
]

# Jongseong (final consonant, 0 = none) -> Unicode order.
# Each entry: (isolated_form, liaison_remain_or_None, liaison_next_cho_index_or_None)
#   isolated_form        -> used when there's nothing to liaise with
#   liaison_remain        -> what (if anything) stays attached to THIS
#                             syllable when liaison happens
#   liaison_next_cho_idx  -> index into CHO for what gets carried onto
#                             the NEXT syllable when liaison happens
JONG = {
    0:  ("",   None, None),
    1:  ("k",  None, 0),    # ㄱ
    2:  ("k",  None, 1),    # ㄲ
    3:  ("k",  "k",  9),    # ㄳ
    4:  ("n",  None, 2),    # ㄴ
    5:  ("n",  "n",  12),   # ㄵ
    6:  ("n",  "n",  None), # ㄶ (h silent on liaison)
    7:  ("t",  None, 3),    # ㄷ
    8:  ("l",  None, 5),    # ㄹ (liaises as 'r')
    9:  ("k",  "l",  0),    # ㄺ
    10: ("m",  "l",  6),    # ㄻ
    11: ("l",  "l",  7),    # ㄼ
    12: ("l",  "l",  9),    # ㄽ
    13: ("l",  "l",  16),   # ㄾ
    14: ("p",  "l",  17),   # ㄿ
    15: ("l",  "l",  None), # ㅀ (h silent on liaison)
    16: ("m",  None, 6),    # ㅁ
    17: ("p",  None, 7),    # ㅂ
    18: ("p",  "p",  9),    # ㅄ
    19: ("t",  None, 9),    # ㅅ
    20: ("t",  None, 10),   # ㅆ
    21: ("ng", "ng", None), # ㅇ (never liaises)
    22: ("t",  None, 12),   # ㅈ
    23: ("t",  None, 14),   # ㅊ
    24: ("k",  None, 15),   # ㅋ
    25: ("t",  None, 16),   # ㅌ
    26: ("p",  None, 17),   # ㅍ
    27: ("t",  None, None), # ㅎ (silent on liaison)
}

NULL_CHOSEONG_INDEX = 11  # ㅇ as an initial (silent, "vowel-start")


def _is_hangul_syllable(ch: str) -> bool:
    return "\uac00" <= ch <= "\ud7a3"


def _decompose(ch: str):
    idx = ord(ch) - SBASE
    lead = idx // NCOUNT
    vowel = (idx % NCOUNT) // TCOUNT
    tail = idx % TCOUNT
    return lead, vowel, tail


def romanize(text: str) -> str:
    """Romanize a line of Korean text (Revised Romanization + liaison)."""
    chars = list(text)
    n = len(chars)
    out = []
    i = 0
    while i < n:
        ch = chars[i]
        if not _is_hangul_syllable(ch):
            out.append(ch)
            i += 1
            continue

        lead, vowel, tail = _decompose(ch)
        nxt = chars[i + 1] if i + 1 < n else None
        next_is_vowel_start = (
            nxt is not None
            and _is_hangul_syllable(nxt)
            and _decompose(nxt)[0] == NULL_CHOSEONG_INDEX
        )

        syll = CHO[lead] + JUNG[vowel]

        if tail == 0:
            out.append(syll)
            i += 1
            continue

        isolated, remain, liaison_cho = JONG[tail]

        if next_is_vowel_start:
            if remain is not None:
                syll += remain
            out.append(syll)
            if liaison_cho is not None:
                out.append(CHO[liaison_cho])
        else:
            out.append(syll + isolated)

        i += 1

    return "".join(out)
