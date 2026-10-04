"""Sanity-check the lyrics a source returned before they are shown.

Why this exists: a lookup can answer with something that *looks* like lyrics but is
not the song's words: a "no lyrics / pure music" placeholder, a Chinese translation
instead of the original, or the lyrics of a longer edition. Romanized, those come out
as pinyin for a Korean or English song. Showing nothing beats showing the wrong song.

Everything here is pure (no network, no GUI) so it can be tested directly.
Decisions are returned as notes and logged by the caller.

The checks, in order:
  1. Placeholders ("纯音乐，请欣赏", "instrumental", ...) are thrown away.
  2. Lyrics that run well past the end of the song are thrown away (another edition).
  3. Chinese-only lyrics for a song whose title/artist are Korean or Japanese, or English
     lyrics for a title marked as the Chinese edition, are thrown away (a translation).
  4. Bilingual lines (original + translation stamped at the same time) are cut down to
     the song's own language.
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
# A script counts as "present" in a song when at least this share of its lines use it.
PRESENT_SHARE = 0.25

class Candidate:
    """The lyrics under test."""
    __slots__ = ("synced", "profile", "main")

    def __init__(self, synced):
        self.synced = synced
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
    """The reason its lyrics are probably a translation, or None. Only
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


def strip_translation_lines(synced, keep_script):
    """Remove translation lines from a bilingual LRC: lines stamped at (almost)
    the same time in different scripts are the original plus its translation,
    so keep the one in `keep_script`. Returns (synced, removed_count)."""
    if not synced:
        return synced, 0
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
        return synced, 0
    return [x for k, x in enumerate(synced) if k not in drop], len(drop)


def check(synced, plain, title="", artist="", duration_ms=None):
    """Judge one source's lyrics. Returns (synced, plain, notes): the lyrics to use (bilingual
    translation lines removed), or (None, None, notes) when they should not be shown. `notes`
    explain every rejection for the log."""
    notes = []
    if not synced or is_placeholder(synced):
        notes.append("only a 'no lyrics / pure music' placeholder (%r) - ignored"
                     % (synced[0][1][:40] if synced else ""))
        return None, None, notes
    c = Candidate(synced)
    if _overruns(c, duration_ms):
        notes.append("last line is at %.0fs and the song is %.0fs long - another edition, ignored"
                     % (c.synced[-1][0], duration_ms / 1000))
        return None, None, notes
    why = _wrong_script(c, title, artist)
    if why:
        notes.append("%s - ignored" % why)
        return None, None, notes
    kept, removed = strip_translation_lines(c.synced, c.main)
    if removed:
        notes.append("removed %d translation line(s) that shared a timestamp with the original" % removed)
    return kept, plain, notes
