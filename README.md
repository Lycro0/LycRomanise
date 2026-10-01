# LycRomanise (Spotify Lyrics Overlay)

A small always on top lyrics strip for Windows that follows whatever you're playing on Spotify.
It shows the current line with karaoke style highlighting, previews the next line, and
romanizes lyrics in other scripts (Korean, Japanese, Chinese, Cyrillic, Greek, Hebrew, Arabic, Devanagari).

Not affiliated with Spotify.

## Install

Download the latest installer from the [Releases](../../releases) page and run it.
On first launch your browser opens Spotify's login page: sign in and click **Agree**.
The app only asks for read only access (currently playing and playback state).

## Using it

  **Drag** the strip to move it. **Lock** it with the padlock that appears on hover, or from the tray menu.
  A locked strip is click through.
  **Tray icon** menu: Lock/Unlock, Settings, Log in / Log out, Start with Windows, Open logs folder, Quit.
  **Settings** covers colours, font sizes, strip size, lyric offset and outline. Changes apply live.
  If Spotify login fails, a message appears on the strip and in the log.

Settings, your Spotify login, the lyrics cache and logs are stored in `%LOCALAPPDATA%\LycRomanise`.

## Lyrics

Lyrics are fetched from QQ Music, NetEase Cloud Music and [LRCLIB](https://lrclib.net) at the same time
and cross checked before one is used. The QQ and NetEase lookups use unofficial endpoints that can change
without notice, and not every song has synced lyrics.

## Use your own Spotify app (optional)

1. Create an app in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard).
2. Add the redirect URI exactly: `http://127.0.0.1:8888/callback` (Spotify does not accept `localhost`).
3. Under **Users and Access**, add your Spotify account (apps in development mode only allow listed users).
4. Copy the **Client ID** into the app's login window or **Settings** and press **Save & connect**.
   Leave the secret empty for the normal PKCE login.

## Run from source

Requires Python 3.9+ on Windows.

```bash
pip install  r "source code/requirements.txt"
python "source code/LyricsOverlay.pyw"
```

The source build has no built in Spotify app, so it opens a login window where you paste your own Client ID (see above).

## Credits

Romanization uses [pypinyin](https://github.com/mozillazg/python pinyin) and
[pykakasi](https://github.com/miurahr/pykakasi); the Korean romanizer is implemented from scratch.
