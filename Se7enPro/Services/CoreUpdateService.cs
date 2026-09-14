using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

public sealed class CoreUpdateService : ICoreUpdateService, IDisposable
{
    private const string AetherAssetName = "aether-windows-x86_64.zip";
    private const string ReleaseApiUrl =
        "https://api.github.com/repos/CluvexStudio/Aether/releases/latest";

    private const string ReleaseDownloadPrefix =
        "https://github.com/CluvexStudio/Aether/releases/download/";

    private const string RequiredSignerSubject = "Cluvex";

    private static readonly bool RequireSignedCoreBinaries = true;

    private const long MaxArchiveBytes = 128L * 1024 * 1024;

    private const long MaxExtractedBytes = 256L * 1024 * 1024;

    private readonly ILogger<CoreUpdateService> _logger;
    private readonly HttpClient _httpClient;

    public CoreUpdateService(ILogger<CoreUpdateService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Se7enPro-CoreUpdater/1.0");
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool IsTrustedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        return uri.AbsoluteUri.StartsWith(ReleaseDownloadPrefix, StringComparison.Ordinal);
    }

    public const string UnknownVersion = "unknown";

    public string GetInstalledVersion(string coreId)
    {
        try
        {
            switch (coreId.ToLowerInvariant())
            {
                case "aether":
                {
                    var exePath = FindAetherExecutable();
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        var ver = QueryExeVersion(exePath, "--version");
                        if (!string.IsNullOrEmpty(ver))
                        {

                            var parts = ver.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2) return parts[1].Trim();
                            return ver.Trim();
                        }

                        var fvi = FileVersionInfo.GetVersionInfo(exePath);
                        if (!string.IsNullOrEmpty(fvi.FileVersion))
                            return fvi.FileVersion;
                    }
                    return UnknownVersion;
                }

                case "tor":
                {
                    var exePath = FindTorExecutable();
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        var ver = QueryExeVersion(exePath, "--version");
                        if (!string.IsNullOrEmpty(ver))
                        {

                            var lines = ver.Split('\n');
                            if (lines.Length > 0)
                            {
                                var first = lines[0].Trim();
                                var idx = first.IndexOf("version ", StringComparison.OrdinalIgnoreCase);
                                if (idx >= 0)
                                {
                                    var rest = first.Substring(idx + 8).Trim();
                                    var space = rest.IndexOf(' ');
                                    return space > 0 ? rest.Substring(0, space) : rest;
                                }
                            }
                        }

                        try
                        {
                            var fvi = FileVersionInfo.GetVersionInfo(exePath);
                            if (!string.IsNullOrEmpty(fvi.FileVersion)) return fvi.FileVersion;
                        }
                        catch { }
                    }
                    return UnknownVersion;
                }

                default:
                    return UnknownVersion;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine installed version for core: {CoreId}", coreId);
            return UnknownVersion;
        }
    }

    public async Task<CoreUpdateInfo> CheckForUpdateAsync(string coreId, CancellationToken ct = default)
    {
        var installed = GetInstalledVersion(coreId);

        if (!coreId.Equals("aether", StringComparison.OrdinalIgnoreCase))
        {

            return new CoreUpdateInfo(
                CoreId: coreId,
                DisplayName: GetCoreDisplayName(coreId),
                InstalledVersion: installed,
                LatestVersion: UnknownVersion,
                HasUpdate: false,
                DownloadUrl: "",
                ReleaseNotes: "This engine ships with the app; Se7en does not check a remote "
                              + "release feed for it, so no update status is available.",
                DownloadSizeBytes: 0
            );
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
            using var res = await _httpClient.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
            var releaseNotes = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";

            var latestVersion = tagName.TrimStart('v', 'V').Trim();
            var downloadUrl = "";
            long sizeBytes = 0;

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "";
                    if (name.Equals(AetherAssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElem) ? urlElem.GetString() ?? "" : "";
                        sizeBytes = asset.TryGetProperty("size", out var sizeElem) ? sizeElem.GetInt64() : 0;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(tagName))
            {
                downloadUrl = $"{ReleaseDownloadPrefix}{Uri.EscapeDataString(tagName)}/{AetherAssetName}";
            }

            if (!string.IsNullOrEmpty(downloadUrl) && !IsTrustedDownloadUrl(downloadUrl))
            {
                _logger.LogWarning(
                    "Rejecting Aether asset URL outside the expected release path: {Url}", downloadUrl);
                downloadUrl = "";
            }

            var hasUpdate = IsNewerVersion(installed, latestVersion);

            return new CoreUpdateInfo(
                CoreId: coreId,
                DisplayName: "Aether (WARP / MASQUE)",
                InstalledVersion: installed,
                LatestVersion: latestVersion,
                HasUpdate: hasUpdate,
                DownloadUrl: downloadUrl,
                ReleaseNotes: releaseNotes,
                DownloadSizeBytes: sizeBytes
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check for updates for core {CoreId}", coreId);
            throw;
        }
    }

    public async Task<bool> UpdateCoreAsync(string coreId, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!coreId.Equals("aether", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Updating core '{coreId}' is not supported yet.");
        }

        var updateInfo = await CheckForUpdateAsync(coreId, ct);
        if (string.IsNullOrEmpty(updateInfo.DownloadUrl))
        {
            throw new InvalidOperationException("Could not find download URL for Aether update.");
        }

        if (!IsTrustedDownloadUrl(updateInfo.DownloadUrl))
        {
            throw new InvalidOperationException(
                $"Refusing to download the core from an untrusted URL: {updateInfo.DownloadUrl}");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "Se7enPro_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, AetherAssetName);
        var extractedExe = Path.Combine(tempDir, "aether.exe");

        try
        {

            progress?.Report(5);
            await DownloadArchiveAsync(updateInfo, zipPath, progress, ct);

            progress?.Report(72);

            ExtractAetherExe(zipPath, extractedExe);

            progress?.Report(78);

            var trust = BinaryTrust.VerifyAuthenticode(extractedExe, RequiredSignerSubject);
            if (!trust.Trusted)
            {
                if (RequireSignedCoreBinaries)
                {
                    throw new InvalidOperationException(
                        $"The downloaded Aether binary failed signature verification and was NOT installed: "
                        + $"{trust.Detail}");
                }
                _logger.LogWarning(
                    "SECURITY: installing an unverified Aether binary because signature enforcement is "
                    + "disabled in this build: {Detail}", trust.Detail);
            }
            else
            {
                _logger.LogInformation("Downloaded Aether binary verified: {Detail}", trust.Detail);
            }

            progress?.Report(84);

            KillOwnAetherProcesses();

            progress?.Report(90);

            var localAppCachedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "aether", EngineProcessNames.Aether);
            AtomicFile.Install(extractedExe, localAppCachedPath);

            var appResourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe");
            try
            {
                AtomicFile.Install(extractedExe, appResourcePath);
            }
            catch (Exception ex)
            {

                _logger.LogInformation(ex,
                    "Could not refresh the bundled copy at {Path}; the cached core was updated", appResourcePath);
            }

            progress?.Report(96);

            var verifiedVer = QueryExeVersion(localAppCachedPath, "--version");
            if (string.IsNullOrWhiteSpace(verifiedVer))
            {
                throw new InvalidOperationException(
                    "The core was installed but did not respond to --version; the previous binary has been "
                    + "left in place as .bak next to it.");
            }

            progress?.Report(100);
            _logger.LogInformation("Aether core successfully installed/verified: {Version}", verifiedVer);
            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }

    private async Task DownloadArchiveAsync(
        CoreUpdateInfo updateInfo, string zipPath, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(
            updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
        if (!string.IsNullOrEmpty(finalUrl) && !IsTrustedDownloadUrl(finalUrl))
        {

            if (!finalUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The core download was redirected to a non-HTTPS location: {finalUrl}");
            }
        }

        var declared = response.Content.Headers.ContentLength
                       ?? (updateInfo.DownloadSizeBytes > 0 ? updateInfo.DownloadSizeBytes : 0);
        if (declared > MaxArchiveBytes)
        {
            throw new InvalidOperationException(
                $"The core archive declares {declared:N0} bytes, above the {MaxArchiveBytes:N0} byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(
            zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            downloadedBytes += bytesRead;
            if (downloadedBytes > MaxArchiveBytes)
            {
                throw new InvalidOperationException(
                    $"The core archive exceeded the {MaxArchiveBytes:N0} byte limit mid-download.");
            }
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);

            if (declared > 0)
            {
                var pct = (int)(5 + (downloadedBytes * 65 / declared));
                progress?.Report(Math.Min(70, pct));
            }
        }
    }

    private static void ExtractAetherExe(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("aether.exe") ??
                    archive.Entries.FirstOrDefault(
                        e => e.Name.Equals("aether.exe", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            throw new FileNotFoundException("aether.exe was not found inside the downloaded archive.");
        }
        if (entry.Length > MaxExtractedBytes)
        {
            throw new InvalidOperationException(
                $"aether.exe inside the archive declares {entry.Length:N0} bytes, above the "
                + $"{MaxExtractedBytes:N0} byte limit.");
        }

        using var source = entry.Open();
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > MaxExtractedBytes)
            {
                throw new InvalidOperationException(
                    $"aether.exe expanded past the {MaxExtractedBytes:N0} byte limit.");
            }
            target.Write(buffer, 0, read);
        }
    }

    private void KillOwnAetherProcesses()
    {
        var ourImages = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "aether", EngineProcessNames.Aether),
            Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe"),
        };

        foreach (var name in new[] { Path.GetFileNameWithoutExtension(EngineProcessNames.Aether), "aether" })
        {
            Process[] found;
            try { found = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in found)
            {
                try
                {
                    var image = WintunRouteApi.TryGetProcessPath(p.Id);
                    if (image is null ||
                        !ourImages.Any(our => string.Equals(image, our, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    _logger.LogInformation("Stopping our Aether core (pid {Pid}) before replacing it", p.Id);
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not stop Aether pid {Pid}", p.Id);
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

    private static string FindAetherExecutable()
    {
        var localCached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Se7en", "aether", EngineProcessNames.Aether);
        if (File.Exists(localCached)) return localCached;

        var appResource = Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe");
        if (File.Exists(appResource)) return appResource;

        return "";
    }

    private static string FindTorExecutable()
    {
        var localCached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Se7en", "tor", EngineProcessNames.Tor);
        if (File.Exists(localCached)) return localCached;

        var appResource = Path.Combine(AppContext.BaseDirectory, "Resources", "tor", "tor.exe");
        if (File.Exists(appResource)) return appResource;

        return "";
    }

    private static string QueryExeVersion(string exePath, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return output.Trim();
        }
        catch
        {
            return "";
        }
    }

    private static bool IsNewerVersion(string current, string latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;

        if (Version.TryParse(CleanVersion(current), out var cVer) &&
            Version.TryParse(CleanVersion(latest), out var lVer))
        {
            return lVer > cVer;
        }

        return !string.Equals(current.Trim(), latest.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanVersion(string v)
    {
        var s = v.Trim().TrimStart('v', 'V');
        var dash = s.IndexOf('-');
        if (dash > 0) s = s.Substring(0, dash);
        return s;
    }

    private static string GetCoreDisplayName(string coreId) => coreId.ToLowerInvariant() switch
    {
        "aether" => "Aether (WARP / MASQUE)",
        "singbox" => "Sing-Box (Universal Engine)",
        "tor" => "Tor (Onion Routing)",
        "xray" => "Xray (V2Ray Platform)",
        _ => coreId
    };
}
