using LycRomanise.Core;
using Windows.Media.Control;

namespace LycRomanise.Media;

public sealed record MediaState(
    string AppId, string Title, string Artist, string Album, long DurationMs,
    long PositionMs, DateTime PositionAt, bool Playing, double Rate)
{
    /// <summary>The position now, carried forward from when the player last reported it.</summary>
    public long PositionNow()
    {
        if (!Playing) return PositionMs;
        var elapsed = (DateTime.UtcNow - PositionAt).TotalMilliseconds * (Rate <= 0 ? 1 : Rate);
        var pos = PositionMs + (long)elapsed;
        return DurationMs > 0 ? Math.Min(pos, DurationMs) : pos;
    }

    public string TrackKey => $"{Artist}\u0001{Title}\u0001{Album}";
}

/// <summary>Reads what is playing from Windows' media controls (SMTC): Spotify, Apple Music, iTunes,
/// Spotifast and any other player that reports to Windows.</summary>
public sealed class MediaWatcher : IDisposable
{
    static readonly string[] MusicApps =
    {
        "spotify", "applemusic", "apple.music", "itunes", "spotifast", "tidal", "deezer", "foobar", "musicbee", "aimp",
        "winamp", "zune", "music.ui", "amazon music", "amazonmusic", "youtube music", "ytmdesktop", "cider", "qobuz", "plexamp",
        "soundcloud", "audirvana", "dopamine", "vlc", "mediaplayer", "pandora",
    };

    readonly Func<bool> _onlyMusicApps;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    readonly System.Threading.Timer _timer;
    MediaState? _last;
    GlobalSystemMediaTransportControlsSession? _session;
    long _lastReportedPos = -1;
    DateTime _lastTimelineAt;
    int _busy;

    public event Action<MediaState?>? Changed;
    public MediaState? Current => _last;

    public MediaWatcher(Func<bool> onlyMusicApps)
    {
        _onlyMusicApps = onlyMusicApps;
        _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => _ = PollAsync();
            _manager.CurrentSessionChanged += (_, _) => _ = PollAsync();
        }
        catch (Exception ex) { Log.Error("media manager", ex); }
        _timer.Change(0, 400);
    }

    bool Accept(string appId)
    {
        if (!_onlyMusicApps()) return true;
        var id = appId.ToLowerInvariant();
        return MusicApps.Any(id.Contains);
    }

    async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var state = await ReadAsync();
            bool changed = state?.TrackKey != _last?.TrackKey || state?.Playing != _last?.Playing
                || state?.AppId != _last?.AppId
                || (state != null && _last != null && Math.Abs(state.PositionNow() - _last.PositionNow()) > 1200);
            _last = state;
            if (changed) Changed?.Invoke(state);
        }
        catch (Exception ex) { Log.Error("media poll", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    async Task<MediaState?> ReadAsync()
    {
        if (_manager == null) return null;
        var sessions = _manager.GetSessions().Where(s => Accept(s.SourceAppUserModelId ?? "")).ToList();
        if (sessions.Count == 0) return null;
        // Whichever is playing wins; else the system's current one; else the first.
        var current = _manager.GetCurrentSession();
        var session = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                      ?? sessions.FirstOrDefault(s => current != null && s.SourceAppUserModelId == current.SourceAppUserModelId)
                      ?? sessions[0];
        _session = session;
        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;
        var info = session.GetPlaybackInfo();
        var tl = session.GetTimelineProperties();
        bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double rate = info?.PlaybackRate ?? 1;

        long reported = (long)tl.Position.TotalMilliseconds;
        DateTime at = tl.LastUpdatedTime.UtcDateTime;
        long duration = (long)(tl.EndTime - tl.StartTime).TotalMilliseconds;
        if (at.Year < 2000) at = DateTime.UtcNow;
        // Players report the timeline only now and then; between reports the position is carried forward.
        if (reported != _lastReportedPos || at != _lastTimelineAt)
        {
            _lastReportedPos = reported;
            _lastTimelineAt = at;
        }
        // Some players (Apple Music) leave LastUpdatedTime in the past without moving the position on pause.
        if (!playing) at = DateTime.UtcNow;

        var artist = props.Artist ?? "";
        var album = props.AlbumTitle ?? "";
        // Apple Music puts "Artist — Album" in the artist field.
        int dash = artist.IndexOf(" — ", StringComparison.Ordinal);
        if (dash > 0 && session.SourceAppUserModelId.Contains("Apple", StringComparison.OrdinalIgnoreCase))
        {
            if (album.Length == 0) album = artist[(dash + 3)..];
            artist = artist[..dash];
        }
        return new MediaState(session.SourceAppUserModelId ?? "", props.Title.Trim(), artist.Trim(), album.Trim(),
            duration, reported, at, playing, rate);
    }

    /// <summary>Is this an ad or a Spotify DJ voice-over rather than a song?</summary>
    public static bool IsAdOrDj(MediaState s)
    {
        if (s.Artist.Length == 0 && (s.Title is "Advertisement" or "Spotify" or "Spotify Free")) return true;
        if (s.Artist is "DJ" or "DJ X" || s.Title.StartsWith("Up next", StringComparison.OrdinalIgnoreCase)) return true;
        return s.DurationMs is > 0 and < 31000 && s.Artist.Length == 0;
    }

    /// <summary>Jumps the player to a position; players that do not allow it ignore the request.</summary>
    public async Task<bool> SeekAsync(long ms)
    {
        var s = _session;
        if (s == null) return false;
        try
        {
            bool ok = await s.TryChangePlaybackPositionAsync(TimeSpan.FromMilliseconds(Math.Max(0, ms)).Ticks);
            if (ok) _ = PollAsync();
            return ok;
        }
        catch (Exception ex) { Log.Error("seek", ex); return false; }
    }

    public async Task TogglePlayAsync()
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task SkipAsync(bool next)
    {
        try { if (_session != null) { if (next) await _session.TrySkipNextAsync(); else await _session.TrySkipPreviousAsync(); } } catch { }
    }

    /// <summary>The cover the player reports for what is playing, as image bytes; null when it has none.</summary>
    public async Task<byte[]?> ThumbnailAsync()
    {
        try
        {
            var s = _session;
            if (s == null) return null;
            var props = await s.TryGetMediaPropertiesAsync();
            if (props?.Thumbnail == null) return null;
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            using var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            return ms.ToArray();
        }
        catch (Exception ex) { Log.Error("thumbnail", ex); return null; }
    }

    public void Dispose() => _timer.Dispose();
}
