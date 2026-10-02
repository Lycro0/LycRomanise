# LycRomanise

A small desktop app that shows synced, scrolling lyrics for whatever is playing on **Spotify** or **Apple Music**, like [Lyricify](https://github.com/WXRIW/Lyricify-App), with one addition: **romanized lyrics** for Korean, Japanese and Chinese songs (and several other scripts).

There is **no Spotify login, no Client ID and no API key**. The app asks Windows what is playing, so it works straight after you install it.

Not affiliated with Spotify, Apple, or Lyricify/WXRIW.

## Quick start

1. Download the latest or `LycRomanise_Setup.exe` from the [Releases page](https://github.com/Lycro0/LycRomanise/releases).
2. Open the **Spotify desktop app** or **Apple Music for Windows** and play a song.
3. Run LycRomanise. A lyrics strip appears and follows the song. Nothing to sign in to.

To quit, right click the tray icon and choose **Quit**.

## Requirements

- Windows 10 or 11
- The Spotify **desktop** app, Apple Music for Windows, or iTunes (the Spotify web player is not supported, because it does not report to Windows)
- Running from source only: Python 3.9 or newer and `pip install -r requirements.txt`

## How it knows what is playing

The Spotify and Apple Music desktop apps publish the current song to the Windows media controls (the same place the volume flyout and lock screen read from): title, artist, album, length, position and play or pause state. LycRomanise reads that locally with the `winrt` packages in `requirements.txt` (`media_client.py`). Nothing is sent to Spotify or Apple, so there are no rate limits.

- If both players are open, whichever one is playing is used.
- Ads and "nothing playing" states show a clear strip.
- **Fallback for Spotify only:** if the `winrt` packages are missing or Windows shows no Spotify session, the app reads the Spotify window title (the artist and song name). That title has no length or position, so timing is counted from when the song was first seen and can drift after a seek.
- Windows does not expose the play queue, so lyrics for the next song are not fetched ahead of time. They load when a song starts.

## What it does

- Fetches synced lyrics from QQ Music, NetEase Cloud Music (unofficial lookups) and [LRCLIB](https://lrclib.net), all at the same time, and cross-checks the answers before using one (see "Lyric sources" below)
- Shows the current line large with the next line underneath, with a smooth slide between lines
- **Karaoke coloring:** the current line fills from yellow to pink as it is sung. NetEase word timing is used when a song has it, otherwise the fill is estimated from the line timestamps
- Detects Korean, Japanese and Chinese lyrics and shows a romanized version (always on). Cyrillic, Greek, Hebrew, Arabic and Devanagari are romanized too
- A frameless, always on top strip that you can drag, lock in place and make click through
- Lives in the **system tray**, with no console window and no taskbar button
- Sharp on scaled displays (125 %, 150 % and so on)
- Checks GitHub for new versions, and shows the version number when it starts

## On the strip

- **At startup** the strip shows `LycRomanise v1.1.0` for 5 seconds. If a newer release exists, a message about it appears right after, for about 12 seconds.
- **Drag** the strip anywhere while it is unlocked.
- **Hover** over it to see a small padlock (and a faint frame while unlocked). Click the padlock, or use the tray menu, to lock or unlock. Locking makes the strip click through, so clicks land on whatever is underneath. The padlock stays clickable so you can always unlock it.
- There is no right click menu. Everything is in the tray menu.
- Notices (for example "No synced lyrics found for this track") appear on the strip itself, and everything is also written to the log.

## Tray menu

Right click the tray icon:

- **LycRomanise v1.1.0** opens the releases page
- **Lock position / Unlock position**
- **Settings…**
- **Start with Windows** (a per user entry, no admin rights needed)
- **Check for updates**
- **Open logs folder**
- **Quit**

## Settings

Open **Settings…** from the tray icon. It covers:

- **Colors:** karaoke unsung and sung colors, next line color
- **Font sizes:** current and next line
- **Strip size:** width and height (values are "at 100 %" so the strip looks the same at any display scaling)
- **Display:** lyric offset in 100 ms steps (positive makes lyrics appear earlier, negative later), outline size, optional title card, Start with Windows
- A live preview

Every change is saved and applied to the running strip within about half a second, so there is nothing to restart. "Reset to defaults" goes back to the built in look.

## Updates

On startup the app checks the latest release of [Lycro0/LycRomanise](https://github.com/Lycro0/LycRomanise/releases) and tells you on the strip if a newer version exists. **Tray > Check for updates** does the same on demand and opens the release page. It never downloads or installs anything by itself.

To turn the automatic check off, add this to `config.json`:

```json
{ "check_updates": false }
```

## Lyric sources

QQ Music, NetEase and LRCLIB are all asked at once, then `lyrics_check.py` cross-checks what comes back before one is used, so a wrong answer from one source does not get romanized and shown:

1. "No lyrics" stand-ins are thrown away (for example the instrumental placeholder QQ and NetEase return).
2. Lyrics in a different script from the other sources (such as a Chinese translation next to Korean or English lines) are dropped. Two sources agreeing outvote one.
3. Lyrics that run more than 8 seconds past the end of the song (a longer edition) lose to ones that fit.
4. With three sources, one whose wording shares nothing with two that agree is dropped.
5. Of what is left, the first of QQ Music, NetEase, LRCLIB wins. Bilingual lines are cut down to the song's own language.

If the song title has an edition tag ("English Version", "Chinese Ver.", Live, Remix, Acoustic, Instrumental), it is read and the matching edition is preferred. A plain title is taken to be the original.

Timing and reliability:

- Each lookup has short timeouts (2 s to connect, 4 s to read), and a source that has not answered after 4 seconds is left out.
- After the first source answers, the others get only 0.8 seconds more before the best answer so far is used.
- A source that was slow or failing is still asked, but not waited for during the next 2 minutes. A source that fails 3 calls in a row is not asked at all for 10 minutes. This is remembered in `provider_health.json`, so a dead site costs nothing on the next start either.
- Results are cached in `lyrics_cache.json` (the last 300 songs), so replays make no lyric requests.
- Word by word timing comes from NetEase's encrypted lyric endpoints (`netease_crypto.py`, needs the `cryptography` package). QQ is line level only.

These sources use unofficial, undocumented endpoints, the same kind most third party lyrics tools rely on. They can change or block requests without notice, and when that happens the source simply stops contributing while the others carry on. Coverage is never universal, especially for less mainstream releases. If no source has synced lyrics, the strip says so.

## The romanizers

- **Korean** (`romanize/korean.py`): Revised Romanization, written from scratch with no dictionary and no external service. It handles liaison, where a final consonant is pronounced as if it starts the next syllable, so 한국어 becomes `hangugeo`, including consonant clusters like 닭이 → `dalgi`. Rarer dictionary dependent rules such as palatalization and tensification are not implemented, but for song lyrics it gets very close to how the line is sung.
- **Japanese** (`romanize/japanese.py`) uses `pykakasi`, and **Chinese** (`romanize/chinese.py`) uses `pypinyin`.
- **Others** (`romanize/others.py`): Cyrillic, Greek, Hebrew, Arabic and Devanagari, including mixed script lines. Arabic and Hebrew are written without most vowels, so vowels are guessed and should be treated as approximate.
- Numbers and symbols are spoken out before romanizing (`24` becomes "twenty-four", `&` becomes "and"), and decorative symbols are dropped. Long lines are split into balanced chunks so they stay readable.

## Where files are kept

- **The .exe or installer:** settings, lyrics cache, provider health and logs live in `%LOCALAPPDATA%\LycRomanise`. Open it from the tray with **Open logs folder**.
- **Running from source:** the same files sit next to `app.py`.

Files you may see there: `appearance.json` (settings), `window_state.json` (strip position and lock state), `lyrics_cache.json`, `provider_health.json`, `config.json` (optional, see below) and `logs/spoti-lyrics.log` (rotates daily, 14 days kept; `logs/crash-native.log` records hard crashes).

Never put `config.json`, `logs/`, `lyrics_cache.json`, `provider_health.json` or `window_state.json` in a zip you share.

## Running from source

```bash
pip install -r requirements.txt
python LyricsOverlay.pyw
```

Double clicking `LyricsOverlay.pyw` also works (Windows runs it with `pythonw`, so there is no console). `python app.py` from a terminal starts a separate console free copy and returns, so closing the terminal never closes the strip. Only one copy runs at a time.

## Optional: the old Spotify API mode

Earlier versions used the Spotify Web API, which needed a login and could hit 429 rate limits. It is still in the code (`spotify_client.py`) but **off by default and not needed**. To switch back, add this to `config.json`:

```json
{ "playback_source": "spotify_api" }
```

You then need your own Spotify app from the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) with the redirect URI `http://127.0.0.1:8888/callback`, and its Client ID entered in Settings. The tray menu then also shows Log in with Spotify and Log out. Most people should leave this alone.

## Known limits

- Spotify and Apple Music **desktop** apps only (no web players).
- Lyrics for the next song are not prefetched, so a song's lyrics load a moment after it starts.
- The same song repeating is not detected as a new play.
- With the window title fallback (Spotify only), position is estimated and can drift after a seek.
- The Windows only parts (transparent background, click through, tray icon, scaling on a real display) cannot be checked outside Windows. If something looks off, `logs/spoti-lyrics.log` says what happened.
- Apple Music support depends on the session name Windows gives the Apple Music app. If lyrics do not appear for it, check the log and open an issue.

## Troubleshooting

**The strip is blank.** Make sure the Spotify or Apple Music desktop app is open and a song is actually playing (the web player does not report to Windows). Check `logs/spoti-lyrics.log`; the first line shows the version and a `playback source` line shows whether Windows media controls or the window title fallback is in use.

**Lyrics never appear for one song.** Lyrics coverage varies. The log lists each source and why it was rejected (look for `cross-check` lines).

**Check the strip is really drawing.** Run `python app.py --selftest`. It builds the strip with a demo song and writes one line to `logs/selftest-result.txt`: `SELFTEST PASS ... lyric_pixels_on_screen=N` with N above 0 means the text is on your screen, and `SELFTEST FAIL` gives the reason.

**The strip is invisible.** About 2 seconds after start the app writes a `window diagnostics` line to the log. To find which piece breaks it, start the app with one of these set (in a terminal, for example `set SPOTI_DEBUG_NO_CLICKTHROUGH=1` and then `python LyricsOverlay.pyw`; `SPOTI_NO_DETACH=1` keeps it in that terminal):

| variable | effect |
| --- | --- |
| `SPOTI_DEBUG_NO_CLICKTHROUGH=1` | never touches the window's extended styles (lock only stops dragging) |
| `SPOTI_DEBUG_NO_INVALIDATE=1` | never asks Windows for repaints |
| `SPOTI_DEBUG_NO_DPI=1` | does not enable DPI awareness |
| `SPOTI_DEBUG_DIAGNOSTICS=1` | logs the diagnostics line again at 6 s and on lock toggles |

**Start with Windows stopped working after moving the app.** Untick and tick it again in the tray menu. The entry is also repaired automatically each time the app is started by hand.

## Project layout

```
app.py                  # tkinter GUI, polling loop, strip rendering (LyricsApp)
media_client.py         # reads what is playing from Windows media controls (Spotify, Apple Music, iTunes)
updater.py              # GitHub release check
version.py              # app version and repo address
lyrics_provider.py      # QQ / NetEase / LRCLIB fetch, disk cache, dead source breaker
lyrics_check.py         # cross checks the sources' answers
netease_crypto.py       # NetEase weapi/eapi request encryption (word timed lyrics)
textnorm.py             # numbers and symbols turned into spoken words
layout.py               # line splitting, chunk timing, karaoke fill maths
drawing.py              # colour maths and outlined text helpers
config.py               # settings, appearance.json, window state
config_editor.py        # settings window (live applied)
tray.py                 # system tray icon and menu (pystray)
winsys.py               # console free relaunch, single instance, start with Windows, DPI, click through
winddiag.py             # window diagnostics
applog.py               # file only logging and crash capture
paths.py                # where data lives (source folder, or %LOCALAPPDATA%\LycRomanise for the .exe)
selftest.py             # python app.py --selftest
spotify_client.py       # optional old Spotify API mode
assets/lyrics-overlay.ico
romanize/               # korean.py japanese.py chinese.py others.py detect.py
```

## Credits

Inspired by [Lyricify](https://github.com/WXRIW/Lyricify-App). LycRomanise is an original implementation built in Python. Lyrics come from QQ Music, NetEase Cloud Music and [LRCLIB](https://lrclib.net). Created by Lycro.
