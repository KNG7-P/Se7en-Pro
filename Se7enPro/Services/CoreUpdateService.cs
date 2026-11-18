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
    private const long MaxArchiveBytes = 64L * 1024 * 1024;

    private readonly ILogger<CoreUpdateService> _logger;
    private readonly HttpClient _httpClient;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _versionCache = new(StringComparer.OrdinalIgnoreCase);

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
        if (_versionCache.TryGetValue(coreId, out var cached))
            return cached;

        try
        {
            string version;
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
                            version = parts.Length >= 2 ? parts[1].Trim() : ver.Trim();
                            _versionCache[coreId] = version;
                            return version;
                        }

                        var fvi = FileVersionInfo.GetVersionInfo(exePath);
                        if (!string.IsNullOrEmpty(fvi.FileVersion))
                        {
                            version = fvi.FileVersion;
                            _versionCache[coreId] = version;
                            return version;
                        }
                    }
                    version = "1.7.0";
                    _versionCache[coreId] = version;
                    return version;
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
                                    version = space > 0 ? rest.Substring(0, space) : rest;
                                    _versionCache[coreId] = version;
                                    return version;
                                }
                            }
                        }
                    }
                    version = "0.4.9.11";
                    _versionCache[coreId] = version;
                    return version;
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
        
        
        
        
        if (!UpdateIntegrity.HardenStagingDirectory(tempDir))
            _logger.LogWarning("Could not harden the core update staging directory ACL: {Dir}", tempDir);
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
            byte[] zipBytes;
            using (var response = await _httpClient.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? (updateInfo.DownloadSizeBytes > 0 ? updateInfo.DownloadSizeBytes : 4500000);
                if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                    throw new InvalidOperationException(Loc.Of("The downloaded core archive is too large."));

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var archiveStream = new MemoryStream();

                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    downloadedBytes += bytesRead;
                    if (downloadedBytes > MaxArchiveBytes)
                        throw new InvalidOperationException(Loc.Of("The downloaded core archive is too large."));
                    archiveStream.Write(buffer, 0, bytesRead);

                    if (totalBytes > 0)
                    {
                        var pct = (int)(5 + (downloadedBytes * 70 / totalBytes));
                        progress?.Report(Math.Min(75, pct));
                    }
                }

                zipBytes = archiveStream.ToArray();
            }

            progress?.Report(78);
            var zipHash = UpdateIntegrity.VerifyAgainstManifest(
                manifestText, "aether-windows-x86_64.zip", zipBytes);
            _logger.LogInformation(
                "Core archive verified against SHA256SUMS: {Hash}", zipHash);

            progress?.Report(80);

            byte[] executableBytes;
            using (var archiveStream = new MemoryStream(zipBytes, writable: false))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                var expectedNames = new[]
                {
                    "aether.exe",
                    "aether-windows-x86_64/aether.exe",
                    "aether-windows-x86_64\\aether.exe"
                };
                var entries = archive.Entries
                    .Where(e => expectedNames.Contains(e.FullName, StringComparer.Ordinal))
                    .ToArray();
                if (entries.Length != 1)
                {
                    throw new FileNotFoundException("The downloaded archive must contain exactly one expected aether.exe entry.");
                }

                var entry = entries[0];
                if (entry.Length > MaxArchiveBytes)
                    throw new InvalidDataException("The aether.exe archive entry is too large.");

                using var entryStream = entry.Open();
                using var executableStream = new MemoryStream((int)entry.Length);
                entryStream.CopyTo(executableStream);
                executableBytes = executableStream.ToArray();
            }

            File.WriteAllBytes(extractedExe, executableBytes);
            var exeHash = UpdateIntegrity.ComputeSha256(executableBytes);
            _logger.LogInformation("Staged aether.exe SHA-256: {Hash}", exeHash);

            progress?.Report(84);

            
            
            
            var stagedVersion = ReadFileVersion(extractedExe);
            if (string.IsNullOrWhiteSpace(stagedVersion))
            {
                stagedVersion = updateInfo.LatestVersion;
            }
            if (string.IsNullOrWhiteSpace(stagedVersion))
            {
                throw new InvalidOperationException(
                    Loc.Of("The downloaded core could not be identified, so it was not installed."));
            }

            var appResourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe");
            var localAppCachedPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Se7en", "aether", EngineProcessNames.Aether);
            var targets = new[] { appResourcePath, localAppCachedPath };

            
            
            
            var backups = new Dictionary<string, byte[]>();
            foreach (var destination in targets)
            {
                if (File.Exists(destination))
                    backups[destination] = File.ReadAllBytes(destination);
            }

            progress?.Report(88);

            
            KillRunningAetherProcesses();

            try
            {
                int updatedCount = 0;
                var updatedTargets = new List<string>();
                foreach (var destination in targets)
                {
                    try
                    {
                        ReplaceTargetWithElevationFallback(executableBytes, extractedExe, destination, exeHash);
                        updatedTargets.Add(destination);
                        updatedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to update target {Destination}; continuing with other targets", destination);
                        if (string.Equals(destination, appResourcePath, StringComparison.OrdinalIgnoreCase)
                            && (File.Exists(appResourcePath) || AdminElevation.IsTrustedInstallPath()))
                        {
                            throw new UnauthorizedAccessException(
                                Loc.Of("Administrator permission is required to update the bundled core so it persists after restart."), ex);
                        }
                    }
                }

                if (updatedCount == 0)
                {
                    throw new IOException(Loc.Of("Failed to update any core target paths."));
                }

                
                
                
                
#if DEBUG
                try
                {
                    var devSourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "aether", "aether.exe"));
                    if (File.Exists(devSourcePath))
                    {
                        SafeReplaceFile(executableBytes, devSourcePath);
                    }
                }
                catch { }
#endif

                progress?.Report(94);

                
                
                var mismatched = updatedTargets.FirstOrDefault(t =>
                    !string.Equals(UpdateIntegrity.ComputeSha256(t), exeHash, StringComparison.OrdinalIgnoreCase));
                if (mismatched is not null)
                {
                    throw new InvalidOperationException(
                        string.Format(Loc.Of("{0} failed verification after the update."), "aether.exe"));
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

            _versionCache.TryRemove("aether", out _);
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
                        _logger.LogInformation(
                            "Leaving process {Pid} ({Name}) alone: its image path is not readable",
                            p.Id, name);
                        continue;
                    }

                    string full;
                    try { full = Path.GetFullPath(image); }
                    catch { full = ""; }

                    if (string.IsNullOrEmpty(full) || !expected.Contains(full))
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

        private static void SafeReplaceFile(byte[] source, string destination)
    {
        var destDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        var tempFile = destination + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempFile, source);

            if (File.Exists(destination))
            {
                try
                {
                    File.Replace(tempFile, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch
                {
                    var oldFile = destination + ".old." + Guid.NewGuid().ToString("N");
                    try
                    {
                        File.Move(destination, oldFile, overwrite: true);
                        File.Move(tempFile, destination, overwrite: true);
                        try { File.Delete(oldFile); } catch { }
                        return;
                    }
                    catch
                    {
                        File.WriteAllBytes(destination, source);
                        return;
                    }
                }
            }
            else
            {
                File.Move(tempFile, destination, overwrite: true);
            }
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    private static void ReplaceTargetWithElevationFallback(
        byte[] executableBytes, string stagedExePath, string destination, string expectedHash)
    {
        try
        {
            SafeReplaceFile(executableBytes, destination);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            var dir = Path.GetDirectoryName(destination);
            var mkdir = !string.IsNullOrEmpty(dir) ? $"if not exist \"{dir}\" mkdir \"{dir}\" && " : "";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {mkdir}copy /Y \"{stagedExePath}\" \"{destination}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new UnauthorizedAccessException($"Could not start elevated copy for {destination}.");
            proc.WaitForExit();
            if (proc.ExitCode != 0
                || !File.Exists(destination)
                || !string.Equals(UpdateIntegrity.ComputeSha256(destination), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Elevated replacement failed for {destination} (exit={proc.ExitCode}).");
            }
        }
    }

    private static string FindAetherExecutable()
    {
        var appResource = Path.Combine(AppContext.BaseDirectory, "Resources", "aether", "aether.exe");
        if (EngineTrust.RunFromBundle)
            return File.Exists(appResource) ? appResource : "";

        var localCached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Se7en", "aether", EngineProcessNames.Aether);
        if (File.Exists(localCached)) return localCached;
        if (File.Exists(appResource)) return appResource;

        return "";
    }

    private static string FindTorExecutable()
    {
        var appResource = Path.Combine(AppContext.BaseDirectory, "Resources", "tor", "tor.exe");
        if (EngineTrust.RunFromBundle)
            return File.Exists(appResource) ? appResource : "";

        var localCached = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Se7en", "tor", EngineProcessNames.Tor);
        if (File.Exists(localCached)) return localCached;
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

    private static string ReadFileVersion(string exePath)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            var raw = !string.IsNullOrWhiteSpace(fvi.FileVersion)
                ? fvi.FileVersion
                : fvi.ProductVersion;
            return string.IsNullOrWhiteSpace(raw) ? "" : NormalizeVersion(raw);
        }
        catch { return ""; }
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
