# LycRomanise v1.2.0

A small Windows desktop overlay that shows synced, scrolling lyrics for whatever is playing on
Spotify or Apple Music, with **romanized lyrics** for Korean, Japanese and Chinese songs (romaja,
romaji, pinyin). Inspired by [Lyricify](https://github.com/WXRIW/Lyricify-App).

An original implementation written in Python. Not affiliated with Spotify, Apple, or Lyricify/WXRIW.

## Download

Get `LycRomanise_Setup.exe` (installer) or `LycRomanise.exe` (portable) from the
[Releases](https://github.com/Lycro0/LycRomanise/releases) page. No account, API key or login is needed.

## What it does

- Reads what is playing from the **Windows media controls**, so it works with the Spotify desktop
  app, Apple Music for Windows and iTunes. If both are open, whichever is playing wins.
- Fetches synced lyrics from [LRCLIB](https://lrclib.net) (free, no key) and checks them before use.
- Shows a small, frameless, always-on-top **strip** with the current line (karaoke-colored, yellow
  turning to pink as the line is sung) and the next line below it, with a slide transition between lines.
- Romanizes Korean, Japanese and Chinese lyrics (always on, no translation). Cyrillic, Greek,
  Hebrew, Arabic-script and Devanagari lyrics are romanized too, including mixed-script lines.
  Hebrew and Arabic are written without most vowels, so their vowels are guessed: treat them as approximate.
- Spells out numbers and symbols before romanizing (`24` becomes "twenty-four", `&` becomes "and").
- Splits long lines into balanced chunks; text that is too wide shrinks to fit, then wraps.
- Ignores Spotify's DJ announcements ("Up next", DJ X) and ads: no lookup, no title card.
- Runs as its own app with no console window, with a tray icon.
- Remembers the strip's position and lock state. Lyrics clear on pause or stop.
- Is sharp on scaled displays (system DPI awareness).

## Using it

- **Move it:** drag the strip while it is unlocked.
- **Lock it:** hover the strip and click the small padlock that appears at the top, or use the tray
  menu. A locked strip is click-through on Windows, so clicks reach whatever is underneath. The
  padlock stays clickable.
- **Tray menu:** Lock/Unlock position, Settings…, Start with Windows, Check for updates, Open logs
  folder, Quit. There is no taskbar button and no right-click menu.
- **Settings…** (from the tray) changes karaoke colors, next-line color, font sizes, strip width and
  height, lyric offset, outline, the title card and Start with Windows. Changes apply live and are saved
  to `appearance.json`. "Reset to defaults" restores the built-in look.
- **Lyric offset** shifts all lyrics in 100 ms steps (`lyric_offset_ms`).
- **Title card** (off by default): turn on `show_title_card` to show "Artist - Song" before the first line.
- **Start with Windows** adds a per-user startup entry (no admin rights needed).

The karaoke fill is an estimate. LRCLIB gives a timestamp for the start of each line, not for each
word, so the fill assumes an even pace across the line.

## Lyrics

Lyrics come only from LRCLIB. Whatever comes back is checked first (`lyrics_check.py`), because
showing nothing is better than showing the wrong song:

1. "No lyrics" placeholders ("纯音乐，请欣赏", "instrumental") are dropped.
2. Lyrics that run more than 8 s past the end of the song are dropped as another edition. This is
   skipped when the matched LRCLIB entry's own length is within 3 s of the song's.
3. All-Chinese lyrics for a song whose title or artist is Korean or Japanese (a translation) are dropped.
4. Bilingual lines (original and translation at the same time) are cut down to the song's own language.

**Matching.** Results are ranked by title, artist and length. A result within about 6 s is preferred.
An exact title and artist match up to 30 s off is still used when nothing closer exists (an album
version against a video version). If "title + artist" finds nothing, the lookup is retried with a
cleaned title (`(feat. X)`, `(with X)`, `- Remastered` and bracketed romanizations removed), then the
first artist only, then a free text search. Accents are ignored when comparing names, and look-alike
apostrophes (′ ´ ’) are also tried as `'` and with no apostrophe.

**Editions.** A tag in the title ("English Version", "Chinese Ver.", "中文版", Live, Remix, Acoustic,
Instrumental) is read. An untagged title is taken to be the original, so a result tagged as another
edition loses to an untagged one, and a matching tag wins. LRCLIB can hold several language versions
with the same title and length. The app has no language preference: it only prefers a language when
the title, artist or album is written in Japanese, Korean or Chinese, or the title names an edition.
Otherwise the first usable entry is used.

**Wrong length after a skip.** Windows can report the previous song's length for a few seconds. The
app waits up to about 2 s while a new song's length equals the previous one's, and looks the lyrics
up again if the length later changes by more than 3 s. The log line
`looking up lyrics for ... (track length Ns)` shows which length was used.

**If LRCLIB is down.** After 3 failed calls in a row it is skipped for 60 seconds (state in
`provider_health.json`; delete it to reset). If LRCLIB was erroring, the song is retried after 15,
30 and 60 seconds.

Results are cached in `lyrics_cache.json`, so replays make no request. Lyrics coverage is not
universal: LRCLIB is community-made, so some tracks have no synced lyrics. Lyrics load when a song starts.

## Updates

On startup the app checks the latest GitHub release of Lycro0/LycRomanise. If it is newer than
`version.py`, a window offers **Update now / Later / Skip this version**. Update now downloads the
file from the release, checks its size and SHA-256, installs it and restarts the app. Tray >
**Check for updates** does the same on demand. Turn the check off with `"check_updates": false` in `config.json`.

- An install made with `LycRomanise_Setup.exe` downloads and runs that file silently.
- A portable `LycRomanise.exe` downloads the new `LycRomanise.exe` and swaps it in after the app closes.
- A copy run from source, a portable exe in a read-only folder, or a release missing the matching
  file just opens the release page.
- "Skip this version" is remembered in `skipped_update.txt` until a newer release appears.

**For each release:** bump `version.py`, `installer.iss` and `version_info.txt`, tag the release
`v<version>`, and attach **both** `LycRomanise_Setup.exe` and `LycRomanise.exe` with exactly those names.

## Running from source

Needs Windows and Python 3.9+.

```bash
pip install -r requirements.txt
```

Then double-click `LyricsOverlay.pyw` (no console window), or run `python app.py`, which starts a
separate console-less copy and returns. Only one copy runs at a time. Quit from the tray icon.

The `winrt-*` packages read the Windows media controls. If they are missing, or Windows shows no
session, the app falls back to the Spotify window title ("Artist - Song"), which has no length or
position, so timing is counted from when the song was first seen and can drift after a seek.

**Building the .exe:** `build_exe.bat` (PyInstaller, uses `LycRomanise.spec`) and `build_installer.bat`
(Inno Setup, uses `installer.iss`). `make_icon.py` regenerates `assets/lyrics-overlay.ico`.

## Files and logs

When installed, settings, cache and logs are in `%LOCALAPPDATA%\LycRomanise`. From source they are next to the app.

| File | Contents |
| --- | --- |
| `appearance.json` | colors, sizes, offset, title card (changed in Settings) |
| `window_state.json` | strip position and lock state |
| `config.json` | optional: `"check_updates": false` turns the update check off |
| `lyrics_cache.json` | cached lyrics |
| `provider_health.json` | LRCLIB outage state |
| `logs/spoti-lyrics.log` | everything, rotating daily, 14 days kept |
| `logs/crash-native.log` | hard interpreter crashes |

The log says why a lookup missed: no results, the HTTP status, or every candidate that was rejected
and why. Don't share `logs/` or `lyrics_cache.json` publicly: they list the songs you played.

## Project layout

```
app.py              GUI, polling loop, the strip
media_client.py     reads Spotify / Apple Music from the Windows media controls
lyrics_provider.py  LRCLIB lookup, matching, cache, outage handling
lyrics_check.py     checks lyrics before use
config.py           settings files and paths
config_editor.py    Settings window
updater.py          GitHub update check and install
update_dialog.py    the update window
tray.py             tray icon and menu
winsys.py           single instance, start with Windows, DPI, click-through
paths.py            where files live
version.py          app version
layout.py           line splitting and karaoke fill
textnorm.py         numbers and symbols to words
drawing.py          colors and outlined text
applog.py           file logging and crash capture
selftest.py         the --selftest check
winddiag.py         window diagnostics
romanize/           korean.py, japanese.py, chinese.py, others.py, detect.py
assets/             lyrics-overlay.ico
```

## Romanization

- **Korean:** `romanize/korean.py` implements Revised Romanization from scratch, with no dictionary
  and no external service. It handles liaison (한국어 becomes `hangugeo`) but not dictionary-dependent
  rules such as palatalization or tensification.
- **Japanese:** `pykakasi`. **Chinese:** `pypinyin`.
- **Others:** `romanize/others.py` (Cyrillic, Greek, Hebrew, Arabic, Devanagari).

## Troubleshooting

**Check the strip is drawing:** `python app.py --selftest` builds the strip with a demo song and
writes one line to `logs/selftest-result.txt`. `SELFTEST PASS ... lyric_pixels_on_screen=N` with N > 0
means the text is on your screen. `SELFTEST FAIL` gives the reason.

**Strip invisible:** about 2 s after start the app writes a `window diagnostics (...)` line to the
log, with a count of lyric pixels on screen. N > 0 with a song playing means the text is visible. To
find which piece breaks on your machine, set one of these before starting:

| variable | effect |
| --- | --- |
| `SPOTI_DEBUG_NO_CLICKTHROUGH=1` | never touches the window's extended styles (lock only stops dragging) |
| `SPOTI_DEBUG_NO_INVALIDATE=1` | never asks Windows for repaints |
| `SPOTI_DEBUG_NO_DPI=1` | does not enable DPI awareness |
| `SPOTI_DEBUG_DIAGNOSTICS=1` | logs the diagnostics line again at 6 s and on lock toggles |
| `SPOTI_NO_DETACH=1` | keeps the app attached to the terminal (output still goes only to the log) |

**Wrong lyrics or no lyrics:** play the song once and check `logs/spoti-lyrics.log`. The
`looking up lyrics for ...` and `lrclib: ...` lines show the length used and the entry chosen or why
every candidate was rejected.
