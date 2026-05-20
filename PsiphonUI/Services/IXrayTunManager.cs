using System;
using System.Threading.Tasks;

namespace PsiphonUI.Services;

public interface IXrayTunManager : IAsyncDisposable
{
    XrayTunState State { get; }

    string? LastError { get; }

    event EventHandler? StateChanged;
}

public enum XrayTunState
{
    Off,
    Starting,
    Running,
    Stopping,
    Error,
}
