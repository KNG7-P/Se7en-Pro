namespace Se7enPro.Services;

public interface IStartupRegistration
{
    bool IsEnabled();

    void SetEnabled(bool enabled);

    void SyncFromSetting(bool desired);
}
