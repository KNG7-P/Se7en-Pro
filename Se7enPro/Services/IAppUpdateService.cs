using System;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed record AppReleaseInfo(
    string Version,
    string ReleasePageUrl,
    string AssetName,
    string AssetUrl,
    long SizeBytes,
        bool IsPortableArchive,
        string ChecksumManifestUrl);

public interface IAppUpdateService
{
    string InstalledVersion { get; }
    Task<AppReleaseInfo> CheckAsync(CancellationToken ct = default);
    Task<string> DownloadAsync(AppReleaseInfo release, IProgress<double>? progress, CancellationToken ct = default);
        Task<bool> InstallAsync(string downloadedPath, string targetVersion);
}
