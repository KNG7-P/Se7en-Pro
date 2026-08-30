using System;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

public sealed record CoreUpdateInfo(
    string CoreId,
    string DisplayName,
    string InstalledVersion,
    string LatestVersion,
    bool HasUpdate,
    string DownloadUrl,
    string ReleaseNotes,
    long DownloadSizeBytes
);

public interface ICoreUpdateService
{
    string GetInstalledVersion(string coreId);
    Task<CoreUpdateInfo> CheckForUpdateAsync(string coreId, CancellationToken ct = default);
    Task<bool> UpdateCoreAsync(string coreId, IProgress<int>? progress = null, CancellationToken ct = default);
}
