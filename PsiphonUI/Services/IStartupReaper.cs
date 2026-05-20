namespace PsiphonUI.Services;

public interface IStartupReaper
{
    void ReapStaleProcesses();
}
