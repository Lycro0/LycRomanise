"""Cross-check the lyrics the three sources returned and pick the right ones.

Why this exists: a source can answer with something that *looks* like lyrics
but is not the song's words - the "pure music, please enjoy" placeholder QQ
and NetEase show for instrumentals (a search for "House of Cards (Full Length
Edition)" can land on such an entry), a Chinese translation instead of the
original, or the lyrics of another edition. Romanized, those come out as
pinyin for a Korean or English song.

Everything here is pure (no network, no GUI) so it can be tested directly.
Decisions are returned as notes and logged by the caller to the log file.

The rules, in order:
  1. Placeholders ("纯音乐，请欣赏", "instrumental", ...) are thrown away.
  2. Lyrics whose script does not fit the other sources (Chinese lines next
     to Korean or English ones) are thrown away when the sources can be
     compared; if only one source is left there is nothing to compare with.
  3. Lyrics that run well past the end of the song are thrown away when a
     source that fits exists.
  4. Of three sources, one that shares no wording with two that agree with each
     other is thrown away (a different song or edition).
  5. The first survivor in the caller's preference order wins. Bilingual lines
     (original + translation stamped at the same time) are cut down to the
     song's own language.
"""

import logging
import re

from romanize.detect import detect_script

log = logging.getLogger("spoti.lyrics")

# A line of lyrics that is really "there are no lyrics" in some language.
PLACEHOLDER_RE = re.compile(
    r"纯音乐|純音樂|純音乐|请欣赏|請欣賞|没有填词|沒有填詞|没有歌词|沒有歌詞|暂无歌词|暫無歌詞|无歌词|無歌詞|"
    r"此歌曲为|此歌曲為|歌词加载中|歌詞なし|インスト|インストゥルメンタル|악기\s*연주|연주곡|"
    r"\binstrumental\b|\bno lyrics\b|\bmusic only\b|\bnot available\b|\blyrics not (?:found|available)\b",
    re.I)

# How far past the song's own length the last lyric line may start before the
# lyrics are treated as belonging to a longer edition.
OVERRUN_S = 8.0
# Word/character overlap (0..1) above which two lyric sets count as "the same words".
SAME_WORDS = 0.35
# A script counts as "present" in a song when at least this share of its lines use it.
PRESENT_SHARE = 0.25

# When the sources are split and nothing else decides: prefer the language a
# song is most likely to be *originally* in over its translations (Chinese
# translations are by far the commonest wrong match for Korean, Japanese and
# English songs).
ORIGINAL_ORDER = ("ko", "ja", "latin", "ru", "el", "he", "ar", "hi", "zh")


class Candidate:
    """Lyrics from one source."""
    __slots__ = ("name", "synced", "plain", "words", "profile", "main")

    def __init__(self, name, synced, plain=None, words=None):
        self.name, self.synced, self.plain, self.words = name, synced, plain, words
        self.profile = script_profile(synced)
        self.main = main_script(self.profile)


def line_script(text):
    """'ko' | 'ja' | 'zh' | ... | 'latin' for a line with letters, else None
    (a line of only symbols such as a music note)."""
    code = detect_script(text)
    if code:
        return code
    return "latin" if any(ch.isalpha() for ch in text or "") else None


def script_profile(synced):
    """{script: share of the lyric lines that use it} (shares add up to 1)."""
    counts = {}
    for _ts, text in synced or []:
        s = line_script(text)
        if s:
            counts[s] = counts.get(s, 0) + 1
    total = sum(counts.values())
    return {s: n / total for s, n in counts.items()} if total else {}


def main_script(profile):
    return max(profile, key=profile.get) if profile else None


def is_placeholder(synced):
    """True for a 'no lyrics' stand-in: a handful of lines, most of them
    'pure music' style notices. A real song with the word 'instrumental' in one
    line is not affected."""
    lines = [text for _ts, text in synced or [] if text and text.strip()]
    if not lines:
        return True
    hits = sum(1 for t in lines if PLACEHOLDER_RE.search(t))
    if not hits:
        return False
    return len(lines) <= 4 or hits / len(lines) >= 0.5


def _tokens(synced):
    """Words (or, for Chinese/Japanese, character pairs) to compare wording with."""
    text = " ".join(t for _ts, t in synced or []).casefold()
    text = re.sub(r"[^\w\s]", " ", text)
    if re.search(r"[\u3040-\u30ff\u3400-\u9fff]", text):
        chars = re.sub(r"\s+", "", text)
        return {chars[i:i + 2] for i in range(len(chars) - 1)}
    return set(text.split())


def similarity(a, b):
    """Jaccard overlap (0..1) of the wording of two lyric sets."""
    ta, tb = _tokens(a), _tokens(b)
    if not ta or not tb:
        return 0.0
    return len(ta & tb) / len(ta | tb)


def compatible(a, b):
    """Could these two be the same song's lyrics as far as script goes? Yes if
    their main scripts match, or one's main script is a real part of the other
    (a K-pop song whose English lines make one source look mostly English)."""
    if not a.main or not b.main:
        return True
    if a.main == b.main:
        return True
    return (b.profile.get(a.main, 0) >= PRESENT_SHARE) or (a.profile.get(b.main, 0) >= PRESENT_SHARE)


def _overruns(c, duration_ms):
    if not duration_ms or not c.synced:
        return False
    return c.synced[-1][0] > duration_ms / 1000 + OVERRUN_S


_TAG_SCRIPT = {"en": "latin", "zh": "zh", "ko": "ko", "ja": "ja"}


def _title_script(title, artist):
    """The script the title asks for: an explicit edition tag ("(English Version)", "中文版")
    first, else the script of the title/artist text (Korean/Japanese/Chinese), else None."""
    from lyrics_provider import language_tag       # local import: lyrics_provider imports this module
    tag = language_tag(title)
    if tag:
        return _TAG_SCRIPT[tag]
    return detect_script("%s %s" % (title, artist))


def _wrong_script(c, title, artist):
    """For a lone source: the reason its lyrics are probably a translation, or None. Only
    the clear case counts - the song's title/artist are Korean or Japanese but the lyrics
    are (almost) all Chinese, the typical Chinese-translation mismatch. English titles
    are never judged (plenty of Mandarin songs have them)."""
    tagged = _title_script(title, "")
    if tagged == "zh" and c.main == "latin" and c.profile.get("zh", 0) < PRESENT_SHARE:
        return "the title says it is the Chinese edition but the lyrics are English (another edition)"
    hint = detect_script("%s %s" % (title, artist))
    if hint in ("ko", "ja") and c.main == "zh" and c.profile.get(hint, 0) < PRESENT_SHARE:
        return "the title/artist are %s but the lyrics are Chinese (a translation)" % hint
    return None


def strip_translation_lines(synced, words, keep_script):
    """Remove translation lines from a bilingual LRC: lines stamped at (almost)
    the same time in different scripts are the original plus its translation,
    so keep the one in `keep_script`. Returns (synced, words, removed_count);
    `words` stays parallel to `synced` (None stays None)."""
    if not synced:
        return synced, words, 0
    drop = set()
    i, n = 0, len(synced)
    while i < n:
        j = i + 1
        while j < n and synced[j][0] - synced[i][0] <= 0.05:
            j += 1
        if j - i > 1:
            group = list(range(i, j))
            scripts = {k: line_script(synced[k][1]) for k in group}
            if len({s for s in scripts.values() if s}) > 1:
                same = [k for k in group if scripts[k] == keep_script]
                keep = set(same) if same else {group[0]}
                drop.update(k for k in group if k not in keep and scripts[k] is not None)
        i = j
    if not drop:
        return synced, words, 0
    kept_s = [x for k, x in enumerate(synced) if k not in drop]
    kept_w = [x for k, x in enumerate(words) if k not in drop] if words and len(words) == len(synced) else words
    return kept_s, kept_w, len(drop)


def choose(results, order, title="", artist="", duration_ms=None):
    """Pick the lyrics to show.

    results  {source name: (synced, plain, words)} for sources that had lyrics
    order    source names, most preferred first
    Returns (Candidate or None, [notes explaining every rejection]).
    """
    notes = []
    cands = []
    for name in order:
        res = results.get(name)
        if not res or not res[0]:
            continue
        synced, plain, words = (tuple(res) + (None, None))[:3]
        if is_placeholder(synced):
            notes.append("%s: only a 'no lyrics / pure music' placeholder (%r) - ignored"
                         % (name, synced[0][1][:40] if synced else ""))
            continue
        cands.append(Candidate(name, synced, plain, words))
    if not cands:
        return None, notes
    if len(cands) == 1:
        c = cands[0]
        # Nothing to compare with, but two checks need no second source.
        if _overruns(c, duration_ms):
            notes.append("%s: only source, but its last line is at %.0fs and the song is %.0fs long - "
                         "another edition, ignored" % (c.name, c.synced[-1][0], duration_ms / 1000))
            return None, notes
        why = _wrong_script(c, title, artist)
        if why:
            notes.append("%s: only source, but %s - ignored" % (c.name, why))
            return None, notes
        notes.append("%s: only source with usable lyrics, nothing to cross-check against" % c.name)
        return _finish(c, notes), notes

    # 2. script must fit the other sources. A source's support = itself + the
    # sources it is compatible with; the best-supported script wins, and ties
    # go to the song's own script (from title/artist) then the original-language order.
    hint = _title_script(title, artist)

    def rank(c):
        if hint and c.main == hint:
            return -1
        return ORIGINAL_ORDER.index(c.main) if c.main in ORIGINAL_ORDER else len(ORIGINAL_ORDER)

    support = {c.name: 1 + sum(1 for o in cands if o is not c and compatible(c, o)) for c in cands}
    best = max(support.values())
    leaders = [c for c in cands if support[c.name] == best]
    lead = min(leaders, key=lambda c: (rank(c), order.index(c.name)))
    kept = []
    for c in cands:
        if c is lead or compatible(c, lead):
            kept.append(c)
        else:
            notes.append("%s: lyrics are mostly %s but %s reads as %s - looks like a translation or another "
                         "song, ignored" % (c.name, c.main, lead.name, lead.main))
    cands = kept

    # 3. lyrics longer than the song
    fits = [c for c in cands if not _overruns(c, duration_ms)]
    if fits and len(fits) < len(cands):
        for c in cands:
            if c not in fits:
                notes.append("%s: last line at %.0fs but the song is %.0fs long - another edition, ignored"
                             % (c.name, c.synced[-1][0], duration_ms / 1000))
        cands = fits

    # 4. with three left, drop one that shares no wording with two that agree
    if len(cands) >= 3:
        agree = {c.name: [o.name for o in cands if o is not c and similarity(c.synced, o.synced) >= SAME_WORDS]
                 for c in cands}
        odd = [c for c in cands if not agree[c.name]]
        pair = [c for c in cands if agree[c.name]]
        if len(odd) == 1 and len(pair) == len(cands) - 1:
            notes.append("%s: wording differs from the other sources, which agree with each other - ignored"
                         % odd[0].name)
            cands = pair
    elif len(cands) == 2:
        sim = similarity(cands[0].synced, cands[1].synced)
        if sim < 0.2:
            notes.append("%s and %s disagree on the wording (overlap %.0f%%) and there is no third source "
                         "to settle it; using %s by preference" % (cands[0].name, cands[1].name, sim * 100,
                                                                    min(cands, key=lambda c: order.index(c.name)).name))

    # 5. preferred survivor
    win = min(cands, key=lambda c: order.index(c.name))
    if len(cands) > 1:
        notes.append("%s: agreed with %s, chosen by source preference" % (win.name, ", ".join(
            o.name for o in cands if o is not win)))
    return _finish(win, notes), notes


def _finish(c, notes):
    """Apply the bilingual clean-up to the winner."""
    synced, words, removed = strip_translation_lines(c.synced, c.words, c.main)
    if removed:
        notes.append("%s: removed %d translation line(s) that shared a timestamp with the original" % (c.name, removed))
        c = Candidate(c.name, synced, c.plain, words)
    return c
