# LycRomanise: full details (v2.0)

The short version is in [README.md](../README.md). This page has everything else.

## The v2 rewrite

v1 was a Python app (Tkinter, pystray, pykakasi, pypinyin, bundled with PyInstaller). v2 rewrites it from scratch in **C# on .NET 10 with WPF**:

- **Native and self-contained.** One exe with the runtime inside; nothing else to install.
- **GPU rendering.** Each line's text shape is built once, and every frame only moves the karaoke sweep. When the music is paused nothing redraws, so the app uses no CPU.
- **Windows' own building blocks.** Playback info comes from the Windows media controls (SMTC), and Japanese kanji are read by Windows' Japanese dictionary (the one the IME uses) instead of a bundled one.
- **Settings carried over.** On first start, colours, strip width, timing offset and the web view settings are imported from v1's `appearance.json`.

## Lyric sources

Asked from the top down; the order and on/off switches are in Settings → Lyric sources. A word-synced answer ends the search; a line-synced one is kept while the rest are asked for word sync (turn off "Keep looking for word sync" to stop at the first synced one).

| Source | What it gives |
| --- | --- |
| Spicy Lyrics | Community syllable syncs, duets, backing vocals. Needs your own free publishable key (`sl_pk_…`) from developers.spicylyrics.org, stored encrypted. |
| BiniLyrics | Apple Music-style TTML by ISRC (lrc.red), with Apple's own romanization |
| Imported lyrics | Your own `.lrc` (including enhanced word-timed LRC), `.ttml` or `.txt` files |
| Musixmatch | Large licensed catalog; word sync (richsync) on many songs; community translations |
| Unison | Community TTML library |
| NetEase Cloud Music | Word timing (yrc); strong on Chinese music |
| LRCLIB | Open library of line-synced lyrics |
| QQ Music | Mandarin, Cantonese and other Asian songs, line synced |
| KuGou | Word timing (krc) for Asian songs |
| Genius | Untimed lyrics as a last resort |

Spicy Lyrics and BiniLyrics look songs up by Spotify track ID and ISRC. Desktop players don't report those, so LycRomanise gets them from Musixmatch's matcher, or from Spotify directly when you're logged in.

Answers are kept in a cache, so a song you play again shows its lyrics at once. **Choose lyrics** (tray, strip or lyrics window) asks every source at once and lets you pick, optionally for good for that song.

## Your own lyric files

Put files named `Artist - Title.lrc` (or `.ttml` / `.txt`) in the imported lyrics folder; subfolders are searched too. Or use Settings → Now playing → **Import lyrics file…** while the song plays. LRC files that list each line twice (original and romanization at the same time) are understood.

## Full lyrics window

Open it with the tray icon, by double-clicking the strip, with **Ctrl + Alt + L**, or from the **Full lyrics** bar in Settings. Scroll freely; it follows the song again after 4 seconds. Click a line to jump there, or click the progress bar to seek. **Space** plays and pauses, **F11** maximizes, **Ctrl + / Ctrl −** change the text size. Buttons at the top right copy the lyrics, choose other lyrics, keep the window on top and hide the player panel.

## Spotify login (optional)

Settings → Spotify account. Logging in lets LycRomanise read exactly which track Spotify is playing and what's queued next: it looks up the next song's lyrics ahead of time, uses the exact track ID, and gets the full-size cover. Only reading is used, never playback control.

Spotify only lets Premium accounts create developer apps. One person with Premium creates the app (redirect URI `http://127.0.0.1:43821/callback`, Web API ticked) and adds friends under **User Management**; everyone then uses the same Client ID with their own login, free accounts included. Without a login everything else still works.

## Keyboard shortcuts

| Keys | Does |
| --- | --- |
| Ctrl + Alt + L | Open or close the full lyrics window |
| Ctrl + Alt + K | Lock or unlock the strip |
| Ctrl + Alt + H | Show or hide the strip |
| Ctrl + Alt + ← / → | This song's lyrics earlier / later |
| Ctrl + Alt + Space | Play / pause |
| Ctrl + mouse wheel on the strip | Nudge this song's timing |

They can be turned off in Settings → General.

## Updates

LycRomanise checks GitHub releases at start-up and every six hours. An installed copy downloads `LycRomanise_Setup.exe` and installs it silently; a portable copy swaps in the new `LycRomanise.exe`. Each download is checked against the SHA-256 that GitHub publishes for the file before it runs.

## Files

Settings, the lyrics cache and logs are in `%APPDATA%\LycRomanise Modern`. The song title file for OBS is `LycRomanise.txt` there too. Uninstalling keeps them.

## Troubleshooting

- **No lyrics:** use **Choose lyrics** to see what every source has, or **Search lyrics again** in the tray menu. Check `logs\lycromanise.log`.
- **Lyrics early or late:** Settings → Timing, or Ctrl + Alt + ← / →.
- **Strip can't be clicked:** it's locked. Unlock it from the tray icon or with Ctrl + Alt + K.
- **Genius never answers:** it needs Windows' built-in `curl.exe` (Windows 10 1803 or later).

## Building it yourself

Needs the .NET 10 SDK and Inno Setup 6. Run `Builder\build.bat`; it writes `App\LycRomanise.exe` and `App\LycRomanise_Setup.exe`. Releases are built by GitHub Actions (`.github/workflows/release.yml`) whenever a `v*` tag is pushed, with notes from `Release Notes/v<version>.md`.

`Source Code/LycRomanise` layout: `Core` (models, settings, HTTP, Spotify, updater), `Sources` (the ten lyric sources and the LRC/TTML/yrc/krc parsers), `Romanize` (Korean, Japanese, Chinese and other scripts), `Media` (Windows media controls), `UI` (strip, lyrics window, settings, tray, picker), `Web` (phone and OBS view).
