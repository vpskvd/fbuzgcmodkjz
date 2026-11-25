using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace InstantReplay;

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    bool NoReleasesYet,
    Version CurrentVersion,
    Version? LatestVersion,
    string TagName,
    string? AssetName,
    string? DownloadUrl,
    long AssetSizeBytes,
    string? ReleaseUrl);

public static class AppUpdater
{
    public const string GitHubOwner = "vpskvd";
    public const string GitHubRepo = "fbuzgcmodkjz";
    private static readonly string LatestReleaseApiUrl =
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"InstantReplay/{CurrentVersion.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            Version? v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v == null) return new Version(1, 0, 0);
            return new Version(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
        }
    }

    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(LatestReleaseApiUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(false, true, CurrentVersion, null, "", null, null, 0, null);
        }

        resp.EnsureSuccessStatusCode();
        await using Stream stream = await resp.Content.ReadAsStreamAsync(ct);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        JsonElement root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var tagEl) ? (tagEl.GetString() ?? "").Trim() : "";
        string? releaseUrl = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
        Version? latest = ParseTagVersion(tag);

        string? assetName = null;
        string? downloadUrl = null;
        long assetSize = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var dEl) ? dEl.GetString() : null;
                long sz = asset.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long sVal) ? sVal : 0;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;

                bool isSetup = name.Contains("setup", StringComparison.OrdinalIgnoreCase);
                if (downloadUrl == null || isSetup)
                {
                    assetName = name;
                    downloadUrl = url;
                    assetSize = sz;
                    if (isSetup) break;
                }
            }
        }

        bool newer = latest != null && latest > CurrentVersion && !string.IsNullOrWhiteSpace(downloadUrl);
        return new UpdateCheckResult(newer, false, CurrentVersion, latest, tag, assetName, downloadUrl, assetSize, releaseUrl);
    }

    public static async Task<string> DownloadInstallerAsync(
        string downloadUrl,
        string? preferredFileName,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        string safeName = string.IsNullOrWhiteSpace(preferredFileName)
            ? "InstantReplaySetup.exe"
            : Path.GetFileName(preferredFileName);
        if (!safeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            safeName += ".exe";

        string destPath = Path.Combine(Path.GetTempPath(), $"ir_update_{Guid.NewGuid():N}_{safeName}");
        try
        {
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            dlClient.DefaultRequestHeaders.UserAgent.ParseAdd($"InstantReplay/{CurrentVersionText}");

            using HttpResponseMessage resp = await dlClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long totalBytes = resp.Content.Headers.ContentLength ?? -1;
            await using Stream src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                byte[] buf = new byte[65536];
                long readTotal = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    readTotal += n;
                    if (totalBytes > 0)
                        progress?.Report(Math.Clamp(readTotal * 100.0 / totalBytes, 0, 100));
                }
                await dst.FlushAsync(ct);
            }

            var info = new FileInfo(destPath);
            if (!info.Exists || info.Length < 65536)
                throw new InvalidOperationException("downloaded installer file is incomplete.");

            using (var check = File.OpenRead(destPath))
            {
                if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
                    throw new InvalidOperationException("downloaded file is not a valid windows executable.");
            }

            progress?.Report(100);
            return destPath;
        }
        catch
        {
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            throw;
        }
    }

    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "--update",
            UseShellExecute = true,
        });
    }

    public static Version? ParseTagVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string clean = tag.Trim().TrimStart('v', 'V');
        int dash = clean.IndexOfAny(['-', '+']);
        if (dash >= 0) clean = clean[..dash];
        string[] parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            return null;
        int build = parts.Length >= 3 && int.TryParse(parts[2], out int b) ? b : 0;
        return new Version(Math.Max(0, major), Math.Max(0, minor), Math.Max(0, build));
    }
}
