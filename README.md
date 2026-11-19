<p align="center">
  <img src="Source%20Code/assets/LycRomanise-Icon.png" alt="LycRomanise icon" width="128">
</p>
<h1 align="center">LycRomanise</h1>
<p align="center">
  Synced, word-by-word lyrics on your desktop for whatever is playing, with <b>romanized lyrics</b> for Korean, Japanese and Chinese songs.<br>
  Works with Spotify, Apple Music and any other player.
</p>
<p align="center">
  <a href="https://github.com/Lycro0/LycRomanise/releases/latest"><b>Download for Windows</b></a> ·
  <a href="Source%20Code/assets/LycRomanise-Trailer.mp4">Watch the trailer</a> ·
  <a href="Source%20Code/DETAILS.md">Full details</a>
</p>

<p align="center">
  <a href="Source%20Code/assets/LycRomanise-Trailer.mp4">
    <img src="Source%20Code/assets/LycRomanise-Preview.gif" alt="LycRomanise trailer: the lyrics strip, nine looks, the full lyrics window, the web view and OBS" width="100%">
  </a>
  <br>
  <sub>Click the preview to watch the full trailer with sound.</sub>
</p>

> **v2 is a full rewrite.** LycRomanise is now a native Windows app written in C# / .NET 10 instead of Python.
> It starts faster, uses the GPU to draw, sleeps when nothing is playing, and brings word-by-word karaoke,
> ten lyric sources and a full lyrics window. Your v1 colours and strip settings are carried over on first start.

## Download

Get **LycRomanise_Setup.exe** (installer) or **LycRomanise.exe** (portable) from the [Releases](https://github.com/Lycro0/LycRomanise/releases) page. Nothing else to install: no Python, no .NET, no account or API key. The app checks for updates and installs them for you.

## What you get

- A lyrics strip on top of your screen: the line being sung fills **word by word**, with the next line underneath
- **Nine looks** to pick from: **Classic** (the original LycRomanise layout, and the default), Modern, Apple Music, Spotify, Neon, Karaoke, Minimal, Sunset and Mint
- A **full lyrics window** in the style of Apple Music: a blurred cover backdrop, every line scrolling with the song, and click any line to jump there
- Korean, Japanese and Chinese lyrics in English letters (romaja, romaji, pinyin), plus Cyrillic, Greek, Arabic, Hebrew and Hindi
- Duets on two sides, backing vocals, and translations under each line
- Lyrics from **ten sources**, asked in order: Spicy Lyrics, BiniLyrics, your own files, Musixmatch, Unison, NetEase, LRCLIB, QQ Music, KuGou and Genius
- A **web view** for phones and tablets that looks like the full lyrics window, plus a separate **transparent page for OBS** that follows your strip's look
- `LycRomanise.txt` for OBS text sources
- Optional **Spotify login** that looks up the next song's lyrics before it starts

## How to use it

1. Install it and play a song. The lyrics appear by themselves.
2. Drag the strip where you want it, then lock it from the tray icon (or **Ctrl + Alt + K**).
3. Double-click the strip or press **Ctrl + Alt + L** for the full lyrics window.
4. Right-click the tray icon for Settings, to pick other lyrics for a song, or to fix the timing.

## Settings

| Page | What is in it |
| --- | --- |
| Now playing | The song, where its lyrics came from, timing, choose or import lyrics |
| Appearance | Looks (Classic, Apple Music and more), layout, word lift and glow, the full lyrics window's motion, colours, font, background |
| Lyrics & romanization | Original / romanized / both, translations, backing vocals |
| Lyric sources | Turn sources on or off and reorder them, Spicy Lyrics key, imported lyrics folder |
| Spotify account | Optional login for next-song prefetch |
| Strip & window | Lock, width, alignment, hide while paused, taskbar button |
| Timing | All songs or this song earlier or later |
| Web view & OBS | Web view and port, links for phones and OBS, song title file |
| General & about | Start with Windows, updates, keyboard shortcuts |

The **Full lyrics** button at the bottom of the Settings sidebar opens the lyrics window from any page. Changes apply instantly.

## Using it with OBS

- **Lyrics on stream:** turn on the web view in Settings, then add a **Browser** source in OBS with the link that ends in `/obs`. It's transparent and copies your strip's look.
- **Song name only:** add a **Text (GDI+)** source, tick **Read from file** and pick `LycRomanise.txt`.

## Need more?

See [DETAILS.md](Source%20Code/DETAILS.md) for every source, the Spotify login, your own lyric files, shortcuts, troubleshooting and building it yourself.

Not affiliated with Spotify, Apple, Musixmatch, Spicy Lyrics, Genius or Lyricify.
