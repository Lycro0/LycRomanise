<p align="center">
  <img src="Source%20Code/assets/LycRomanise-Icon.png" alt="LycRomanise icon" width="128">
</p>
<h1 align="center">LycRomanise</h1>
<p align="center">
  Synced, word-by-word lyrics on your desktop for whatever is playing, with romanized lyrics for Korean, Japanese and Chinese songs.<br>
  Works with Spotify, Apple Music and any other player.
</p>
<p align="center">
  <a href="https://github.com/Lycro0/LycRomanise/releases/latest"><b>Download for Windows</b></a> ·
  <a href="https://youtu.be/kiYvWNL_SuU">Watch the trailer on YouTube</a> ·
  <a href="Source%20Code/DETAILS.md">Full details</a>
</p>

<p align="center">
  <a href="https://youtu.be/kiYvWNL_SuU">
    <img src="Source%20Code/assets/LycRomanise-Trailer-Preview.gif" alt="LycRomanise trailer: the lyrics strip, fully customisable looks, the full lyrics window, the web view and OBS" width="100%">
  </a>
  <br>
  <sub>Click the preview to watch the full trailer on YouTube.</sub>
</p>

> Version 2 is a rewrite in C# and .NET 10; version 1 was written in Python. It starts faster, draws on the GPU and does nothing while the music is paused. Your v1 colours and strip settings are copied over the first time it starts.

## Download

Get `LycRomanise_Setup.exe` (the installer) or `LycRomanise.exe` (a portable copy) from the [Releases](https://github.com/Lycro0/LycRomanise/releases) page. You don't need Python, .NET, an account or an API key. The app checks for new versions and installs them itself.

## What it does

- Shows a lyrics strip on top of your screen. The line being sung fills in word by word, and the next line waits underneath.
- Has nine looks to pick from. Classic, the original LycRomanise layout, is the default; the others are Modern, Apple Music, Spotify, Neon, Karaoke, Minimal, Sunset and Mint.
- Opens a full lyrics window like Apple Music's, with the album cover blurred behind lines that scroll with the song. Click a line to jump to it. The strip hides while this window is open.
- Writes Korean, Japanese and Chinese lyrics in English letters (romaja, romaji and pinyin), and also handles Cyrillic, Greek, Arabic, Hebrew and Hindi.
- Puts duet singers on opposite sides and shows backing vocals and translations under each line.
- Asks ten lyric sources in order: Spicy Lyrics, BiniLyrics, your own files, Musixmatch, Unison, NetEase, LRCLIB, QQ Music, KuGou and Genius.
- Has a web view for phones and tablets that looks like the full lyrics window, and a separate transparent page for OBS that uses your strip's look.
- Keeps the song name in `LycRomanise.txt` for OBS text sources.
- Can log in to Spotify to load the next song's lyrics before it starts (optional).

## How to use it

1. Install it and play a song. The lyrics appear on their own.
2. Drag the strip where you want it, then lock it from the tray icon or with Ctrl + Alt + K.
3. Double-click the strip or press Ctrl + Alt + L for the full lyrics window. Press S there for its settings.
4. Right-click the tray icon for Settings, to pick other lyrics for a song, or to fix the timing.

## Settings

| Page | What is in it |
| --- | --- |
| Now playing | The song, where its lyrics came from, timing, choose or import lyrics |
| Appearance | Looks, layout, word lift and glow, motion in the full lyrics window, colours, font, background |
| Lyrics & romanization | Original, romanized or both; translations; backing vocals |
| Lyric sources | Turn sources on or off and reorder them, Spicy Lyrics key, imported lyrics folder |
| Spotify account | Optional login for loading the next song early |
| Strip & window | Lock, width, alignment, hide while paused, hide while the full lyrics window is open, taskbar button |
| Timing | Move the lyrics earlier or later, for all songs or just this one |
| Web view & OBS | Web view and port, a QR code for your phone, the OBS link, the song title file |
| General & about | Start with Windows, updates, keyboard shortcuts |

The Full lyrics button at the bottom of the sidebar opens the lyrics window from any page. Changes apply as soon as you make them.

## Using it with OBS

To show lyrics on stream, turn on the web view in Settings and add a Browser source in OBS with the link that ends in `/obs`. The page is transparent and uses your strip's look.

To show only the song name, add a Text (GDI+) source, tick "Read from file" and pick `LycRomanise.txt`.

## More

[DETAILS.md](Source%20Code/DETAILS.md) covers each lyric source, the Spotify login, your own lyric files, keyboard shortcuts, troubleshooting and how to build the app.

Not affiliated with Spotify, Apple, Musixmatch, Spicy Lyrics, Genius or Lyricify.
