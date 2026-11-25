using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

public sealed class AppUpdateService : IAppUpdateService
{
    private const string RepoApi = "https://api.github.com/repos/KNG7-P/Se7en-Pro/releases/latest";
    private const string RepoPage = "https://github.com/KNG7-P/Se7en-Pro/releases/latest";

    private static readonly TimeSpan ApiBudget = TimeSpan.FromSeconds(20);

    private readonly ILogger<AppUpdateService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private string _verifiedAssetPath = "";
    private string _verifiedAssetHash = "";

    public AppUpdateService(ILogger<AppUpdateService> logger)
    {
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Se7enPro-Updater/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public string InstalledVersion { get; } = ReadInstalledVersion();

    private static string ReadInstalledVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version
                ?? typeof(AppUpdateService).Assembly.GetName().Version;
        if (v is null) return "0.0.0";
        return v.Build < 0 ? $"{v.Major}.{v.Minor}" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public async Task<AppReleaseInfo> CheckAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, RepoApi);
        using var res = await _http.SendAsync(req, ct);

        if (res.StatusCode == System.Net.HttpStatusCode.Forbidden ||
            res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            throw new AppUpdateException(Loc.Of("GitHub API rate limit exceeded. Please try again later."));
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new AppUpdateException(Loc.Of("No releases found on GitHub."));
        if (!res.IsSuccessStatusCode)
            throw new AppUpdateException(string.Format(Loc.Of("GitHub API returned {0}"), (int)res.StatusCode));

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = ReadString(root, "tag_name");
        var version = tag.TrimStart('v', 'V').Trim();
        if (version.Length == 0) throw new AppUpdateException(Loc.Of("No releases found on GitHub."));

        var page = ReadString(root, "html_url");
        if (page.Length == 0) page = RepoPage;

        var asset = PickAsset(root);
        if (asset is null)
            throw new AppUpdateException(Loc.Of("This release has no downloadable build for Windows."));

        var manifestUrl = FindChecksumManifest(root);

        var (name, url, size, portable) = asset.Value;
        return new AppReleaseInfo(version, page, name, url, size, portable, manifestUrl);
    }

        private static string FindChecksumManifest(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var a in assets.EnumerateArray())
        {
            var name = ReadString(a, "name");
            if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
            {
                return ReadString(a, "browser_download_url");
            }
        }

        return "";
    }

    private static (string Name, string Url, long Size, bool Portable)? PickAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var isInstalled = IsInstalledInstallation();
        var isSelfContained = IsSelfContainedRuntime();
        var is64Bit = Environment.Is64BitProcess;

        var candidates = new System.Collections.Generic.List<(string Name, string Url, long Size, bool Portable, int Score)>();

        foreach (var a in assets.EnumerateArray())
        {
            var name = ReadString(a, "name");
            var url = ReadString(a, "browser_download_url");
            if (name.Length == 0 || url.Length == 0) continue;

            var lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".exe") && !lower.EndsWith(".zip")) continue;

            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            var isPortable = lower.EndsWith(".zip");

            var hasX86 = lower.Contains("x86") && !lower.Contains("x64");
            var hasX64 = lower.Contains("x64");
            int archPenalty;
            if (is64Bit)
            {
                if (hasX64) archPenalty = 0;
                else if (hasX86) archPenalty = 100;
                else archPenalty = 10;
            }
            else
            {
                if (hasX86) archPenalty = 0;
                else if (hasX64) archPenalty = 100;
                else archPenalty = 10;
            }

            var formatPenalty = (isInstalled == !isPortable) ? 0 : 20;

            var assetWithoutDotnet = lower.Contains("without_dotnet");
            var runtimePenalty = (isSelfContained != assetWithoutDotnet) ? 0 : 10;

            var totalScore = archPenalty + formatPenalty + runtimePenalty;
            candidates.Add((name, url, size, isPortable, totalScore));
        }

        if (candidates.Count == 0) return null;

        var best = candidates.OrderBy(c => c.Score).First();
        return (best.Name, best.Url, best.Size, best.Portable);
    }

    private static bool IsInstalledInstallation()
    {
        return File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe")) ||
               File.Exists(Path.Combine(AppContext.BaseDirectory, "unins001.exe")) ||
               !RootIsWritable();
    }

    private static bool IsSelfContainedRuntime()
    {
        return File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll")) ||
               File.Exists(Path.Combine(AppContext.BaseDirectory, "clrjit.dll"));
    }

    private static string ReadString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static bool? _rootWritable;

        private static bool RootIsWritable()
    {
        if (_rootWritable.HasValue) return _rootWritable.Value;

        var ok = false;
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".se7en-write-probe-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            ok = true;
        }
        catch
        {
            ok = false;
        }
        _rootWritable = ok;
        return ok;
    }

    public async Task<string> DownloadAsync(AppReleaseInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(release.ChecksumManifestUrl))
        {
            throw new AppUpdateException(Loc.Of(
                "This release does not publish a SHA256SUMS file, so the update cannot be verified. Refusing to install it."));
        }

        
        
        try
        {
            UpdateIntegrity.EnsureTrustedUrl(release.AssetUrl, Loc.Of("Update download"));
            UpdateIntegrity.EnsureTrustedUrl(release.ChecksumManifestUrl, Loc.Of("Checksum manifest"));
        }
        catch (Exception ex)
        {
            throw new AppUpdateException(ex.Message);
        }

        var baseDir = Path.Combine(BuildIdentity.TempRoot, "update");
        string dir;
        try
        {
            if (!UpdateIntegrity.HardenStagingDirectory(baseDir))
                _logger.LogWarning("Could not harden the update staging directory ACL: {Dir}", baseDir);
            dir = baseDir;
            if (Directory.Exists(dir))
            {
                foreach (var stale in Directory.EnumerateFiles(dir))
                {
                    try { File.Delete(stale); } catch { }
                }
            }
        }
        catch
        {
            dir = Path.Combine(BuildIdentity.TempRoot, "updates", Guid.NewGuid().ToString("N"));
            try
            {
                if (!UpdateIntegrity.HardenStagingDirectory(dir))
                    _logger.LogWarning("Could not harden the fallback update directory ACL: {Dir}", dir);
            }
            catch { Directory.CreateDirectory(dir); }
        }

        
        
        
        var assetLeaf = Path.GetFileName(release.AssetName);
        if (string.IsNullOrWhiteSpace(assetLeaf) ||
            assetLeaf is "." or ".." ||
            assetLeaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new AppUpdateException(Loc.Of("The release asset name is not usable."));
        }

        var target = Path.Combine(dir, assetLeaf);

        
        
        string manifestText;
        using (var manifestReq = new HttpRequestMessage(HttpMethod.Get, release.ChecksumManifestUrl))
        using (var manifestRes = await _http.SendAsync(manifestReq, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!manifestRes.IsSuccessStatusCode)
                throw new AppUpdateException(Loc.Of("Could not download the checksum file for this release."));
            manifestText = await manifestRes.Content.ReadAsStringAsync(ct);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new AppUpdateException(string.Format(Loc.Of("The download failed with status {0}."), (int)res.StatusCode));

        var total = res.Content.Headers.ContentLength ?? release.SizeBytes;
        await using (var source = await res.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(Math.Min(99.0, done * 100.0 / total));
            }
        }

        
        
        
        if (res.Content.Headers.ContentLength is long declared && new FileInfo(target).Length != declared)
            throw new AppUpdateException(Loc.Of("The downloaded file is incomplete. Try again."));

        try
        {
            var hash = UpdateIntegrity.VerifyAgainstManifest(manifestText, assetLeaf, target);
            _verifiedAssetPath = Path.GetFullPath(target);
            _verifiedAssetHash = hash;
            _logger.LogInformation("Update asset {Asset} verified against SHA256SUMS: {Hash}", assetLeaf, hash);
        }
        catch (Exception ex)
        {
            try { File.Delete(target); } catch { }
            throw new AppUpdateException(ex.Message);
        }

        progress?.Report(100);
        _logger.LogInformation("Downloaded and verified {Asset} ({Bytes} bytes) for the in-app update", assetLeaf, total);
        return target;
    }

    public async Task<bool> InstallAsync(string downloadedPath, string targetVersion)
    {
        if (string.IsNullOrEmpty(_verifiedAssetHash) ||
            !string.Equals(Path.GetFullPath(downloadedPath), _verifiedAssetPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppUpdateException(Loc.Of("This file was not verified by the updater, so nothing was installed."));
        }

        FileStream locked;
        try
        {
            locked = new FileStream(downloadedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex)
        {
            throw new AppUpdateException(Loc.Of("The downloaded update is no longer available.") + " " + ex.Message);
        }

        using (locked)
        {
            var actual = Convert.ToHexString(SHA256.HashData(locked)).ToLowerInvariant();
            if (!string.Equals(actual, _verifiedAssetHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppUpdateException(Loc.Of(
                    "The downloaded update changed after it was verified, so nothing was installed."));
            }

            if (downloadedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(downloadedPath) { UseShellExecute = true });
                await Task.Delay(1500);
                return true;
            }

            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            var script = BuildApplyScript(Path.GetFullPath(downloadedPath), _verifiedAssetHash, targetVersion);
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

            var needsElevation = !IsDirectoryWritable(AppContext.BaseDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = needsElevation,
                CreateNoWindow = !needsElevation,
            };
            if (needsElevation)
            {
                psi.Verb = "runas";
            }

            try
            {
                if (Process.Start(psi) is null)
                {
                    throw new AppUpdateException(Loc.Of("The update helper did not start, so nothing was installed."));
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new AppUpdateException(Loc.Of(
                    "The update was cancelled at the elevation prompt. Nothing was installed."));
            }
            catch (Exception ex) when (ex is not AppUpdateException)
            {
                throw new AppUpdateException(
                    Loc.Of("The update helper could not start, so nothing was installed.") + " " + ex.Message);
            }

            return true;
        }
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".se7en-write-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildApplyScript(string zipPath, string expectedHash, string expectedVersion) => $$"""
        $AppPid = {{Environment.ProcessId}}
        $Root = '{{AppContext.BaseDirectory.TrimEnd('\\').Replace("'", "''")}}'
        $Zip = '{{zipPath.Replace("'", "''")}}'
        $Expected = '{{expectedHash}}'
        $ExpectedVersion = '{{expectedVersion.Replace("'", "''")}}'
        $log = Join-Path $env:TEMP 'Se7enPro-Update.log'
        function Add-Log([string]$m) { try { Add-Content -Path $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $m) } catch {} }
        Add-Log ('update start; root=' + $Root)

        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 400 }
        if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) { Add-Log 'app still running, aborting'; return }
        Start-Sleep -Milliseconds 1200

        function Restart-App { try { Start-Process -FilePath (Join-Path $Root 'Se7enPro.exe') -WorkingDirectory $Root } catch { Add-Log 'relaunch failed' } }

        # One read feeds both the hash check and every file written below, so the
        # bytes cannot change between verification and the copy (the archive lives
        # in a folder this user can write to).
        try { $bytes = [System.IO.File]::ReadAllBytes($Zip) }
        catch { Add-Log ('cannot read the archive: ' + $_.Exception.Message); Restart-App; return }

        $sha = [System.Security.Cryptography.SHA256]::Create()
        $actual = ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
        if ($actual -ne $Expected) { Add-Log 'archive hash mismatch; nothing installed'; Restart-App; return }

        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $ms = New-Object System.IO.MemoryStream(,$bytes)
        $arc = New-Object System.IO.Compression.ZipArchive($ms, [System.IO.Compression.ZipArchiveMode]::Read)

        # The payload sits at the archive root or inside a single top folder.
        $names = @($arc.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $prefix = ''
        if ($names -notcontains 'Se7enPro.exe') {
            $hit = @($names | Where-Object { $_ -match '^[^/]+/Se7enPro\.exe$' })
            if ($hit.Count -eq 0) { Add-Log 'the archive has no payload root; nothing installed'; $arc.Dispose(); Restart-App; return }
            $prefix = $hit[0].Substring(0, $hit[0].LastIndexOf('/') + 1)
        }

        function Read-EntryVersion([string]$entryName) {
            $tmp = Join-Path $env:TEMP ('se7en-probe-' + [guid]::NewGuid().ToString('N') + '.exe')
            try {
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($arc.GetEntry($entryName), $tmp, $true)
                return [string](Get-Item -LiteralPath $tmp).VersionInfo.FileVersion
            } catch { return '' }
            finally { Remove-Item -LiteralPath $tmp -Force }
        }

        $reported = Read-EntryVersion ($prefix + 'Se7enPro.exe')
        # FileVersion can carry a suffix ("10.0.17763.1 (WinBuild.160101.0800)"),
        # so keep only the leading dotted number.
        $normalized = ($reported -split '\s+')[0] -replace '\+.*$', ''
        $ok = $false
        try { $ok = ([version]$normalized) -ge ([version]$ExpectedVersion) } catch {}
        if (-not $ok) { Add-Log ('payload version ' + $reported + ' does not match ' + $ExpectedVersion + '; nothing installed'); $arc.Dispose(); Restart-App; return }

        # A newer bundled core must not be downgraded by the release payload.
        $keepCore = $false
        try {
            $coreOld = Join-Path $Root 'Resources\aether\aether.exe'
            if ($arc.GetEntry($prefix + 'Resources/aether/aether.exe') -and (Test-Path -LiteralPath $coreOld)) {
                $oldVer = (Get-Item -LiteralPath $coreOld).VersionInfo.ProductVersion
                $newVer = Read-EntryVersion ($prefix + 'Resources/aether/aether.exe')
                if (([version]$oldVer) -gt ([version]$newVer)) { $keepCore = $true; Add-Log ('keeping newer bundled Aether core ' + $oldVer + ' over ' + $newVer) }
            }
        } catch {}

        $failed = 0
        $backupDir = Join-Path $env:TEMP ('Se7enPro-Update-' + [guid]::NewGuid().ToString('N'))
        try { New-Item -ItemType Directory -Path $backupDir -Force -ErrorAction Stop | Out-Null }
        catch {
            Add-Log ('cannot create rollback backup directory: ' + $_.Exception.Message)
            $arc.Dispose(); $ms.Dispose(); return
        }
        $backups = @()
        $addedFiles = @()
        $prepared = @{}
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
        try {
        foreach ($entry in $arc.Entries) {
            $rel = $entry.FullName.Replace('\', '/')
            if ($rel.Length -eq 0 -or $rel.EndsWith('/')) { continue }
            if ($prefix.Length -gt 0) {
                # Refuse entries that do not sit under the payload root.
                if (-not $rel.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
                $rel = $rel.Substring($prefix.Length)
            }
            if ($keepCore -and $rel -eq 'Resources/aether/aether.exe') { continue }
            # The previous version trusted the relative path blindly, so a crafted
            # archive layout could aim a copy outside the program folder.
            $target = [System.IO.Path]::GetFullPath((Join-Path $Root ($rel.Replace('/', '\'))))
            if (-not $target.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                Add-Log ('refused out-of-root target ' + $target); $failed++; continue
            }
            $dir = Split-Path $target -Parent
            try { if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null } } catch {}
            if (-not $prepared.ContainsKey($target)) {
                if ((Test-Path -LiteralPath $target) -and -not (Test-Path -LiteralPath $target -PathType Leaf)) {
                    $failed++; Add-Log ('target is not a file ' + $target); continue
                }
                if (Test-Path -LiteralPath $target -PathType Leaf) {
                    $backup = Join-Path $backupDir ([guid]::NewGuid().ToString('N') + '.bak')
                    try {
                        Copy-Item -LiteralPath $target -Destination $backup -Force -ErrorAction Stop
                        $backups += [pscustomobject]@{ Target = $target; Backup = $backup }
                    } catch {
                        $failed++; Add-Log ('could not back up ' + $target); continue
                    }
                } else {
                    $addedFiles += $target
                }
                $prepared[$target] = $true
            }
            $written = $false
            for ($i = 0; $i -lt 6 -and -not $written; $i++) {
                try { [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true); $written = $true }
                catch { Start-Sleep -Milliseconds 700 }
            }
            if (-not $written) { $failed++; Add-Log ('could not write ' + $target) }
        }
        } catch { $failed++; Add-Log ('apply failed: ' + $_.Exception.Message) }
        $arc.Dispose()
        $ms.Dispose()

        Add-Log ('copy finished, failures=' + $failed)
        if ($failed -eq 0) {
            Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
            Restart-App
        } else {
            foreach ($item in $backups) {
                try { Copy-Item -LiteralPath $item.Backup -Destination $item.Target -Force -ErrorAction Stop }
                catch { Add-Log ('could not restore ' + $item.Target + ': ' + $_.Exception.Message) }
            }
            foreach ($target in $addedFiles) {
                try { Remove-Item -LiteralPath $target -Force -ErrorAction Stop }
                catch { if (Test-Path -LiteralPath $target) { Add-Log ('could not remove new file ' + $target + ': ' + $_.Exception.Message) } }
            }
            Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
            Add-Log 'update failed; rollback attempted; not relaunching'
        }
        """;

        public sealed class AppUpdateException : Exception
    {
        public AppUpdateException(string message) : base(message) { }
    }
}
