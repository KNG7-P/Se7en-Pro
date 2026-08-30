using System;

namespace Se7enPro.Services;

public interface IKillSwitchService
{
    bool IsActive { get; }
    void Arm();
    void Disarm();
    void Reconcile();
}
