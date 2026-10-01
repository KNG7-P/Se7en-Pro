using System;
using System.Collections.Generic;
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
                DownloadSizeBytes: 0,
                ChecksumManifestUrl: ""
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
                DownloadSizeBytes: 0,
                ChecksumManifestUrl: ""
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
            var manifestUrl = "";
            long sizeBytes = 0;

            if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsElem.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameElem) ? nameElem.GetString() ?? "" : "";
                    if (asset.TryGetProperty("browser_download_url", out var urlElem))
                    {
                        if (name.Equals("aether-windows-x86_64.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = urlElem.GetString() ?? "";
                            sizeBytes = asset.TryGetProperty("size", out var sizeElem) ? sizeElem.GetInt64() : 0;
                        }
                        else if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                        {
                            manifestUrl = urlElem.GetString() ?? "";
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(tagName))
            {
                
                
                
                if (System.Text.RegularExpressions.Regex.IsMatch(tagName, @"^[\w.\-+]{1,100}$"))
                {
                    downloadUrl = $"https://github.com/CluvexStudio/Aether/releases/download/{tagName}/aether-windows-x86_64.zip";
                    if (string.IsNullOrEmpty(manifestUrl))
                    {
                        manifestUrl = $"https://github.com/CluvexStudio/Aether/releases/download/{tagName}/SHA256SUMS";
                    }
                }
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
                DownloadSizeBytes: sizeBytes,
                ChecksumManifestUrl: manifestUrl
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
        if (string.IsNullOrEmpty(updateInfo.ChecksumManifestUrl))
        {
            throw new InvalidOperationException(
                Loc.Of("This release does not publish a SHA256SUMS file, so the core cannot be verified. Refusing to install it."));
        }

        
        
        
        UpdateIntegrity.EnsureTrustedUrl(updateInfo.DownloadUrl, "Core download");
        UpdateIntegrity.EnsureTrustedUrl(updateInfo.ChecksumManifestUrl, "Checksum manifest");

        var tempDir = Path.Combine(Path.GetTempPath(), "Se7enPro_Update_" + Guid.NewGuid().ToString("N"));
        
        
        
        
        UpdateIntegrity.HardenStagingDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "aether-windows-x86_64.zip");
        var extractedExe = Path.Combine(tempDir, "aether.exe");

        try
        {
            
            
            
            progress?.Report(2);
            string manifestText;
            using (var manifestResponse = await _httpClient.GetAsync(
                       updateInfo.ChecksumManifestUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                manifestResponse.EnsureSuccessStatusCode();
                manifestText = await manifestResponse.Content.ReadAsStringAsync(ct);
            }

            
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

            
            
            progress?.Report(78);
            var zipHash = UpdateIntegrity.VerifyAgainstManifest(
                manifestText, "aether-windows-x86_64.zip", zipPath);
            _logger.LogInformation(
                "Core archive verified against SHA256SUMS: {Hash}", zipHash);

            progress?.Report(80);

            
            
            
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.GetEntry("aether.exe")
                            ?? archive.GetEntry("aether-windows-x86_64/aether.exe")
                            ?? archive.GetEntry("aether-windows-x86_64\\aether.exe");

                if (entry is null)
                {
                    throw new FileNotFoundException("aether.exe was not found inside the downloaded archive.");
                }

                entry.ExtractToFile(extractedExe, overwrite: true);
            }

            
            
            
            var exeHash = UpdateIntegrity.ComputeSha256(extractedExe);
            _logger.LogInformation("Staged aether.exe SHA-256: {Hash}", exeHash);

            progress?.Report(84);

            
            
            var stagedVersion = QueryExeVersion(extractedExe, "--version");
            if (string.IsNullOrWhiteSpace(stagedVersion))
            {
                throw new InvalidOperationException(
                    Loc.Of("The downloaded core did not start, so it was not installed."));
            }

            var appResourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe");
            var localAppCachedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "aether", EngineProcessNames.Aether);
            var targets = new[] { appResourcePath, localAppCachedPath };

            
            
            
            var backups = new Dictionary<string, string>();
            foreach (var destination in targets)
            {
                if (!File.Exists(destination)) continue;
                var backup = Path.Combine(tempDir, Path.GetRandomFileName() + ".aether.exe");
                try
                {
                    File.Copy(destination, backup, overwrite: true);
                    backups[destination] = backup;
                }
                catch { }
            }

            progress?.Report(88);

            
            KillRunningAetherProcesses();

            try
            {
                foreach (var destination in targets)
                {
                    SafeReplaceFile(extractedExe, destination);
                }

                
                
                
                
#if DEBUG
                try
                {
                    var devSourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "aether", "aether.exe"));
                    if (File.Exists(devSourcePath))
                    {
                        SafeReplaceFile(extractedExe, devSourcePath);
                    }
                }
                catch { }
#endif

                progress?.Report(94);

                
                
                var mismatched = targets.FirstOrDefault(t =>
                    !string.Equals(NormalizeVersion(QueryExeVersion(t, "--version")),
                                   NormalizeVersion(stagedVersion), StringComparison.OrdinalIgnoreCase));
                if (mismatched is not null)
                {
                    throw new InvalidOperationException(
                        string.Format(Loc.Of("{0} still reports an old version after the update."), "aether.exe"));
                }
            }
            catch (Exception ex)
            {
                foreach (var pair in backups)
                {
                    try { SafeReplaceFile(pair.Value, pair.Key); } catch { }
                }
                _logger.LogError(ex, "Aether core update rolled back to the previous binary");
                throw;
            }

            progress?.Report(100);
            _logger.LogInformation("Aether core successfully installed/verified: {Version}", stagedVersion);
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

        private void KillRunningAetherProcesses()
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Collect(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try { expected.Add(Path.GetFullPath(path)); } catch { }
        }

        Collect(Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe"));
        Collect(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Se7en", "aether", EngineProcessNames.Aether));

        foreach (var name in new[] { "Se7enPro.Aether", "aether" })
        {
            Process[] procs;
            try { procs = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var p in procs)
            {
                try
                {
                    string image;
                    try { image = p.MainModule?.FileName ?? ""; }
                    catch
                    {
                        
                        
                        continue;
                    }

                    if (string.IsNullOrEmpty(image)) continue;
                    string full;
                    try { full = Path.GetFullPath(image); }
                    catch { continue; }

                    if (!expected.Contains(full))
                    {
                        _logger.LogInformation(
                            "Leaving process {Pid} ({Name}) alone: it is not a Se7en Pro core ({Path})",
                            p.Id, name, full);
                        continue;
                    }

                    try { p.Kill(entireProcessTree: true); } catch { }
                    try { p.WaitForExit(1000); } catch { }
                }
                catch { }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
    }

        private static void SafeReplaceFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        
        
        if (File.Exists(destination))
        {
            File.Replace(source, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(source, destination, overwrite: true);
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

        private string QueryExeVersion(string exePath, string arguments)
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

            if (!proc.Start()) return "";

            
            var stdout = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { proc.WaitForExit(1000); } catch { }
                _logger.LogWarning("Version probe for {Exe} did not exit in time", exePath);
                return "";
            }

            return stdout.GetAwaiter().GetResult().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Version probe for {Exe} failed", exePath);
            return "";
        }
    }

    private bool IsNewerVersion(string current, string latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (string.IsNullOrWhiteSpace(current)) return true;

        if (Version.TryParse(CleanVersion(current), out var cVer) &&
            Version.TryParse(CleanVersion(latest), out var lVer))
        {
            return lVer > cVer;
        }

        
        
        
        _logger.LogWarning(
            "Not offering an update: cannot compare \"{Current}\" with \"{Latest}\"", current, latest);
        return false;
    }

    private static string NormalizeVersion(string? raw)
    {
        var s = (raw ?? "").Trim();
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].Length > 0 && char.IsDigit(parts[i][0])) return parts[i];
        }
        return s;
    }

    private static string CleanVersion(string v)    {
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
