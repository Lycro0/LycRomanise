using LycRomanise.Core;
using Windows.Media.Control;

namespace LycRomanise.Media;

public sealed record MediaState(
    string AppId, string Title, string Artist, string Album, long DurationMs,
    long PositionMs, DateTime PositionAt, bool Playing, double Rate)
{
    public long PositionNow()
    {
        if (!Playing) return PositionMs;
        var elapsed = (DateTime.UtcNow - PositionAt).TotalMilliseconds * (Rate <= 0 ? 1 : Rate);
        var pos = PositionMs + (long)elapsed;
        return DurationMs > 0 ? Math.Min(pos, DurationMs) : pos;
    }

    public string TrackKey => $"{Artist}\u0001{Title}\u0001{Album}";
}

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
    DateTime _playingSince;
    bool _wasPlaying;
    long _lastPausedPos = -1;
    int _busy, _again, _failures;
    GlobalSystemMediaTransportControlsSession? _subscribed;
    volatile bool _sessionsChanged;

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
            _manager.SessionsChanged += (_, _) => { _sessionsChanged = true; _ = PollAsync(); };
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
        // A trigger during a poll is not dropped: one more poll runs right after (an event may carry the news).
        if (Interlocked.Exchange(ref _busy, 1) == 1) { Interlocked.Exchange(ref _again, 1); return; }
        try
        {
            var state = await ReadAsync();
            bool changed = state?.TrackKey != _last?.TrackKey || state?.Playing != _last?.Playing
                || state?.AppId != _last?.AppId
                // The length can arrive after the title (Spotify, on a quick skip back): that is news too.
                || (state != null && _last != null && Math.Abs(state.DurationMs - _last.DurationMs) > 1500)
                || (state != null && _last != null && Math.Abs(state.PositionNow() - _last.PositionNow()) > 1200);
            _last = state;
            _failures = 0;
            if (changed) Changed?.Invoke(state);
        }
        catch (Exception ex)
        {
            Log.Error("media poll", ex);
            // A player that keeps failing (closing, hung) is treated as stopped, so the strip doesn't stay up.
            if (++_failures == 3 && _last != null) { _last = null; Changed?.Invoke(null); }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            if (Interlocked.Exchange(ref _again, 0) == 1) _ = PollAsync();
        }
    }

    void OnSessionEvent(GlobalSystemMediaTransportControlsSession s, object e) => _ = PollAsync();

    // Session events poll at once so resumes and seeks are not up to 400 ms late; the timer is only a fallback.
    void Follow(GlobalSystemMediaTransportControlsSession? session)
    {
        // Same player as before: keep its handlers (the projection may hand out a new wrapper each poll).
        if (ReferenceEquals(session, _subscribed) || (session != null && _subscribed != null && !_sessionsChanged
            && session.SourceAppUserModelId == _subscribed.SourceAppUserModelId)) return;
        _sessionsChanged = false;
        try
        {
            if (_subscribed != null)
            {
                _subscribed.MediaPropertiesChanged -= OnSessionEvent;
                _subscribed.TimelinePropertiesChanged -= OnSessionEvent;
                _subscribed.PlaybackInfoChanged -= OnSessionEvent;
            }
        }
        catch { }   // a player that closed
        _subscribed = null;
        if (session == null) return;
        try
        {
            session.MediaPropertiesChanged += OnSessionEvent;
            session.TimelinePropertiesChanged += OnSessionEvent;
            session.PlaybackInfoChanged += OnSessionEvent;
            _subscribed = session;
        }
        catch (Exception ex) { Log.Error("media events", ex); }
    }

    async Task<MediaState?> ReadAsync()
    {
        if (_manager == null) return null;
        var sessions = _manager.GetSessions().Where(s => Accept(s.SourceAppUserModelId ?? "")).ToList();
        if (sessions.Count == 0) { Follow(null); return null; }
        var current = _manager.GetCurrentSession();
        var session = sessions.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                      ?? sessions.FirstOrDefault(s => current != null && s.SourceAppUserModelId == current.SourceAppUserModelId)
                      ?? sessions[0];
        _session = session;
        Follow(session);
        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;
        var info = session.GetPlaybackInfo();
        var tl = session.GetTimelineProperties();
        if (tl == null) return null;
        bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double rate = info?.PlaybackRate ?? 1;

        long reported = (long)tl.Position.TotalMilliseconds;
        DateTime at = tl.LastUpdatedTime.UtcDateTime;
        long duration = (long)(tl.EndTime - tl.StartTime).TotalMilliseconds;
        if (at.Year < 2000) at = DateTime.UtcNow;
        if (reported != _lastReportedPos || at != _lastTimelineAt)
        {
            _lastReportedPos = reported;
            _lastTimelineAt = at;
        }
        // Some players (Apple Music) leave LastUpdatedTime in the past without moving the position on pause.
        if (!playing) at = DateTime.UtcNow;
        // On resume Spotify briefly keeps the paused report; counted from the pause the lyrics would race ahead.
        if (playing && !_wasPlaying) _playingSince = DateTime.UtcNow;
        _wasPlaying = playing;
        if (playing && at < _playingSince && reported == _lastPausedPos) at = _playingSince;
        if (!playing) _lastPausedPos = reported;

        var artist = props.Artist ?? "";
        var album = props.AlbumTitle ?? "";
        // Apple Music puts "Artist U+2014 Album" in the artist field.
        string AppleJoin = " " + (char)0x2014 + " ";
        int dash = artist.IndexOf(AppleJoin, StringComparison.Ordinal);
        if (dash > 0 && (session.SourceAppUserModelId ?? "").Contains("Apple", StringComparison.OrdinalIgnoreCase))
        {
            if (album.Length == 0) album = artist[(dash + 3)..];
            artist = artist[..dash];
        }
        return new MediaState(session.SourceAppUserModelId ?? "", props.Title.Trim(), artist.Trim(), album.Trim(),
            duration, reported, at, playing, rate);
    }

    public static bool IsAdOrDj(MediaState s)
    {
        if (s.Artist.Length == 0 && (s.Title is "Advertisement" or "Spotify" or "Spotify Free")) return true;
        if (s.Artist is "DJ" or "DJ X" || s.Title.StartsWith("Up next", StringComparison.OrdinalIgnoreCase)) return true;
        return s.DurationMs is > 0 and < 31000 && s.Artist.Length == 0;
    }

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

    public void Dispose()
    {
        _timer.Dispose();
        Follow(null);
    }
}
