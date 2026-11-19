using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LycRomanise.Core;

namespace LycRomanise.Sources;

public interface ILyricsSource
{
    string Key { get; }
    string Name { get; }
    string Detail { get; }
    Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct);
}

static partial class SourceUtil
{
    // The credits Chinese catalogs put at the top of the lyrics: "作词：…", "Composed by: …", "编曲 : …".
    [GeneratedRegex(@"^\s*(作?词|作?曲|词曲|編曲|编曲|作詞|制作人|製作人|制作|監製|监制|混音|母带|和声|和音|吉他|贝斯|鼓|弦乐|录音|錄音|出品|发行|發行|统筹|企划|OP|SP|ISRC|Lyrics?(\s+by)?|Lyricist|Composed\s+by|Composer|Music(\s+by)?|Produced\s+by|Producer|Written\s+by|Arranged\s+by|Arranger|Mixed\s+by|Mastered\s+by|Vocals?\s+by)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex CreditLine();

    public static LyricsResult? Result(string key, string provider, List<LyricLine>? lines, Timing? timing = null)
    {
        if (lines == null || lines.Count == 0) return null;
        lines = lines.Where((l, i) => !(i < 16 && CreditLine().IsMatch(l.Text))).ToList();
        if (lines.Count == 0) return null;
        return new LyricsResult { SourceKey = key, Provider = provider, Lines = lines, Timing = timing ?? Karaoke.TimingOf(lines) };
    }

    public static bool CloseLength(int a, int b, int slack) => a <= 0 || b <= 0 || Math.Abs(a - b) <= slack;
}

// ===================================================================== Spicy Lyrics
/// <summary>Spicy Lyrics Developer Platform, on the user's own publishable key, by Spotify track id.
/// Community syllable syncs, else Apple Music's or Spotify's. The API's terms ask for the credit to show.</summary>
public sealed class SpicyLyricsSource : ILyricsSource
{
    const string Api = "https://api.spicylyrics.org/v1/lyrics/";
    public string Key => "spicy";
    public string Name => "Spicy Lyrics";
    public string Detail => "Community syllable syncs, duets and backing vocals (needs your free key)";

    readonly Func<string?> _key;
    DateTime _waitUntil = DateTime.MinValue, _refusedUntil = DateTime.MinValue;
    public string? Status { get; private set; }

    public SpicyLyricsSource(Func<string?> key) { _key = key; }

    public static string? KeyProblem(string key)
    {
        if (key.StartsWith("sl_sk_")) return "That is a secret key, which belongs on a server. Paste the publishable one (sl_pk_...).";
        if (!key.StartsWith("sl_pk_") || key.Length < 12 || key.Any(c => c < 0x21 || c > 0x7E)) return "That is not a publishable key (sl_pk_...).";
        return null;
    }

    public void ResetRefusal() { _refusedUntil = DateTime.MinValue; Status = null; }

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrEmpty(key)) { Status = "Needs a key"; return null; }
        if (q.SpotifyId is not { Length: 22 }) return null;
        if (DateTime.UtcNow < _refusedUntil || DateTime.UtcNow < _waitUntil) return null;
        var reply = await Http.GetAsync(Api + q.SpotifyId, new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + key, ["Accept"] = "application/json", ["User-Agent"] = Http.AppAgent,
        }, ct);
        if (reply.Status is 429 or 503)
        {
            var wait = reply.Response?.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
            _waitUntil = DateTime.UtcNow + (wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait);
            return null;
        }
        if (reply.Status is 401 or 403)
        {
            var code = reply.Json.Dig("Body", "error").Str() ?? reply.Json.Dig("error").Str() ?? "";
            Status = code switch
            {
                "origin_not_allowed" or "origins_not_configured" => "Key rejected: allow \"No origin header\"",
                "application_paused" => "Key rejected: paused",
                "key_revoked" => "Key rejected: revoked",
                "key_not_found" or "key_malformed" or "malformed_authorization" => "Key rejected: not found",
                _ => "Key rejected",
            };
            _refusedUntil = DateTime.UtcNow.AddMinutes(10);
            Log.Info($"spicy: key refused ({reply.Status} {code})");
            return null;
        }
        if (reply.Status != 200) return null;
        Status = null;
        return Parse(reply.Json);
    }

    static string? S(JsonNode? o, string name) => o.Dig(name).Str();

    static LyricLine? SyllableLine(JsonNode? part, string field)
    {
        var words = new List<LyricWord>();
        bool runsOn = false;
        foreach (var syl in part.Dig("Syllables").Items())
        {
            var text = S(syl, field)?.Trim();
            if (string.IsNullOrEmpty(text)) continue;
            var start = syl.Dig("StartTime").Ms();
            words.Add(new LyricWord { Text = text, Start = start, End = Math.Max(syl.Dig("EndTime").Ms(), start), Joined = words.Count > 0 && runsOn });
            runsOn = syl.Dig("IsPartOfWord").Truthy();
        }
        if (words.Count == 0) return null;
        long ls = part.Dig("StartTime") != null ? part.Dig("StartTime").Ms() : words[0].Start;
        long le = part.Dig("EndTime") != null ? part.Dig("EndTime").Ms() : words[^1].End;
        return new LyricLine { Words = words, Start = ls, End = Math.Max(le, ls) };
    }

    public static LyricsResult? Parse(JsonNode? root)
    {
        var body = root.Dig("Body");
        if (body is not JsonObject) return null;
        var type = S(body, "Type");
        var lines = new List<LyricLine>();
        if (type == "Syllable")
        {
            foreach (var item in body.Dig("Content").Items())
            {
                var lead = item.Dig("Lead");
                var line = SyllableLine(lead, "Text");
                if (line == null) continue;
                line.Align = item.Dig("OppositeAligned").Truthy() ? LineAlign.Trailing : LineAlign.Leading;
                var bgSyl = new JsonArray();
                var spelt = new List<string>();
                var translated = new List<string>();
                if (S(lead, "TranslatedText") is { } tl) translated.Add(tl);
                foreach (var g in item.Dig("Background").Items())
                {
                    foreach (var s in g.Dig("Syllables").Items()) bgSyl.Add(s.DeepClone());
                    if (S(g, "TransliteratedText") is { } st) spelt.Add(st);
                    if (S(g, "TranslatedText") is { } gt) translated.Add(gt);
                }
                var bg = new JsonObject { ["Syllables"] = bgSyl };
                line.Backing = SyllableLine(bg, "Text");
                if (line.Backing != null) line.Backing.Align = line.Align;
                line.Pronunciation = Karaoke.Pronunciation(SyllableLine(lead, "TransliteratedText")?.Words, S(lead, "TransliteratedText"), line);
                if (line.Backing != null)
                    line.Backing.Pronunciation = Karaoke.Pronunciation(SyllableLine(bg, "TransliteratedText")?.Words, string.Join(" ", spelt), line.Backing);
                var tr = string.Join(" ", translated);
                if (tr.Length > 0 && Karaoke.BareText(tr) != Karaoke.BareText(line.Text)) line.Translation = tr;
                lines.Add(line);
            }
        }
        else if (type == "Line")
        {
            var kept = body.Dig("Content").Items().Where(i => S(i, "Text") is { } t && t.Trim() is { Length: > 0 } tt && tt != "♪").ToList();
            var starts = new List<long>();
            var texts = new List<string>();
            var map = new List<int>();
            for (int i = 0; i < kept.Count; i++)
            {
                starts.Add(kept[i].Dig("StartTime").Ms());
                texts.Add(S(kept[i], "Text")!);
                map.Add(i);
                long end = kept[i].Dig("EndTime").Ms();
                if (end > 0 && i + 1 < kept.Count && kept[i + 1].Dig("StartTime").Ms() > end) { starts.Add(end); texts.Add(""); map.Add(-1); }
            }
            lines = Karaoke.EstimatedLines(starts, texts);
            for (int i = 0; i < lines.Count && i < kept.Count; i++)
            {
                if (kept[i].Dig("OppositeAligned").Truthy()) lines[i].Align = LineAlign.Trailing;
                lines[i].Pronunciation = Karaoke.Pronunciation(null, S(kept[i], "TransliteratedText"), lines[i]);
                var tr = S(kept[i], "TranslatedText");
                if (tr != null && Karaoke.BareText(tr) != Karaoke.BareText(lines[i].Text)) lines[i].Translation = tr;
            }
        }
        else if (type == "Static")
        {
            lines = Karaoke.StaticLines(body.Dig("Lines").Items().Select(r => S(r, "Text") ?? ""));
        }
        if (lines.Count == 0) return null;

        var result = new LyricsResult { SourceKey = "spicy", Lines = lines, Timing = Karaoke.TimingOf(lines), CreditRequired = true };
        var source = S(body, "source");
        result.Provider = source switch
        {
            "apple_music" => "Apple Music via Spicy Lyrics",
            "spotify" => "Spotify via Spicy Lyrics",
            _ => "Spicy Lyrics",
        };
        if (source == "spicy_lyrics")
        {
            var people = body.Dig("UploadAttribution");
            string? maker = people.Dig("Maker", "username").Str(), uploader = people.Dig("Uploader", "username").Str();
            foreach (var role in new[] { "Uploader", "Maker" })
            {
                var name = people.Dig(role, "username").Str();
                var url = people.Dig(role, "url").Str();
                if (name != null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http") && result.CreditLinks.All(l => l.Url != url))
                    result.CreditLinks.Add(($"{role}: {name}", url!));
            }
            if (maker != null && uploader != null && maker != uploader) result.Provider = $"Spicy Lyrics, synced by {maker}, uploaded by {uploader}";
            else if ((maker ?? uploader) is { } who) result.Provider = $"Spicy Lyrics, synced by {who}";
        }
        return result;
    }
}

// ===================================================================== BiniLyrics
/// <summary>Apple-style TTML by ISRC from lrc.red, else BiniLyrics' search by name.</summary>
public sealed class BiniLyricsSource : ILyricsSource
{
    public string Key => "bini";
    public string Name => "BiniLyrics";
    public string Detail => "Apple Music style word sync, duets, backing vocals and romanization";
    readonly Func<string?> _translationLang;
    public BiniLyricsSource(Func<string?> translationLang) { _translationLang = translationLang; }

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(q.Isrc))
        {
            var r = await Http.GetAsync($"https://lrc.red/s/{Uri.EscapeDataString(q.Isrc)}.ttml", null, ct);
            if (r.Ok && From(r.Body) is { } byIsrc) return byIsrc;
        }
        if (q.Title.Length == 0 || q.Artist.Length == 0) return null;
        var search = await Http.GetAsync(Http.Url("https://lyrics-api.binimum.org/", new Dictionary<string, string?>
        {
            ["track"] = q.Title, ["artist"] = q.Artist, ["album"] = q.Album.Length > 0 ? q.Album : null,
            ["duration"] = q.Seconds > 0 ? q.Seconds.ToString() : null,
        }), null, ct);
        JsonNode? best = null;
        bool bestWord = false;
        int bestOff = int.MaxValue;
        foreach (var f in search.Json.Dig("results").Items())
        {
            if (f.Dig("lyricsUrl").Str() == null) continue;
            int len = (int)f.Dig("duration").Num();
            int off = q.Seconds > 0 && len > 0 ? Math.Abs(len - q.Seconds) : 0;
            if (off > 4) continue;
            bool word = f.Dig("timing_type").Str() == "word";
            if (best != null && !(word && !bestWord) && (bestWord != word || off >= bestOff)) continue;
            best = f; bestWord = word; bestOff = off;
        }
        if (best == null) return null;
        var ttml = await Http.GetAsync(best.Dig("lyricsUrl").Str()!, null, ct);
        return ttml.Ok ? From(ttml.Body) : null;
    }

    LyricsResult? From(string ttml) => SourceUtil.Result(Key, Name, Formats.ParseTtml(ttml, _translationLang()));
}

// ===================================================================== Imported
/// <summary>The user's own lyric files: .ttml, .lrc (incl. enhanced), .txt, named "Artist - Title" or
/// "Title", in the imported lyrics folder (searched recursively).</summary>
public sealed class ImportedSource : ILyricsSource
{
    public string Key => "imported";
    public string Name => "Imported lyrics";
    public string Detail => "Your own .lrc / .ttml / .txt files from the imported lyrics folder";
    readonly Func<string> _folder;
    public ImportedSource(Func<string> folder) { _folder = folder; }

    public static readonly string[] Extensions = { ".ttml", ".lrc", ".txt" };

    public Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        var path = Find(q);
        if (path == null) return Task.FromResult<LyricsResult?>(null);
        try { return Task.FromResult(Load(path)); }
        catch (Exception ex) { Log.Error("imported " + path, ex); return Task.FromResult<LyricsResult?>(null); }
    }

    public string? Find(TrackQuery q)
    {
        var folder = _folder();
        if (!Directory.Exists(folder)) return null;
        string title = Normalize.MatchKey(Normalize.CleanTitle(q.Title)), artist = Normalize.MatchKey(Normalize.LeadArtist(q.Artist));
        string? titleOnly = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!Extensions.Contains(ext)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                string a = Normalize.MatchKey(name[..dash]), t = Normalize.MatchKey(name[(dash + 3)..]);
                if (t == title && a.Length > 0 && (a.Contains(artist) || artist.Contains(a))) return file;
                // "Title - Artist" works too.
                if (a == title && t.Length > 0 && (t.Contains(artist) || artist.Contains(t))) return file;
            }
            else if (Normalize.MatchKey(name) == title) titleOnly ??= file;
        }
        return titleOnly;
    }

    public LyricsResult? Load(string path)
    {
        var text = File.ReadAllText(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        List<LyricLine>? lines = ext == ".ttml" || text.TrimStart().StartsWith("<") ? Formats.ParseTtml(text)
            : Formats.ParseLrc(text) ?? Formats.ParsePlain(text);
        var r = SourceUtil.Result(Key, "Imported: " + Path.GetFileName(path), lines);
        return r;
    }
}

// ===================================================================== Musixmatch
/// <summary>Musixmatch, through its mobile app's anonymous token. Also the matcher the app uses to find
/// a track's Spotify id and ISRC for Spicy Lyrics and BiniLyrics.</summary>
public sealed class MusixmatchSource : ILyricsSource
{
    const string Api = "https://apic-appmobile.musixmatch.com/ws/1.1/";
    const string AppId = "mac-ios-v2.0";
    public string Key => "musixmatch";
    public string Name => "Musixmatch";
    public string Detail => "Huge licensed catalog with word sync (richsync) on many tracks";

    string? _token;
    DateTime _tokenRefused = DateTime.MinValue;
    readonly SemaphoreSlim _tokenGate = new(1, 1);
    readonly Dictionary<string, (JsonNode? calls, DateTime at)> _macroCache = new();
    readonly string _tokenFile = Path.Combine(Paths.Data, "mxm.token");

    static readonly Dictionary<string, string> Headers = new()
    {
        ["Accept"] = "application/json",
        ["x-mxm-app-version"] = "10.1.1",
        ["X-User-Agent"] = "Musixmatch/2025120901 CFNetwork/3860.300.31 Darwin/25.2.0",
        ["User-Agent"] = "Musixmatch/2025120901 CFNetwork/3860.300.31 Darwin/25.2.0",
    };

    async Task<JsonNode?> Call(string method, Dictionary<string, string?> query, CancellationToken ct)
    {
        var q = new Dictionary<string, string?> { ["format"] = "json", ["app_id"] = AppId };
        foreach (var kv in query) q[kv.Key] = kv.Value;
        var reply = await Http.GetAsync(Http.Url(Api + method, q), Headers, ct);
        return reply.Json.Dig("message");
    }

    async Task<string?> Token(CancellationToken ct, bool renew = false)
    {
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (renew) { _token = null; try { File.Delete(_tokenFile); } catch { } }
            if (_token == null && File.Exists(_tokenFile)) _token = File.ReadAllText(_tokenFile).Trim();
            if (!string.IsNullOrEmpty(_token)) return _token;
            if (DateTime.UtcNow - _tokenRefused < TimeSpan.FromMinutes(10)) return null;
            var msg = await Call("token.get", new(), ct);
            var fresh = msg.Dig("body", "user_token").Str();
            if (fresh == null || fresh == "UpgradeRequired")
            {
                _tokenRefused = DateTime.UtcNow;
                Log.Info($"musixmatch: token refused ({msg.Dig("header", "status_code").Str()})");
                return null;
            }
            _token = fresh;
            try { File.WriteAllText(_tokenFile, fresh); } catch { }
            return fresh;
        }
        finally { _tokenGate.Release(); }
    }

    /// <summary>macro.subtitles.get for the track, kept for the session. Runs the matcher and every
    /// lyrics lookup in one call.</summary>
    async Task<JsonNode?> Macro(TrackQuery q, CancellationToken ct)
    {
        var cacheKey = q.Key + "|" + q.Seconds;
        lock (_macroCache)
            if (_macroCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.at < TimeSpan.FromHours(2)) return hit.calls;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var token = await Token(ct, renew: attempt > 0);
            if (token == null) return null;
            var query = new Dictionary<string, string?>
            {
                ["usertoken"] = token, ["namespace"] = "lyrics_richsynched", ["subtitle_format"] = "mxm",
                ["optional_calls"] = "track.richsync", ["richsync_compact_type"] = "words",
                ["q_track"] = q.Title, ["q_artist"] = q.Artist,
                ["q_album"] = q.Album.Length > 0 ? q.Album : null,
                ["q_duration"] = q.Seconds > 0 ? q.Seconds.ToString() : null,
            };
            if (q.SpotifyId != null) query["track_spotify_id"] = q.SpotifyId;
            var msg = await Call("macro.subtitles.get", query, ct);
            var status = (int)msg.Dig("header", "status_code").Num();
            if (status == 401 && attempt == 0) continue;
            var calls = msg.Dig("body", "macro_calls");
            if (status != 200 || calls is not JsonObject) return null;
            lock (_macroCache)
            {
                if (_macroCache.Count > 60) _macroCache.Clear();
                _macroCache[cacheKey] = (calls, DateTime.UtcNow);
            }
            return calls;
        }
        return null;
    }

    /// <summary>Fills in what Musixmatch's matcher knows of the track: Spotify id, ISRC, its names.</summary>
    public async Task Identify(TrackQuery q, CancellationToken ct)
    {
        if (q.Title.Length == 0) return;
        try
        {
            var calls = await Macro(q, ct);
            var track = calls.Dig("matcher.track.get", "message", "body", "track");
            if (track is not JsonObject) return;
            var len = (int)track.Dig("track_length").Num();
            // A matcher hit for another recording (a live version, a cover) would mislead the id sources.
            if (q.Seconds > 0 && len > 0 && Math.Abs(len - q.Seconds) > 8) return;
            q.SpotifyId ??= track.Dig("track_spotify_id").Str();
            q.Isrc ??= track.Dig("track_isrc").Str();
            q.MusixmatchCommontrack = track.Dig("commontrack_id").Str();
            if (q.Seconds <= 0) q.Seconds = len;
            if (q.Album.Length == 0 && track.Dig("album_name").Str() is { } album) q.Album = album;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error("musixmatch identify", ex); }
    }

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        var calls = await Macro(q, ct);
        if (calls == null) return null;
        if (calls.Dig("matcher.track.get", "message", "body", "track", "instrumental").Truthy())
            return new LyricsResult { SourceKey = Key, Provider = Name, Instrumental = true };
        var trackLen = (int)calls.Dig("matcher.track.get", "message", "body", "track", "track_length").Num();
        if (!SourceUtil.CloseLength(trackLen, q.Seconds, 8)) return null;

        var rich = calls.Dig("track.richsync.get", "message", "body", "richsync");
        if (rich is JsonObject && !rich.Dig("restricted").Truthy() && Parse(rich.Dig("richsync_body").Str()) is { } rbody)
        {
            var lines = new List<LyricLine>();
            foreach (var entry in rbody.Items())
            {
                long start = entry.Dig("ts").Ms(), end = entry.Dig("te").Ms();
                var line = RichsyncLine(entry, start, end);
                if (line != null) lines.Add(line);
            }
            if (lines.Count > 0) return SourceUtil.Result(Key, Name, lines, Timing.Word);
        }
        var sub = calls.Dig("track.subtitles.get", "message", "body", "subtitle_list").Items().FirstOrDefault().Dig("subtitle");
        if (sub is JsonObject && !sub.Dig("restricted").Truthy() && Parse(sub.Dig("subtitle_body").Str()) is JsonArray sbody && sbody.Count > 0)
        {
            var starts = sbody.Select(x => x.Dig("time", "total").Ms()).ToList();
            var texts = sbody.Select(x => x.Dig("text").Str() ?? "").ToList();
            if (Karaoke.EstimatedLines(starts, texts) is { Count: > 0 } est) return SourceUtil.Result(Key, Name, est, Timing.Line);
        }
        var plain = calls.Dig("track.lyrics.get", "message", "body", "lyrics");
        if (plain is JsonObject && !plain.Dig("restricted").Truthy() && plain.Dig("lyrics_body").Str() is { } pb)
        {
            pb = Regex.Replace(pb, @"\n\*{7}.*$", "", RegexOptions.Singleline);   // the "not for commercial use" footer
            return SourceUtil.Result(Key, Name, Formats.ParsePlain(pb), Timing.None);
        }
        return null;
    }

    static JsonNode? Parse(string? s) { if (s == null) return null; try { return JsonNode.Parse(s); } catch { return null; } }

    /// <summary>[{ts, te, x, l:[{c, o}]}]: o is from the line start; a space entry ends the word before it.</summary>
    static LyricLine? RichsyncLine(JsonNode entry, long start, long end)
    {
        var words = new List<LyricWord>();
        bool open = false, spaced = true;
        foreach (var part in entry.Dig("l").Items())
        {
            var text = part.Dig("c") is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            if (text == null) continue;
            long at = start + part.Dig("o").Ms();
            var trimmed = text.Trim();
            var last = words.Count > 0 ? words[^1] : null;
            if (trimmed.Length == 0)
            {
                if (last != null) last.End = Math.Max(at, last.Start);
                open = false; spaced = true;
                continue;
            }
            if (open && last != null && !Karaoke.UnspacedScript(trimmed)) { last.Text += trimmed; continue; }
            if (last != null) last.End = Math.Max(at, last.Start);
            words.Add(new LyricWord { Text = trimmed, Start = at, End = Math.Max(end, at), Joined = !spaced && words.Count > 0 });
            open = !Karaoke.UnspacedScript(trimmed);
            spaced = false;
        }
        if (words.Count == 0) return null;
        return new LyricLine { Words = words, Start = start, End = Math.Max(end, words[^1].End) };
    }

    /// <summary>The community translations of the track's lines in a language, keyed by the line's folded text.</summary>
    public async Task<Dictionary<string, string>?> Translations(TrackQuery q, string language, CancellationToken ct)
    {
        if (q.MusixmatchCommontrack == null) await Identify(q, ct);
        if (q.MusixmatchCommontrack == null) return null;
        var token = await Token(ct);
        if (token == null) return null;
        var codes = language switch { "zh-Hans" => new[] { "zh" }, "zh-Hant" => new[] { "z1" }, "nb" => new[] { "nb", "no" }, _ => new[] { language } };
        var found = new Dictionary<string, string>();
        foreach (var code in codes)
        {
            for (int page = 1; page <= 5; page++)
            {
                var msg = await Call("crowd.track.translations.get", new()
                {
                    ["usertoken"] = token, ["commontrack_id"] = q.MusixmatchCommontrack, ["selected_language"] = code,
                    ["comment_format"] = "text", ["part"] = "user", ["translation_fields_set"] = "minimal",
                    ["page"] = page.ToString(), ["page_size"] = "100",
                }, ct);
                if ((int)msg.Dig("header", "status_code").Num() != 200) break;
                int before = found.Count;
                var list = msg.Dig("body", "translations_list").Items().ToList();
                foreach (var e in list)
                {
                    var snippet = e.Dig("translation", "snippet").Str();
                    var text = e.Dig("translation", "description").Str();
                    if (snippet == null || text == null || text == snippet) continue;
                    var k = LineKey(snippet);
                    if (k.Length > 0) found[k] = text;
                }
                if (found.Count == before || list.Count < 20) break;
            }
            if (found.Count > 0) break;
        }
        return found.Count > 0 ? found : null;
    }

    public static string LineKey(string text)
    {
        var folded = new string(text.Normalize(NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        return string.Join(' ', Regex.Split(folded, @"[^\p{L}\p{N}]+").Where(w => w.Length > 0));
    }
}

// ===================================================================== Unison
public sealed class UnisonSource : ILyricsSource
{
    public string Key => "unison";
    public string Name => "Unison";
    public string Detail => "Community TTML library with word sync";
    readonly Func<string?> _translationLang;
    public UnisonSource(Func<string?> translationLang) { _translationLang = translationLang; }

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        if (q.Title.Length == 0 || q.Artist.Length == 0) return null;
        var r = await Http.GetAsync(Http.Url("https://unison.boidu.dev/lyrics", new Dictionary<string, string?>
        {
            ["song"] = q.Title, ["artist"] = q.Artist, ["album"] = q.Album.Length > 0 ? q.Album : null,
            ["duration"] = q.Seconds > 0 ? q.Seconds.ToString() : null,
        }), null, ct);
        var data = r.Json.Dig("data");
        if (data is not JsonObject) return null;
        if (!SourceUtil.CloseLength((int)data.Dig("duration").Num(), q.Seconds, 4)) return null;
        var lyrics = data.Dig("lyrics").Str();
        var lines = data.Dig("format").Str() switch
        {
            "ttml" => Formats.ParseTtml(lyrics, _translationLang()),
            "lrc" => Formats.ParseLrc(lyrics),
            _ => null,
        };
        return SourceUtil.Result(Key, Name, lines);
    }
}

// ===================================================================== NetEase
public sealed class NetEaseSource : ILyricsSource
{
    public string Key => "netease";
    public string Name => "NetEase Cloud Music";
    public string Detail => "Word timing (yrc) for many tracks others only line-time; strong on Chinese music";

    static Task<Http.Reply> Get(string path, Dictionary<string, string?> q, CancellationToken ct) =>
        Http.GetAsync(Http.Url("https://music.163.com/api/" + path, q), new Dictionary<string, string>
        {
            ["Referer"] = "https://music.163.com", ["User-Agent"] = Http.BrowserAgent,
        }, ct);

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        var lead = Normalize.LeadArtist(q.Artist);
        if (q.Title.Length == 0 || lead.Length == 0 || q.Seconds <= 0) return null;
        var title = Normalize.CleanTitle(q.Title);
        var found = await Get("search/get", new() { ["s"] = $"{title} {lead}", ["type"] = "1", ["limit"] = "10" }, ct);
        var fitting = found.Json.Dig("result", "songs").Items()
            .Where(s => Math.Abs((int)(s.Dig("duration").Num() / 1000) - q.Seconds) <= 3)
            .Where(s => Normalize.SameTitle(s.Dig("name").Str(), q.Title))
            .Where(s => s.Dig("artists").Items().Any(a => Normalize.SameArtist(a.Dig("name").Str(), lead)))
            .OrderBy(s => Math.Abs((int)(s.Dig("duration").Num() / 1000) - q.Seconds))
            .Take(3).ToList();
        LyricsResult? lineTimed = null;
        foreach (var song in fitting)
        {
            var id = song.Dig("id").Str();
            if (id == null) continue;
            var lr = await Get("song/lyric/v1", new() { ["id"] = id, ["lv"] = "1", ["yv"] = "1", ["tv"] = "-1" }, ct);
            if (Formats.ParsePieces(lr.Json.Dig("yrc", "lyric").Str(), false) is { } words) return SourceUtil.Result(Key, Name, words, Timing.Word);
            lineTimed ??= SourceUtil.Result(Key, Name, Formats.ParseLrc(lr.Json.Dig("lrc", "lyric").Str()), Timing.Line);
        }
        return lineTimed;
    }
}

// ===================================================================== LRCLIB
public sealed class LrcLibSource : ILyricsSource
{
    public string Key => "lrclib";
    public string Name => "LRCLIB";
    public string Detail => "Open library of line-synced lyrics, no key needed";
    static readonly Dictionary<string, string> H = new() { ["User-Agent"] = Http.AppAgent };

    static LyricsResult? From(JsonNode? rec)
    {
        if (rec is not JsonObject) return null;
        if (rec.Dig("instrumental").Truthy()) return new LyricsResult { SourceKey = "lrclib", Provider = "LRCLIB", Instrumental = true };
        if (Formats.ParseLrc(rec.Dig("syncedLyrics").Str()) is { } synced) return SourceUtil.Result("lrclib", "LRCLIB", synced);
        var plain = rec.Dig("plainLyrics").Str();
        return plain == null ? null : SourceUtil.Result("lrclib", "LRCLIB", Formats.ParsePlain(plain), Timing.None);
    }

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        if (q.Title.Length == 0 || q.Artist.Length == 0) return null;
        var baseQuery = new Dictionary<string, string?> { ["track_name"] = q.Title, ["artist_name"] = q.Artist };
        if (q.Album.Length > 0) baseQuery["album_name"] = q.Album;
        if (q.Seconds > 0)
        {
            var exact = new Dictionary<string, string?>(baseQuery) { ["duration"] = q.Seconds.ToString() };
            var r = await Http.GetAsync(Http.Url("https://lrclib.net/api/get", exact), H, ct);
            if (r.Ok && From(r.Json) is { } hit) return hit;
        }
        baseQuery.Remove("album_name");
        var s = await Http.GetAsync(Http.Url("https://lrclib.net/api/search", baseQuery), H, ct);
        JsonNode? best = null;
        foreach (var rec in s.Json.Items())
        {
            int len = (int)rec.Dig("duration").Num();
            if (q.Seconds > 0 && len > 0 && Math.Abs(len - q.Seconds) > 4) continue;
            if (best == null) { best = rec; continue; }
            bool synced = rec.Dig("syncedLyrics").Str() != null, was = best.Dig("syncedLyrics").Str() != null;
            if (synced != was) { if (synced) best = rec; continue; }
            if (q.Seconds > 0 && Math.Abs(len - q.Seconds) < Math.Abs((int)best.Dig("duration").Num() - q.Seconds)) best = rec;
        }
        return From(best);
    }
}

// ===================================================================== QQ Music
public sealed class QQMusicSource : ILyricsSource
{
    public string Key => "qq";
    public string Name => "QQ Music";
    public string Detail => "Mandarin, Cantonese and other Asian catalog, line synced";

    static async Task<JsonNode?> Ask(string name, object request, CancellationToken ct)
    {
        var body = new Dictionary<string, object> { ["comm"] = new { ct = "19", cv = "1859", uin = "0" }, [name] = request };
        var r = await Http.PostJsonAsync("https://u.y.qq.com/cgi-bin/musicu.fcg", body, new Dictionary<string, string>
        {
            ["Referer"] = "https://y.qq.com/", ["User-Agent"] = Http.BrowserAgent, ["Accept"] = "application/json",
        }, ct);
        return r.Json.Dig(name, "data");
    }

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        var lead = Normalize.LeadArtist(q.Artist);
        var titleKey = Normalize.MatchKey(Normalize.CleanTitle(q.Title));
        if (titleKey.Length == 0 || Normalize.MatchKey(lead).Length == 0) return null;
        var data = await Ask("req", new
        {
            module = "music.search.SearchCgiService", method = "DoSearchForQQMusicDesktop",
            param = new { query = $"{Normalize.CleanTitle(q.Title)} {lead}", search_type = 0, grp = 1, num_per_page = 8, page_num = 1 },
        }, ct);
        string? songId = null;
        foreach (var song in data.Dig("body", "song", "list").Items())
        {
            var name = song.Dig("title").Str() ?? song.Dig("songname").Str();
            if (Normalize.MatchKey(name) != titleKey) continue;
            if (!SourceUtil.CloseLength((int)song.Dig("interval").Num(), q.Seconds, 6)) continue;
            if (!song.Dig("singer").Items().Any(s => Normalize.SameArtist(s.Dig("name").Str(), q.Artist))) continue;
            songId = song.Dig("id").Str();
            if (songId != null) break;
        }
        if (songId == null) return null;
        var lyr = await Ask("music.musichallSong.PlayLyricInfo.GetPlayLyricInfo", new
        {
            module = "music.musichallSong.PlayLyricInfo", method = "GetPlayLyricInfo",
            param = new { crypt = 0, qrc = 0, songID = long.Parse(songId) },
        }, ct);
        var b64 = lyr.Dig("lyric").Str();
        if (b64 == null) return null;
        try
        {
            var lrc = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            return SourceUtil.Result(Key, Name, Formats.ParseLrc(lrc), Timing.Line);
        }
        catch { return null; }
    }
}

// ===================================================================== KuGou
public sealed class KuGouSource : ILyricsSource
{
    public string Key => "kugou";
    public string Name => "KuGou";
    public string Detail => "Word timing (krc) for Mandarin, Cantonese and other Asian songs";
    static readonly byte[] KrcKey = { 64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105 };
    static readonly Dictionary<string, string> H = new() { ["User-Agent"] = Http.BrowserAgent };

    public static List<LyricLine>? Decode(string? content)
    {
        if (content == null) return null;
        byte[] packed;
        try { packed = Convert.FromBase64String(content); } catch { return null; }
        if (packed.Length <= 4 || packed.Length > (1 << 20) || Encoding.ASCII.GetString(packed, 0, 4) != "krc1") return null;
        var zipped = new byte[packed.Length - 4];
        for (int i = 0; i < zipped.Length; i++) zipped[i] = (byte)(packed[i + 4] ^ KrcKey[i % KrcKey.Length]);
        try
        {
            using var z = new ZLibStream(new MemoryStream(zipped), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            var buf = new byte[16384];
            int n;
            while ((n = z.Read(buf)) > 0) { outMs.Write(buf, 0, n); if (outMs.Length > (1 << 20)) return null; }
            var text = Encoding.UTF8.GetString(outMs.ToArray()).TrimStart('﻿');
            return Formats.ParsePieces(text, true);
        }
        catch { return null; }
    }

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        var titleKey = Normalize.MatchKey(Normalize.CleanTitle(q.Title));
        var lead = Normalize.LeadArtist(q.Artist);
        if (titleKey.Length == 0 || Normalize.MatchKey(lead).Length == 0) return null;
        long ms = q.Seconds * 1000L;
        var search = await Http.GetAsync(Http.Url("https://krcs.kugou.com/search", new Dictionary<string, string?>
        {
            ["ver"] = "1", ["man"] = "yes", ["client"] = "mobi", ["hash"] = "", ["album_audio_id"] = "",
            ["keyword"] = $"{lead} - {Normalize.CleanTitle(q.Title)}", ["duration"] = ms > 0 ? ms.ToString() : null,
        }), H, ct);
        var fitting = search.Json.Dig("candidates").Items()
            .Where(c => c.Dig("id").Str() != null && c.Dig("accesskey").Str() != null && c.Dig("product_from").Str() != "ugc")
            .Where(c => Normalize.MatchKey(c.Dig("song").Str()) == titleKey && Normalize.SameArtist(c.Dig("singer").Str(), lead))
            .Where(c => ms <= 0 || c.Dig("duration").Num() <= 0 || Math.Abs((long)c.Dig("duration").Num() - ms) <= 8000)
            .OrderBy(c => Math.Abs((long)c.Dig("duration").Num() - ms)).Take(3).ToList();
        foreach (var c in fitting)
        {
            var dl = await Http.GetAsync(Http.Url("https://lyrics.kugou.com/download", new Dictionary<string, string?>
            {
                ["ver"] = "1", ["client"] = "pc", ["id"] = c.Dig("id").Str(), ["accesskey"] = c.Dig("accesskey").Str(),
                ["fmt"] = "krc", ["charset"] = "utf8",
            }), H, ct);
            if ((int)dl.Json.Dig("status").Num() == 200 && Decode(dl.Json.Dig("content").Str()) is { } lines)
                return SourceUtil.Result(Key, Name, lines, Timing.Word);
        }
        return null;
    }
}

// ===================================================================== Genius
/// <summary>Genius: plain, untimed lyrics as a last resort, read from the song's page.</summary>
public sealed partial class GeniusSource : ILyricsSource
{
    public string Key => "genius";
    public string Name => "Genius";
    public string Detail => "Untimed lyrics as a last resort, so you at least see the words";

    public async Task<LyricsResult?> AskAsync(TrackQuery q, CancellationToken ct)
    {
        if (q.Title.Length == 0) return null;
        var lead = Normalize.LeadArtist(q.Artist);
        var title = Normalize.CleanTitle(q.Title);
        var s = await Http.CurlGetAsync(Http.Url("https://genius.com/api/search/song", new Dictionary<string, string?>
        {
            ["q"] = $"{title} {lead}", ["per_page"] = "5",
        }), Http.AppAgent, ct);
        string? url = null;
        var titleKey = Normalize.MatchKey(title);
        if (!s.Ok) Log.Info($"genius: search answered {s.Status} {s.Body[..Math.Min(120, s.Body.Length)]}");
        foreach (var hit in s.Json.Dig("response", "sections").Items().SelectMany(sec => sec.Dig("hits").Items()))
        {
            var r = hit.Dig("result");
            var t = Normalize.MatchKey(r.Dig("title").Str());
            if (!(t == titleKey || t.StartsWith(titleKey) || titleKey.StartsWith(t))) continue;
            if (!Normalize.SameArtist(r.Dig("primary_artist", "name").Str() ?? r.Dig("artist_names").Str(), lead)) continue;
            url = r.Dig("url").Str();
            if (url != null) break;
        }
        if (url == null || !url.StartsWith("https://genius.com/")) return null;
        var page = await Http.CurlGetAsync(url, Http.AppAgent, ct);
        if (!page.Ok) return null;
        var text = ExtractLyrics(page.Body);
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Section headers like [Chorus] are not sung.
        var rows = text.Split('\n').Where(l => !SectionHeader().IsMatch(l.Trim()));
        var r2 = SourceUtil.Result(Key, Name, Karaoke.StaticLines(rows), Timing.None);
        if (r2 != null) r2.CreditLinks.Add(("Genius", url));
        return r2;
    }

    [GeneratedRegex(@"^\[[^\]]*\]$")] private static partial Regex SectionHeader();
    [GeneratedRegex(@"<(/?)([a-zA-Z0-9]+)([^>]*)>")] private static partial Regex Tag();

    /// <summary>The text of every data-lyrics-container element, without the parts Genius marks as
    /// excluded from selection (headers, ads); &lt;br&gt; is a new line.</summary>
    public static string ExtractLyrics(string html)
    {
        var sb = new StringBuilder();
        int pos = 0;
        while (true)
        {
            int at = html.IndexOf("data-lyrics-container=\"true\"", pos, StringComparison.Ordinal);
            if (at < 0) break;
            int open = html.LastIndexOf('<', at);
            int tagEnd = html.IndexOf('>', at);
            if (open < 0 || tagEnd < 0) break;
            int depth = 1, skipDepth = 0, i = tagEnd + 1;
            while (i < html.Length && depth > 0)
            {
                int lt = html.IndexOf('<', i);
                if (lt < 0) break;
                if (skipDepth == 0) sb.Append(WebUtility.HtmlDecode(html[i..lt]));
                var m = Tag().Match(html, lt);
                if (!m.Success || m.Index != lt) { if (skipDepth == 0) sb.Append('<'); i = lt + 1; continue; }
                bool closing = m.Groups[1].Value == "/";
                var name = m.Groups[2].Value.ToLowerInvariant();
                bool selfClose = m.Groups[3].Value.EndsWith('/') || name is "br" or "img" or "hr" or "input" or "meta";
                if (name == "br" && skipDepth == 0) sb.Append('\n');
                if (!selfClose)
                {
                    if (closing)
                    {
                        if (skipDepth > 0 && depth == skipDepth) skipDepth = 0;
                        depth--;
                    }
                    else
                    {
                        depth++;
                        if (skipDepth == 0 && m.Groups[3].Value.Contains("data-exclude-from-selection")) skipDepth = depth;
                    }
                }
                i = m.Index + m.Length;
            }
            sb.Append('\n');
            pos = i;
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }
}
