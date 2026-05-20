namespace PsiphonUI.Services;

public interface ISystemProxyService
{
    void Set(int httpProxyPort);

    void Clear();

    void RestoreIfCrashed();
}
