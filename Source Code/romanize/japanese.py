"""Japanese -> Hepburn romaji, using the pykakasi library plus fixes for what pykakasi gets wrong in
song lyrics:

  * particles: は read "wa", へ read "e", を read "o" (pykakasi has no grammar, so it says ha / he / wo)
  * kanji read out of context: a short list of very common words and single kanji whose first
    dictionary reading is the wrong one for lyrics (今日 kyou, 君 kimi, 私 watashi, 夢 yume, ...)
  * long vowels from the prolonged-sound mark: コーヒー -> koohii, not "ko-hi-"
  * full-width letters, digits and punctuation are made ordinary first (NFKC)

pykakasi can't tell a particle は from a は inside a word without a dictionary of grammar, so the
particle rule only fires where it is safe: は at the end of a run of hiragana (これは, あなたは,
こんにちは) and not for laughs like あはは. Everything else is left as pykakasi wrote it.
"""

import re
import unicodedata

import pykakasi

_kks = pykakasi.kakasi()

# pykakasi.convert() segments a line into one chunk per underlying word/particle. A chunk whose
# *original* text is pure punctuation (or whitespace already in the source) stays glued to the
# previous word - "sky!" not "sky !" - everything else gets a space before it.
_PUNCT_ONLY_RE = re.compile(r"^[^\w]+$", re.UNICODE)
_HIRAGANA_RUN_RE = re.compile(r"^[\u3041-\u309f]+$")
_KANJI = "\u4e00-\u9fff\u3400-\u4dbf"

# Whole words whose default reading is wrong far too often in lyrics.
_WORDS = {
    "今日": "きょう", "明日": "あした", "昨日": "きのう", "今夜": "こんや", "今朝": "けさ",
    "今年": "ことし", "大人": "おとな", "一人": "ひとり", "二人": "ふたり", "友達": "ともだち",
    "彼女": "かのじょ", "本当": "ほんとう", "一緒": "いっしょ", "時々": "ときどき", "景色": "けしき",
    "何故": "なぜ", "今更": "いまさら", "素敵": "すてき", "真っ直ぐ": "まっすぐ", "永遠": "えいえん",
    "運命": "うんめい", "未来": "みらい", "世界": "せかい", "大丈夫": "だいじょうぶ",
}
# Single kanji read as the everyday word. Only used when the kanji stands alone: not next to another
# kanji (a compound) and not followed by kana that continue a word (光る, 愛する) - only by a particle.
_SINGLE = {
    "君": "きみ", "僕": "ぼく", "私": "わたし", "俺": "おれ", "愛": "あい", "夢": "ゆめ",
    "心": "こころ", "空": "そら", "涙": "なみだ", "声": "こえ", "手": "て", "目": "め", "夜": "よる",
    "風": "かぜ", "光": "ひかり", "花": "はな", "歌": "うた", "嘘": "うそ", "恋": "こい",
    "海": "うみ", "雨": "あめ", "月": "つき", "星": "ほし", "雪": "ゆき", "道": "みち", "今": "いま",
    "貴方": "あなた",
}
_PARTICLE_STARTS = "がはをにのでともへやか"

_WORDS_RE = re.compile("|".join(sorted(map(re.escape, _WORDS), key=len, reverse=True)))
_SINGLE_RE = re.compile(
    "(?<![%s])(%s)(?![%s])(?=$|[^\u3041-\u309f]|[%s])"
    % (_KANJI, "|".join(map(re.escape, _SINGLE)), _KANJI, _PARTICLE_STARTS)
)


def _apply_readings(text):
    """Swap the words/kanji above for kana so pykakasi reads them the way a song does."""
    text = _WORDS_RE.sub(lambda m: _WORDS[m.group(0)], text)
    return _SINGLE_RE.sub(lambda m: _SINGLE[m.group(1)], text)


def _hepburn(text):
    return "".join(item.get("hepburn", "") for item in _kks.convert(text))


def _fix_run(orig, hep):
    """Particle fixes for a run of hiragana `orig` that pykakasi read as `hep`."""
    if "を" in orig:                                     # を is only ever the particle: "o"
        pieces = orig.split("を")
        out = []
        for i, piece in enumerate(pieces):
            if piece:
                out.append(_fix_run(piece, _hepburn(piece)))
            if i < len(pieces) - 1:
                out.append("o")
        return " ".join(out)
    if len(orig) == 1 and orig == "は":
        return "wa"
    if len(orig) == 1 and orig == "へ":
        return "e"
    if orig.endswith("は") and len(orig) > 1 and hep.endswith("ha"):
        head = orig[:-1]
        if not re.fullmatch("[あはわ]+", head):          # あはは / ははは: laughs, not a particle
            return hep[:-2] + "wa"
    if orig.endswith("へ") and len(orig) > 1 and hep.endswith("he"):
        return hep[:-2] + "e"
    return hep


def _long_vowels(orig, hep):
    """ー (prolonged sound) comes out as '-': repeat the vowel before it instead."""
    if "ー" in orig and "-" in hep:
        return re.sub(r"([aiueo])-", r"\1\1", hep)
    return hep


def romanize(text: str) -> str:
    text = unicodedata.normalize("NFKC", text)
    text = _apply_readings(text)
    parts = []
    for item in _kks.convert(text):
        orig = item.get("orig", "") or ""
        piece = item.get("hepburn", "")
        if not piece:
            continue
        if _HIRAGANA_RUN_RE.match(orig):
            piece = _fix_run(orig, piece)
        piece = _long_vowels(orig, piece)
        if parts and not _PUNCT_ONLY_RE.match(orig):
            parts.append(" ")
        parts.append(piece)
    return "".join(parts)
