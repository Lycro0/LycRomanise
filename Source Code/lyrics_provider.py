"""LycRomanise lyric lookup.

Synced lyrics come from LRCLIB (https://lrclib.net), a free, open,
community-maintained lyrics database that needs no API key. Coverage is not
universal (it is community contributed), so some tracks, especially less
mainstream K-pop/J-pop/C-pop releases, may simply not exist there.

SOURCES lists the online lookups in order of preference; the first one that returns
usable lyrics wins and the rest are not asked. LRCLIB is the only one today,
a backup service can be added by writing one function and listing it there.
Your own .lrc files (see lyrics_folders) are always looked at first.
"""

import atexit
import json
import logging
import os
import re
import threading
import time
import unicodedata
from collections import OrderedDict

import requests
from concurrent.futures import Future

import lyrics_check
from paths import write_json_atomic
from romanize.detect import detect_script

LRCLIB_GET = "https://lrclib.net/api/get"
LRCLIB_SEARCH = "https://lrclib.net/api/search"
# LRCLIB asks clients to identify themselves.
LRCLIB_HEADERS = {"User-Agent": "LycRomanise (https://github.com/Lycro0/LycRomanise)"}

log = logging.getLogger("lycro.lyrics")

# A search hit whose length is within this of the Spotify track is a safe match.
MAX_DURATION_MISMATCH_MS = 6000
# Slightly different edits (album vs. video version, a few seconds of intro or outro) are
# still the same song, so a hit up to this far off is used as a second choice, but only when
# its title, artist and edition (live, remix, language version) match exactly. Further off it is another recording and is rejected.
# (The lyrics themselves are also checked against the song length in lyrics_check.py.)
LOOSE_DURATION_MISMATCH_MS = 30000
# (connect, read) seconds for every lyrics request. Short on purpose: a slow
# site must not hold up a song change.
TIMEOUT = (2, 4)
# LRCLIB gets this much time in total across its retries, so a miss does not hold up the song.
LRCLIB_BUDGET_S = 5.0

from paths import DATA_DIR as _APP_DIR
CACHE_PATH = os.path.join(_APP_DIR, "lyrics_cache.json")
CACHE_MAX_ENTRIES = 300
# Bump when the way lyrics are chosen changes: older cache entries are then ignored and
# looked up again.
CACHE_VERSION = 10   # 10: instrumental markers, offset-aware parser; 9: no language preference, exact-length entries skip the overrun check; 8: Korean-first tie-break (removed); 7: language-edition tie-break; 6: lookups made with a stale song length are dropped; 5: LRCLIB only, no word timing (v1.2.0); 4: cleaned-title retries; 3: language tags

# --- provider health -------------------------------------------------------------
# A source that keeps failing at the network level (timeouts, HTTP 5xx/403/429) is
# not even asked for BREAKER_PAUSE_S. Remembered in a small file, so a dead site costs
# nothing on the first song after a restart either. After the pause one failure re-opens it.
BREAKER_FAILS = 3
BREAKER_PAUSE_S = 60.0
HEALTH_PATH = os.path.join(_APP_DIR, "provider_health.json")
_health = None
_health_lock = threading.Lock()


def _health_state():
    """Load once. {"fails": {src: n}, "down_until": {src: epoch_s}}"""
    global _health
    if _health is None:
        try:
            with open(HEALTH_PATH, "r", encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError):
            data = {}
        if not isinstance(data, dict):
            data = {}
        for key in ("fails", "down_until"):
            if not isinstance(data.get(key), dict):
                data[key] = {}
        _health = data
    return _health


def _health_save():
    try:
        write_json_atomic(HEALTH_PATH, _health)
    except OSError as exc:
        log.warning("couldn't write provider health file: %s", exc)


_last_outage = 0.0      # monotonic time of the latest network-level failure (HTTP 5xx/403/429, timeout)


def outage_since(t):
    """True if any lyrics site failed at the network level after monotonic time t, so an empty
    result may just mean "couldn't ask", not "doesn't exist"."""
    return _last_outage >= t


def _record_result(source, ok):
    """Count one finished call for the breaker. ok=False = a network-level failure."""
    with _health_lock:
        h = _health_state()
        fails = h["fails"]
        if ok:
            if fails.get(source):
                fails[source] = 0
                h["down_until"].pop(source, None)
                _health_save()
            return
        global _last_outage
        _last_outage = time.monotonic()
        fails[source] = fails.get(source, 0) + 1
        if fails[source] >= BREAKER_FAILS:
            h["down_until"][source] = time.time() + BREAKER_PAUSE_S
            log.warning("source %s failed %d calls in a row; not asking it for the next %.0f seconds",
                        source, fails[source], BREAKER_PAUSE_S)
        _health_save()


def source_paused(source):
    """True while the breaker holds this source back."""
    with _health_lock:
        h = _health_state()
        until = h["down_until"].get(source)
        if not until:
            return False
        if time.time() < until:
            return True
        # pause over: allow a probe; a single further failure pauses it again
        h["down_until"].pop(source, None)
        h["fails"][source] = BREAKER_FAILS - 1
        _health_save()
        log.info("source %s: pause over, trying it again", source)
        return False


def _safe_json(resp):
    """resp.json() but never blows up the caller. Returns a dict on success, otherwise {}."""
    try:
        data = resp.json()
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def _make_session():
    """One shared connection pool: repeat lookups reuse the TLS connection instead of redoing the handshake."""
    from requests.adapters import HTTPAdapter
    sess = requests.Session()
    adapter = HTTPAdapter(pool_connections=4, pool_maxsize=8)
    sess.mount("https://", adapter)
    sess.mount("http://", adapter)
    return sess


_session = _make_session()


# A lyrics answer is a few hundred KB at most. A reply bigger than this, or one that trickles in for
# longer than the deadline (the read timeout only limits the gap between two chunks), is dropped
# instead of being held in memory / holding up the song.
MAX_BODY_BYTES = 4_000_000
BODY_DEADLINE_S = 10.0


def _read_body(resp):
    """Read the whole body within MAX_BODY_BYTES / BODY_DEADLINE_S and keep it on `resp` (so resp.json()
    works as usual). False when it was too big or too slow."""
    try:
        if int(resp.headers.get("Content-Length") or 0) > MAX_BODY_BYTES:
            return False
    except ValueError:
        pass
    body, deadline = bytearray(), time.monotonic() + BODY_DEADLINE_S
    for chunk in resp.iter_content(chunk_size=65536):
        body += chunk
        if len(body) > MAX_BODY_BYTES or time.monotonic() > deadline:
            return False
    resp._content, resp._content_consumed = bytes(body), True
    return True


def _http(source, method, url, ok_statuses=(), **kwargs):
    """One HTTP call with the reason for any failure logged. Returns the
    response only when the status is 200, else None."""
    try:
        resp = _session.request(method, url, timeout=TIMEOUT, stream=True, **kwargs)
    except requests.RequestException as exc:
        log.info("%s: request to %s failed: %s: %s", source, url.split("?")[0], type(exc).__name__, exc)
        _record_result(source, False)
        return None
    keep = False            # only a complete 200 answer is handed back with its connection still in use
    try:
        if resp.status_code in ok_statuses:
            _record_result(source, True)
            return resp
        if resp.status_code != 200:
            log.info("%s: %s answered HTTP %s", source, url.split("?")[0], resp.status_code)
            _record_result(source, not (resp.status_code >= 500 or resp.status_code in (403, 429)))
            return None
        try:
            complete = _read_body(resp)
        except (requests.RequestException, OSError) as exc:
            log.info("%s: reading the answer from %s failed: %s: %s", source, url.split("?")[0],
                     type(exc).__name__, exc)
            _record_result(source, False)
            return None
        if not complete:
            log.warning("%s: answer from %s was too large or too slow; ignored", source, url.split("?")[0])
            _record_result(source, False)
            return None
        _record_result(source, True)
        keep = True
        return resp
    finally:
        if not keep:
            resp.close()


def _fold_latin_accents(text):
    """"JAŸ-Z" -> "JAY-Z", "Beyoncé" -> "Beyonce". Only accents on Latin letters are dropped:
    Japanese dakuten and Korean jamo must stay intact."""
    out, base = [], ""
    for ch in unicodedata.normalize("NFD", text):
        if unicodedata.category(ch) == "Mn" and base and ord(base) < 0x250:
            continue
        out.append(ch)
        if unicodedata.category(ch) != "Mn":
            base = ch
    return unicodedata.normalize("NFC", "".join(out))


def _norm(text):
    """Comparable form of a title/artist: NFKC, casefolded, Latin accents folded, without bracketed
    extras ("(feat. X)", "[Remastered]"), a trailing " - Remaster/Live/..."
    suffix, or punctuation."""
    text = _fold_latin_accents(unicodedata.normalize("NFKC", text or "").casefold())
    text = re.sub(r"[\(\[（【].*?[\)\]）】]", " ", text)
    text = re.sub(r"\s-\s.*$", " ", text)
    text = re.sub(r"[^\w\s]", " ", text)
    return " ".join(text.split())


# Language editions: "Love Talk" and "Love Talk (English Version)" are different lyric sets that
# _norm() makes look identical (it drops brackets). The tag is read from the raw title.
_LANG_TAGS = (
    ("en", r"english|eng\.?\s*ver|英文|英語|영어"),
    ("zh", r"chinese|mandarin|cantonese|中文|华语|國語|国语|普通话|粤语|중국어"),
    ("ko", r"korean|kor\.?\s*ver|韩文|韓文|韩语|韓語|한국어"),
    ("ja", r"japanese|jpn?\.?\s*ver|日文|日语|日語|日本語|일본어"),
)


# Non-language editions: a different recording, so its lyrics/timing may not match the studio one.
_KIND_TAGS = (
    ("instrumental", r"instrumental|inst\.|karaoke|off vocal|伴奏|인스트|반주"),
    ("remix", r"remix|rmx|リミックス|리믹스"),
    ("live", r"\blive\b|concert|unplugged|ライブ|라이브|演唱会|現場|现场"),
    ("acoustic", r"acoustic|アコースティック|어쿠스틱"),
)


def kind_tag(title):
    """'instrumental' | 'remix' | 'live' | 'acoustic' when the raw title names such an edition."""
    text = unicodedata.normalize("NFKC", title or "").casefold()
    for code, pattern in _KIND_TAGS:
        if re.search(pattern, text):
            return code
    return None


def language_tag(title):
    """'en' | 'zh' | 'ko' | 'ja' when the raw title names a language edition, else None."""
    text = unicodedata.normalize("NFKC", title or "").casefold()
    for code, pattern in _LANG_TAGS:
        if re.search(pattern, text):
            return code
    return None


def _tag_score(want_tag, cand_tag):
    """Adjustment for a candidate's edition tag against what is playing. An untagged title is
    taken to be the original edition, so a candidate tagged as a *translation* edition loses;
    a matching tag wins; conflicting tags lose hard."""
    if want_tag == cand_tag:
        return 6 if want_tag else 0
    if want_tag and cand_tag:
        return -40
    if cand_tag and not want_tag:
        return -25
    return -8          # playing "(English Version)" but the candidate is untagged: mildly less likely


_NO_SPLIT_ARTISTS = (re.compile(r"tyler,\s*the creator", re.I),)


def _split_artists(artist_name):
    """Split "A, B & C feat. D" into artists. Names that contain a separator themselves stay whole:
    "AC/DC" (a slash without spaces), "X Japan" (x only counts between spaces), "Tyler, The Creator"."""
    name = artist_name or ""
    for rx in _NO_SPLIT_ARTISTS:
        name = rx.sub(lambda m: m.group(0).replace(",", ""), name)
    parts = re.split(r",|&|;|\s/\s|\bfeat\.?\b|\bft\.?\b|\bwith\b|(?<=[\w)])\s[x\u00d7]\s", name, flags=re.I)
    return [p for p in (_norm(p) for p in parts) if p]


def _artist_match(w, h):
    """Same artist: equal, or one contains the other when both are long enough not to match by accident."""
    return w == h or (min(len(w), len(h)) >= 4 and (w in h or h in w))


# --- which language edition of the lyrics ------------------------------------------------
# LRCLIB can hold several language versions with the same title, artist and length. Only explicit
# signals are used to choose between them: a title/artist/album written in Japanese/Korean/Chinese,
# or a title naming an edition ("Japanese Ver."). With no such signal there is no preference, and
# any language is accepted.
_tl = threading.local()     # length of the LRCLIB entry that was matched, for the lyrics check


def lyrics_script(synced):
    """'ko' | 'ja' | 'zh' | ... | 'en' (all Latin) for a list of (time, text) lines or an LRC string."""
    if isinstance(synced, str):
        text = synced
    else:
        text = " ".join(str(line[1]) for line in synced[:25])
    return detect_script(text) or "en"


def preferred_script(title="", artist="", album=""):
    """The language edition to prefer when entries tie, or None for no preference."""
    own = detect_script("%s %s %s" % (title, artist, album))
    if own:
        return own
    return language_tag(title)


def _pick_candidate(source, cands, title, artist, duration_ms, want_script=None):
    """cands: [{"id", "name", "artists": [str], "dur_ms"}]. Choose the best
    match, or return None and log exactly why nothing was acceptable."""
    if not cands:
        log.info("%s: search returned no results for %r - %r", source, title, artist)
        return None
    want_title = _norm(title)
    want_artists = _split_artists(artist)
    want_tag = language_tag(title)
    want_kind = kind_tag(title)
    best, best_score, notes = None, None, []
    loose, loose_score = None, None      # same song, but the length is further off
    for c in cands:
        name = _norm(c["name"])
        if want_title and name == want_title:
            t = 2
        elif want_title and name and (want_title in name or name in want_title):
            t = 1
        else:
            t = 0
        have = [_norm(a) for a in c["artists"]]
        a = 1 if any(w and any(_artist_match(w, h) for h in have if h) for w in want_artists) else 0
        diff_s = abs(c["dur_ms"] - duration_ms) / 1000 if duration_ms and c["dur_ms"] else None
        is_loose = diff_s is not None and diff_s * 1000 > MAX_DURATION_MISMATCH_MS
        same_edition = language_tag(c["name"]) == want_tag and kind_tag(c["name"]) == want_kind
        if is_loose and not (t == 2 and a and same_edition and diff_s * 1000 <= LOOSE_DURATION_MISMATCH_MS):
            notes.append("%r by %s: length off by %.0fs" % (c["name"], "/".join(c["artists"]), diff_s))
            continue
        if t == 0 and not (a and diff_s is not None and diff_s <= 2):
            notes.append("%r by %s: title doesn't match" % (c["name"], "/".join(c["artists"])))
            continue
        score = (t * 10 + a * 5 - (diff_s if diff_s is not None else 3)
                 + _tag_score(want_tag, language_tag(c["name"]))
                 + _tag_score(want_kind, kind_tag(c["name"])))
        if want_script and c.get("_r") and c["_r"].get("syncedLyrics"):
            score += 8 if lyrics_script(c["_r"]["syncedLyrics"]) == want_script else -8
        if is_loose:
            if loose_score is None or score > loose_score:
                loose, loose_score = c, score
        elif best_score is None or score > best_score:
            best, best_score = c, score
    if best is None and loose is not None:
        best = loose
        log.info("%s: no hit of the right length, using %r by %s whose length is off by %.0fs", source,
                 best["name"], "/".join(best["artists"]), abs(best["dur_ms"] - duration_ms) / 1000)
    if best is None:
        log.info("%s: %d results for %r - %r, none usable: %s", source, len(cands), title, artist,
                 "; ".join(notes[:4]))
        return None
    log.info("%s: matched %r by %s (id %s)", source, best["name"], "/".join(best["artists"]), best["id"])
    return best


INSTRUMENTAL_MARK = "\u266a"        # shown for an instrumental break (a long gap between two lyric lines)
INSTRUMENTAL_MIN_GAP_S = 6.0         # shorter pauses just keep the previous line on screen
_TS_RE = re.compile(r"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]")
_OFFSET_RE = re.compile(r"^\s*\[offset:\s*([+-]?\d+)\s*\]", re.I | re.M)
_WORD_TAG_RE = re.compile(r"<\d{1,3}:\d{2}(?:[.:]\d{1,3})?>")
_CONTROL_RE = re.compile(r"[\x00-\x08\x0b-\x1f\x7f-\x9f\u2028\u2029]")
MAX_LRC_LINES = 4000        # a real song has a few dozen to a few hundred; more is junk (or hostile)
MAX_LINE_CHARS = 400        # a lyric line longer than this is cut: the strip draws it character by character


def parse_lrc(lrc_text: str):
    """Parse LRC text into a sorted list of (timestamp_seconds, text).

    Handles several stamps on one line ([00:10.00][00:50.00]Chorus), stamps without a fraction,
    word-timing tags (<00:12.30>) and the [offset:] header. A blank stamp (an instrumental
    break) becomes an INSTRUMENTAL_MARK line when the break lasts INSTRUMENTAL_MIN_GAP_S or more;
    shorter blanks, leading blanks and a trailing end marker are dropped."""
    lrc_text = lrc_text or ""
    m = _OFFSET_RE.search(lrc_text)
    offset = int(m.group(1)) / 1000.0 if m else 0.0
    items = []
    for raw in lrc_text.splitlines():
        pos, stamps = 0, []
        while True:
            m = _TS_RE.match(raw, pos)
            if not m:
                break
            minutes, seconds, frac = m.groups()
            frac = (frac or "0").ljust(3, "0")[:3]
            stamps.append(int(minutes) * 60 + int(seconds) + int(frac) / 1000)
            pos = m.end()
        if not stamps:
            continue
        text = " ".join(_CONTROL_RE.sub(" ", _WORD_TAG_RE.sub("", raw[pos:])).split())[:MAX_LINE_CHARS]
        for ts in stamps:
            items.append((max(0.0, ts - offset), text))
        if len(items) > MAX_LRC_LINES * 2:
            break
    items.sort(key=lambda pair: pair[0])
    del items[MAX_LRC_LINES * 2:]
    lines = []
    for k, (ts, text) in enumerate(items):
        if text:
            lines.append((ts, text))
            continue
        nxt = next((t for t, tx in items[k + 1:] if tx), None)
        if (lines and lines[-1][1] != INSTRUMENTAL_MARK and nxt is not None
                and nxt - ts >= INSTRUMENTAL_MIN_GAP_S):
            lines.append((ts, INSTRUMENTAL_MARK))
    return lines[:MAX_LRC_LINES]


# Bracketed credit / re-release notes that are not part of a song's real title. Edition tags
# such as "(English Version)" or "(Live)" are NOT in this list: they change the lyrics.
_CREDIT_BRACKET_RE = re.compile(
    r"\s*[\(\[（【]\s*(?:feat\.?|ft\.?|featuring|with|prod\.?|produced|from|remaster\w*|\d{4}\s*remaster\w*"
    r"|bonus|deluxe|explicit|clean)\b[^\)\]）】]*[\)\]）】]", re.I)
_REMASTER_SUFFIX_RE = re.compile(r"\s-\s(?:\d{4}\s+)?(?:remaster\w*|mono|stereo|single version|radio edit)\b.*$", re.I)


def _clean_title(title):
    """The title without "(feat. X)", "(with X)", "- Remastered 2011" and, for a non-Latin
    title, a bracketed romanization: "セツナハナビ (Setsuna Hanabi)" -> "セツナハナビ"."""
    t = unicodedata.normalize("NFKC", title or "")
    t = _CREDIT_BRACKET_RE.sub("", t)
    t = _REMASTER_SUFFIX_RE.sub("", t)
    m = re.match(r"^(.*?[^\x00-\u024f\u2000-\u206f].*?)\s*[\(\[]\s*[A-Za-z0-9 ,.'!?&-]+\s*[\)\]]\s*$", t)
    if m and not language_tag(t):
        t = m.group(1)
    return " ".join(t.split()) or (title or "")


def _first_artist(artist_name):
    parts = _split_raw(artist_name)
    return (parts[0] if parts else "").strip()


def _split_raw(artist_name):
    name = artist_name or ""
    for rx in _NO_SPLIT_ARTISTS:
        name = rx.sub(lambda m: m.group(0).replace(",", "\0"), name)
    parts = re.split(r",|&|;|\s/\s|\bfeat\.?\b|\bft\.?\b", name, flags=re.I)
    return [p.replace("\0", ",") for p in parts]


_APOSTROPHE_LOOKALIKES = str.maketrans({c: "'" for c in "\u2032\u00b4\u0060\u2019\u2018\u02bc\u02b9\u0092\u2035\uff07"})


def ascii_quotes(text):
    """"I\u2032m In Love" / "I\u00b4m" / "I\u2019m" -> "I'm": Spotify and LRCLIB often disagree on which
    apostrophe-like character a title uses, which breaks exact lookups."""
    return (text or "").translate(_APOSTROPHE_LOOKALIKES)


def _lrclib_variants(track_name, artist_name):
    """(title, artist) pairs to try, most exact first: as played, cleaned title, cleaned title
    with only the first artist (Spotify joins collaborators, LRCLIB often lists one)."""
    clean, first = _clean_title(track_name), _first_artist(artist_name)
    plain = ascii_quotes(clean)
    pairs = [(track_name, artist_name), (clean, artist_name), (clean, first),
             (plain, artist_name), (plain, first)]
    if "'" in plain:        # some entries are stored without the apostrophe ("Im In Love")
        pairs += [(plain.replace("'", ""), first)]
    return [p for p in dict.fromkeys(pairs) if p[0] and p[1]]


def _lrclib_get(params):
    """Exact lookup. A 404 just means LRCLIB does not know the song (not a failure)."""
    resp = _http("lrclib", "GET", LRCLIB_GET, params=params, headers=LRCLIB_HEADERS, ok_statuses=(404,))
    if resp is not None and resp.status_code == 200:
        data = _safe_json(resp)
        if isinstance(data.get("syncedLyrics"), str) and data["syncedLyrics"]:
            return data
    return None


def _lrclib_search(params):
    """Results of /api/search that have synced lyrics, as candidates for _pick_candidate."""
    resp = _http("lrclib", "GET", LRCLIB_SEARCH, params=params, headers=LRCLIB_HEADERS)
    if resp is None:
        return []
    try:
        results = resp.json()
    except ValueError:
        return []
    if not isinstance(results, list):
        return []
    return [{"id": r.get("id"), "name": _text(r.get("trackName") or r.get("name")),
             "artists": [_text(r.get("artistName"))], "dur_ms": _ms(r.get("duration")), "_r": r}
            for r in results[:100]
            if isinstance(r, dict) and isinstance(r.get("syncedLyrics"), str) and r["syncedLyrics"]]


def _text(value):
    return value if isinstance(value, str) else ""


def _ms(seconds):
    """Seconds (any JSON value) -> whole milliseconds; 0 when it isn't a sane number."""
    if isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or not 0 <= seconds < 86400:
        return 0
    return int(seconds * 1000)


def _fetch_from_lrclib(track_name, artist_name, album_name=None, duration_ms=None):
    """Exact lookup first, then searches that retry with a cleaned title / first artist only /
    a free-text query, always ranked by _pick_candidate. When several entries tie, the
    edition in the title/artist's own language wins, if any (see preferred_script)."""
    started = time.monotonic()
    variants = _lrclib_variants(track_name, artist_name)
    secs = round(duration_ms / 1000) if duration_ms else None
    want = preferred_script(track_name, artist_name, album_name or "")
    fallback = None         # an exact hit in another language edition than the preferred one
    for i, (t, a) in enumerate(variants):
        if i and time.monotonic() - started > LRCLIB_BUDGET_S:
            break
        params = {"track_name": t, "artist_name": a}
        if album_name and i == 0:
            params["album_name"] = album_name
        if secs:
            params["duration"] = secs
        data = _lrclib_get(params)
        if data:
            got = lyrics_script(data["syncedLyrics"])
            log.info("lrclib: exact match id %s %r by %s (%.0fs, album %r, %s lyrics)%s", data.get("id"),
                     data.get("trackName"), data.get("artistName"), data.get("duration") or 0,
                     data.get("albumName"), got, " after cleaning to %r - %r" % (t, a) if i else "")
            if want and got != want:
                fallback = data
                log.info("lrclib: that entry is in %s but %s is preferred: checking for another edition", got, want)
                break
            _tl.match_dur_ms = _ms(data.get("duration"))
            return parse_lrc(data["syncedLyrics"]), data.get("plainLyrics")
    # Exact match failed (duration off by more than 2s, other album, ...): searches, loosest last.
    queries = [{"track_name": t, "artist_name": a} for t, a in variants]
    clean, first = _clean_title(track_name), _first_artist(artist_name)
    queries.append({"q": ("%s %s" % (ascii_quotes(clean), first)).strip()})
    seen = {}
    best = None
    for params in queries:
        if time.monotonic() - started > LRCLIB_BUDGET_S:
            break
        for c in _lrclib_search(params):
            seen.setdefault(c["id"], c)
        cands = list(seen.values())
        song = _pick_candidate("lrclib", cands, track_name, artist_name, duration_ms, want) if cands else None
        if song:
            if not want or lyrics_script(song["_r"]["syncedLyrics"]) == want:
                _tl.match_dur_ms = song["dur_ms"]
                return parse_lrc(song["_r"]["syncedLyrics"]), song["_r"].get("plainLyrics")
            best = song     # right song, other language: keep looking in the remaining queries
    if fallback is not None:        # an exact (title, artist, length) hit is a better guess than a search hit
        _tl.match_dur_ms = _ms(fallback.get("duration"))
        return parse_lrc(fallback["syncedLyrics"]), fallback.get("plainLyrics")
    if best is not None:
        _tl.match_dur_ms = best["dur_ms"]
        return parse_lrc(best["_r"]["syncedLyrics"]), best["_r"].get("plainLyrics")
    log.info("lrclib: no synced lyrics for %r - %r", track_name, artist_name)
    return None, None


_cache_lock = threading.Lock()
_cache = None            # OrderedDict, oldest first; loaded from disk once, then kept in memory
_missing = None          # {key: epoch_s}: songs the lyrics site answered "not found" for
_dirty = False
_flush_timer = None
FLUSH_DELAY_S = 3.0
MISSING_PATH = os.path.join(_APP_DIR, "lyrics_missing.json")
MISSING_TTL_S = 6 * 3600
MISSING_MAX_ENTRIES = 500


def _read_json_dict(path):
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _ensure_loaded():
    """Load both caches from disk the first time they are needed (caller holds _cache_lock)."""
    global _cache, _missing
    if _cache is None:
        _cache = OrderedDict(_read_json_dict(CACHE_PATH))
    if _missing is None:
        now = time.time()
        _missing = {k: t for k, t in _read_json_dict(MISSING_PATH).items()
                    if isinstance(t, (int, float)) and now - t < MISSING_TTL_S}


def _schedule_flush():
    """Write to disk a few seconds after the last change, not on every song (caller holds _cache_lock)."""
    global _dirty, _flush_timer
    _dirty = True
    if _flush_timer is None:
        _flush_timer = threading.Timer(FLUSH_DELAY_S, flush_cache)
        _flush_timer.daemon = True
        _flush_timer.start()


def flush_cache():
    """Write pending cache changes now. Also runs at exit, so nothing is lost on a normal close."""
    global _dirty, _flush_timer
    with _cache_lock:
        _flush_timer = None
        if not _dirty or _cache is None:
            return
        _dirty = False
        while len(_cache) > CACHE_MAX_ENTRIES:
            _cache.popitem(last=False)         # drop the least recently used
        while len(_missing) > MISSING_MAX_ENTRIES:
            _missing.pop(next(iter(_missing)))
        cache_copy, missing_copy = dict(_cache), dict(_missing)
    try:
        write_json_atomic(CACHE_PATH, cache_copy, ensure_ascii=False)
        write_json_atomic(MISSING_PATH, missing_copy)
    except OSError as exc:
        log.warning("couldn't write lyrics cache: %s", exc)
        with _cache_lock:
            _dirty = True          # keep the changes: the next write (or the exit hook) tries again


atexit.register(flush_cache)


def _cache_lookup(key):
    """(lyrics, plain) from memory, marking the entry as just used; None when absent or out of date."""
    global _dirty
    with _cache_lock:
        _ensure_loaded()
        hit = _cache.get(key)
        if isinstance(hit, dict) and hit.get("synced") and hit.get("v") == CACHE_VERSION:
            lines = _valid_lines(hit["synced"])
            plain = hit.get("plain")
            if lines is None or lyrics_check.is_placeholder(lines):
                if lines is None:                      # damaged or hand-edited entry: drop it, look it up again
                    log.warning("ignoring a damaged lyrics cache entry")
                    _cache.pop(key, None)
                    _dirty = True
                return None
            _cache.move_to_end(key)
            # The new "last used" order is saved with the next write or at exit, instead of
            # rewriting the whole cache file every time a song is replayed.
            _dirty = True
            return lines, plain if isinstance(plain, str) else None
    return None


def _valid_lines(raw):
    """[(seconds, text)] from what the cache file holds, or None if it isn't exactly that."""
    if not isinstance(raw, list) or len(raw) > MAX_LRC_LINES:
        return None
    out = []
    for item in raw:
        if (not isinstance(item, (list, tuple)) or len(item) != 2 or isinstance(item[0], bool)
                or not isinstance(item[0], (int, float)) or not isinstance(item[1], str)
                or item[0] != item[0] or item[0] in (float("inf"), float("-inf"))):
            return None
        out.append((float(item[0]), item[1][:MAX_LINE_CHARS]))
    return out


def _known_missing(key):
    with _cache_lock:
        _ensure_loaded()
        t = _missing.get(key)
        if t is None:
            return False
        if time.time() - t >= MISSING_TTL_S:
            del _missing[key]
            return False
        return True


def _remember_missing(key):
    with _cache_lock:
        _ensure_loaded()
        _missing.pop(key, None)
        _missing[key] = time.time()
        _schedule_flush()


def forget(track_name, artist_name, duration_ms=None):
    """Drop what is stored for a song (its lyrics and any remembered 'not found'), so the next
    fetch_lyrics call asks the lyrics site again."""
    key = _cache_key(track_name, artist_name, duration_ms)
    with _cache_lock:
        _ensure_loaded()
        _cache.pop(key, None)
        _missing.pop(key, None)
        _schedule_flush()


def _cache_key(track_name, artist_name, duration_ms):
    return f"{track_name}|{artist_name}|{round((duration_ms or 0) / 1000)}"


# ----------------------------------------------------------------- local .lrc files --

def lyrics_folders():
    """Folders searched for your own .lrc files: <data folder>/lyrics, plus any folders listed under
    "lyrics_folders" in config.json."""
    folders = [os.path.join(_APP_DIR, "lyrics")]
    try:
        import config
        folders += config.config_str_list("lyrics_folders")
    except Exception:
        pass
    return folders


def _loose(text):
    """Like _norm() but keeps everything after a " - " (a file name is "Artist - Title")."""
    text = _fold_latin_accents(unicodedata.normalize("NFKC", text or "").casefold())
    text = re.sub(r"[^\w\s]", " ", text)
    return " ".join(text.split())


def _fetch_from_local(track_name, artist_name, album_name=None, duration_ms=None):
    """Your own synced lyrics: a file called "Artist - Title.lrc" (or "Title - Artist.lrc", or just
    "Title.lrc") in one of lyrics_folders(). No network; the first match wins."""
    titles = {_loose(track_name), _loose(_clean_title(track_name))} - {""}
    artists = ({_loose(a) for a in _split_raw(artist_name)} | {_loose(artist_name)}) - {""}
    wanted = set(titles)
    for t in titles:
        for a in artists:
            wanted.add("%s %s" % (a, t))
            wanted.add("%s %s" % (t, a))
    for folder in lyrics_folders():
        try:
            names = os.listdir(folder)
        except OSError:
            continue
        for name in names:
            stem, ext = os.path.splitext(name)
            if ext.lower() != ".lrc" or _loose(stem) not in wanted:
                continue
            path = os.path.join(folder, name)
            try:
                with open(path, "rb") as fh:
                    raw = fh.read(2_000_000)
                text = raw.decode("utf-8-sig", errors="replace")
            except OSError:
                log.warning("couldn't read %s", path, exc_info=True)
                continue
            synced = parse_lrc(text)
            if synced:
                log.info("local lyrics file %s (%d lines)", name, len(synced))
                return synced, None
    return None, None


# --------------------------------------------------------- "wrong lyrics?" picker --

def search_alternatives(track_name, artist_name, duration_ms=None, limit=12):
    """Other LRCLIB entries for the playing song, closest in length first, for the "Wrong lyrics?"
    window: [{"id", "title", "artist", "album", "dur_s", "lines", "synced", "plain"}]. A site
    outage gives []; the caller can tell that from "no entries" with outage_since()."""
    clean, first = _clean_title(track_name), _first_artist(artist_name)
    queries = [{"q": ("%s %s" % (ascii_quotes(clean), first)).strip()},
               {"track_name": clean, "artist_name": first},
               {"track_name": clean}]
    seen = {}
    started = time.monotonic()
    for params in queries:
        if seen and time.monotonic() - started > LRCLIB_BUDGET_S:
            break
        for c in _lrclib_search(params):
            seen.setdefault(c["id"], c)
    want = (duration_ms or 0) / 1000.0
    out = []
    for c in seen.values():
        r = c["_r"]
        synced = r.get("syncedLyrics") or ""
        lines = len(parse_lrc(synced))
        if not lines:
            continue
        out.append({"id": c["id"], "title": c["name"], "artist": (c["artists"] or [""])[0],
                    "album": r.get("albumName") or "", "dur_s": c["dur_ms"] / 1000.0, "lines": lines,
                    "synced": synced, "plain": r.get("plainLyrics")})
    out.sort(key=lambda x: (abs(x["dur_s"] - want) if want else 0, -x["lines"]))
    return out[:limit]


def use_alternative(track_name, artist_name, duration_ms, alt):
    """Make one entry from search_alternatives() the lyrics for this song (also for next time).
    Returns (synced_lines, plain_text), or (None, None) if the entry has no usable lines."""
    synced = parse_lrc(alt.get("synced") or "")
    if not synced:
        return None, None
    plain = alt.get("plain")
    key = _cache_key(track_name, artist_name, duration_ms)
    with _cache_lock:
        _ensure_loaded()
        _cache.pop(key, None)
        _cache[key] = {"v": CACHE_VERSION, "src": "picked", "synced": synced, "plain": plain}
        _missing.pop(key, None)
        _schedule_flush()
    log.info("lyrics for %r set by hand to LRCLIB entry %s", track_name, alt.get("id"))
    return synced, plain


# Online lookups in order of preference: (name, function(track, artist, album, duration_ms) ->
# (synced_lines, plain_text)). The first one with usable lyrics wins. Your own .lrc files are not
# listed here: fetch_lyrics() looks at them first (see _try_local).
SOURCES = (
    ("lrclib", _fetch_from_lrclib),
)


def _try_local(track_name, artist_name, album_name, duration_ms):
    """Your own .lrc file for this song, or None. Looked at before the cache so that adding, fixing or
    deleting a file takes effect at once, and never written to the cache for the same reason.
    The file is trusted as to length (it is yours), but a 'no lyrics' placeholder is still ignored."""
    try:
        synced, plain = _fetch_from_local(track_name, artist_name, album_name, duration_ms)
    except Exception:
        log.exception("reading your local lyrics files failed")
        return None
    if not synced:
        return None
    synced, plain, notes = lyrics_check.check(synced, plain, track_name, artist_name, None)
    for note in notes:
        log.info("check %r (local): %s", track_name, note)
    return (synced, plain) if synced else None

# Spotify's DJ announcements ("Up next" by "DJ X") and ads have no lyrics.
_NON_SONG_ARTIST_RE = re.compile(r"\bdj\s*x\b", re.I)
_NON_SONG_TITLES = {"up next", "advertisement", "spotify"}


def is_non_song(track_name, artist_name):
    """True for the Spotify DJ voice ('Up next' / 'DJ X') and ads: nothing to look up or show."""
    title = " ".join(unicodedata.normalize("NFKC", track_name or "").casefold().split()).strip(" .!…")
    artist = unicodedata.normalize("NFKC", artist_name or "").casefold()
    if _NON_SONG_ARTIST_RE.search(artist) or _NON_SONG_ARTIST_RE.search(title):
        return True
    return title in _NON_SONG_TITLES and (not artist or artist in ("spotify", "advertisement", "dj x"))


def fetch_lyrics(track_name, artist_name, album_name=None, duration_ms=None):
    """Return (synced_lines, plain_text) for a track, or (None, None) if no source has it.

    synced_lines: [(timestamp_seconds, text)]. Results are cached on disk so replaying a
    song never asks the lyrics site again."""
    if is_non_song(track_name, artist_name):
        log.info("not a song (%r - %r), no lyrics lookup", track_name, artist_name)
        return None, None
    local = _try_local(track_name, artist_name, album_name, duration_ms)
    if local:
        return local
    key = _cache_key(track_name, artist_name, duration_ms)
    hit = _cache_lookup(key)
    if hit:
        return hit
    if _known_missing(key):
        log.info("lyrics site had no lyrics for %r a while ago, not asking again yet", track_name)
        return None, None

    # One lookup per song at a time: if a retry or re-lookup is already fetching this song, a
    # second request just waits for that result.
    with _inflight_lock:
        fut = _inflight.get(key)
        owner = fut is None
        if owner:
            fut = _inflight[key] = Future()
    if not owner:
        try:
            return fut.result(timeout=SOURCE_WAIT_S)
        except Exception:
            return None, None
    try:
        result = _fetch_uncached(key, track_name, artist_name, album_name, duration_ms)
    except BaseException as exc:
        fut.set_exception(exc)
        raise
    finally:
        with _inflight_lock:
            _inflight.pop(key, None)
    fut.set_result(result)
    return result


_inflight = {}
_inflight_lock = threading.Lock()
# How long a second request for the same song waits for the one already running.
SOURCE_WAIT_S = 12.0


def _fetch_uncached(key, track_name, artist_name, album_name, duration_ms):
    started = time.monotonic()
    asked_any = False
    for name, lookup in SOURCES:
        if source_paused(name):
            global _last_outage
            _last_outage = time.monotonic()
            log.info("source %s is paused after repeated failures, not asked for %r", name, track_name)
            continue
        _tl.match_dur_ms = None
        asked_any = True
        try:
            synced, plain = lookup(track_name, artist_name, album_name, duration_ms)
        except Exception as exc:     # one broken source must never take the app down
            log.exception("lyrics source %s crashed: %s", name, exc)
            continue
        if not synced:
            log.info("source %s: no synced lyrics for %r", name, track_name)
            continue
        # An entry whose own length matches the song is the right edition even if its last
        # timestamp runs a few seconds long, so the "runs past the end" check is skipped for it.
        md = getattr(_tl, "match_dur_ms", None)
        trusted = bool(md and duration_ms and abs(md - duration_ms) <= 3000)
        synced, plain, notes = lyrics_check.check(synced, plain, track_name, artist_name,
                                                  None if trusted else duration_ms)
        for note in notes:
            log.info("check %r (%s): %s", track_name, name, note)
        if not synced:
            continue
        log.info("lyrics for %r found via %s (%d lines) in %.1fs", track_name, name, len(synced),
                 time.monotonic() - started)
        with _cache_lock:
            _ensure_loaded()
            _cache.pop(key, None)
            _cache[key] = {"v": CACHE_VERSION, "src": name, "synced": synced, "plain": plain}
            _missing.pop(key, None)
            _schedule_flush()
        return synced, plain
    log.info("no synced lyrics found for %r - %s", track_name, artist_name)
    # Remember a genuine "not found" (the site answered), but never an outage: a failed or
    # skipped request says nothing about whether the song exists.
    if asked_any and not outage_since(started):
        _remember_missing(key)
    return None, None
