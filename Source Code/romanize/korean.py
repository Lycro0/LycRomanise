"""
Korean -> Latin romanization (Revised Romanization of Korean, RR 2000).

Rule-based and dictionary-free. Before writing the letters it applies the standard
pronunciation rules between neighbouring syllables, the way RR transcribes them:
  - liaison (연음): a final consonant moves onto a following vowel, 한국어 -> hangugeo
  - consonant clusters (겹받침) split correctly during liaison, 닭이 -> dalgi
  - nasalization (비음화): 합니다 -> hamnida, 국물 -> gungmul, 먹는다 -> meongneunda
  - ㄹ assimilation: 신라 -> silla, 종로 -> jongno, 심리 -> simni
  - ㅎ aspiration (격음화): 좋다 -> jota, 놓고 -> noko, 입학 -> ipak
  - palatalization (구개음화): 같이 -> gachi, 굳이 -> guji, 닫히다 -> dachida
  - compatibility jamo such as ㅋㅋ / ㅠㅠ are read too
As RR itself does, tensification (경음화) is not written (학교 -> hakgyo, 합계 -> hapgye).
Word-specific irregularities that need a dictionary (e.g. 밟다 vs 넓다) are not handled.
"""

SBASE = 0xAC00
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


K_TAILS = {1, 2, 3, 9, 24}
T_TAILS = {7, 19, 20, 22, 23, 25, 27}
P_TAILS = {14, 17, 18, 26}
ASPIRATE = {0: 15, 3: 16, 12: 14}          # ㄱ ㄷ ㅈ -> ㅋ ㅌ ㅊ after ㅎ

COMPAT = {
    "ㄱ": "g", "ㄲ": "kk", "ㄴ": "n", "ㄷ": "d", "ㄸ": "tt", "ㄹ": "r", "ㅁ": "m", "ㅂ": "b", "ㅃ": "pp",
    "ㅅ": "s", "ㅆ": "ss", "ㅇ": "ng", "ㅈ": "j", "ㅉ": "jj", "ㅊ": "ch", "ㅋ": "k", "ㅌ": "t", "ㅍ": "p",
    "ㅎ": "h", "ㅏ": "a", "ㅐ": "ae", "ㅑ": "ya", "ㅒ": "yae", "ㅓ": "eo", "ㅔ": "e", "ㅕ": "yeo", "ㅖ": "ye",
    "ㅗ": "o", "ㅘ": "wa", "ㅙ": "wae", "ㅚ": "oe", "ㅛ": "yo", "ㅜ": "u", "ㅝ": "wo", "ㅞ": "we", "ㅟ": "wi",
    "ㅠ": "yu", "ㅡ": "eu", "ㅢ": "ui", "ㅣ": "i",
}


def _apply_rules(sy):
    """sy: list of [lead, vowel, tail, lead_str_or_None, tail_str_or_None] for one run of syllables.
    Changes them in place according to the pronunciation rules between each syllable and the next."""
    for i in range(len(sy) - 1):
        a, b = sy[i], sy[i + 1]
        t, lead, vowel = a[2], b[0], b[1]
        if t == 0:
            continue
        if lead == 11:                                   # vowel start: palatalization, else liaison later
            if vowel == 20 and t in (7, 25):
                a[2] = 0
                b[0] = 12 if t == 7 else 14
            continue
        if lead == 18 and vowel == 20 and t == 7:        # 닫히 -> 다치
            a[2], b[0] = 0, 14
            continue
        if t in (27, 6, 15) and lead in ASPIRATE:        # ㅎ + ㄱ/ㄷ/ㅈ
            b[0] = ASPIRATE[lead]
            a[2] = {27: 0, 6: 4, 15: 8}[t]
            continue
        if t in (27, 6, 15) and lead == 9 and t == 27:   # 좋소 -> josso
            b[0], a[2] = 10, 0
            continue
        if t == 27 and lead in (2, 6):                   # 놓는 -> nonneun
            a[2] = 4
            t = 4
        if lead == 18 and t in K_TAILS | T_TAILS | P_TAILS:   # ㄱ/ㄷ/ㅂ/ㅈ + ㅎ
            if t in (1, 24, 2, 3):
                b[0], a[2] = 15, 0
            elif t in (17, 26):
                b[0], a[2] = 17, 0
            elif t in (22, 23):
                b[0], a[2] = 14, 0
            else:
                b[0], a[2] = 16, 0
            continue
        group = "k" if t in K_TAILS else "t" if t in T_TAILS else "p" if t in P_TAILS else None
        if lead in (2, 6) and group:                    # stop + ㄴ/ㅁ -> nasal
            a[4] = {"k": "ng", "t": "n", "p": "m"}[group]
            continue
        if lead == 5:                                    # ㄹ after another consonant
            if t == 4:                                   # ㄴ+ㄹ -> ll
                a[4], b[3] = "l", "l"
            elif t in (8, 11, 12, 13, 15):               # ㄹ+ㄹ -> ll
                b[3] = "l"
            elif group in ("k", "p") or t in (16, 21):   # ㄱ ㅂ ㅁ ㅇ + ㄹ -> n (+ nasal)
                b[3] = "n"
                if group == "k":
                    a[4] = "ng"
                elif group == "p":
                    a[4] = "m"
            continue
        if lead == 2 and t in (8, 11, 12, 13, 15):       # ㄹ+ㄴ -> ll
            b[3] = "l"


def romanize(text: str) -> str:
    """Romanize a line of Korean text (Revised Romanization with the pronunciation rules above)."""
    chars = list(text)
    n = len(chars)
    out = []
    i = 0
    while i < n:
        ch = chars[i]
        if ch in COMPAT:
            out.append(COMPAT[ch])
            i += 1
            continue
        if not _is_hangul_syllable(ch):
            out.append(ch)
            i += 1
            continue
        j = i
        while j < n and _is_hangul_syllable(chars[j]):
            j += 1
        sy = [list(_decompose(c)) + [None, None] for c in chars[i:j]]
        _apply_rules(sy)
        for k, (lead, vowel, tail, lead_s, tail_s) in enumerate(sy):
            syll = (lead_s if lead_s is not None else CHO[lead]) + JUNG[vowel]
            if tail == 0:
                out.append(syll)
                continue
            if tail_s is not None:
                out.append(syll + tail_s)
                continue
            isolated, remain, liaison_cho = JONG[tail]
            nxt = sy[k + 1] if k + 1 < len(sy) else None
            if nxt is not None and nxt[0] == NULL_CHOSEONG_INDEX:
                if remain is not None:
                    syll += remain
                out.append(syll)
                if liaison_cho is not None:
                    out.append(CHO[liaison_cho])
            else:
                out.append(syll + isolated)
        i = j
    return "".join(out)
