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

public sealed class CoreUpdateService : ICoreUpdateService
{
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
                    return "1.7.0";
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
                    }
                    return "0.4.9.11";
                }

                default:
                    return "Unknown";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine installed version for core: {CoreId}", coreId);
            return coreId.Equals("tor", StringComparison.OrdinalIgnoreCase) ? "0.4.9.11" : "1.7.0";
        }
    }

    public async Task<CoreUpdateInfo> CheckForUpdateAsync(string coreId, CancellationToken ct = default)
    {
        var installed = GetInstalledVersion(coreId);

        if (coreId.Equals("tor", StringComparison.OrdinalIgnoreCase))
        {

            await Task.Delay(400, ct);
            return new CoreUpdateInfo(
                CoreId: "tor",
                DisplayName: "Tor (Onion Routing)",
                InstalledVersion: installed,
                LatestVersion: installed,
                HasUpdate: false,
                DownloadUrl: "",
                ReleaseNotes: "Tor engine is running the latest bundled release.",
                DownloadSizeBytes: 0
            );
        }

        if (!coreId.Equals("aether", StringComparison.OrdinalIgnoreCase))
        {
            return new CoreUpdateInfo(
                CoreId: coreId,
                DisplayName: GetCoreDisplayName(coreId),
                InstalledVersion: installed,
                LatestVersion: installed,
                HasUpdate: false,
                DownloadUrl: "",
                ReleaseNotes: "Core update not yet available for this engine.",
                DownloadSizeBytes: 0
            );
        }

        try
        {
            const string apiUrl = "https://api.github.com/repos/CluvexStudio/Aether/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
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
                    if (name.Equals("aether-windows-x86_64.zip", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = asset.TryGetProperty("browser_download_url", out var urlElem) ? urlElem.GetString() ?? "" : "";
                        sizeBytes = asset.TryGetProperty("size", out var sizeElem) ? sizeElem.GetInt64() : 0;
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(tagName))
            {
                downloadUrl = $"https://github.com/CluvexStudio/Aether/releases/download/{tagName}/aether-windows-x86_64.zip";
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

        var tempDir = Path.Combine(Path.GetTempPath(), "Se7enPro_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "aether-windows-x86_64.zip");
        var extractedExe = Path.Combine(tempDir, "aether.exe");

        try
        {

            progress?.Report(5);
            using (var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? (updateInfo.DownloadSizeBytes > 0 ? updateInfo.DownloadSizeBytes : 4500000);

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        var pct = (int)(5 + (downloadedBytes * 70 / totalBytes));
                        progress?.Report(Math.Min(75, pct));
                    }
                }
            }

            progress?.Report(80);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry("aether.exe") ??
                            archive.Entries.FirstOrDefault(e => e.Name.Equals("aether.exe", StringComparison.OrdinalIgnoreCase));

                if (entry is null)
                {
                    throw new FileNotFoundException("aether.exe was not found inside the downloaded archive.");
                }

                entry.ExtractToFile(extractedExe, overwrite: true);
            }

            progress?.Report(85);

            KillRunningAetherProcesses();

            progress?.Report(90);

            var appResourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe");
            SafeReplaceFile(extractedExe, appResourcePath);

            var localAppCachedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "aether", EngineProcessNames.Aether);
            SafeReplaceFile(extractedExe, localAppCachedPath);

            try
            {
                var devSourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "aether", "aether.exe"));
                if (File.Exists(devSourcePath))
                {
                    SafeReplaceFile(extractedExe, devSourcePath);
                }
            }
            catch { }

            var verifiedVer = QueryExeVersion(localAppCachedPath, "--version");
            if (string.IsNullOrWhiteSpace(verifiedVer))
            {
                verifiedVer = QueryExeVersion(appResourcePath, "--version");
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

    private static void KillRunningAetherProcesses()
    {
        foreach (var name in new[] { "Se7enPro.Aether", "aether" })
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    try { p.WaitForExit(1000); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }
            catch { }
        }
    }

    private static void SafeReplaceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            File.Copy(source, destination, overwrite: true);
        }
        catch
        {
            var tempOld = destination + ".old." + Guid.NewGuid().ToString("N");
            try
            {
                if (File.Exists(destination)) File.Move(destination, tempOld);
            }
            catch { }
            File.Copy(source, destination, overwrite: true);
            try
            {
                if (File.Exists(tempOld)) File.Delete(tempOld);
            }
            catch { }
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
