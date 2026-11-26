using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LycRomanise.Core;

public static class Paths
{
    public static readonly string Data = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LycRomanise");
    // The v2.0 to v2.1 data folder.
    public static readonly string OldData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LycRomanise Modern");
    public static string Settings => Path.Combine(Data, "settings.json");
    public static string Cache => Path.Combine(Data, "lyrics-cache");
    public static string Logs => Path.Combine(Data, "logs");
    public static string DefaultImported => Path.Combine(Data, "Imported Lyrics");

    static Paths()
    {
        MigrateOldFolder();
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(DefaultImported);
    }

    static void MigrateOldFolder()
    {
        try
        {
            if (!Directory.Exists(OldData)) return;
            if (!Directory.Exists(Data)) { Directory.Move(OldData, Data); return; }
            MergeInto(OldData, Data);
            Directory.Delete(OldData, true);
        }
        catch { /* a file in use: try again next start */ }
    }

    static void MergeInto(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(f));
            if (!File.Exists(dest)) File.Move(f, dest);
        }
        foreach (var d in Directory.GetDirectories(from)) MergeInto(d, Path.Combine(to, Path.GetFileName(d)));
    }

    public static string FromOld(string path) =>
        path.StartsWith(OldData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || path.Equals(OldData, StringComparison.OrdinalIgnoreCase)
            ? Data + path[OldData.Length..] : path;

    public static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Encoding.UTF8);
        File.Move(tmp, path, true);
    }
}

public static class Log
{
    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(Paths.Logs, "lycromanise.log");

    public static void Info(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never break the app */ }
        }
    }

    public static void Error(string where, Exception ex) => Info($"ERROR {where}: {ex.GetType().Name}: {ex.Message}");
}

public static class Http
{
    public const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";
    public const string AppAgent = "LycRomanise/2.0 (https://github.com/Lycro0/LycRomanise)";

    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AllowAutoRedirect = true,
    })
    {
        Timeout = TimeSpan.FromSeconds(8),
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };

    public static string Url(string baseUrl, IEnumerable<KeyValuePair<string, string?>> query)
    {
        var sb = new StringBuilder(baseUrl);
        bool first = !baseUrl.Contains('?');
        foreach (var (k, v) in query)
        {
            if (v == null) continue;
            sb.Append(first ? '?' : '&').Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
            first = false;
        }
        return sb.ToString();
    }

    public sealed record Reply(int Status, string Body, HttpResponseMessage? Response)
    {
        public JsonNode? Json { get { try { return JsonNode.Parse(Body); } catch { return null; } } }
        public bool Ok => Status is >= 200 and < 300;
    }

    public static async Task<Reply> GetAsync(string url, IDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower };
        return await SendAsync(req, headers, ct);
    }

    public static async Task<Reply> PostJsonAsync(string url, object body, IDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        return await SendAsync(req, headers, ct);
    }

    // Set when a request failed (network, 429, 5xx), so the resulting miss is not cached as final.
    public static readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<bool>?> Degraded = new();

    public static void MarkDegraded() { if (Degraded.Value is { } box) box.Value = true; }

    static Reply Note(Reply r)
    {
        if (r.Status is 0 or 429 or >= 500) MarkDegraded();
        return r;
    }

    static async Task<Reply> SendAsync(HttpRequestMessage req, IDictionary<string, string>? headers, CancellationToken ct)
    {
        if (headers != null)
            foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        if (!req.Headers.Contains("User-Agent")) req.Headers.TryAddWithoutValidation("User-Agent", AppAgent);
        try
        {
            var resp = await Client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return Note(new Reply((int)resp.StatusCode, body, resp));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Info($"http {req.RequestUri?.Host}: {ex.GetType().Name} {ex.Message}");
            return Note(new Reply(0, "", null));
        }
    }

    static readonly string SystemCurl = Path.Combine(Environment.SystemDirectory, "curl.exe");

    // Windows' curl.exe, for sites whose Cloudflare front turns away .NET's TLS handshake (Genius).
    public static async Task<Reply> CurlGetAsync(string url, string userAgent, CancellationToken ct)
    {
        if (!File.Exists(SystemCurl)) return await GetAsync(url, new Dictionary<string, string> { ["User-Agent"] = userAgent }, ct);
        var psi = new System.Diagnostics.ProcessStartInfo(SystemCurl)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-s", "-L", "--compressed", "--max-time", "8", "-A", userAgent, "-w", "\n%{http_code}", url }) psi.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            using var reg = ct.Register(() => { try { p.Kill(); } catch { } });
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            int nl = output.LastIndexOf('\n');
            int status = nl >= 0 && int.TryParse(output[(nl + 1)..].Trim(), out var st) ? st : 0;
            return Note(new Reply(status, nl >= 0 ? output[..nl] : output, null));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Info($"curl {url}: {ex.Message}");
            return Note(new Reply(0, "", null));
        }
    }

    // ---- JSON helpers: forgiving reads, never throw ----
    public static JsonNode? Dig(this JsonNode? node, params string[] path)
    {
        foreach (var key in path)
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(key, out node)) return null;
        }
        return node;
    }

    public static string? Str(this JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s.Length > 0 ? s : null;
        if (v.TryGetValue<double>(out var d)) return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (v.TryGetValue<long>(out var l)) return l.ToString();
        return null;
    }

    public static double Num(this JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : 0;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        if (v.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        return 0;
    }

    public static bool Truthy(this JsonNode? node) => node.Num() != 0 || (node is JsonValue v && v.TryGetValue<bool>(out var b) && b);

    public static IEnumerable<JsonNode> Items(this JsonNode? node) =>
        node is JsonArray a ? a.Where(x => x != null)! : Enumerable.Empty<JsonNode>();

    public static long Ms(this JsonNode? seconds) => (long)Math.Round(seconds.Num() * 1000);
}
