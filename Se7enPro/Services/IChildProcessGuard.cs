using System.Diagnostics;

namespace Se7enPro.Services;

public interface IChildProcessGuard
{
    void Adopt(Process process);
}
