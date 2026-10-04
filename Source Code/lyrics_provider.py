"""LycRomanise lyric lookup.

Synced lyrics come from LRCLIB (https://lrclib.net), a free, open,
community-maintained lyrics database that needs no API key. Coverage is not
universal (it is community contributed), so some tracks, especially less
mainstream K-pop/J-pop/C-pop releases, may simply not exist there.

SOURCES lists the lookups in order of preference; the first one that returns
usable lyrics wins and the rest are not asked. LRCLIB is the only one today,
a backup service can be added by writing one function and listing it there.
"""

import json
import logging
import os
import re
import threading
import time
import unicodedata

import requests
from concurrent.futures import Future

import lyrics_check
from romanize.detect import detect_script

LRCLIB_GET = "https://lrclib.net/api/get"
LRCLIB_SEARCH = "https://lrclib.net/api/search"
# LRCLIB asks clients to identify themselves.
LRCLIB_HEADERS = {"User-Agent": "LycRomanise (https://github.com/Lycro0/LycRomanise)"}

log = logging.getLogger("spoti.lyrics")

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
CACHE_VERSION = 8   # 8: automatic Korean-first language tie-break; 7: language-edition tie-break; 6: lookups made with a stale song length are dropped; 5: LRCLIB only, no word timing (v1.2.0); 4: cleaned-title retries; 3: language tags

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
        with open(HEALTH_PATH, "w", encoding="utf-8") as f:
            json.dump(_health, f)
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


LRC_LINE_RE = re.compile(r"\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)")


def _safe_json(resp):
    """resp.json() but never blows up the caller. Returns a dict on success, otherwise {}."""
    try:
        data = resp.json()
    except ValueError:
        return {}
    return data if isinstance(data, dict) else {}


def _http(source, method, url, ok_statuses=(), **kwargs):
    """One HTTP call with the reason for any failure logged. Returns the
    response only when the status is 200, else None."""
    try:
        resp = requests.request(method, url, timeout=TIMEOUT, **kwargs)
    except requests.RequestException as exc:
        log.info("%s: request to %s failed: %s: %s", source, url.split("?")[0], type(exc).__name__, exc)
        _record_result(source, False)
        return None
    if resp.status_code in ok_statuses:
        _record_result(source, True)
        return resp
    if resp.status_code != 200:
        log.info("%s: %s answered HTTP %s", source, url.split("?")[0], resp.status_code)
        _record_result(source, not (resp.status_code >= 500 or resp.status_code in (403, 429)))
        return None
    _record_result(source, True)
    return resp


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


def _split_artists(artist_name):
    parts = re.split(r",|&|;|/|\bfeat\.?\b|\bft\.?\b|\bwith\b|\bx\b", artist_name or "", flags=re.I)
    return [p for p in (_norm(p) for p in parts) if p]


# --- which language edition of the lyrics ------------------------------------------------
# LRCLIB often holds several entries with the same title, artist and length that are different
# language versions (e.g. Korean and Japanese versions of a K-pop song, both just titled
# "Every Night (Version 2)"). Nothing in the title tells them apart, so the tie is broken
# automatically, with no setting: a title/artist/album written in Japanese/Korean/Chinese prefers
# that language; a title that names a language edition ("Japanese Ver.") prefers that edition;
# otherwise the original-language entry (Korean) beats translated editions (Japanese/Chinese/English).
# If only the other language exists it is still used, so no song loses its lyrics.


def lyrics_script(synced):
    """'ko' | 'ja' | 'zh' | ... | 'en' (all Latin) for a list of (time, text) lines or an LRC string."""
    if isinstance(synced, str):
        text = synced
    else:
        text = " ".join(str(line[1]) for line in synced[:25])
    return detect_script(text) or "en"


def preferred_script(title="", artist="", album=""):
    """The language edition to prefer when entries tie."""
    own = detect_script("%s %s %s" % (title, artist, album))
    if own:
        return own
    return language_tag(title) or "ko"


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
        a = 1 if any(w and any(w in h or h in w for h in have if h) for w in want_artists) else 0
        diff_s = abs(c["dur_ms"] - duration_ms) / 1000 if duration_ms and c["dur_ms"] else None
        is_loose = diff_s is not None and diff_s * 1000 > MAX_DURATION_MISMATCH_MS
        same_edition = language_tag(c["name"]) == want_tag and kind_tag(c["name"]) == want_kind
        if is_loose and not (t == 2 and a and same_edition and diff_s * 1000 <= LOOSE_DURATION_MISMATCH_MS):
            notes.append("%r by %s: length off by %.0fs" % (c["name"], "/".join(c["artists"]), diff_s))
            continue
        if t == 0 and not (a and diff_s is not None and diff_s <= 2):
            notes.append("%r by %s: title doesn't match" % (c["name"], "/".join(c["artists"])))
            continue
        score = (t * 10 + a * 5 - (diff_s or 3) + _tag_score(want_tag, language_tag(c["name"]))
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


def parse_lrc(lrc_text: str):
    """Parse LRC-format text into a sorted list of (timestamp_seconds, text)."""
    lines = []
    for raw in lrc_text.splitlines():
        m = LRC_LINE_RE.match(raw)
        if not m:
            continue
        minutes, seconds, frac, text = m.groups()
        frac = frac.ljust(3, "0")[:3]
        ts = int(minutes) * 60 + int(seconds) + int(frac) / 1000
        text = text.strip()
        if text:
            lines.append((ts, text))
    lines.sort(key=lambda pair: pair[0])
    return lines


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
    m = re.match(r"^(.*?[^\x00-\x24a-zA-Z\s].*?)\s*[\(\[]\s*[A-Za-z0-9 ,.'!?&-]+\s*[\)\]]\s*$", t)
    if m and not language_tag(t):
        t = m.group(1)
    return " ".join(t.split()) or (title or "")


def _first_artist(artist_name):
    return (re.split(r",|&|;|/|\bfeat\.?\b|\bft\.?\b", artist_name or "", flags=re.I)[0] or "").strip()


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
        if data.get("syncedLyrics"):
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
    return [{"id": r.get("id"), "name": r.get("trackName") or r.get("name") or "",
             "artists": [r.get("artistName") or ""], "dur_ms": int((r.get("duration") or 0) * 1000), "_r": r}
            for r in results if isinstance(r, dict) and r.get("syncedLyrics")]


def _fetch_from_lrclib(track_name, artist_name, album_name=None, duration_ms=None):
    """Exact lookup first, then searches that retry with a cleaned title / first artist only /
    a free-text query, always ranked by _pick_candidate. When several entries tie, the Korean
    (original-language) edition wins (see preferred_script)."""
    started = time.monotonic()
    variants = _lrclib_variants(track_name, artist_name)
    secs = round(duration_ms / 1000) if duration_ms else None
    want = preferred_script(track_name, artist_name, album_name or "")
    fallback = None         # an exact hit in another language edition than the preferred one
    for i, (t, a) in enumerate(variants):
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
                return parse_lrc(song["_r"]["syncedLyrics"]), song["_r"].get("plainLyrics")
            best = song     # right song, other language: keep looking in the remaining queries
    if best is None and fallback is not None:
        return parse_lrc(fallback["syncedLyrics"]), fallback.get("plainLyrics")
    if best is not None:
        return parse_lrc(best["_r"]["syncedLyrics"]), best["_r"].get("plainLyrics")
    log.info("lrclib: no synced lyrics for %r - %r", track_name, artist_name)
    return None, None


_cache_lock = threading.Lock()


def _load_cache():
    try:
        with open(CACHE_PATH, "r", encoding="utf-8") as f:
            data = json.load(f)
        return data if isinstance(data, dict) else {}
    except (OSError, ValueError):
        return {}


def _save_cache(cache):
    # Keep only the newest entries so the file can't grow forever.
    while len(cache) > CACHE_MAX_ENTRIES:
        cache.pop(next(iter(cache)))
    try:
        with open(CACHE_PATH, "w", encoding="utf-8") as f:
            json.dump(cache, f, ensure_ascii=False)
    except OSError as exc:
        log.warning("couldn't write lyrics cache: %s", exc)


# Lookups in order of preference: (name, function(track, artist, album, duration_ms) ->
# (synced_lines, plain_text)). The first one with usable lyrics wins.
SOURCES = (
    ("lrclib", _fetch_from_lrclib),
)

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
    key = f"{track_name}|{artist_name}|{round((duration_ms or 0) / 1000)}"
    hit = _load_cache().get(key)
    if (isinstance(hit, dict) and hit.get("synced") and hit.get("v") == CACHE_VERSION
            and not lyrics_check.is_placeholder(hit["synced"])):
        return [tuple(x) for x in hit["synced"]], hit.get("plain")

    # One lookup per song at a time: if the prefetch is already fetching this song when it
    # starts playing, the player just waits for that result.
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
    for name, lookup in SOURCES:
        if source_paused(name):
            global _last_outage
            _last_outage = time.monotonic()
            log.info("source %s is paused after repeated failures, not asked for %r", name, track_name)
            continue
        try:
            synced, plain = lookup(track_name, artist_name, album_name, duration_ms)
        except Exception as exc:     # one broken source must never take the app down
            log.exception("lyrics source %s crashed: %s", name, exc)
            continue
        if not synced:
            log.info("source %s: no synced lyrics for %r", name, track_name)
            continue
        synced, plain, notes = lyrics_check.check(synced, plain, track_name, artist_name, duration_ms)
        for note in notes:
            log.info("check %r (%s): %s", track_name, name, note)
        if not synced:
            continue
        log.info("lyrics for %r found via %s (%d lines) in %.1fs", track_name, name, len(synced),
                 time.monotonic() - started)
        with _cache_lock:
            cache = _load_cache()
            cache.pop(key, None)
            cache[key] = {"v": CACHE_VERSION, "src": name, "synced": synced, "plain": plain}
            _save_cache(cache)
        return synced, plain
    log.info("no synced lyrics found for %r - %s", track_name, artist_name)
    return None, None
