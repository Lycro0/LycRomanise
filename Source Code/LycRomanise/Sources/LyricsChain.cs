using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LycRomanise.Core;

namespace LycRomanise.Sources;

/// <summary>Asks the sources in the user's order and keeps the best answer: word timing beats line
/// timing beats plain text. With "prefer word sync" off, the first synced answer wins.</summary>
public sealed class LyricsChain
{
    readonly Func<AppSettings> _get;
    AppSettings _settings => _get();
    public MusixmatchSource Musixmatch { get; }
    public SpicyLyricsSource Spicy { get; }
    public ImportedSource Imported { get; }
    public IReadOnlyDictionary<string, ILyricsSource> All { get; }

    static readonly JsonSerializerOptions CacheJson = new() { Converters = { new JsonStringEnumConverter() } };
    const int CacheVersion = 2;

    public LyricsChain(Func<AppSettings> settings)
    {
        _get = settings;
        Musixmatch = new MusixmatchSource();
        Spicy = new SpicyLyricsSource(() => _settings.SpicyKey);
        Imported = new ImportedSource(() => _settings.ImportedFolder);
        var list = new ILyricsSource[]
        {
            Spicy, new BiniLyricsSource(() => null), Imported, Musixmatch, new UnisonSource(() => null),
            new NetEaseSource(), new LrcLibSource(), new QQMusicSource(), new KuGouSource(), new GeniusSource(),
        };
        All = list.ToDictionary(s => s.Key);
    }

    static int Rank(LyricsResult? r) => r == null ? -1 : r.Instrumental ? 0 : (int)r.Timing + 1;

    static string CachePath(TrackQuery q)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(q.Key + "|" + q.Seconds)))[..20];
        return Path.Combine(Paths.Cache, hash + ".json");
    }

    sealed class CacheEntry
    {
        public int Version { get; set; }
        public string Order { get; set; } = "";
        public DateTime At { get; set; }
        public LyricsResult? Result { get; set; }
    }

    string OrderSignature() => string.Join(",", _settings.Sources.Where(s => s.Enabled).Select(s => s.Key))
        + (_settings.PreferWordSync ? "+w" : "") + (string.IsNullOrEmpty(_settings.SpicyKey) ? "" : "+k");

    public LyricsResult? FromCache(TrackQuery q)
    {
        try
        {
            var path = CachePath(q);
            if (!File.Exists(path)) return null;
            var e = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path), CacheJson);
            if (e == null || e.Version != CacheVersion || e.Order != OrderSignature()) return null;
            // A miss is remembered for a day, a hit for a month.
            var age = DateTime.UtcNow - e.At;
            if (e.Result == null ? age > TimeSpan.FromDays(1) : age > TimeSpan.FromDays(30)) return null;
            return e.Result ?? new LyricsResult { SourceKey = "", Provider = "" };
        }
        catch { return null; }
    }

    void ToCache(TrackQuery q, LyricsResult? r)
    {
        try
        {
            if (r?.SourceKey == "imported") return;   // the file may change; it is read fresh each time
            Paths.WriteAtomic(CachePath(q), JsonSerializer.Serialize(new CacheEntry
            {
                Version = CacheVersion, Order = OrderSignature(), At = DateTime.UtcNow, Result = r,
            }, CacheJson));
        }
        catch (Exception ex) { Log.Error("cache write", ex); }
    }

    public void Forget(TrackQuery q) { try { File.Delete(CachePath(q)); } catch { } }

    public static void ClearCache()
    {
        foreach (var f in Directory.EnumerateFiles(Paths.Cache, "*.json")) { try { File.Delete(f); } catch { } }
    }

    async Task<LyricsResult?> AskOne(ILyricsSource source, TrackQuery q, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await source.AskAsync(q, timeout.Token);
            if (r != null) r.SourceKey = source.Key;
            Log.Info($"{source.Key}: {(r == null ? "nothing" : r.Instrumental ? "instrumental" : $"{r.Lines.Count} lines, {r.Timing} timing")} ({sw.ElapsedMilliseconds} ms)");
            return r;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Info($"{source.Key}: timed out");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error(source.Key, ex);
            return null;
        }
    }

    /// <summary>The lyrics for the track. Calls progress with each source as it is asked.</summary>
    public async Task<LyricsResult?> FetchAsync(TrackQuery q, Action<string>? progress, CancellationToken ct, bool useCache = true)
    {
        if (useCache && FromCache(q) is { } cached)
        {
            Log.Info($"cache: {q.Artist} - {q.Title} -> {(cached.SourceKey == "" ? "no lyrics" : cached.Provider)}");
            if (cached.SourceKey != "") return cached;
            // A remembered miss, unless the user has since dropped a file in the imported folder.
            if (Imported.Find(q) == null) return null;
        }

        progress?.Invoke("Identifying track…");
        await Musixmatch.Identify(q, ct);
        Log.Info($"track: {q.Artist} - {q.Title} ({q.Seconds}s) spotify={q.SpotifyId ?? "-"} isrc={q.Isrc ?? "-"}");

        if (_settings.SongSourceOverride.TryGetValue(q.Key, out var forced) && All.TryGetValue(forced, out var only))
        {
            progress?.Invoke($"Asking {only.Name}…");
            var r = await AskOne(only, q, ct);
            if (r is { HasLines: true }) return r;
        }

        LyricsResult? best = null;
        foreach (var entry in _settings.Sources)
        {
            if (!entry.Enabled || !All.TryGetValue(entry.Key, out var source)) continue;
            progress?.Invoke($"Asking {source.Name}…");
            var r = await AskOne(source, q, ct);
            if (r == null || (!r.HasLines && !r.Instrumental)) continue;
            if (r.Instrumental) { best ??= r; continue; }
            if (Rank(r) > Rank(best)) best = r;
            if (best!.Timing == Timing.Word) break;
            if (best.Timing == Timing.Line && !_settings.PreferWordSync) break;
        }
        // Instrumental only counts when nobody had words.
        ToCache(q, best);
        return best;
    }

    /// <summary>Every enabled source's answer, asked at once, for the "choose lyrics" picker.</summary>
    public async Task<List<LyricsResult>> FetchAllAsync(TrackQuery q, CancellationToken ct)
    {
        await Musixmatch.Identify(q, ct);
        var tasks = All.Values.Select(s => AskOne(s, q, ct)).ToList();
        var results = await Task.WhenAll(tasks);
        var order = _settings.Sources.Select(s => s.Key).ToList();
        return results.Where(r => r is { HasLines: true }).Select(r => r!)
            .OrderBy(r => order.IndexOf(r.SourceKey)).ToList();
    }

    public void Remember(TrackQuery q, LyricsResult r) => ToCache(q, r);
}
