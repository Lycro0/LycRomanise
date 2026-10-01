"""Spoti-Lyrics Overlay — lyric lookup.

Ask QQ Music, NetEase Cloud Music (word-level timing when available) and
LRCLIB at the same time, cross-check what they return (lyrics_check.py) and
use the best match — the same multi-source approach Lyricify itself uses.

LRCLIB (https://lrclib.net) is a free, open, community-maintained lyrics
database with no API key required. Coverage isn't universal (it's
community-contributed), so some tracks — especially less mainstream
K-pop/J-pop/C-pop releases — may only exist on QQ Music or NetEase.
"""

import base64
import binascii
import html
import json
import logging
import os
import re
import threading
import time
import unicodedata
from concurrent.futures import FIRST_COMPLETED, Future, ThreadPoolExecutor, wait

import requests

import lyrics_check
import netease_crypto

LRCLIB_GET = "https://lrclib.net/api/get"
LRCLIB_SEARCH = "https://lrclib.net/api/search"

# Backup sources, used only when LRCLIB comes back empty. Both are
# unofficial, undocumented web endpoints (no official public API or key
# exists for either) — reverse-engineered the same way most lyrics tools,
# including Lyricify's own other sources, work in practice. Because
# they're undocumented, either can change or start blocking requests with
# no notice; if that happens the affected fallback just quietly stops
# contributing lyrics rather than breaking anything — LRCLIB (tried
# first, always) is unaffected either way, and each backup is independent
# of the other.
NETEASE_SEARCH = "https://music.163.com/api/search/get/web"
NETEASE_LYRIC = "https://music.163.com/api/song/lyric"
# The "v1" lyric endpoint additionally returns "yrc" — NetEase's word-by-word
# timed lyrics — for songs that have them (yv=1 asks for it).
NETEASE_LYRIC_V1 = "https://music.163.com/api/song/lyric/v1"
# The plain v1 call above normally has NO yrc: NetEase only hands word-timed
# lyrics to its own (encrypted) clients. These are the same lyric call as the
# web player (weapi) and the desktop client (eapi) make - see netease_crypto.py.
NETEASE_LYRIC_WEAPI = "https://music.163.com/weapi/song/lyric/v1"
NETEASE_LYRIC_EAPI = "https://interface.music.163.com/eapi/song/lyric/v1"
NETEASE_EAPI_PATH = "/api/song/lyric/v1"

log = logging.getLogger("spoti.lyrics")

# Order of preference: QQ Music, then NetEase, then LRCLIB. All three are
# queried at the same time (see _fetch_uncached), their answers are compared
# (lyrics_check.py), and this order decides who wins among answers that agree.
SOURCE_ORDER = ("qq", "netease", "lrclib")
# A search hit whose length differs from the Spotify track by more than
# this is treated as a different recording (live/remix/wrong song) and
# rejected rather than used with lyrics that would drift out of sync.
MAX_DURATION_MISMATCH_MS = 6000
# (connect, read) seconds for every lyrics request. Short on purpose: a slow
# site must not hold up a song change.
TIMEOUT = (2, 4)
# The most we wait for a preferred source before falling back to the next
# one, counted from the moment the song's lookup starts.
SOURCE_WAIT_S = 4.0
# Once one source has answered, the others get only this much longer before the
# best answer so far is used (they are still cross-checked if they make it).
GRACE_AFTER_FIRST_S = 1.2
# Every source is waited for (up to SOURCE_WAIT_S) so its answer can be
# cross-checked and QQ/NetEase keep their place ahead of LRCLIB. Except a source
# that just failed to answer in time or errored out: it is still asked, but not
# waited for during the next SLOW_SOURCE_SKIP_S seconds, so one dead site does
# not add SOURCE_WAIT_S to every song.
SLOW_SOURCE_SKIP_S = 120.0
_slow_until = {}

from paths import DATA_DIR as _APP_DIR
CACHE_PATH = os.path.join(_APP_DIR, "lyrics_cache.json")
CACHE_MAX_ENTRIES = 300
# Bump when the way lyrics are chosen changes: older cache entries (which may
# hold a placeholder or a translation picked before cross-checking existed)
# are then ignored and looked up again.
CACHE_VERSION = 3   # 3: version/language tags ("English Version" vs the original) are told apart
NETEASE_SEARCH_POST = "https://music.163.com/api/search/get"
NETEASE_HEADERS = {
    "Referer": "https://music.163.com/",
    "Origin": "https://music.163.com",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
    # NetEase's web API is far more willing to answer clients that look like its own web player.
    "Cookie": "appver=2.0.2; os=pc; osver=Microsoft-Windows-10-Professional-build-19045-64bit",
}

QQ_SEARCH = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp"
QQ_LYRIC = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"
QQ_HEADERS = {
    # QQ Music's endpoints reject requests without a same-site-looking
    # Referer.
    "Referer": "https://y.qq.com/portal/player.html",
    "Origin": "https://y.qq.com",
    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
}
# Query-string boilerplate QQ's own web player always sends.
QQ_COMMON = {"inCharset": "utf8", "outCharset": "utf-8", "platform": "yqq", "needNewCode": 0,
             "g_tk": 5381, "loginUin": 0, "hostUin": 0, "notice": 0}


# QQ Music's old search (c.y.qq.com/soso/... client_search_cp) answers HTTP 500 or hangs for
# many clients now. Its desktop client's search goes through this one instead; the "comm"
# block is required (without it the server answers code 2001 with an empty list).
QQ_SEARCH_V2 = "https://u.y.qq.com/cgi-bin/musicu.fcg"
QQ_COMM = {"ct": 24, "cv": 0, "uin": "0"}

# --- provider health -------------------------------------------------------------
# A source that keeps failing at the network level (timeouts, HTTP 5xx/403/429) is
# not even asked for BREAKER_PAUSE_S. Remembered in a small file, so a dead site costs
# nothing on the first song after a restart either. After the pause one failure re-opens it.
BREAKER_FAILS = 3
BREAKER_PAUSE_S = 600.0
HEALTH_PATH = os.path.join(_APP_DIR, "provider_health.json")
_health = None
_health_lock = threading.Lock()


def _health_state():
    """Load once. {"fails": {src: n}, "down_until": {src: epoch_s}, "netease_search": "POST"|"GET"}"""
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


def reset_health():
    """Forget everything (tests; also handy after fixing your network)."""
    global _health
    with _health_lock:
        _health = {"fails": {}, "down_until": {}}


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
        fails[source] = fails.get(source, 0) + 1
        if fails[source] >= BREAKER_FAILS:
            h["down_until"][source] = time.time() + BREAKER_PAUSE_S
            log.warning("source %s failed %d calls in a row; not asking it for the next %.0f minutes",
                        source, fails[source], BREAKER_PAUSE_S / 60)
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
    """resp.json() but never blows up the caller, and tolerant of JSONP
    ("callback({...})" - QQ Music answers like that when it feels like it).
    Returns a dict on success, otherwise {}."""
    try:
        data = resp.json()
    except ValueError:
        try:
            text = resp.text.strip()
            if not text.startswith(("{", "[")) and "(" in text and text.endswith((")", ");")):
                text = text[text.index("(") + 1:text.rindex(")")]
            data = json.loads(text)
        except (ValueError, AttributeError):
            return {}
    return data if isinstance(data, dict) else {}


def _http(source, method, url, **kwargs):
    """One HTTP call with the reason for any failure logged. Returns the
    response only when the status is 200, else None."""
    try:
        resp = requests.request(method, url, timeout=TIMEOUT, **kwargs)
    except requests.RequestException as exc:
        log.info("%s: request to %s failed: %s: %s", source, url.split("?")[0], type(exc).__name__, exc)
        _record_result(source, False)
        return None
    if resp.status_code != 200:
        log.info("%s: %s answered HTTP %s", source, url.split("?")[0], resp.status_code)
        _record_result(source, not (resp.status_code >= 500 or resp.status_code in (403, 429)))
        return None
    _record_result(source, True)
    return resp


def _norm(text):
    """Comparable form of a title/artist: NFKC, casefolded, without bracketed
    extras ("(feat. X)", "[Remastered]"), a trailing " - Remaster/Live/..."
    suffix, or punctuation."""
    text = unicodedata.normalize("NFKC", text or "").casefold()
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


def _pick_candidate(source, cands, title, artist, duration_ms):
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
        if diff_s is not None and diff_s * 1000 > MAX_DURATION_MISMATCH_MS:
            notes.append("%r by %s: length off by %.0fs" % (c["name"], "/".join(c["artists"]), diff_s))
            continue
        if t == 0 and not (a and diff_s is not None and diff_s <= 2):
            notes.append("%r by %s: title doesn't match" % (c["name"], "/".join(c["artists"])))
            continue
        score = (t * 10 + a * 5 - (diff_s or 3) + _tag_score(want_tag, language_tag(c["name"]))
                 + _tag_score(want_kind, kind_tag(c["name"])))
        if best_score is None or score > best_score:
            best, best_score = c, score
    if best is None:
        log.info("%s: %d results for %r - %r, none usable: %s", source, len(cands), title, artist,
                 "; ".join(notes[:4]))
        return None
    log.info("%s: matched %r by %s (id %s)", source, best["name"], "/".join(best["artists"]), best["id"])
    return best


CREDIT_LINE_RE = re.compile(
    r"^\s*(作词|作詞|作曲|编曲|編曲|制作人?|製作人?|监制|混音|母带|词|曲|lyrics?|composed?|composer|arranged?|"
    r"arranger|producer|words|music)\s*[:：]", re.I)


def _drop_credits(lines):
    """Provider LRC often opens with "作词 : xxx" style credit lines."""
    return [(ts, text) for ts, text in lines if not (ts < 15 and CREDIT_LINE_RE.match(text))]


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


YRC_LINE_RE = re.compile(r"^\[(\d+),(\d+)\](.*)$")
# A word's text runs until the next "(start,dur,0)" marker - it may itself contain
# parentheses, e.g. a backing vocal written "(hey)".
YRC_WORD_RE = re.compile(r"\((\d+),(\d+),-?\d+\)(.*?)(?=\(\d+,\d+,-?\d+\)|$)")


def parse_yrc(yrc_text: str):
    """Parse NetEase word-timed lyrics ("yrc") into
    (synced_lines, words) where synced_lines is [(start_s, text)] and
    words is a parallel list of [(start_s, end_s, word_text), ...].
    Lines that don't match (JSON metadata lines etc.) are skipped."""
    synced, words = [], []
    for raw in yrc_text.splitlines():
        m = YRC_LINE_RE.match(raw.strip())
        if not m:
            continue
        line_start = int(m.group(1)) / 1000
        parts = []
        for wm in YRC_WORD_RE.finditer(m.group(3)):
            w_start = int(wm.group(1)) / 1000
            w_end = w_start + int(wm.group(2)) / 1000
            parts.append((w_start, w_end, wm.group(3)))
        text = "".join(p[2] for p in parts).strip()
        if text and parts:
            synced.append((line_start, text))
            words.append(parts)
    order = sorted(range(len(synced)), key=lambda i: synced[i][0])
    return [synced[i] for i in order], [words[i] for i in order]


def _netease_search_once(method, params):
    if method == "POST":
        resp = _http("netease", "POST", NETEASE_SEARCH_POST, data=params, headers=NETEASE_HEADERS)
    else:
        resp = _http("netease", "GET", NETEASE_SEARCH, params=params, headers=NETEASE_HEADERS)
    return _safe_json(resp) if resp is not None else None


def _netease_candidates(query):
    """Search NetEase. The method that worked last time (remembered in the health
    file; POST until told otherwise) goes first, the other one is the backup."""
    params = {"s": query, "type": 1, "offset": 0, "limit": 10, "total": "true", "csrf_token": ""}
    with _health_lock:
        first = _health_state().get("netease_search", "POST")
    order = [first, "GET" if first == "POST" else "POST"]
    data = {}
    for i, method in enumerate(order):
        got = _netease_search_once(method, params)
        if got is None:
            continue
        data = got
        if isinstance(data.get("result"), dict) and data["result"].get("songs"):
            if i:
                with _health_lock:
                    _health_state()["netease_search"] = method
                    _health_save()
                log.info("netease: search via %s works, %s did not; using %s first from now on",
                         method, first, method)
            break
        log.info("netease: %s search gave code=%s, no songs%s", method, data.get("code"),
                 "; trying " + order[1] if i == 0 else "")
    if data.get("code") not in (None, 200):
        log.info("netease: search answered code %s (%s) - usually blocked or rate limited",
                 data.get("code"), data.get("msg") or data.get("message"))
    result = data.get("result")
    songs = result.get("songs") if isinstance(result, dict) else None
    out = []
    for s_ in songs or []:
        if not isinstance(s_, dict) or not s_.get("id"):
            continue
        artists = [a.get("name", "") for a in (s_.get("artists") or s_.get("ar") or []) if isinstance(a, dict)]
        out.append({"id": s_["id"], "name": s_.get("name", ""), "artists": artists,
                    "dur_ms": s_.get("duration") or s_.get("dt") or 0})
    return out


def _yrc_from(data):
    """The word-timed lyric text out of a lyric response, or None."""
    yrc = data.get("yrc") if isinstance(data, dict) else None
    text = yrc.get("lyric") if isinstance(yrc, dict) else None
    return text if isinstance(text, str) and text.strip() else None


def _netease_encrypted_yrc(song_id):
    """Ask NetEase's encrypted lyric endpoints (weapi, then eapi) for word-timed
    lyrics. Returns the yrc text or None; every failure is logged with its reason
    and never raises."""
    if not netease_crypto.available():
        log.info("netease: word-timed lyrics need the 'cryptography' package "
                 "(pip install -r requirements.txt): %s", netease_crypto.IMPORT_ERROR)
        return None
    body = {"id": song_id, "cp": False, "tv": 0, "lv": 0, "rv": 0, "kv": 0, "yv": 0, "ytv": 0, "yrv": 0}
    attempts = (
        ("weapi", NETEASE_LYRIC_WEAPI, dict(body, csrf_token=""), netease_crypto.weapi_encrypt),
        ("eapi", NETEASE_LYRIC_EAPI, dict(body, header=json.dumps({"os": "pc", "appver": "2.10.13"})),
         lambda payload: netease_crypto.eapi_encrypt(NETEASE_EAPI_PATH, payload)),
    )
    for name, url, payload, encrypt in attempts:
        try:
            form = encrypt(payload)
        except Exception:
            log.exception("netease: couldn't build the %s request", name)
            continue
        resp = _http("netease", "POST", url, data=form, headers=NETEASE_HEADERS)
        data = _safe_json(resp) if resp is not None else {}
        if data and data.get("code") not in (None, 200):
            log.info("netease: %s lyric call answered code %s", name, data.get("code"))
        text = _yrc_from(data)
        if text:
            log.info("netease: word-timed (yrc) lyrics received via %s for song %s", name, song_id)
            return text
        if resp is not None:
            log.info("netease: %s answered but the song has no word-timed lyrics (keys: %s)", name,
                     ",".join(sorted(k for k in data if k != "code")) or "none")
    return None


def _fetch_from_netease(track_name, artist_name, duration_ms=None):
    """Lookup against NetEase Cloud Music's web search + lyric endpoints.
    Returns (synced_lines, plain_text, words), or (None, None, None) - and
    logs *why* - on no match or any network/parsing hiccup."""
    first_artist = (re.split(r",|&|;", artist_name or "")[0] or "").strip()
    song = None
    for query in dict.fromkeys(q for q in (f"{track_name} {first_artist}".strip(), track_name) if q):
        song = _pick_candidate("netease", _netease_candidates(query), track_name, artist_name, duration_ms)
        if song:
            break
    if not song:
        return None, None, None

    # v1 endpoint: word-timed "yrc" (when the song has it) plus the plain "lrc".
    v1 = _http("netease", "GET", NETEASE_LYRIC_V1, headers=NETEASE_HEADERS,
               params={"id": song["id"], "lv": -1, "kv": -1, "tv": -1, "yv": 1})
    v1_data = _safe_json(v1) if v1 is not None else {}
    yrc_text = _yrc_from(v1_data)
    if not yrc_text:
        # The plain endpoint doesn't carry yrc; the encrypted ones do (when the song has it).
        log.info("netease: no word-timed (yrc) lyrics from the plain endpoint (keys: %s); trying encrypted",
                 ",".join(sorted(k for k in v1_data if k not in ("code",))) or "none")
        yrc_text = _netease_encrypted_yrc(song["id"])
    if yrc_text:
        synced, words = parse_yrc(yrc_text)
        if synced:
            return _drop_credits(synced), None, words
        log.info("netease: yrc present but nothing parsed from it")

    lrc = v1_data.get("lrc")
    lrc_text = lrc.get("lyric") if isinstance(lrc, dict) else None
    if not lrc_text:
        old = _http("netease", "GET", NETEASE_LYRIC, headers=NETEASE_HEADERS,
                    params={"id": song["id"], "lv": 1, "kv": 1, "tv": -1})
        old_data = _safe_json(old) if old is not None else {}
        lrc = old_data.get("lrc")
        lrc_text = lrc.get("lyric") if isinstance(lrc, dict) else None
        if old_data.get("nolyric") or old_data.get("uncollected"):
            log.info("netease: song %s has no lyrics (instrumental or uncollected)", song["id"])
    if not lrc_text:
        log.info("netease: no lyric text for song %s", song["id"])
        return None, None, None
    synced = _drop_credits(parse_lrc(lrc_text))
    if not synced:
        log.info("netease: lyric text for song %s had no timed lines", song["id"])
        return None, None, None
    return synced, None, None


def _qq_lyric_text(raw):
    """QQ's lyric field is plain text with HTML entities ("[00&#58;01&#46;23]")
    when nobase64=1 works, or base64 when it doesn't. Handle both."""
    if not isinstance(raw, str) or not raw.strip():
        return ""
    text = html.unescape(raw)
    if "[" not in text:
        try:
            text = html.unescape(base64.b64decode(raw, validate=False).decode("utf-8", "replace"))
        except (binascii.Error, ValueError):
            return ""
    return text


def _qq_items(block):
    cands = []
    for it in (block.get("list") if isinstance(block, dict) else None) or []:
        if not isinstance(it, dict):
            continue
        mid = it.get("songmid") or it.get("mid")
        if not mid:
            continue
        cands.append({
            "id": mid, "name": it.get("songname") or it.get("title") or it.get("name") or "",
            "artists": [x.get("name", "") for x in (it.get("singer") or []) if isinstance(x, dict)],
            "dur_ms": (it.get("interval") or 0) * 1000,
        })
    return cands


def _qq_candidates(query):
    """Search QQ Music: the current musicu.fcg endpoint first, the old one only when the
    new one answered but with an error code (never after a timeout: that would just
    double the wait)."""
    body = {"comm": QQ_COMM, "req_1": {"method": "DoSearchForQQMusicDesktop", "module": "music.search.SearchCgiService",
            "param": {"num_per_page": 10, "page_num": 1, "query": query, "search_type": 0}}}
    resp = _http("qq", "POST", QQ_SEARCH_V2, headers=dict(QQ_HEADERS, **{"Content-Type": "application/json"}),
                 data=json.dumps(body))
    if resp is None:
        return []
    data = _safe_json(resp)
    req = data.get("req_1") if isinstance(data.get("req_1"), dict) else {}
    if req.get("code") in (0, None) and data.get("code") in (0, None):
        d = req.get("data")
        cands = _qq_items((d.get("body") or {}).get("song") if isinstance(d, dict) else None)
        if cands:
            return cands
        log.info("qq: musicu search answered, no songs for %r", query)
        return []
    log.info("qq: musicu search answered code %s/%s; trying the old endpoint", data.get("code"), req.get("code"))
    resp = _http("qq", "GET", QQ_SEARCH, headers=QQ_HEADERS,
                 params=dict(QQ_COMMON, w=query, format="json", p=1, n=10))
    data = _safe_json(resp) if resp is not None else {}
    d = data.get("data")
    return _qq_items(d.get("song") if isinstance(d, dict) else None)


def _fetch_from_qq(track_name, artist_name, duration_ms=None):
    """QQ Music lookup (line-level only: its word-timed QRC format is
    encrypted and not handled). Logs why it misses."""
    first_artist = (re.split(r",|&|;", artist_name or "")[0] or "").strip()
    song = None
    for query in dict.fromkeys(q for q in (f"{track_name} {first_artist}".strip(), track_name) if q):
        cands = _qq_candidates(query)
        song = _pick_candidate("qq", cands, track_name, artist_name, duration_ms)
        if song:
            break
    if not song:
        return None, None, None

    resp = _http("qq", "GET", QQ_LYRIC, headers=QQ_HEADERS,
                 params=dict(QQ_COMMON, songmid=song["id"], format="json", nobase64=1))
    data = _safe_json(resp) if resp is not None else {}
    text = _qq_lyric_text(data.get("lyric"))
    if not text:
        log.info("qq: no lyric text for %s (retcode=%s)", song["id"], data.get("retcode", data.get("code")))
        return None, None, None
    synced = _drop_credits(parse_lrc(text))
    if not synced:
        log.info("qq: lyric text for %s had no timed lines", song["id"])
        return None, None, None
    return synced, None, None


def _fetch_from_lrclib(track_name, artist_name, album_name=None, duration_ms=None):
    params = {"track_name": track_name, "artist_name": artist_name}
    if album_name:
        params["album_name"] = album_name
    if duration_ms:
        params["duration"] = round(duration_ms / 1000)

    try:
        resp = requests.get(LRCLIB_GET, params=params, timeout=TIMEOUT)
        if resp.status_code == 200:
            data = _safe_json(resp)
            if data.get("syncedLyrics"):
                return parse_lrc(data["syncedLyrics"]), data.get("plainLyrics"), None
    except (requests.RequestException, AttributeError, TypeError) as exc:
        log.info("lrclib: lookup failed: %s", exc)

    # Exact match failed (e.g. duration mismatch or missing album) — fall
    # back to the looser search endpoint and take the first synced result.
    try:
        resp = requests.get(
            LRCLIB_SEARCH,
            params={"track_name": track_name, "artist_name": artist_name},
            timeout=TIMEOUT,
        )
        if resp.status_code == 200:
            try:
                results = resp.json()
            except ValueError:
                results = []
            if not isinstance(results, list):
                results = []
            cands = [{"id": r.get("id"), "name": r.get("trackName") or r.get("name") or "",
                      "artists": [r.get("artistName") or ""], "dur_ms": int((r.get("duration") or 0) * 1000),
                      "_r": r}
                     for r in results if isinstance(r, dict) and r.get("syncedLyrics")]
            # Same ranking as the other sources (title, artist, length, edition tag) - not simply
            # the first synced hit, which is how a different edition's lyrics slipped in.
            song = _pick_candidate("lrclib", cands, track_name, artist_name, duration_ms) if cands else None
            if song:
                return parse_lrc(song["_r"]["syncedLyrics"]), song["_r"].get("plainLyrics"), None
    except (requests.RequestException, AttributeError, TypeError) as exc:
        log.info("lrclib: search failed: %s", exc)
    log.info("lrclib: no synced lyrics for %r - %r", track_name, artist_name)
    return None, None, None


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


def fetch_lyrics(track_name, artist_name, album_name=None, duration_ms=None):
    """Return (synced_lines, plain_text, words) for a track, or
    (None, None, None) if no source has it.

    synced_lines: [(timestamp_seconds, text)]. words: a parallel list of
    [(start_s, end_s, word_text)] per line when the source had word-level
    timing (NetEase), otherwise None. Results are cached on disk so replaying a song never re-queries any
    lyrics site.
    """
    key = f"{track_name}|{artist_name}|{round((duration_ms or 0) / 1000)}"
    cache = _load_cache()
    hit = cache.get(key)
    if (isinstance(hit, dict) and hit.get("synced") and hit.get("v") == CACHE_VERSION
            and not lyrics_check.is_placeholder(hit["synced"])):
        return (
            [tuple(x) for x in hit["synced"]],
            hit.get("plain"),
            [[tuple(w) for w in line] for line in hit["words"]] if hit.get("words") else None,
        )

    # One lookup per song at a time: if the prefetch is already fetching this
    # song when it starts playing, the player just waits for that result.
    with _inflight_lock:
        fut = _inflight.get(key)
        owner = fut is None
        if owner:
            fut = _inflight[key] = Future()
    if not owner:
        try:
            return fut.result(timeout=SOURCE_WAIT_S * 3)
        except Exception:
            return None, None, None
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
_cache_lock = threading.Lock()


def _fetch_uncached(key, track_name, artist_name, album_name, duration_ms):
    """Ask all sources at once, cross-check what they return (lyrics_check.py)
    and keep the best. Every source is waited for up to SOURCE_WAIT_S, except
    ones that were slow or failing a moment ago (see SLOW_SOURCE_SKIP_S), so one
    dead site cannot hold up every song change."""
    sources = {
        "qq": lambda: _fetch_from_qq(track_name, artist_name, duration_ms),
        "netease": lambda: _fetch_from_netease(track_name, artist_name, duration_ms),
        "lrclib": lambda: _fetch_from_lrclib(track_name, artist_name, album_name, duration_ms),
    }
    started = time.monotonic()
    pool = ThreadPoolExecutor(max_workers=len(sources), thread_name_prefix="lyrics")
    results = {}
    try:
        paused = {n for n in SOURCE_ORDER if source_paused(n)}
        if paused == set(SOURCE_ORDER):
            paused = set()           # everything is paused: ask everyone anyway
        for name in sorted(paused):
            log.info("source %s is paused after repeated failures, not asked for %r", name, track_name)
        futures = {pool.submit(sources[name]): name for name in SOURCE_ORDER if name not in paused}
        pending = set(futures)
        # Sources that were slow/broken a moment ago are asked but not waited for.
        skipped = {n for n in SOURCE_ORDER if _slow_until.get(n, 0) > started}
        if skipped == set(SOURCE_ORDER):
            skipped = set()          # nobody healthy: wait for everyone as usual
        for name in skipped:
            log.info("source %s was slow or failing recently, not waiting long for it", name)
        deadline = started + SOURCE_WAIT_S
        while pending:
            now = time.monotonic()
            if now >= deadline or all(futures[f] in skipped for f in pending):
                break      # out of time, or only sources we decided not to wait for are left
            done, pending = wait(pending, timeout=deadline - now, return_when=FIRST_COMPLETED)
            for fut in done:
                name = futures[fut]
                try:
                    res = tuple(fut.result() or ())
                    synced, plain, words = (res + (None,) * 3)[:3]
                except Exception as exc:  # one broken source must never block the others
                    log.exception("lyrics source %s crashed: %s", name, exc)
                    _slow_until[name] = time.monotonic() + SLOW_SOURCE_SKIP_S
                    continue
                if not synced:
                    log.info("source %s: no synced lyrics for %r", name, track_name)
                    continue
                results[name] = (synced, plain, words)
                deadline = min(deadline, time.monotonic() + GRACE_AFTER_FIRST_S)
        for fut in pending:
            name = futures[fut]
            if name not in skipped:
                _slow_until[name] = time.monotonic() + SLOW_SOURCE_SKIP_S
            log.info("source %s: no answer in time for %r, left out%s", name, track_name,
                     "" if name in skipped else " (not waited for during the next %.0fs)" % SLOW_SOURCE_SKIP_S)
    finally:
        pool.shutdown(wait=False)   # slower sources we no longer need finish in the background

    picked, notes = lyrics_check.choose(results, SOURCE_ORDER, track_name, artist_name, duration_ms)
    for note in notes:
        log.info("cross-check %r: %s", track_name, note)
    if picked is None:
        log.info("no synced lyrics found for %r - %s", track_name, artist_name)
        return None, None, None
    log.info("lyrics for %r found via %s (%d lines%s) in %.1fs, from %d source(s): %s", track_name, picked.name,
             len(picked.synced), ", word-timed" if picked.words else "", time.monotonic() - started,
             len(results), ", ".join(n for n in SOURCE_ORDER if n in results))
    with _cache_lock:
        cache = _load_cache()
        cache.pop(key, None)
        cache[key] = {"v": CACHE_VERSION, "src": picked.name, "synced": picked.synced,
                      "plain": picked.plain, "words": picked.words}
        _save_cache(cache)
    return picked.synced, picked.plain, picked.words
