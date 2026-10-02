# Lyricify Korean

A small desktop app that shows synced, scrolling lyrics for whatever's
playing on Spotify, like [Lyricify](https://github.com/WXRIW/Lyricify-App),
with one addition: **romanized lyrics for Korean songs**, alongside the
usual Chinese (pinyin) and Japanese (romaji) support.

This is an original, from-scratch implementation (Lyricify itself is
closed-source freeware; only a small lyrics-formatting helper library is
open source). It's built in Python so it runs on Windows, macOS, and Linux.

Not affiliated with Spotify or with Lyricify/WXRIW.

## Apple Music, and update checks

Apple Music for Windows (and iTunes) also publish to the Windows media controls, so they work the same way as Spotify (no window-title fallback for them). If both are open, whichever is playing wins.

On startup the app checks the latest GitHub release of Lycro0/LycRomanise and shows a notice on the strip if it is newer than `version.py`; tray > **Check for updates** checks on demand and opens the release page. It never downloads or installs anything. Turn it off with `"check_updates": false` in `config.json`. For each release, bump `version.py`, `installer.iss` and `version_info.txt`, and tag the release `v<version>`.

## No Spotify API needed (default)

The app no longer talks to Spotify's Web API by default, so there is no login, no Client ID,
no 429 rate limit and no Users and Access list. It asks **Windows** what Spotify is playing:
the Spotify desktop app publishes title, artist, album, length, position and play/pause to the
Windows media controls, and `media_client.py` reads that locally.

- Needs the **Spotify desktop app on Windows** (not the web player). Install the packages in
  `requirements.txt` (the `winrt-*` ones do the reading).
- If those packages are missing or Windows shows no Spotify session, it falls back to the
  Spotify window title (the artist and song name). That has no length or position, so timing is counted
  from when the song was first seen and can drift after a seek.
- Windows doesn't expose the queue, so next-song lyrics prefetch is off; lyrics load when a song starts.
- To go back to the old API login, add `"playback_source": "spotify_api"` to `config.json`.

## What it does

- Polls Spotify for your currently playing track
- Fetches synced lyrics from unofficial QQ Music and NetEase Cloud Music
  lookups and from [LRCLIB](https://lrclib.net) (a free, open, community-run
  lyrics database, no API key needed). All three are asked at the same time
  and their answers are **cross-checked** before one is used (see "Lyric
  sources" below); if they agree, the first of QQ → NetEase → LRCLIB wins,
  the same multi-source approach Lyricify itself uses
- Shows the current line large, with the previous/next lines dimmed above
  and below, scrolling as the song plays
- Detects whether the lyrics are Korean, Japanese, or Chinese and shows a
  romanized version (always on; there is no translation and no toggle)
- Keeps the window always-on-top, like a mini lyrics overlay
- **Desktop Lyrics** mode, the app's main view: a small floating strip
  with the current line's romanization on top (karaoke-colored, turning
  from yellow to pink as playback moves through it) and the next line
  underneath, with a squeeze-up slide/fade transition between lines and a
  lockable, draggable position. Text that's too wide for the strip
  shrinks to fit, and wraps onto a second line as a last resort instead
  of getting cut off.
- Runs as its **own app with no console window**: a tray icon (Lock,
  Settings, Start with Windows, Quit) and a launcher that never
  opens a black window. The icon is `assets/lyrics-overlay.ico` (a dark tile with a
  pink-to-yellow karaoke line; `python make_icon.py` regenerates it) and is also the
  Settings window's icon. If the file is missing the tray falls back to a generated one.
- **Sharp on scaled displays.** The app declares itself DPI-aware before its first window
  exists, and every pixel size (strip size, text slots, outline, padlock, hover area) is
  multiplied by the display scale (125 %, 150 %, ...). Font sizes are in points and scale by
  themselves. The strip's width/height in Settings are "at 100 %" values, so a 720 x 150 strip
  looks the same size at any scaling. It uses *system* DPI awareness: crisp on the main
  display's scale; on a second monitor with a different scale Windows stretches it (slightly
  soft) rather than the strip resizing while you drag it between screens.
- A **settings window** for colors, font sizes, strip size and more that
  applies changes **live** (no restart).
- Shows **problems on the strip itself** (Spotify sign-in failed, credentials
  missing) instead of only in the log.

## Starting, quitting, logs

- **Start it:** double-click `LyricsOverlay.pyw` (Windows runs it with
  `pythonw`, so no console at all), or `LyricsOverlay.bat` (a console flashes
  for an instant, then it's gone). `run.bat` is the same thing under the old
  name. `python app.py` from a terminal also works: it starts a separate
  console-less copy and returns, so closing that terminal never closes the strip.
- **It's independent of any console.** Only one copy runs at a time; starting
  a second one does nothing.
- **Quit:** tray icon → **Quit**.
  (There's no taskbar button; the strip is a frameless overlay, and the tray
  icon is its presence in the system. If the tray icon is missing, `pystray`
  or `pillow` isn't installed: `pip install -r requirements.txt`.)
- **Tray menu:** Lock/Unlock position, Settings…, Start with
  Windows, Open logs folder, Quit.
- **Logs:** everything (info, errors, tracebacks, anything that would have
  been printed) goes to **`logs/spoti-lyrics.log`** next to the app (rotates
  daily, 14 days kept; `logs/crash-native.log` for hard interpreter crashes).
  Nothing is written to a console.
- **Start with Windows:** tray menu or Settings. It adds a per-user entry
  (`HKCU\...\Run`, no admin rights) that launches `LyricsOverlay.pyw`;
  untick it to remove it. If you move the app folder, untick and tick again.

## Desktop Lyrics mode

Desktop Lyrics is what opens by default: a frameless, floating strip
that sits over your desktop or other apps:

- **Drag** anywhere on it (while unlocked) to reposition it.
- **No right-click menu.** Lock/unlock, Settings (text size, lyric offset,
  colours) and Quit are all in the tray icon menu. Romanization is always on.
- **Quiet by default.** An idle strip shows only the lyrics: no frame and
  no icon. Move the pointer over it and a small drawn padlock appears at the
  top-centre (and, while unlocked, a faint 1 px frame showing it can be
  dragged); both fade away shortly after the pointer leaves. Click the
  padlock, or use the tray menu, to lock/unlock. On Windows, locking also
  makes the strip click-through, so clicks land on whatever's underneath
  it. The padlock stays clickable so you can always unlock it again. On
  macOS/Linux, locking still stops it from being dragged, but true
  click-through isn't available there.
- **Notices:** if Spotify sign-in fails or the credentials are missing, a
  short message appears on the strip until it's fixed (it's also logged).
- **Karaoke coloring**: the current line starts yellow and fills to pink,
  left to right, as the song reaches each part of it, with the fill edge
  softly blended rather than snapping instantly from one color to the
  other. LRCLIB only gives a timestamp for the *start* of each line (not
  per word), so the fill assumes an even pace across the line's
  characters. This is a close approximation for most lyrics, not
  frame-accurate word timing.
- **Line transitions**: the upcoming line rises and grows from the
  "next line" slot up into the main position, then the *finished line
  simply vanishes* the moment the next one starts, with no fade and no ghost.
  (On Windows the strip's black background is a color-key, not real
  alpha, so "fading" text can only ever turn dark-but-opaque, which is
  the black smear that used to linger. Everything is drawn at full color
  for that reason, and the earlier motion-blur trail was removed too.)
- **Lyricify-style text**: the next line is regular-weight in the same
  yellow as unsung text, and all text has a black outline (drawn in
  near-black `#010101`, because pure black is the transparency key on
  Windows and would vanish).
- **Sync**: NetEase lyrics with *word-level timing* (when the song has
  them) drive the karaoke fill directly; otherwise it estimates from line
  timestamps. Settings → *Lyric offset* nudges everything in
  100 ms steps (saved as `lyric_offset_ms`), also editable in
  `config_editor.py`. Polls only nudge the running position instead of
  snapping it, unless you seek.
- **Auto-resizing text**: if a line (or the next-line preview) is too wide
  to fit the strip at the normal size, it shrinks down just for that line.
  If it's still too wide even at the smallest readable size (which in
  practice mostly meant one long space-less run in the Japanese
  romanizer), it wraps onto a second line, breaking at a word boundary
  where there is one, or by character count as a last resort, rather
  than getting cut off or unreadably tiny.
- **True see-through transparency** (so your actual desktop shows through
  the black background) is a Windows-only tkinter trick. On macOS/Linux
  you still get the frameless, draggable, floating strip, just with a
  solid dark background.

### Changing colors, sizes, etc.

Open **Settings…** from the tray icon.
It covers the karaoke colors, the next-line color, font sizes, the strip's
width/height, lyric offset, outline, the optional title card and
Start with Windows. **Every change is saved and applied to the running
strip within about half a second**, so there's no Save-and-restart. (The app
watches `appearance.json`, so `python config_editor.py` on its own, or
editing the file by hand, is picked up live too.) Delete `appearance.json`
or hit "Reset to defaults" to go back to the built-in look.

## The Korean romanizer

`romanize/korean.py` implements Revised Romanization (RR 2000, South
Korea's official standard) from scratch, with no dictionary and no external
service. It correctly handles **liaison** (연음), where a syllable's final
consonant is pronounced as if it starts the next syllable. For example, 한국어
becomes `hangugeo`, not a letter-by-letter split such as `hangug` + `eo`.
This includes consonant clusters like 닭이 → `dalgi`.

It does *not* implement rarer, dictionary-dependent rules like
palatalization (구개음화) or tensification (경음화). See the comment at
the top of that file for details and examples. For song lyrics it gets
very close to how the line is actually sung.

Try it with no setup at all:

```bash
python demo_romanize.py
```

## Log in with Spotify (for the .exe / sharing with friends)

On first launch the browser opens Spotify's login page; the user signs in and clicks Agree. No client ID or secret is typed in. If the build has no built-in app ID and no saved details, a **Log in with Spotify** window opens instead, with an *Advanced* section (link to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)) for using your own app. Re-login any time: tray icon > **Log in with Spotify**. This uses Spotify's PKCE login, so no secret is shipped.

If a login window is left open in the browser, clicking the button again just reminds you to finish it; close that tab and wait a moment (or restart the app) to start over.

One-time setup by the app owner (Lycro):
1. Create an app in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) with redirect URI `http://127.0.0.1:8888/callback`.
2. Paste its **Client ID** into `BUILTIN_CLIENT_ID` in `config.py`, then build the .exe.
3. Under **Users and Access**, add each friend's Spotify account (name + email). Development Mode apps only allow a few users (5 at the time of writing, see below), and anyone not on the list gets a 403 after logging in.

---

## Setup

### 1. Requirements

- Python 3.9+
- **A Spotify Premium account.** As of February 2026, Spotify requires the
  developer/app owner to have an active Premium subscription to use
  Development Mode at all. The app will simply stop working if that
  subscription lapses.
- On Linux, tkinter's system package if it isn't already installed:
  `sudo apt install python3-tk` (Debian/Ubuntu) or the equivalent for your
  distro.

### 2. Install dependencies

```bash
pip install -r requirements.txt
```

### 3. Create a Spotify app

1. Go to the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard)
   and create an app.
2. Under **Redirect URIs**, add exactly:
   ```
   http://127.0.0.1:8888/callback
   ```
   Spotify no longer accepts `localhost` as a redirect host (only the
   explicit loopback address `127.0.0.1` is allowed, and it's the one
   exception to their HTTPS-only rule). Using `localhost` here will fail.
3. Under **Users and Access**, add your own Spotify account as an
   authorized user (Development Mode apps are capped at 5 users).
4. Copy the **Client ID** and **Client Secret** from the app's Settings page.

### 4. Add your credentials

**Easiest: in the app.** Start it (it opens fine without credentials and says so on the strip),
use the tray icon → **Settings…**, paste the **Client ID** and **Client
secret** into the *Spotify* section and press **Save & connect**. They are written to
`config.json` (the secret is masked; tick *Show secret* to check it) and used immediately, with no
restart. The browser approval page opens the first time (or after you change the Client ID, since
a saved login belongs to the app that made it). If the environment variables below are set they
take priority, and the window says so.

Or set environment variables:

```bash
export SPOTIPY_CLIENT_ID="your_client_id"
export SPOTIPY_CLIENT_SECRET="your_client_secret"
```

...or copy `config.example.json` to `config.json` and fill it in:

```bash
cp config.example.json config.json
```

`config.json` is for local use only. Don't commit it or share it. (Running `python config_editor.py`
on its own also saves credentials, but a strip that is already running only picks them up when
saved from its own Settings window, or after a restart.)

### 5. Run it

Double-click `LyricsOverlay.pyw` (no console window). See "Starting,
quitting, logs" above for the other ways and how to quit.

The first run opens your browser to log in to Spotify and approve access;
after that, the token is cached locally (`.spotify_token_cache`) and it
won't ask again until the token expires.

## Notes & limitations

- **Spotify rate limits.** The app polls Spotify every 1 s (2 s when
  nothing is playing) and interpolates in between, and never blocks on the network (polling and lyric lookups run
  on a background thread). If Spotify still answers 429, it honors
  `Retry-After` and pauses polling instead of hammering. There's no way
  to *bypass* the limit; the fix is to make fewer requests.
- **Lyric sources** are all asked at once and cross-checked (`lyrics_check.py`) before one is
  used, so a wrong answer from one source doesn't get romanized and shown. In order:
  1. "No lyrics" stand-ins are thrown away. QQ/NetEase show "纯音乐，请欣赏" ("pure music, please
     enjoy") for instrumentals, and a search for an edition such as "House of Cards (Full Length
     Edition)" can land on one; romanized that is pinyin for a Korean song. If that is all there is,
     the strip says no lyrics were found instead.
  2. Lyrics in a different script from the other sources (Chinese lines next to Korean or English
     ones, for example a translation or another song) are dropped. Two sources agreeing outvote one; on a
     1-1 tie the song's own script (from its title/artist) wins, otherwise the original language
     is preferred over Chinese (order: Korean, Japanese, Latin, Cyrillic, Greek, Hebrew, Arabic,
     Devanagari, Chinese). A K-pop song with English lines is not mistaken for a translation.
  3. Lyrics that run more than 8 s past the end of the song (a longer edition) lose to ones that fit.
  4. With three sources, one whose wording shares nothing with two that agree is dropped.
  5. Of what is left the first of QQ Music → NetEase → LRCLIB wins. Bilingual lines (original and
     translation stamped at the same time) are cut down to the song's own language.

  When a source is the only one with lyrics there is nothing to compare it with, but two checks
  still run: lyrics whose last line is more than 8 s past the end of the song are dropped, and so
  are all-Chinese lyrics for a song whose title/artist are Korean or Japanese (a translation).
  English titles are never judged this way. (Showing nothing beats showing the wrong song.)
  Every rejection and its reason is written to the log (`cross-check ...` lines) and printed by
  `diagnose_lyrics.py`. All three are waited for, up to 6 s in total, so QQ/NetEase keep their place
  ahead of LRCLIB. A source that misses that deadline (or crashes) is still asked but not waited for
  during the next 2 minutes (`SLOW_SOURCE_SKIP_S`), so a dead QQ doesn't add 6 s to every song. The cache
  entries written by older versions are ignored once (they could hold a placeholder), so each
  song is looked up again the next time it plays. Search results
  are ranked by title, artist and length (a result whose length differs
  from the Spotify track by more than ~6 s is rejected), and if
  "title + artist" finds nothing the title alone is tried. Results are
  cached in `lyrics_cache.json` so replays make no lyrics requests. QQ's
  word-level format (QRC) is encrypted and not supported; QQ is
  line-level only.
- **Search endpoints and dead sources.** QQ is searched through the current `u.y.qq.com/cgi-bin/musicu.fcg`
  endpoint (POST, with the `comm` block it requires); the old `client_search_cp` one is only tried
  when the new one answers with an error code. NetEase search goes POST first (GET is the backup) and
  remembers which one worked. A source that fails 3 calls in a row (timeout, HTTP 5xx/403/429) is not
  asked at all for 10 minutes, and that is remembered in `provider_health.json`, so a dead site costs
  nothing even on the first song after a restart; after the pause one failed call pauses it again.
  Delete `provider_health.json` to reset it. (Not verified against the live QQ site from the
  development sandbox, so check the log's `qq:` lines.)
- **Why a source missed is in the log** (`logs/spoti-lyrics.log`): no
  results, HTTP status, an error code from the site, every candidate that
  was rejected and why, or "no word-timed lyrics in the response". To check
  one song by hand: `python diagnose_lyrics.py "Title" "Artist" 215`
  (prints each source's result and reasons; the one console tool).
  **Word-by-word (karaoke) timing needs NetEase's `yrc` data.** The plain, unauthenticated
  lyric endpoint does not return it, so when a NetEase match has no `yrc` there the app now asks
  NetEase's *encrypted* lyric endpoints (first the web player's `weapi`, then the desktop
  client's `eapi`, in `netease_crypto.py`, which needs the `cryptography` package). That is where
  word-timed lyrics come from. What the log tells you:
  `word-timed (yrc) lyrics received via weapi` = worked;
  `answered but the song has no word-timed lyrics` = NetEase simply has none for that song
  (line timing is used); `answered HTTP ...` / `code ...` = NetEase refused the request;
  `need the 'cryptography' package` = run `pip install -r requirements.txt`.
  Two things to know: the encrypted requests are only made when NetEase is actually used, and
  because QQ Music comes first in the source order, a song that QQ has never reaches NetEase
  (so it stays line-timed). This has been checked against the published algorithm and
  constants and with tests, but **not against NetEase's live servers**.
- **Lyrics coverage isn't universal.** LRCLIB is community-contributed, so
  some tracks, especially less mainstream releases, won't have synced
  lyrics available; QQ Music and NetEase are asked at the same time for that
  reason. Their lookups use unofficial, undocumented endpoints (there's
  no official public API for them), the same kind of reverse-engineered
  source most third-party lyrics tools rely on. They can change or start
  blocking requests with no notice, at which point that source just
  quietly stops contributing lyrics and the others are unaffected. If a track has no lyrics anywhere, the app says so rather
  than showing nothing.
- **Lyrics timing accounts for network latency**, adding back roughly half
  of each Spotify request's round-trip time so lines don't consistently
  fire late on a slower connection, but it's still an estimate, not a
  perfectly precise clock.
- **Reading playback state doesn't need extra Spotify API approval.** It's
  a standard read-only scope, but Spotify has tightened Development Mode
  access generally over the past couple of years (fewer endpoints, a
  5-user cap, the Premium requirement above). If something that used to
  work suddenly 403s, check the
  [Spotify for Developers blog](https://developer.spotify.com/blog) for
  the latest policy changes.
- The main polling loop does its network calls synchronously for
  simplicity, so there can be a brief (~1 second) pause right when a track
  changes while lyrics are fetched. Not noticeable during normal listening.
- **The Desktop Lyrics animation and transparency fixes (squeeze-up
  transition, motion blur, the anti-residue changes, the forced Windows
  repaint) were written and reasoned through carefully, including
  reproducing the exact reported symptoms, but couldn't be verified
  against a real display in the environment they were written in**, because
  there's no Windows machine or GUI available there. The constants that control
  feel (`LINE_ANIM_SECONDS`, `MOTION_BLUR_STEPS`, `FADE_INVISIBLE_ALPHA`,
  near the top of `app.py`) are deliberately easy to find and tweak if
  something still looks off once you actually see it running.
- The NetEase lyrics fallback's JSON handling was hardened against the
  `AttributeError: 'str' object has no attribute 'get'` crash (an
  unofficial endpoint occasionally returning a string instead of the
  expected object). Every `.json()` call on that path now checks the
  shape it got back instead of assuming it.

## More features

- **Title card**: off by default. Turn on `show_title_card` (Settings) to show the artist and song name until the first lyric line.
- **Long lines** are split into balanced chunks so they stay readable; word-timed lyrics keep karaoke sync per chunk.
- **Numbers and symbols** are spoken out (`24` → "twenty-four", `&` → "and") before romanizing; decorative symbols are dropped.
- **Romanization** for Korean, Japanese, Chinese, Cyrillic, Greek, Hebrew, Arabic-script and Devanagari lyrics, including mixed-script lines. Arabic and Hebrew are written without most vowels, so vowels are *guessed* (an "a" between consonants): treat them as approximate. Known simplifications: Arabic emphatic letters (ص ض ط ظ) and ح/ه are not distinguished from their plain forms, long vowels come out short, and the definite article isn't assimilated; Hebrew always guesses "a", and a vowelled sheva is dropped.
- **Remembers** window position and lock state; lyrics clear on pause/stop; the lyrics of the next 2 queued tracks are fetched ahead of time (on a separate thread, so they never delay the current song).
- **Logs** go to `logs/spoti-lyrics.log` next to the app (there is no console at all).
- **Symbol wording** per language (`&` → "and" / 앤 / アンド ...): `$1` reads "one dollar", Russian nouns follow the number (1 процент, 2 процента, 5 процентов).

## Project layout

```
app.py                  # tkinter GUI, polling loop, Desktop Lyrics rendering (LyricsApp)
drawing.py              # pure drawing helpers: colour maths, outlined text
winddiag.py             # window diagnostics line + on-screen lyric pixel count
selftest.py             # python app.py --selftest (real-Tk check, writes logs/selftest-result.txt)
layout.py               # line splitting, chunk timing, karaoke fill maths
textnorm.py             # numbers and symbols turned into spoken words
applog.py               # file-only logging + crash capture (no console output)
winsys.py               # console-less relaunch, single instance, start-with-Windows, DPI awareness,
                        # click-through / repaint calls
tray.py                 # system-tray icon and menu (pystray)
assets/lyrics-overlay.ico  # the app / tray icon (make_icon.py regenerates it)
make_icon.py            # draws the icon (only needed to change its design)
netease_crypto.py       # NetEase weapi/eapi request encryption (word-timed lyrics)
config_editor.py        # settings window (live-applied)
diagnose_lyrics.py      # per-source lyric lookup + cross-check verdict for one song
spotify_client.py       # Spotify auth + "what's playing" polling
lyrics_provider.py      # NetEase / QQ / LRCLIB fetch + disk cache + dead-source breaker
lyrics_check.py         # cross-checks the sources' answers (placeholders, wrong script, translations)
config.py               # credentials + appearance.json + window state
demo_romanize.py        # standalone romanizer demo

LyricsOverlay.bat       # same, from a batch file (run.bat forwards to it)
tests/                  # test_app.py, test_launcher.py, test_windows_paths.py (fake Tk) + test_realtk.py (real Tk)
romanize/
  korean.py  japanese.py  chinese.py  others.py  detect.py
```

## Tests, and what they can't cover

`python tests/test_app.py`, `python tests/test_launcher.py` and `python tests/test_windows_paths.py` run with a
fake tkinter and a fake Spotify, and no network. They cover the strip logic,
hover/lock chrome, notices, live settings, the log-only output (nothing
reaches a console), the launcher/relaunch/single-instance logic, the
start-with-Windows registry entry, the tray menu wiring and the lyric
source parsing. `test_windows_paths.py` adds: the NetEase encryption (layer-by-layer decrypt
checks) and the weapi → eapi → line-timed fallback chain, DPI awareness and 150 % scaling of the
strip, the click-through / repaint / registry helpers against fake Windows APIs, saving and applying
Spotify credentials without a restart, the tray icon file and its thread-safe state snapshot, and
the queue/prefetch edge cases (empty or short queue, shuffle order, duplicates, podcast episodes,
repeat-one, a song change mid-prefetch, a 429 on the queue call). They **cannot** check anything that needs a real Windows
display or account: click-through, the transparent background, the tray
icon actually appearing, the registry write, the real pythonw relaunch, real DPI scaling on a
scaled monitor, the new scrolling Settings layout under a real Tk,
Spotify sign-in and the live lyric sites. Those need a run on your machine;
if something looks off, `logs/spoti-lyrics.log` says what happened.


## Windows-only paths: what was reviewed

Read through (and where noted, changed) because they only run on a real Windows display:

- **Click-through** (locking): now goes through `winsys.set_clickthrough` with proper 64-bit
  function signatures instead of untyped `ctypes` calls; behaviour is the same (layered +
  transparent while locked, layered only when unlocked, since the transparent colour key needs
  the layered style). **Repaint** (`InvalidateRect`) is unchanged apart from the typed call.
- **Padlock stacking**: the padlock and the strip are separate topmost windows, and clicking the
  strip lifts it above the padlock. The padlock is now raised again after a click/drag
  (previously only when it reappeared). On Windows the strip's black background is see-through
  anyway, so this mainly matters elsewhere.
- **Tray**: the tray runs on its own thread and used to read Tk variables and the registry when
  its menu opened, which Tk does not allow from another thread. It now reads a plain snapshot
  that is rebuilt on the Tk thread whenever something changes.
- **Single instance**: the mutex check now uses ctypes' own saved last-error value, which
  nothing can overwrite between the call and the check.
- **Start with Windows**: at every start the entry is rewritten if it points to an old location
  (moved folder / new Python), so untick-and-tick is no longer needed. Note: with the
  Microsoft Store Python, `python.exe` lives in a versioned folder that changes on update;
  the self-repair above fixes the entry the next time the app is started by hand.
- **pythonw relaunch**: read through, no change (the console copy exits before the mutex is
  taken, the child is told not to detach again).
- **Queue / prefetch**: see the tests. Prefetch runs once per song change, using Spotify's
  `me/player/queue`. With shuffle on, that list is whatever order Spotify reports at that moment;
  if Spotify then plays something else, or you add songs to the queue afterwards, that song
  simply isn't prefetched and its lyrics load normally when it starts (a second or two).

## Speed notes

Lyric lookups use short timeouts (3 s connect, 5 s read) and a source that hasn't answered after
6 s is skipped. Spotify is polled every 2 s (faster near the end of a song), so a manual skip is
noticed within about 2 s.


## Troubleshooting

### Check the strip is really drawing (`--selftest`)

`python app.py --selftest` (or `pythonw LyricsOverlay.pyw --selftest`) builds the real strip with a
demo song, with Spotify polling turned off so nothing can replace the demo, waits until the window is
mapped, and exits. It prints nothing. The result is one line in `logs/selftest-result.txt`:

- `SELFTEST PASS ... lyric_pixels_on_screen=N` with N > 0 means the text is really on your screen.
  (Confirmed on a real Windows PC: `canvas_items=36`, `lyric_pixels_on_screen=2372`.)
- `SELFTEST FAIL` gives the reason: playing state, track, lyrics state, line index and the scene the
  strip was told to show, plus the full error if setup crashed.

If the log folder can't be written or rotated, log lines go to `spoti-lyrics-fallback.log` in your temp
folder (`%TEMP%`) instead of being lost. `tests/test_realtk.py` runs the same check under real Tk
(on Linux: `xvfb-run -a python tests/test_realtk.py`; it skips itself without tkinter or a display).

### If the strip is invisible (window diagnostics)

Lyrics being found (`lyrics ready: N lines` in the log) does not prove they are on screen. About 2 s
after start the app writes one `window diagnostics (...)` line to `logs/spoti-lyrics.log`:
mapped/state/geometry, `-topmost/-alpha/-transparentcolor`, the Win32 window handles and extended
style, the layered colour key, canvas item count and `lyric pixels on screen: N` (a screen grab of
the strip counting yellow/pink pixels; it uses numpy when installed, otherwise samples every third
pixel). **N > 0 with a song playing means the text is visible.** N = 0 with items > 0 means Windows is
not showing what Tk drew. Set `SPOTI_DEBUG_DIAGNOSTICS=1` to also log it at 6 s and on every lock toggle.

Window styles change only when you lock/unlock (and once at startup if it was locked), after the
window is mapped, on the top-level Win32 window. Unlocking never adds the layered style, and the
transparent colour is re-applied and read back after every style change. Repaints use `RedrawWindow`
without erasing; a failure is logged once at WARNING.

To find which piece breaks it on your machine, start the app with one of these set (in a terminal:
`set SPOTI_DEBUG_NO_CLICKTHROUGH=1` then `python LyricsOverlay.pyw`; `SPOTI_NO_DETACH=1` keeps it in
that terminal, though output still goes only to the log):

| variable | effect |
| --- | --- |
| `SPOTI_DEBUG_NO_CLICKTHROUGH=1` | never touches the window's extended styles (lock only stops dragging) |
| `SPOTI_DEBUG_NO_INVALIDATE=1` | never asks Windows for repaints |
| `SPOTI_DEBUG_NO_DPI=1` | does not enable DPI awareness |
| `SPOTI_DEBUG_DIAGNOSTICS=1` | log the diagnostics line again at 6 s and on lock toggles |

### Wrong edition of a song (e.g. "Love Talk" vs "Love Talk (English Version)")

An edition tag in the title ("English Version", "Chinese Ver.", "中文版", Korean/Japanese equivalents,
and Live, Remix, Acoustic, Instrumental) is read. A plain title is taken to be the original, so a
result tagged as another edition loses to an untagged one, and a matching tag wins. LRCLIB's search
fallback ranks results the same way (title, artist, length, tag). If the title says "Chinese Version"
but the only lyrics are English, they are dropped. The lyrics cache version is 3, so songs cached with
the wrong edition are looked up again once. Limit: a song whose Spotify title has no tag and whose
sources carry no tag either can't be told apart by name; length (a couple of seconds' difference
between editions) then decides.

### Never share these

Never put `config.json`, `.spotify_token_cache`, `logs/`, `lyrics_cache.json`, `provider_health.json`
or `window_state.json` in a zip you share. If they ever were, regenerate the Spotify client secret in
the Spotify dashboard.
