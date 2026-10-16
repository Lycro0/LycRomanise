# LycRomanise

Synced lyrics on your desktop for whatever is playing on Spotify or Apple Music, with **romanized lyrics** for Korean, Japanese and Chinese songs.

## Download

Get **LycRomanise_Setup.exe** (installer) or **LycRomanise.exe** (portable) from the [Releases](https://github.com/Lycro0/LycRomanise/releases) page. No account or API key needed.

## What you get

- A small lyrics strip that sits on top of your screen, with the line being sung and the next line
- Korean, Japanese and Chinese lyrics shown in English letters (romaja, romaji, pinyin)
- Lyrics from LRCLIB, with lrcmux as a backup
- A **web view** for phones, tablets and OBS browser sources
- `LycRomanise.txt` for OBS, always holding the song that is playing

## How to use it

1. Install it and play a song. The lyrics appear by themselves.
2. Drag the strip where you want it, then lock it from the tray icon.
3. Right-click the tray icon to open **Settings**, turn on the web view, or fix wrong lyrics.

## Settings

| Tab | What is in it |
| --- | --- |
| Visuals | Colours and the artist and song title card |
| Window | Strip size, hide the lock button, taskbar button, reset position |
| Timing | Make this song's lyrics earlier or later |
| Web and OBS | Web view and port, link and QR code, OBS text file, hide lyrics on this PC |

Changes apply instantly.

## Using it with OBS

- **Lyrics on stream:** turn on the web view in Settings, then add a **Browser** source in OBS and paste the link.
- **Song name only:** add a **Text (GDI+)** source, tick **Read from file** and pick `LycRomanise.txt`.

## Need more?

See [DETAILS.md](DETAILS.md) for troubleshooting, your own lyric files, running from source and building.

Not affiliated with Spotify, Apple or Lyricify.
