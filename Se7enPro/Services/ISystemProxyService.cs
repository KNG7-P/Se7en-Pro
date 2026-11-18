namespace Se7enPro.Services;

public interface ISystemProxyService
{
    void Set(int httpProxyPort);

    void Set(int httpProxyPort, System.Collections.Generic.IEnumerable<string>? bypassEntries);

    void Clear();

    void RestoreIfCrashed();

        bool IsApplied { get; }
}
