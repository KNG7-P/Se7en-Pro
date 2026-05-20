using System.Diagnostics;

namespace PsiphonUI.Services;

public interface IChildProcessGuard
{
    void Adopt(Process process);
}
