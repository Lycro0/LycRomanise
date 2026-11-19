using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace LycRomanise.Core;

public sealed record SpotifyTrack(
    string Id, string Title, string Artist, string Album, int DurationMs, string? Isrc, string? ImageUrl)
{
    public TrackQuery ToQuery() => new()
    {
        Title = Title, Artist = Artist, Album = Album, Seconds = (int)Math.Round(DurationMs / 1000.0),
        SpotifyId = Id, Isrc = Isrc,
    };
}

public sealed record SpotifyNow(SpotifyTrack? Track, long ProgressMs, bool Playing, DateTime At);

/// <summary>Spotify Web API on the user's own login (Authorization Code with PKCE, no secret). Only the
/// read endpoints are used (what is playing and what is queued), and they work on free accounts too.</summary>
public sealed class SpotifyClient
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    const string Scopes = "user-read-currently-playing user-read-playback-state";

    readonly Func<AppSettings> _settings;
    readonly Action _save;
    string? _accessToken;
    DateTime _expires;
    readonly SemaphoreSlim _gate = new(1, 1);
    DateTime _backoffUntil;

    public string? UserName { get; private set; }
    public bool LoggedIn => !string.IsNullOrEmpty(_settings().SpotifyRefreshToken) && !string.IsNullOrEmpty(_settings().SpotifyClientId);
    public string? LastError { get; private set; }

    public SpotifyClient(Func<AppSettings> settings, Action save)
    {
        _settings = settings;
        _save = save;
    }

    // ------------------------------------------------------------------ login
    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Opens Spotify's login page in the browser and waits for it to send the browser back
    /// to the app on 127.0.0.1. Returns null on success, else what went wrong.</summary>
    public async Task<string?> LoginAsync(CancellationToken ct)
    {
        var clientId = _settings().SpotifyClientId?.Trim();
        if (string.IsNullOrEmpty(clientId)) return "Paste your Spotify app's Client ID first.";
        var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var url = Http.Url("https://accounts.spotify.com/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = RedirectUri,
            ["code_challenge_method"] = "S256", ["code_challenge"] = challenge, ["scope"] = Scopes, ["state"] = state,
        });

        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 43821);
            listener.Start();
        }
        catch (Exception ex) { return "Port 43821 is in use: " + ex.Message; }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, timeout.Token);
                var first = Encoding.ASCII.GetString(buf, 0, n).Split("\r\n")[0].Split(' ');
                if (first.Length < 2 || !first[1].StartsWith("/callback")) { await Reply(stream, "Not found"); continue; }
                var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + first[1]).Query);
                if (query["state"] != state) { await Reply(stream, "This login link is out of date. Try again from LycRomanise."); return "The login was not the one LycRomanise started."; }
                if (query["error"] is { } err) { await Reply(stream, "Spotify login was cancelled. You can close this tab."); return "Spotify said: " + err; }
                var code = query["code"];
                var tokenErr = await ExchangeAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code", ["code"] = code ?? "", ["redirect_uri"] = RedirectUri,
                    ["client_id"] = clientId, ["code_verifier"] = verifier,
                }, timeout.Token);
                await Reply(stream, tokenErr == null
                    ? "You're logged in to LycRomanise. You can close this tab."
                    : "Login failed: " + WebUtility.HtmlEncode(tokenErr));
                if (tokenErr == null) await LoadProfileAsync(ct);
                return tokenErr;
            }
        }
        catch (OperationCanceledException) { return "The login timed out."; }
        finally { listener.Stop(); }
    }

    static async Task Reply(NetworkStream stream, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>LycRomanise</title><body style=\"font-family:Segoe UI,sans-serif;background:#111;color:#eee;display:grid;place-items:center;height:100vh;margin:0\"><div style=\"text-align:center\"><div style=\"font-size:42px\">♪</div><h2>{message}</h2></div>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(body);
    }

    async Task<string?> ExchangeAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.Client.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!resp.IsSuccessStatusCode) return json.Dig("error_description").Str() ?? json.Dig("error").Str() ?? resp.StatusCode.ToString();
            _accessToken = json.Dig("access_token").Str();
            _expires = DateTime.UtcNow.AddSeconds(json.Dig("expires_in").Num() - 60);
            if (json.Dig("refresh_token").Str() is { } refresh) { _settings().SpotifyRefreshToken = refresh; _save(); }
            LastError = null;
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return ex.Message; }
    }

    public void Logout()
    {
        _settings().SpotifyRefreshToken = null;
        _save();
        _accessToken = null;
        UserName = null;
    }

    async Task<string?> TokenAsync(CancellationToken ct)
    {
        if (!LoggedIn) return null;
        await _gate.WaitAsync(ct);
        try
        {
            if (_accessToken != null && DateTime.UtcNow < _expires) return _accessToken;
            var err = await ExchangeAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = _settings().SpotifyRefreshToken!, ["client_id"] = _settings().SpotifyClientId!,
            }, ct);
            if (err != null)
            {
                LastError = err;
                Log.Info("spotify: refresh failed: " + err);
                if (err.Contains("revoked", StringComparison.OrdinalIgnoreCase) || err.Contains("invalid", StringComparison.OrdinalIgnoreCase)) Logout();
                return null;
            }
            return _accessToken;
        }
        finally { _gate.Release(); }
    }

    async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        if (DateTime.UtcNow < _backoffUntil) return null;
        var token = await TokenAsync(ct);
        if (token == null) return null;
        var r = await Http.GetAsync("https://api.spotify.com/v1/" + path, new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }, ct);
        if (r.Status == 429)
        {
            var wait = r.Response?.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            _backoffUntil = DateTime.UtcNow + wait;
            Log.Info($"spotify: rate limited for {wait.TotalSeconds:0}s");
            return null;
        }
        if (r.Status == 401) { _accessToken = null; return null; }
        if (r.Status == 204 || !r.Ok) return null;
        return r.Json;
    }

    public async Task LoadProfileAsync(CancellationToken ct)
    {
        var me = await GetAsync("me", ct);
        UserName = me.Dig("display_name").Str() ?? me.Dig("id").Str();
    }

    static SpotifyTrack? Track(JsonNode? t)
    {
        if (t.Dig("type").Str() != "track" || t.Dig("id").Str() is not { } id) return null;
        var artists = string.Join(", ", t.Dig("artists").Items().Select(a => a.Dig("name").Str()).Where(n => n != null));
        var image = t.Dig("album", "images").Items().FirstOrDefault().Dig("url").Str();
        return new SpotifyTrack(id, t.Dig("name").Str() ?? "", artists, t.Dig("album", "name").Str() ?? "",
            (int)t.Dig("duration_ms").Num(), t.Dig("external_ids", "isrc").Str(), image);
    }

    public async Task<SpotifyNow?> CurrentlyPlayingAsync(CancellationToken ct)
    {
        var j = await GetAsync("me/player/currently-playing?additional_types=track", ct);
        if (j == null) return null;
        return new SpotifyNow(Track(j.Dig("item")), (long)j.Dig("progress_ms").Num(), j.Dig("is_playing").Truthy(), DateTime.UtcNow);
    }

    public async Task<List<SpotifyTrack>> QueueAsync(CancellationToken ct)
    {
        var j = await GetAsync("me/player/queue", ct);
        return j.Dig("queue").Items().Select(Track).Where(t => t != null).Select(t => t!).ToList();
    }
}
