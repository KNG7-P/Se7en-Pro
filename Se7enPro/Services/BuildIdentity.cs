using System;

namespace Se7enPro.Services;

// Single place that answers "which install am I?".
//
// The production build keeps every identifier it has always used. The SplitDebug
// configuration compiles with ISOLATED_DEBUG defined, which swaps the whole identity —
// data folder, temp folder, mutex, activation event, Run key and the three WFP
// sublayer GUIDs — so a debug copy can be installed and run beside the real one
// without touching its settings, its single-instance mutex, its autostart entry or
// its firewall filters.
public static class BuildIdentity
{
#if ISOLATED_DEBUG
    public const bool IsIsolatedDebug = true;

    public const string AppFolderName = "Se7enPro_SplitDebug";

    public const string TempFolderName = "Se7enPro_SplitDebug";

    public const string SingleInstanceMutexName = @"Local\Se7enPro_SplitDebug_SingleInstance";

    public const string ShowWindowEventName = @"Local\Se7enPro_SplitDebug_ShowWindowEvent";

    public const string RunValueName = "Se7enPro_SplitDebug";

    // Firewall sublayers. Distinct GUIDs mean the debug build's filters live in their
    // own sublayers and can never be added to or removed from the production ones.
    public const string KillSwitchSublayerGuid = "b4d2e8a1-7c53-4f19-a6d0-9e2f1b7c3a84";

    public const string DnsSublayerGuid = "c7a91f3e-2d64-4b8a-9f07-5a1c8e6d2b93";

    public const string SplitSublayerGuid = "d8b3a5c2-6e41-4f79-8c25-3b7a9e0f4d16";
#else
    public const bool IsIsolatedDebug = false;

    public const string AppFolderName = "Se7en";

    public const string TempFolderName = "Se7en";

    public const string SingleInstanceMutexName = @"Local\Se7enPro_SingleInstance";

    public const string ShowWindowEventName = @"Local\Se7enPro_ShowWindowEvent";

    public const string RunValueName = "Se7enPro";

    public const string KillSwitchSublayerGuid = "6f2c4b1a-9d38-4f6e-8a1b-3c5d7e9f0a2b";

    public const string DnsSublayerGuid = "7b1e4c2a-8d39-4a7f-9b2c-4d6e8f0a1b3c";

    public const string SplitSublayerGuid = "1f3b6a72-6a1e-4e8f-9a30-2c5d9b7e4a55";
#endif

    public static string LocalAppDataRoot =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

    public static string TempRoot =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), TempFolderName);
}
