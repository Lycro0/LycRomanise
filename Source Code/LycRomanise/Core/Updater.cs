using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace LycRomanise.Core;

public sealed record UpdateAsset(string Name, string Url, long Size, string Sha256);
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string Notes, List<UpdateAsset> Assets);

public static class Updater
{
    public const string Repo = "Lycro0/LycRomanise";
    public const string ExeName = "LycRomanise.exe";
    public const string SetupName = "LycRomanise_Setup.exe";
    const long MaxBytes = 400L * 1024 * 1024;

    public static void CleanUp()
    {
        try { var p = Environment.ProcessPath; if (p != null) File.Delete(p + ".old"); } catch { }
    }

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
    public static string CurrentText => Current.ToString(3);
    public static string ReleasesUrl => $"https://github.com/{Repo}/releases";

    // Inno Setup leaves unins000.exe beside an installed copy.
    public static bool Installed => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    static bool Trusted(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https"
        && (u.Host == "github.com" || u.Host.EndsWith(".githubusercontent.com") || u.Host == "api.github.com");

    static Version? Parse(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var parts = t.Split('.', '-', '+').Take(3).Select(p => int.TryParse(p, out var n) ? n : -1).ToList();
        if (parts.Count == 0 || parts.Any(p => p < 0)) return null;
        while (parts.Count < 3) parts.Add(0);
        return new Version(parts[0], parts[1], parts[2]);
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            var r = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json", ["User-Agent"] = "LycRomanise/" + CurrentText,
            }, ct);
            if (!r.Ok) { Log.Info($"update check: HTTP {r.Status}"); return null; }
            var j = r.Json;
            if (j.Dig("draft").Truthy() || j.Dig("prerelease").Truthy()) return null;
            var tag = j.Dig("tag_name").Str() ?? "";
            var v = Parse(tag);
            if (v == null || v <= Current) { Log.Info($"up to date ({CurrentText}, latest {tag})"); return null; }
            var assets = j.Dig("assets").Items().Select(a =>
            {
                var digest = a.Dig("digest").Str() ?? "";
                return new UpdateAsset(a.Dig("name").Str() ?? "", a.Dig("browser_download_url").Str() ?? "",
                    (long)a.Dig("size").Num(), digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : "");
            }).ToList();
            var page = j.Dig("html_url").Str();
            Log.Info($"update available: {tag}");
            return new UpdateInfo(v, tag, Trusted(page) ? page! : ReleasesUrl, (j.Dig("body").Str() ?? "").Trim(), assets);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error("update check", ex); return null; }
    }

    public static UpdateAsset? AssetFor(UpdateInfo info) =>
        info.Assets.FirstOrDefault(a => a.Name.Equals(Installed ? SetupName : ExeName, StringComparison.OrdinalIgnoreCase)
                                        && Trusted(a.Url) && a.Sha256.Length == 64);

    public static bool CanSelfUpdate(UpdateInfo info) => AssetFor(info) != null;

    // Returns an error message, or null when the update has started.
    public static async Task<string?> InstallAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var asset = AssetFor(info);
        if (asset == null) return "This release has no file this copy can update itself with.";
        if (asset.Size > MaxBytes) return "The download is unexpectedly large.";
        var dir = Path.Combine(Path.GetTempPath(), "LycRomanise-update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, asset.Name);
        var part = dest + ".part";
        try
        {
            // A portable copy must be able to replace its own exe: find out now, while the app can still say so.
            if (!Installed)
            {
                var probe = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, ".lycromanise-write-test");
                try { File.WriteAllText(probe, ""); File.Delete(probe); }
                catch (Exception ex) { return "This folder cannot be written to, so the update cannot replace the app (" + ex.Message + "). Download it from the release page instead."; }
            }
            try { File.Delete(part); } catch { }
            using (var resp = await Http.Client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (!Trusted(resp.RequestMessage?.RequestUri?.ToString())) return "The download was redirected somewhere unexpected.";
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var file = File.Create(part);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    done += n;
                    if (done > MaxBytes) return "The download is unexpectedly large.";
                    sha.AppendData(buf, 0, n);
                    await file.WriteAsync(buf.AsMemory(0, n), ct);
                    progress?.Report(asset.Size > 0 ? done / (double)asset.Size : 0);
                }
                if (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() != asset.Sha256)
                    return "The download did not match GitHub's checksum, so it was not installed.";
            }
            // Only a complete, checked file ever takes the real name.
            File.Move(part, dest, true);
            if (Installed)
            {
                Process.Start(new ProcessStartInfo(dest, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
            }
            else
            {
                // A running exe can be renamed, so swap now while failures can still be shown. The new exe is
                // started only after this process exits, or the single-instance lock would turn it away.
                var exe = Environment.ProcessPath!;
                var old = exe + ".old";
                File.Move(exe, old, true);
                try { File.Move(dest, exe); }
                catch { try { File.Move(old, exe, true); } catch { } throw; }
                string Quote(string v) => "'" + v.Replace("'", "''") + "'";
                var ps = $"Wait-Process -Id {Environment.ProcessId} -Timeout 30 -ErrorAction SilentlyContinue; Start-Process -FilePath {Quote(exe)}";
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps));
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encoded}") { CreateNoWindow = true, UseShellExecute = false });
            }
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("update install", ex);
            return ex.Message;
        }
    }
}
