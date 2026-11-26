using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LycRomanise.Core;

namespace LycRomanise.Web;

// The page polls /state and draws the sweep itself, so the PC sends almost nothing.
public sealed class WebServer
{
    readonly AppController _app;
    TcpListener? _listener;
    CancellationTokenSource? _cts;
    int _version;
    string _lyricsJson = "{\"lines\":[]}";
    public int Port { get; private set; }
    public bool Running => _listener != null;
    public string Url => $"http://{LanAddress()}:{Port}/";

    public WebServer(AppController app) { _app = app; }

    public static string LanAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                if (ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || ni.Description.Contains("Hyper-V")) continue;
                var props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                foreach (var a in props.UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork) return a.Address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";   // a literal address: never waits on name resolution
    }

    public void Start(int port)
    {
        try
        {
            Port = port;
            _cts = new CancellationTokenSource();
            // Dual-stack, so http://localhost (which tries ::1 first) connects without a refused IPv6 attempt.
            _listener = TcpListener.Create(port);
            _listener.Start();
            _ = AcceptLoop(_listener, _cts.Token);
            Log.Info($"web view on {Url}");
            Notify(lyricsChanged: true);
        }
        catch (Exception ex)
        {
            Log.Error("web start", ex);
            _listener = null;
            _app.Tray?.Toast($"The web view could not use port {port}: {ex.Message}");
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    // UI thread only.
    public void Notify(bool lyricsChanged = false)
    {
        if (!lyricsChanged) return;
        _version++;
        var lines = _app.CurrentLines;
        var r = _app.CurrentResult;
        object W(LyricLine? l) => l == null ? null! : new
        {
            s = l.Start, e = l.End,
            w = l.Words.Select(w => new { t = w.Text, s = w.Start, e = w.End, j = w.Joined }),
        };
        _lyricsJson = JsonSerializer.Serialize(new
        {
            v = _version,
            synced = r?.Synced ?? true,
            credit = r?.HasLines == true && (_app.Settings.ShowSourceCredit || r.CreditRequired) ? r.Provider : null,
            lines = lines.Select(l => new { m = W(l.Main), sub = W(l.Sub), b = W(l.Backing), tr = l.Translation, a = l.Align == LineAlign.Trailing ? 1 : 0 }),
        });
    }

    string StateJson()
    {
        var m = _app.NowPlaying;
        var s = _app.Settings;
        return JsonSerializer.Serialize(new
        {
            v = _version,
            title = m?.Title, artist = m?.Artist,
            playing = m?.Playing ?? false,
            pos = _app.Clock(),
            dur = m?.DurationMs ?? 0,
            album = m?.Album,
            cover = _app.CoverBytes != null ? _app.CoverVersion : 0,
            colors = new { unsung = s.UnsungColor, sung = s.SungColor, next = s.NextLineColor, outline = s.OutlineColor, sub = s.SubColor },
            font = s.FontFamily, size = s.FontSize, bold = s.Bold, outlineWidth = s.OutlineWidth, glow = s.Glow,
            layout = s.StripStyle.ToString(), lift = s.WordLift, wordGlow = s.WordGlow, motion = s.LyricsMotion.ToString(),
            showNext = s.ShowNextLine, showTr = s.ShowTranslation, showBacking = s.ShowBackingVocals,
        });
    }

    async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    async Task Serve(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                // ReceiveTimeout does not apply to async reads: a deadline so a silent client cannot hold its socket.
                using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                reqCts.CancelAfter(10000);
                ct = reqCts.Token;
                var stream = client.GetStream();
                var buf = new byte[4096];
                int n = await stream.ReadAsync(buf, ct);
                if (n <= 0) return;
                var request = Encoding.ASCII.GetString(buf, 0, n);
                var firstLine = request.Split("\r\n")[0].Split(' ');
                if (firstLine.Length < 2 || firstLine[0] != "GET") { await Send(stream, 405, "text/plain", "method not allowed", ct); return; }
                var path = firstLine[1].Split('?')[0];
                if (!HostAllowed(request)) { await Send(stream, 403, "text/plain", "forbidden", ct); return; }
                switch (path)
                {
                    case "/" when firstLine[1].Contains("obs=1"):
                    case "/obs":
                        await Send(stream, 200, "text/html; charset=utf-8", Page("web.obs.html"), ct);
                        break;
                    case "/":
                    case "/index.html":
                        await Send(stream, 200, "text/html; charset=utf-8", Page("web.index.html"), ct);
                        break;
                    case "/cover":
                        var cover = _app.CoverBytes;
                        if (cover == null) await Send(stream, 404, "text/plain", "no cover", ct);
                        else await SendBytes(stream, "image/jpeg", cover, ct);
                        break;
                    case "/state":
                        string state = "";
                        await _app.Overlay.Dispatcher.InvokeAsync(() => state = StateJson());
                        await Send(stream, 200, "application/json", state, ct);
                        break;
                    case "/lyrics":
                        await Send(stream, 200, "application/json", _lyricsJson, ct);
                        break;
                    default:
                        await Send(stream, 404, "text/plain", "not found", ct);
                        break;
                }
            }
            catch { }
        }
    }

    // Only names that really point at this machine: stops a DNS-rebinding page from reading what is playing.
    static bool HostAllowed(string request)
    {
        var line = request.Split("\r\n").FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase));
        if (line == null) return true;   // HTTP/1.0
        var host = line[5..].Trim();
        if (host.StartsWith('[')) host = host[1..Math.Max(1, host.IndexOf(']'))];
        else if (host.Count(c => c == ':') == 1) host = host[..host.IndexOf(':')];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(host, out _)
               || host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    static readonly Dictionary<string, string> _pages = new();
    static string Page(string name)
    {
        lock (_pages)
        {
            if (_pages.TryGetValue(name, out var page)) return page;
            using var s = typeof(WebServer).Assembly.GetManifestResourceStream(name);
            using var r = new StreamReader(s!);
            return _pages[name] = r.ReadToEnd();
        }
    }

    static async Task SendBytes(NetworkStream stream, string type, byte[] body, CancellationToken ct)
    {
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
    }

    static async Task Send(NetworkStream stream, int status, string type, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\nConnection: close\r\n\r\n";   // no CORS: the pages are same-origin, other sites must not read this
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
    }
}
