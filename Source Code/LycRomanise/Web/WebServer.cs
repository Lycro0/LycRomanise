using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LycRomanise.Core;

namespace LycRomanise.Web;

/// <summary>A tiny web server for phones, tablets and OBS browser sources on the same network. The page
/// polls /state and draws the sweep itself, so the PC sends almost nothing.</summary>
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
        return "localhost";
    }

    public void Start(int port)
    {
        try
        {
            Port = port;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
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

    /// <summary>Called on the UI thread when the track or lyrics change.</summary>
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
            credit = r?.HasLines == true ? r.Provider : null,
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
            colors = new { unsung = s.UnsungColor, sung = s.SungColor, next = s.NextLineColor, outline = s.OutlineColor, sub = s.SubColor },
            font = s.FontFamily,
            showNext = s.ShowNextLine, showTr = s.ShowTranslation,
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
                client.ReceiveTimeout = 5000;
                var stream = client.GetStream();
                var buf = new byte[4096];
                int n = await stream.ReadAsync(buf, ct);
                if (n <= 0) return;
                var request = Encoding.ASCII.GetString(buf, 0, n);
                var firstLine = request.Split("\r\n")[0].Split(' ');
                if (firstLine.Length < 2 || firstLine[0] != "GET") { await Send(stream, 405, "text/plain", "method not allowed", ct); return; }
                var path = firstLine[1].Split('?')[0];
                switch (path)
                {
                    case "/":
                    case "/index.html":
                        await Send(stream, 200, "text/html; charset=utf-8", Page(), ct);
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

    static string? _page;
    static string Page()
    {
        if (_page != null) return _page;
        using var s = typeof(WebServer).Assembly.GetManifestResourceStream("web.index.html");
        using var r = new StreamReader(s!);
        return _page = r.ReadToEnd();
    }

    static async Task Send(NetworkStream stream, int status, string type, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
    }
}
