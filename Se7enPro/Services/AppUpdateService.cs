using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
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
    private const string AppExe = "Se7enPro.exe";

    private static readonly TimeSpan ApiBudget = TimeSpan.FromSeconds(20);

    private readonly ILogger<AppUpdateService> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };

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

        var wantedArchive = RootIsWritable();
        var fallback = new System.Collections.Generic.List<(string Name, string Url, long Size, bool Portable)>();

        foreach (var a in assets.EnumerateArray())
        {
            var name = ReadString(a, "name");
            var url = ReadString(a, "browser_download_url");
            if (name.Length == 0 || url.Length == 0) continue;

            var lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".exe") && !lower.EndsWith(".zip")) continue;

            var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            var portable = lower.EndsWith(".zip");
            fallback.Add((name, url, size, portable));

            var forX86 = lower.Contains("x86") && !lower.Contains("x64");
            if (forX86 == Environment.Is64BitProcess) continue;

            var kindMatches = portable == wantedArchive;
            
            
            var prefersNoRuntime = lower.Contains("with_dotnet") && !lower.Contains("without_dotnet");

            if (kindMatches && prefersNoRuntime) return (name, url, size, portable);
        }

        if (fallback.Count == 0) return null;

        
        
        var relaxed = fallback
            .OrderBy(x => RootIsWritable() == x.Portable ? 0 : 1)
            .ThenBy(x => x.Name.ToLowerInvariant().Contains("with_dotnet") ? 0 : 1)
            .ThenBy(x => x.Name.ToLowerInvariant().Contains("x86") ? 1 : 0)
            .First();
        return (relaxed.Name, relaxed.Url, relaxed.Size, relaxed.Portable);
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

        var dir = Path.Combine(Path.GetTempPath(), "Se7enPro", "update");
        
        
        
        UpdateIntegrity.HardenStagingDirectory(dir);
        foreach (var stale in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(stale); } catch { }
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

    public Task<bool> InstallAsync(string downloadedPath, string targetVersion)
    {
        if (downloadedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            
            
            Process.Start(new ProcessStartInfo(downloadedPath) { UseShellExecute = true });
            return Task.FromResult(true);
        }

        var stage = Path.Combine(Path.GetTempPath(), "Se7enPro", "stage", Guid.NewGuid().ToString("N"));
        UpdateIntegrity.HardenStagingDirectory(stage);
        ZipFile.ExtractToDirectory(downloadedPath, stage);

        
        
        var root = LocatePayloadRoot(stage)
                   ?? throw new AppUpdateException(Loc.Of("The downloaded archive does not contain Se7en Pro."));

        var incoming = Path.Combine(root, AppExe);
        var reported = FileVersionInfo.GetVersionInfo(incoming).FileVersion ?? "";

        
        
        
        if (string.IsNullOrWhiteSpace(reported) ||
            !Version.TryParse(NormalizeFileVersion(reported), out var reportedVer) ||
            !Version.TryParse(NormalizeFileVersion(targetVersion), out var targetVer))
        {
            throw new AppUpdateException(Loc.Of(
                "The downloaded build could not be identified, so it was not installed."));
        }

        if (reportedVer < targetVer)
        {
            throw new AppUpdateException(
                $"The downloaded build reports {reported} but {targetVersion} was expected. Refusing to install an older build.");
        }

        _logger.LogInformation("Staged Se7en Pro {Version} (reported {Reported}) from {Root}", targetVersion, reported, root);

        
        
        
        
        
        var workDir = Path.Combine(Path.GetTempPath(), "Se7enPro", "apply", Guid.NewGuid().ToString("N"));
        UpdateIntegrity.HardenStagingDirectory(workDir);
        var script = Path.Combine(workDir, "apply-update.ps1");
        File.WriteAllText(script, BuildApplyScript(root), new UTF8Encoding(false));

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        var psi = new ProcessStartInfo
        {
            FileName = powershell,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" " +
                        $"-AppPid {Environment.ProcessId} -Root \"{AppContext.BaseDirectory.TrimEnd('\\')}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Process.Start(psi);

        try { Directory.Delete(workDir, recursive: true); } catch { }
        return Task.FromResult(true);
    }

        private static string NormalizeFileVersion(string raw)
    {
        var v = raw.Trim();
        var plus = v.IndexOf('+');
        if (plus > 0) v = v[..plus];
        return v;
    }

    private static string? LocatePayloadRoot(string stage)
    {
        if (File.Exists(Path.Combine(stage, AppExe))) return stage;
        return Directory.EnumerateDirectories(stage)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, AppExe)));
    }

        private static string BuildApplyScript(string stage) =>
        """
        param([int]$AppPid, [string]$Root)
        $Stage = '@STAGE@'
        $log = Join-Path $env:TEMP 'Se7enPro-Update.log'
        function Add-Log([string]$m) { try { Add-Content -Path $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $m) } catch {} }
        Add-Log ('update start; root=' + $Root)

        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline -and (Get-Process -Id $AppPid -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 400 }
        if (Get-Process -Id $AppPid -ErrorAction SilentlyContinue) { Add-Log 'app still running, aborting'; return }
        Start-Sleep -Milliseconds 1200

        # A newer bundled core must not be downgraded by the release payload.
        $coreNew = Join-Path $Stage 'Resources\aether\aether.exe'
        $coreOld = Join-Path $Root 'Resources\aether\aether.exe'
        $keep = $null
        if ((Test-Path $coreNew) -and (Test-Path $coreOld)) {
            try {
                $a = (Get-Item $coreOld).VersionInfo.ProductVersion
                $b = (Get-Item $coreNew).VersionInfo.ProductVersion
                if ([version]$a -gt [version]$b) {
                    $keep = Join-Path $env:TEMP ('se7en-aether-' + [guid]::NewGuid().ToString('N'))
                    Copy-Item $coreOld $keep -Force
                    Add-Log ('keeping newer bundled Aether core ' + $a + ' over ' + $b)
                }
            } catch {}
        }

        $failed = 0
        $rootFull = [System.IO.Path]::GetFullPath($Root)
        foreach ($file in Get-ChildItem -Path $Stage -Recurse -File) {
            # Refuse anything that does not resolve to a path strictly inside the
            # stage directory. The previous version trusted
            # Substring($Stage.Length) blindly, so a reparse point or a crafted
            # archive layout could aim a copy outside the program folder.
            $rel = $file.FullName.Substring($Stage.Length).TrimStart('\')
            $target = [System.IO.Path]::GetFullPath((Join-Path $Root $rel))
            if (-not $target.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
                Add-Log ('refused out-of-root target ' + $target); $failed++; continue
            }
            $dir = Split-Path $target -Parent
            try { if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null } } catch {}
            $copied = $false
            for ($i = 0; $i -lt 6 -and -not $copied; $i++) {
                try { Copy-Item -LiteralPath $file.FullName -Destination $target -Force; $copied = $true }
                catch { Start-Sleep -Milliseconds 700 }
            }
            if (-not $copied) { $failed++; Add-Log ('could not write ' + $target) }
        }

        if ($keep) { try { Copy-Item $keep $coreOld -Force; Remove-Item $keep -Force } catch {} }

        Add-Log ('copy finished, failures=' + $failed)
        # A failed copy is not silently papered over by relaunching the old build
        # as if the update had worked.
        if ($failed -eq 0) {
            try { Start-Process -FilePath (Join-Path $Root 'Se7enPro.exe') -WorkingDirectory $Root } catch { Add-Log 'relaunch failed' }
        } else {
            Add-Log 'update incomplete; not relaunching'
        }
        try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force } catch {}
        """.Replace("@STAGE@", stage.Replace("'", "''"));

        public sealed class AppUpdateException : Exception
    {
        public AppUpdateException(string message) : base(message) { }
    }
}
