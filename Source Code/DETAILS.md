# LycRomanise: full details (v2.2)

The short version is in [README.md](../README.md).

## The v2 rewrite

Version 1 was a Python app (Tkinter, pystray, pykakasi and pypinyin, bundled with PyInstaller). Version 2 is written from scratch in C# on .NET 10 with WPF.

It ships as one exe with the .NET runtime inside, so there is nothing else to install. Each line's text shape is built once and each frame only moves the karaoke sweep, so drawing is cheap; when the music is paused nothing redraws at all. Playback information comes from the Windows media controls (SMTC), and Japanese kanji are read with Windows' own Japanese dictionary, the one the IME uses. On first start, the colours, strip width, timing offset and web view settings are copied from v1's `appearance.json`.

## Lyric sources

The sources are asked from the top of the list down. You can reorder them and turn them off in Settings → Lyric sources. A word-synced answer ends the search. A line-synced one is kept while the rest are asked for word sync; turn off "Keep looking for word sync" to stop at the first synced answer.

| Source | What it gives |
| --- | --- |
| Spicy Lyrics | Community syllable syncs, duets and backing vocals. Needs your own free publishable key (`sl_pk_…`) from developers.spicylyrics.org, which is stored encrypted. |
| BiniLyrics | Apple Music TTML found by ISRC (lrc.red), with Apple's own romanization |
| Imported lyrics | Your own `.lrc` (including enhanced word-timed LRC), `.ttml` or `.txt` files |
| Musixmatch | A large licensed catalog, word sync (richsync) on many songs, and community translations |
| Unison | A community TTML library |
| NetEase Cloud Music | Word timing (yrc), with good coverage of Chinese music |
| LRCLIB | An open library of line-synced lyrics |
| QQ Music | Mandarin, Cantonese and other Asian songs, line synced |
| KuGou | Word timing (krc) for Asian songs |
| Genius | Untimed lyrics, used when nothing else has the song |

Spicy Lyrics and BiniLyrics look songs up by Spotify track ID and ISRC. Desktop players don't report those, so LycRomanise gets them from Musixmatch's matcher, or straight from Spotify when you're logged in.

Answers are cached, so a song you play again shows its lyrics at once. Choose lyrics (in the tray menu, on the strip or in the lyrics window) asks every source at once and lets you pick one, and you can keep that choice for the song.

The desktop strip never shows where the lyrics came from. The full lyrics window and the Now playing page in Settings do.

## Your own lyric files

Put files named `Artist - Title.lrc` (or `.ttml` or `.txt`) in the imported lyrics folder; subfolders are searched too. You can also use Settings → Now playing → Import lyrics file… while the song plays. LRC files that list each line twice, once in the original and once romanized, at the same time stamp, are read correctly.

## Looks

Settings → Appearance starts with a gallery of looks. One click sets the layout, colours, outline, glow, background and how the words move, and you can change any part of it afterwards.

| Look | What it is |
| --- | --- |
| Classic | The original LycRomanise layout and the default: the line being sung at the top, the next line straight under it, yellow turning pink, romanized. The next line rises and grows into place. |
| Modern | Original and romanized together, a bolder outline, and the next line at the bottom |
| Apple Music | White words that light up, lift and glow as they're sung, on frosted glass |
| Spotify | Grey turning white on a dark card |
| Neon | Cyan turning magenta, with a glow |
| Karaoke | Big white words filling blue, with a thick outline |
| Minimal | Only the line being sung |
| Sunset and Mint | Colour variations of Modern |

The strip layout (Classic, Modern or Minimal), word lift and the glow on sung words can also be set on their own.

## Full lyrics window

Open it from the tray icon, by double-clicking the strip, with Ctrl + Alt + L, or with the Full lyrics bar in Settings. The desktop strip hides while it is open and comes back when you close or minimize it; you can turn that off.

Press S or click the gear for its settings: the motion style (Apple Music, Glow, Float, Calm or Snappy), a blurred-cover or plain black background, romanized, original or both, translations, backing vocals, text size, timing for the current song, keep on top, and whether the player panel shows. The buttons fade out while you watch and come back when you move the mouse.

You can scroll freely, and the window follows the song again after 4 seconds. Click a line to jump there, or click the progress bar to seek. Space plays and pauses, F11 maximizes, Esc closes the settings panel or the window, and Ctrl + and Ctrl − change the text size.

## Spotify login (optional)

Logging in from Settings → Spotify account lets LycRomanise see exactly which track Spotify is playing and what is queued next. It uses that to load the next song's lyrics early, to look songs up by their exact track ID, and to get the full-size cover. It only reads; it never controls playback.

Spotify only lets Premium accounts create developer apps. One person with Premium creates the app (redirect URI `http://127.0.0.1:43821/callback`, with Web API ticked) and adds friends under User Management. Everyone then uses the same Client ID with their own login, free accounts included. Everything else works without a login.

## Keyboard shortcuts

| Keys | Does |
| --- | --- |
| Ctrl + Alt + L | Open or close the full lyrics window |
| Ctrl + Alt + K | Lock or unlock the strip |
| Ctrl + Alt + H | Show or hide the strip |
| Ctrl + Alt + ← / → | Move this song's lyrics earlier or later |
| Ctrl + Alt + Space | Play or pause |
| Ctrl + mouse wheel on the strip | Nudge this song's timing |

You can turn them off in Settings → General.

## Updates

LycRomanise checks GitHub releases when it starts and every six hours after that. An installed copy downloads `LycRomanise_Setup.exe` and installs it silently; a portable copy swaps in the new `LycRomanise.exe`. Before running a download, the app checks it against the SHA-256 that GitHub publishes for the file.

## Files

Settings, the lyrics cache and logs are in `%APPDATA%\LycRomanise`, along with `LycRomanise.txt`, the song title file for OBS. Uninstalling keeps them. Versions 2.0 and 2.1 used `%APPDATA%\LycRomanise Modern`, and 2.2 moves everything from there on first start.

## Troubleshooting

If there are no lyrics, use Choose lyrics to see what every source has, or Search lyrics again in the tray menu, and check `logs\lycromanise.log`.

If the lyrics are early or late, fix it in Settings → Timing or with Ctrl + Alt + ← / →.

If the strip can't be clicked, it's locked. Unlock it from the tray icon or with Ctrl + Alt + K.

Genius needs Windows' built-in `curl.exe`, which comes with Windows 10 1803 and later.

## Building it yourself

You need the .NET 10 SDK and Inno Setup 6. Run `Builder\build.bat`; it writes `App\LycRomanise.exe` and `App\LycRomanise_Setup.exe`. GitHub Actions (`.github/workflows/release.yml`) builds a release whenever a `v*` tag is pushed, using the notes in `Release Notes/v<version>.md`.

The code in `Source Code/LycRomanise` is split into `Core` (models, settings, HTTP, Spotify, the updater), `Sources` (the ten lyric sources and the LRC, TTML, yrc and krc parsers), `Romanize` (Korean, Japanese, Chinese and other scripts), `Media` (the Windows media controls), `UI` (the strip, lyrics window, settings, tray and picker) and `Web` (the phone and OBS pages).

For testing, `LycRomanise.exe --selftest <folder> [logic|render|sources|deep|app]` runs the built-in tests and writes a report to the folder. The `app` mode drives the whole app with simulated playback and restores your settings afterwards.
