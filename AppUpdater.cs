using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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
    string? AssetDigest,
    string? ReleaseUrl);

public static class AppUpdater
{
    public const string GitHubOwner = "vpskvd";
    public const string GitHubRepo = "fbuzgcmodkjz";
    private const string InstallerName = "InstantReplaySetup.exe";
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
            return new UpdateCheckResult(false, true, CurrentVersion, null, "", null, null, 0, null, null);
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
        string? assetDigest = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var dEl) ? dEl.GetString() : null;
                long sz = asset.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long sVal) ? sVal : 0;
                string? digest = asset.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
                if (!string.Equals(name, InstallerName, StringComparison.OrdinalIgnoreCase) ||
                    !IsInstallerUrl(url) || !IsSha256(digest) || sz < 65536) continue;

                assetName = name;
                downloadUrl = url;
                assetSize = sz;
                assetDigest = digest;
                break;
            }
        }

        bool newer = latest != null && latest > CurrentVersion && !string.IsNullOrWhiteSpace(downloadUrl);
        return new UpdateCheckResult(newer, false, CurrentVersion, latest, tag, assetName, downloadUrl, assetSize, assetDigest, releaseUrl);
    }

    private static bool IsInstallerUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
        url.IsDefaultPort && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.StartsWith($"/{GitHubOwner}/{GitHubRepo}/releases/download/", StringComparison.OrdinalIgnoreCase) &&
        url.AbsolutePath.EndsWith($"/{InstallerName}", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? digest) => digest is { Length: 71 } &&
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest[7..].All(Uri.IsHexDigit);

    public static async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<double>? progress,
        CancellationToken ct = default)
    {
        if (!IsInstallerUrl(update.DownloadUrl) || !IsSha256(update.AssetDigest) || update.AssetSizeBytes < 65536)
            throw new InvalidOperationException("Release installer metadata is missing or invalid.");

        string destPath = Path.Combine(Path.GetTempPath(), $"ir_update_{Guid.NewGuid():N}_{InstallerName}");
        bool created = false;
        try
        {
            using var dlClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            dlClient.DefaultRequestHeaders.UserAgent.ParseAdd($"InstantReplay/{CurrentVersionText}");

            using HttpResponseMessage resp = await dlClient.GetAsync(update.DownloadUrl!, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long totalBytes = update.AssetSizeBytes;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream src = await resp.Content.ReadAsStreamAsync(ct);
            await using (var dst = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                created = true;
                byte[] buf = new byte[65536];
                long readTotal = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    readTotal += n;
                    if (readTotal > totalBytes) throw new InvalidDataException("Installer exceeds the release asset size.");
                    hash.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    progress?.Report(Math.Min(99, readTotal * 100.0 / totalBytes));
                }
                if (readTotal != totalBytes) throw new InvalidDataException("Installer size differs from the release asset.");
                await dst.FlushAsync(ct);
            }

            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.AssetDigest![7..])))
                throw new InvalidDataException("Installer SHA-256 differs from the GitHub release asset.");

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
            try { if (created) File.Delete(destPath); } catch { }
            throw;
        }
    }

    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = "/SP- /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS --update",
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
