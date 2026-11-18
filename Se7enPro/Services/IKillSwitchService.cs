using System;

namespace Se7enPro.Services;

public interface IKillSwitchService
{
    bool IsActive { get; }

    string? LastError { get; }

    void Arm();
    void Disarm();
    void Reconcile();
}
