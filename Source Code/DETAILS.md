# LycRomanise: full details (v1.8.1)

The short version is in [README.md](README.md). This page has everything else.

A small Windows desktop overlay that shows synced, scrolling lyrics for whatever is playing on
Spotify, Spotifast or Apple Music, with **romanized lyrics** for Korean, Japanese and Chinese songs (romaja,
romaji, pinyin). Inspired by [Lyricify](https://github.com/WXRIW/Lyricify-App).

An original implementation written in Python. Not affiliated with Spotify, Spotifast, Apple, or Lyricify/WXRIW.

## Download

Get `LycRomanise_Setup.exe` (installer) or `LycRomanise.exe` (portable) from the
[Releases](https://github.com/Lycro0/LycRomanise/releases) page. No account, API key or login is needed.

## What it does

- Reads what is playing from the **Windows media controls**, so it works with the Spotify desktop
  app, [Spotifast](https://spotifast.rocks) (the open source, low-memory Spotify client), Apple Music
  for Windows and iTunes. If more than one is open, whichever is playing wins.
- Fetches synced lyrics from [LRCLIB](https://lrclib.net) (free, no key), then from [lrcmux](https://lrcmux.dev) when LRCLIB has
  nothing, and checks them before use. More LRCLIB-compatible servers can be added (see "More lyric sources" below).
- Shows a small, frameless, always-on-top **strip** with the current line (karaoke-colored, yellow
  turning to pink as the line is sung) and the next line below it, with a slide transition between lines.
- Romanizes Korean, Japanese and Chinese lyrics (always on, no translation). Cyrillic, Greek,
  Hebrew, Arabic-script and Devanagari lyrics are romanized too, including mixed-script lines.
  Hebrew and Arabic are written without most vowels, so their vowels are guessed: treat them as approximate.
- Shows numbers and symbols as they are written in the lyrics.
- Shows a ♪ during long instrumental breaks instead of leaving the last lyric on screen.
- Keeps a text file, `LycRomanise.txt`, with the song that is playing, for an OBS text source.
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
- **Hide the padlock:** turn on "Hide the lock icon" in Settings (`hide_lock_icon`). The padlock never
  appears, and the strip is locked and unlocked only from the tray menu (Lock position / Unlock
  position). If the tray icon isn't running, the padlock is kept so the strip can still be unlocked.
- **Show it in OBS:** add a Window Capture source and pick "LycRomanise Lyrics" (capture method "Windows 10
  (1903 and later)"). The strip has real per-pixel transparency by default, so OBS shows it see-through with no
  filter. If OBS still doesn't list it, turn on "Show a taskbar button for the strip" in Settings
  (`show_in_taskbar`, applies live). If the strip ever fails to draw with true transparency (or Pillow / Segoe UI
  is missing), the app logs it and falls back to the old colour-key strip, which OBS shows with a black
  background (add a Color Key filter set to black).
- **Tray menu:** Lock/Unlock position, Settings, Start with Windows, Web view, Show web view, Wrong lyrics? Pick another, Check for updates, Open logs folder, Quit. Lyrics timing and "Look up lyrics again" are in Settings, on the Timing tab.
  folder, Quit. By default there is no taskbar button (see "Show a taskbar button" in Settings) and no right-click menu.
- **Settings…** (from the tray) has four tabs: Look (colours, outline, title card), Text & size (strip size, lyric offset), Behaviour (lock icon, taskbar button, reset position, Start with Windows) and Phone & OBS (mobile view, OBS text file, hide the strip on this PC). Changes apply live and are saved
  to `appearance.json`. "Reset to defaults" restores the built-in look.
- **Lyric offset** shifts all lyrics in 100 ms steps (`lyric_offset_ms`).
- **Per-song timing:** Ctrl+Alt+Left / Right nudges the current song's lyrics by 100 ms and remembers it for that song (`song_offsets.json`); Ctrl+Alt+Down resets it.
- **Title card** (`show_title_card`, on by default): the artist and song name show for about 10 seconds when a song starts (it ends
  sooner if the first lyric begins before that). Only the artist and title are shown, no lyric under it. It is drawn in the
  "Already sung" colour with the text outline colour you pick.
- **Text outline:** Settings has a "Text outline" colour (`outline_rgb`). It is used for every line on the strip, the title
  card included.
- **Mobile view** (off by default, `mobile_enabled`): tick "Show the lyrics on phones and tablets" in Settings (or use the tray
  menu). LycRomanise then serves a small web page on your network: open `http://<this PC's address>:8765` in Safari or Chrome on
  a phone/tablet on the same Wi-Fi (or scan the QR code from "Show address / QR code…"). The page shows the same karaoke line,
  next line and title card with your colours and outline. Tap it once for full screen.
  **OBS:** add a Browser source with the same address, `http://<this PC's address>:8765`, for a transparent, lyrics-only page (the lyrics keep the same shape as the strip on your PC, 720x150 by default, and the whole strip is scaled to fit the
  browser source, so any source size works and the lyrics are never squashed). The page is transparent in OBS
  (OBS browser sources are detected automatically, so even the plain address is see-through there).
- **OBS text file** (`now_playing_file`, on by default): LycRomanise keeps `LycRomanise.txt` up to date with one line, for
  example `Steve Lacy - Love 2 Fast`. In OBS add a **Text (GDI+)** source, tick **Read from file** and pick that file. Settings,
  Phone & OBS, shows its full path and has **Copy path** and **Open folder** buttons. The text stays on the song while it is
  paused, is emptied when nothing has been playing for 10 seconds (so skips and ads do not flicker it) and when LycRomanise quits.
  Choose what it says with **Text** in the same card (`now_playing_format`: `artist_title`, `title_artist` or `title`). To keep the
  file somewhere else, set `"now_playing_path"` in `config.json` to a full file path or to a folder. The text is written as it
  is in the player (not romanized), as UTF-8.
- **Hide the lyrics on this PC** (`hide_desktop_strip`): tick it in Settings when you only want the lyrics in the OBS
  browser source or on phones. The strip then draws nothing on your screen. It only applies while the Mobile view is on.
- **Reset lyrics position:** the button in Settings puts the strip back in its default place (centred near the top). The port can be changed (`mobile_port`).
  Windows may ask to allow LycRomanise through the firewall the first time: allow it on Private networks. Nothing is sent
  outside your network, and only the lyric text currently on the strip is served.
- **Start with Windows** adds a per-user startup entry (no admin rights needed).

The karaoke fill is an estimate. LRCLIB gives a timestamp for the start of each line, not for each
word, so the fill assumes an even pace across the line.

## Lyrics

Lyrics come from your own `.lrc` files first (see below), then LRCLIB, then lrcmux. Whatever comes back is checked first (`lyrics_check.py`), because
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

**More lyric sources.** lrcmux asks several lyrics sites at once and answers in the same format as LRCLIB, so it goes through the
same matching and checks. It is only asked when LRCLIB has no usable lyrics. Your own LRCLIB-compatible server (or a mirror) can be added
under `"extra_lyrics_apis"` in `config.json`, for example `"extra_lyrics_apis": ["https://lrclib.example.org/api"]` (https only, up to 4,
asked after lrcmux). The log names the source that answered (`lyrics for ... found via lrcmux`).

**If a source is down.** After 3 failed calls in a row it is skipped for 60 seconds (state in
`provider_health.json`; delete it to reset). If LRCLIB was erroring, the song is retried after 15,
30 and 60 seconds.

Results are cached in `lyrics_cache.json` (and songs LRCLIB doesn't have are remembered for 6 hours in `lyrics_missing.json`), so replays make no request. Lyrics coverage is not
universal: LRCLIB is community-made, so some tracks have no synced lyrics. Lyrics load when a song starts.

## Updates

On startup the app checks the latest GitHub release of Lycro0/LycRomanise. If it is newer than
`version.py`, a window offers **Update now / Later / Skip this version**. Update now downloads the
file from the release, checks its size and SHA-256 (a release without a published checksum is never installed
automatically: the release page opens instead), checks the file again just before it runs, installs it and restarts the app. Tray >
**Check for updates** does the same on demand. Turn the check off with `"check_updates": false` in `config.json`.

- An install made with `LycRomanise_Setup.exe` downloads and runs that file silently.
- A portable `LycRomanise.exe` downloads the new `LycRomanise.exe` and swaps it in after the app closes.
- A copy run from source, a portable exe in a read-only folder, or a release missing the matching
  file just opens the release page.
- "Skip this version" is remembered in `skipped_update.txt` until a newer release appears.

**For each release:** bump `version.py` here and `installer.iss` and `version_info.txt` in the `builder` folder, tag the release
`v<version>`, and attach **both** `LycRomanise_Setup.exe` and `LycRomanise.exe` with exactly those names.

## Running from source

Needs Windows and Python 3.9+.

```bash
pip install -r requirements.txt
```

Then double-click `LyricsOverlay.pyw` (no console window), or run `python app.py`, which starts a
separate console-less copy and returns. Only one copy runs at a time. Quit from the tray icon.

The `winrt-*` packages read the Windows media controls. If they are missing, or Windows shows no
session, the app falls back to the player's window title (artist, a dash, then the song), which has no length or
position, so timing is counted from when the song was first seen and can drift after a seek.

**Building the .exe:** everything for that is in the `builder` folder next to this one: `build_exe.bat` (PyInstaller, uses
`LycRomanise.spec`) and `build_installer.bat` (Inno Setup, uses `installer.iss`). `make_icon.py` there regenerates `assets/lyrics-overlay.ico`.

## Files and logs

When installed, settings, cache and logs are in `%LOCALAPPDATA%\LycRomanise`. From source they are next to the app.

| File | Contents |
| --- | --- |
| `appearance.json` | colors, sizes, offset, title card, lock icon, OBS text file (changed in Settings) |
| `LycRomanise.txt` | the song that is playing, for an OBS text source |
| `window_state.json` | strip position and lock state |
| `config.json` | optional: `"check_updates": false` turns the update check off; `"extra_players"`, `"lyrics_folders"`, `"extra_lyrics_apis"` and `"now_playing_path"` (see above) |
| `lyrics_cache.json` | cached lyrics |
| `song_offsets.json` | the timing tweaks you made per song |
| `provider_health.json` | lyrics source outage state |
| `logs/lycromanise.log` | everything, rotating daily, 14 days kept |
| `logs/crash-native.log` | hard interpreter crashes (a few lines about a thread are harmless; see Troubleshooting) |

The log says why a lookup missed: no results, the HTTP status, or every candidate that was rejected
and why. Don't share `logs/` or `lyrics_cache.json` publicly: they list the songs you played.

## Project layout

```
app.py              GUI, polling loop, the strip
media_client.py     reads Spotify / Spotifast / Apple Music from the Windows media controls
lyrics_provider.py  LRCLIB and lrcmux lookup, matching, cache, outage handling
nowplaying.py       the LycRomanise.txt file for OBS
alpha_window.py     the strip drawn with true per-pixel transparency
lyrics_check.py     checks lyrics before use
config.py           settings files and paths
config_editor.py    Settings window
updater.py          GitHub update check and install
update_dialog.py    the update window
lyrics_picker.py    the "Wrong lyrics?" window
mobile_server.py    the built-in web view (phones, tablets, OBS browser source)
mobile_dialog.py    the web view address and QR code windows
song_offsets.py     per-song timing offsets
tray.py             tray icon and menu
winsys.py           single instance, start with Windows, DPI, click-through
paths.py            where files live
version.py          app version
layout.py           line splitting and karaoke fill
drawing.py          colors and outlined text
applog.py           file logging and crash capture
romanize/           korean.py, japanese.py, chinese.py, others.py, detect.py
assets/             lyrics-overlay.ico
```

## Your own lyrics, more players, wrong lyrics

- **Your own `.lrc` files:** save synced lyrics as `Artist - Title.lrc` (or `Title - Artist.lrc`, or `Title.lrc`)
  in the `lyrics` folder next to `lyrics_cache.json`, or in any folder listed in `config.json`:
  `"lyrics_folders": ["D:\\Music\\Lyrics"]`. They are used before LRCLIB. Use *Look up lyrics again* after editing a file.
- **More players:** Spotify, Spotifast, Apple Music, iTunes, Tidal, Deezer, Amazon Music, Windows Media Player,
  foobar2000, MusicBee and the YouTube Music desktop apps work out of the box. For anything else add part of its
  Windows media-session id to `config.json`: `"extra_players": ["vlc", "chrome"]` (the log lists the ids Windows
  reported when nothing matched). A browser tab such as YouTube is read like any other player, so only add `chrome` or `msedge` if you want that.
- **Wrong lyrics?** Tray menu > *Wrong lyrics? Pick another...* lists the other lyric sets LRCLIB (and lrcmux, when LRCLIB has few) have for the song
  (closest in length first). Double-click one to use it; it is remembered for that song.

## Romanization

- **Korean:** `romanize/korean.py` implements Revised Romanization from scratch, with no dictionary
  and no external service. It applies the standard pronunciation rules (liaison, nasalization such as 합니다 -> `hamnida`,
  ㄹ assimilation such as 신라 -> `silla`, ㅎ aspiration, palatalization). Like RR itself it does not write
  tensification, and dictionary-dependent irregular readings are not handled.
- **Japanese:** `pykakasi`, with fixes in `romanize/japanese.py`: particles (は wa, へ e, を o), the everyday reading of common words and lone kanji (今日, 君, 私...), and long vowels. pykakasi has no grammar, so a は inside a hiragana run is only turned into `wa` at the end of the run (これは, あなたは).
- **Chinese:** `pypinyin`, with tone changes (不, 一) where the installed version supports them and tidied punctuation.
- **Others:** `romanize/others.py` (Cyrillic, Greek, Hebrew, Arabic, Devanagari).

## Troubleshooting

**Strip invisible or in the wrong place:** use *Reset lyrics position* in Settings > Window, and make sure the
tray icon shows the strip is unlocked. If it still doesn't appear, look at `logs/lycromanise.log` for lines about
"true transparency". If that fails, the app switches to the plain strip by itself.

**Started from a terminal:** `python app.py` hands over to a console-less copy and exits. Set
`SPOTI_NO_DETACH=1` to keep it attached to the terminal (output still goes only to the log).

**A player isn't picked up:** the log line `no supported player among the Windows media sessions: [...]`
lists what Windows is showing, and `reading playback from media session ...` names the one in use. If
your player is listed there but not read, its name needs adding to `APP_MATCHES` in `media_client.py`.

**Music note shown as a square:** fixed in v1.8.0. The true transparency strip draws with Segoe UI, which has no ♪, so it now
borrows the glyph from Segoe UI Symbol.

**`crash-native.log` has lines about code 0x8001010e:** these were Windows complaining that a media-session object was released on
the wrong thread. v1.8.0 keeps all media-session calls on one thread, so no new lines should appear. They never stopped the app.

**Wrong lyrics or no lyrics:** play the song once and check `logs/lycromanise.log`. The
`looking up lyrics for ...` and `lrclib: ...` / `lrcmux: ...` lines show the length used and the entry chosen or why
every candidate was rejected.

## Privacy and security notes

- Logs (`logs/`) keep song titles and artists for 14 days, and `lyrics_cache.json` lists the songs you played. Don't share them publicly.
- "Start with Windows" is off by default; turn it on in Settings if you want it.
- The mobile page only answers devices on your local network (private address ranges); other addresses are refused. It
  also only answers requests addressed by IP address, `localhost` or this PC's own name (`MYPC`, `MYPC.local`, `MYPC.lan`),
  which stops a web page from reaching it through DNS rebinding. If you reach the PC by another name, list it under
  `"mobile_allowed_hosts": ["name"]` in `config.json`. The page is sent with a strict Content-Security-Policy.
- Lyrics from the internet are size-limited (reply size and time, number and length of lines) and checked before they
  are cached or drawn; a damaged cache entry is dropped and looked up again.
- Automatic update checks never open your browser. If this copy can't update itself, use tray > Check for updates.
