"""Turn numbers and symbols in lyrics into the words a singer would say.

    "I'm 24 & it's 2am"   ->  "I'm twenty-four and it's two am"
    "24살 100%"            ->  "이십사살 백퍼센트" (read out by the romanizer afterwards)
    "1999"                ->  "nineteen ninety-nine"
    "♪ ♪ ♪"               ->  (removed)

The language of the number words follows the line: Korean, Japanese,
Chinese, Arabic and Russian lines get numbers in that language (so the
romanizer can then read them out); everything else gets English words.
Decorative symbols (music notes, hearts, stars, emoji, brackets...) are just
dropped, since a singer doesn't say them.
"""

import re
import unicodedata

from romanize.detect import detect_script

# ===================================================================== English

_EN_ONES = ("zero one two three four five six seven eight nine ten eleven twelve "
            "thirteen fourteen fifteen sixteen seventeen eighteen nineteen").split()
_EN_TENS = "  twenty thirty forty fifty sixty seventy eighty ninety".split(" ")
_EN_TENS = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"]

def _en_below_1000(n):
    if n < 20:
        return _EN_ONES[n]
    if n < 100:
        t, o = divmod(n, 10)
        return _EN_TENS[t] + ("-" + _EN_ONES[o] if o else "")
    h, r = divmod(n, 100)
    return _EN_ONES[h] + " hundred" + (" " + _en_below_1000(r) if r else "")

def en_words(n):
    if n < 1000:
        return _en_below_1000(n)
    parts = []
    for val, name in ((10**12, "trillion"), (10**9, "billion"), (10**6, "million"), (1000, "thousand")):
        if n >= val:
            parts.append(en_words(n // val) + " " + name)
            n %= val
    if n:
        parts.append(_en_below_1000(n))
    return " ".join(parts)

def en_year(n):
    hi, lo = divmod(n, 100)
    if lo == 0:
        return en_words(hi) + " hundred"
    if lo < 10:
        return en_words(hi) + " oh " + en_words(lo)
    return en_words(hi) + " " + en_words(lo)

_ORD_IRREG = {"one": "first", "two": "second", "three": "third", "five": "fifth",
              "eight": "eighth", "nine": "ninth", "twelve": "twelfth"}

def en_ordinal(n):
    words = en_words(n)
    head, sep, last = re.split(r"([ -])(?=[^ -]+$)", words, maxsplit=1) if re.search(r"[ -]", words) else ("", "", words)
    if last in _ORD_IRREG:
        last = _ORD_IRREG[last]
    elif last.endswith("y"):
        last = last[:-1] + "ieth"
    else:
        last += "th"
    return head + sep + last

_EN_DECADES = {20: "twenties", 30: "thirties", 40: "forties", 50: "fifties",
               60: "sixties", 70: "seventies", 80: "eighties", 90: "nineties"}

# ===================================================================== Korean

_KO_DIGITS = "영일이삼사오육칠팔구"
_KO_NATIVE_ATTR = ["", "한", "두", "세", "네", "다섯", "여섯", "일곱", "여덟", "아홉"]
_KO_NATIVE_TENS = ["", "열", "스물", "서른", "마흔", "쉰", "예순", "일흔", "여든", "아흔"]
# Counters that take native Korean numbers (한 개, 두 명, 세 살...)
_KO_NATIVE_COUNTERS = ("개", "명", "살", "마리", "번", "시", "권", "잔", "병", "장", "대", "곳")

def _ko_group(g):
    out = ""
    for d, p in zip((g // 1000, g // 100 % 10, g // 10 % 10, g % 10), ("천", "백", "십", "")):
        if d == 0:
            continue
        out += (p if (d == 1 and p) else _KO_DIGITS[d] + p)
    return out

def ko_sino(n):
    if n == 0:
        return "영"
    out = ""
    for val, name in ((10**12, "조"), (10**8, "억"), (10**4, "만")):
        if n >= val:
            q = n // val
            n %= val
            out += ("" if (q == 1 and name == "만") else _ko_group(q)) + name
    return out + (_ko_group(n) if n else "")

def ko_native(n):
    """1-99 in native Korean, in the form used before a counter word."""
    if n == 20:
        return "스무"
    t, o = divmod(n, 10)
    return _KO_NATIVE_TENS[t] + _KO_NATIVE_ATTR[o]

# ================================================================ Japanese/Chinese

_JA_DIGITS = "〇一二三四五六七八九"
_ZH_DIGITS = "零一二三四五六七八九"

def _cjk_group(g, digits, zero, omit_one):
    s = ""
    started = pending = False
    for d, p in zip((g // 1000, g // 100 % 10, g // 10 % 10, g % 10), ("千", "百", "十", "")):
        if d == 0:
            if started:
                pending = True
            continue
        if pending and zero:
            s += zero
        started = True
        pending = False
        s += p if (d == 1 and p and omit_one) else digits[d] + p
    return s

def _cjk_int(n, digits, zero, omit_one, big):
    if n == 0:
        return digits[0]
    if zero and 10 <= n < 20:              # Chinese: 十一, not 一十一
        return "十" + (digits[n - 10] if n > 10 else "")
    groups = []
    while n:
        groups.append(n % 10000)
        n //= 10000
    out = ""
    gap = False
    for gi in range(len(groups) - 1, -1, -1):
        g = groups[gi]
        if g == 0:
            gap = True
            continue
        s = _cjk_group(g, digits, zero, omit_one)
        if zero and out and (gap or g < 1000):
            s = zero + s
        out += s + big[gi]
        gap = False
    return out

def ja_words(n):
    return _cjk_int(n, _JA_DIGITS, "", True, ("", "万", "億", "兆"))

def zh_words(n):
    return _cjk_int(n, _ZH_DIGITS, "零", False, ("", "万", "亿", "兆"))

# ======================================================================= Arabic

_AR_ONES = ["صفر", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة",
            "عشرة", "أحد عشر", "اثنا عشر", "ثلاثة عشر", "أربعة عشر", "خمسة عشر", "ستة عشر",
            "سبعة عشر", "ثمانية عشر", "تسعة عشر"]
_AR_TENS = ["", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون"]
_AR_HUND = ["", "مئة", "مئتان", "ثلاثمئة", "أربعمئة", "خمسمئة", "ستمئة", "سبعمئة", "ثمانمئة", "تسعمئة"]

def ar_words(n):
    if n < 20:
        return _AR_ONES[n]
    if n < 100:
        t, o = divmod(n, 10)
        return (_AR_ONES[o] + " و" + _AR_TENS[t]) if o else _AR_TENS[t]
    if n < 1000:
        h, r = divmod(n, 100)
        return _AR_HUND[h] + (" و" + ar_words(r) if r else "")
    th, r = divmod(n, 1000)
    if th == 1:
        s = "ألف"
    elif th == 2:
        s = "ألفان"
    elif th <= 10:
        s = _AR_ONES[th] + " آلاف"
    else:
        s = ar_words(th) + " ألف"
    return s + (" و" + ar_words(r) if r else "")

# ====================================================================== Russian

_RU_ONES = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
            "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
            "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"]
_RU_TENS = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят",
            "восемьдесят", "девяносто"]
_RU_HUND = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот",
            "восемьсот", "девятьсот"]

def _ru_plural(n, forms):
    if 11 <= n % 100 <= 19:
        return forms[2]
    if n % 10 == 1:
        return forms[0]
    if 2 <= n % 10 <= 4:
        return forms[1]
    return forms[2]

def _ru_below_1000(n, fem=False):
    parts = []
    h, r = divmod(n, 100)
    if h:
        parts.append(_RU_HUND[h])
    if r >= 20:
        t, o = divmod(r, 10)
        parts.append(_RU_TENS[t])
        if o:
            parts.append(("одна", "две")[o - 1] if (fem and o in (1, 2)) else _RU_ONES[o])
    elif r:
        parts.append(("одна", "две")[r - 1] if (fem and r in (1, 2)) else _RU_ONES[r])
    return " ".join(parts)

def ru_words(n):
    if n == 0:
        return "ноль"
    parts = []
    for val, forms, fem in ((10**9, ("миллиард", "миллиарда", "миллиардов"), False),
                            (10**6, ("миллион", "миллиона", "миллионов"), False),
                            (10**3, ("тысяча", "тысячи", "тысяч"), True)):
        q = (n // val) % 1000
        if q:
            parts.append(_ru_below_1000(q, fem) + " " + _ru_plural(q, forms))
    if n % 1000:
        parts.append(_ru_below_1000(n % 1000))
    return " ".join(parts)

# ================================================================ dispatch tables

_DIGIT_NAMES = {
    "en": _EN_ONES[:10], "ko": list(_KO_DIGITS), "ja": list(_JA_DIGITS), "zh": list(_ZH_DIGITS),
    "ar": _AR_ONES[:10], "ru": _RU_ONES[:10],
}
_INT_WORDS = {"en": en_words, "ko": ko_sino, "ja": ja_words, "zh": zh_words, "ar": ar_words, "ru": ru_words}
_POINT = {"en": " point ", "ko": "점", "ja": "点", "zh": "点", "ar": " فاصلة ", "ru": " запятая "}
_SPACED = {"en", "ar", "ru"}   # languages that separate words with spaces
_MAX_INT = {"en": 10**15, "ko": 10**16, "ja": 10**16, "zh": 10**16, "ar": 10**6, "ru": 10**12}

# symbol -> word, per language
_SYMBOLS = {
    "en": {"&": "and", "+": "plus", "=": "equals", "@": "at", "%": "percent", "×": "times",
           "÷": "divided by", "°": "degrees", "$": "dollars", "€": "euros", "£": "pounds",
           "¥": "yen", "₩": "won", "#": "hashtag"},
    "ko": {"&": "앤", "+": "플러스", "=": "이퀄", "@": "앳", "%": "퍼센트", "×": "곱하기",
           "÷": "나누기", "°": "도", "$": "달러", "€": "유로", "£": "파운드", "¥": "엔",
           "₩": "원", "#": "샵"},
    "ja": {"&": "アンド", "+": "プラス", "=": "イコール", "@": "アット", "%": "パーセント",
           "×": "かける", "÷": "わる", "°": "度", "$": "ドル", "€": "ユーロ", "£": "ポンド",
           "¥": "円", "₩": "ウォン", "#": "ハッシュタグ"},
    "zh": {"&": "和", "+": "加", "=": "等于", "@": "艾特", "%": "百分之", "×": "乘",
           "÷": "除以", "°": "度", "$": "美元", "€": "欧元", "£": "英镑", "¥": "元",
           "₩": "韩元", "#": "井号"},
    "ar": {"&": "و", "+": "زائد", "=": "يساوي", "@": "آت", "%": "بالمئة", "×": "ضرب",
           "÷": "قسمة", "°": "درجة", "$": "دولار", "€": "يورو", "£": "جنيه", "¥": "ين",
           "₩": "وون", "#": "هاشتاق"},
    "ru": {"&": "и", "+": "плюс", "=": "равно", "@": "эт", "%": "процентов",
           "×": "умножить на", "÷": "разделить на", "°": "градусов", "$": "долларов",
           "€": "евро", "£": "фунтов", "¥": "иен", "₩": "вон", "#": "хэштег"},
}
_NUMBER_WORD = {"en": "number", "ko": "번", "ja": "ナンバー", "zh": "第", "ar": "رقم", "ru": "номер"}

_CURRENCY = "$€£¥₩"
_NUM = r"\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?"

# Things that are purely decorative and never spoken.
_DECOR = set("♪♫♬♩♭♯♥♡❤❥★☆✦✧✩✪✫✬✭✮✯✰●○◆◇■□▲△▼▽◎◉※→←↑↓⇒⇐"
             "～〜~・·•^*|\\_`【】「」『』《》〈〉（）"
             "[]{}<>“”‘’")
_SPACE_LIKE = set("／/＼\\|")

_EN_SINGULAR = {"$": "dollar", "€": "euro", "£": "pound", "¥": "yen", "₩": "won", "%": "percent", "°": "degree"}
# Russian noun forms for 1 / 2-4 / 5+ (the euro doesn't decline)
_RU_UNITS = {"$": ("доллар", "доллара", "долларов"), "€": ("евро",) * 3, "£": ("фунт", "фунта", "фунтов"),
             "¥": ("иена", "иены", "иен"), "₩": ("вона", "воны", "вон"),
             "%": ("процент", "процента", "процентов"), "°": ("градус", "градуса", "градусов")}

def _unit_word(symbol, number_text, lang, sym):
    """Spoken word for a currency/percent symbol that follows `number_text`."""
    digits = number_text.replace(",", "")
    is_int = digits.isdigit()
    if lang == "en" and is_int and int(digits) == 1 and symbol in _EN_SINGULAR:
        return _EN_SINGULAR[symbol]
    if lang == "ru" and symbol in _RU_UNITS:
        forms = _RU_UNITS[symbol]
        return _ru_plural(int(digits), forms) if is_int else forms[1]   # 2.5 -> "два целых пять десятых процента"
    return sym[symbol]

def _lang_for(text, forced=None):
    if forced:
        return forced if forced in _INT_WORDS else "en"
    code = detect_script(text)
    return code if code in ("ko", "ja", "zh", "ar", "ru") else "en"

def line_language(text):
    """Language code normalize() would use for this line."""
    return _lang_for(text)


def _digits_only(s, lang):
    names = _DIGIT_NAMES[lang]
    words = [names[int(c)] for c in s if c.isdigit()]
    return (" " if lang in _SPACED else "").join(words)

def _int_to_words(s, lang, ordinal=False, native=False, year_ok=True):
    """s: string of ASCII digits (no commas)."""
    n = int(s)
    if (len(s) > 1 and s[0] == "0") or n >= _MAX_INT[lang] or len(s) > 15:
        return _digits_only(s, lang)
    if lang == "en":
        if ordinal:
            return en_ordinal(n)
        if year_ok and len(s) == 4 and ((1100 <= n <= 1999) or (2010 <= n <= 2099) or (n % 100 == 0 and 1100 <= n < 2000)):
            return en_year(n)
        return en_words(n)
    if lang == "ko" and native and 1 <= n <= 99:
        return ko_native(n)
    return _INT_WORDS[lang](n)

def _number_to_words(token, lang, following="", ordinal=False):
    token = token.replace(",", "")
    if "." in token:
        whole, frac = token.split(".", 1)
        return (_int_to_words(whole, lang, year_ok=False) + _POINT[lang] + _digits_only(frac, lang))
    native = False
    if lang == "ko":
        rest = following.lstrip()
        if rest.startswith(_KO_NATIVE_COUNTERS):
            native = True
    return _int_to_words(token, lang, ordinal=ordinal, native=native)

def _ascii_digits(text):
    return "".join(str(unicodedata.decimal(c)) if unicodedata.category(c) == "Nd" and not c.isascii() else c
                   for c in text)

def normalize(text, lang=None):
    """Spell out numbers/symbols and drop decoration. `lang` overrides the
    per-line language guess ('en', 'ko', 'ja', 'zh', 'ar', 'ru')."""
    if not text:
        return text
    text = _ascii_digits(text)
    lang = _lang_for(text, lang)
    sym = _SYMBOLS[lang]
    spaced = lang in _SPACED

    # Typographic quotes/dashes -> plain ones
    text = (text.replace("‘", "'").replace("’", "'").replace("“", '"').replace("”", '"')
                .replace("—", " - ").replace("–", "-").replace("＆", "&").replace("＋", "+"))

    # "<3" is a heart
    text = re.sub(r"<3+", " ", text)

    def wrap(w):
        return f" {w} " if spaced else w

    # Currency before a number: "$5" -> "5 dollars" ("$1" -> "1 dollar"; Russian
    # declines the noun by the number: 1 доллар, 2 доллара, 5 долларов)
    text = re.sub(rf"([{_CURRENCY}])\s?({_NUM})",
                  lambda m: f"{m.group(2)} {_unit_word(m.group(1), m.group(2), lang, sym)}", text)
    # Percent
    if lang == "zh":
        text = re.sub(rf"({_NUM})\s?%", lambda m: sym["%"] + m.group(1), text)
    else:
        text = re.sub(rf"({_NUM})\s?%",
                      lambda m: m.group(1) + wrap(_unit_word("%", m.group(1), lang, sym)), text)
    # "#1" -> "number one"
    text = re.sub(r"#\s?(\d+)", lambda m: (wrap(_NUMBER_WORD[lang]) + m.group(1)) if lang != "ko" and lang != "zh"
                  else (m.group(1) + _NUMBER_WORD[lang] if lang == "ko" else _NUMBER_WORD[lang] + m.group(1)), text)

    if lang == "en":
        # 90s / '90s -> nineties ; 1st 2nd 3rd 4th -> first second third fourth
        text = re.sub(r"(?<!\d)'?(\d0)s\b", lambda m: wrap(_EN_DECADES.get(int(m.group(1)), m.group(1) + "s")), text)
        text = re.sub(r"\b(\d+)(?:st|nd|rd|th)\b", lambda m: wrap(en_ordinal(int(m.group(1)))) if len(m.group(1)) < 13 else m.group(0), text, flags=re.I)
        # clock times 3:30 -> three thirty
        def _clock(m):
            h, mi = int(m.group(1)), int(m.group(2))
            if mi == 0:
                return wrap(en_words(h) + " o'clock")
            return wrap(en_words(h) + " " + ("oh " + en_words(mi) if mi < 10 else en_words(mi)))
        text = re.sub(r"\b(\d{1,2}):(\d{2})\b", _clock, text)
    else:
        text = re.sub(r"(?<=\d):(?=\d)", " ", text)

    # 24/7 style slashes between digits become a space
    text = re.sub(r"(?<=\d)[/／](?=\d)", " ", text)

    # Cardinal numbers
    def _num(m):
        words = _number_to_words(m.group(0), lang, following=m.string[m.end():m.end() + 4])
        return wrap(words)
    text = re.sub(_NUM, _num, text)

    # Remaining symbols with a spoken form
    def _sym(m):
        return wrap(sym[m.group(0)])
    text = re.sub("[" + re.escape("".join(k for k in sym if k != "#")) + "]", _sym, text)
    text = text.replace("#", wrap(sym["#"]) if re.search(r"#\w", text) else " ")

    # Decoration and emoji out, separators to spaces
    out = []
    for ch in text:
        if ch in _SPACE_LIKE:
            out.append(" ")
        elif ch in _DECOR:
            continue
        elif ch in "\ufe0f\u200b\u200c\u200d":
            continue
        elif unicodedata.category(ch) in ("So", "Sk", "Cs", "Co") and ch not in "°":
            continue
        else:
            out.append(ch)
    text = "".join(out)
    text = re.sub(r"\s+", " ", text).strip()
    text = re.sub(r"\s+([,.!?;:])", r"\1", text)
    return text
