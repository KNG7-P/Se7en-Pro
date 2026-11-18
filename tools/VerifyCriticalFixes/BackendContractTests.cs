using System;
using System.IO;

namespace VerifyCriticalFixes;

internal static class BackendContractTests
{
    internal static void Run()
    {
        Program.Section("Backend regressions — kill switch, proxy backup and route teardown");

        var wfp = File.ReadAllText(FindSource("WfpKillSwitch.cs", "Se7enPro\\Services"));
        Check(ContainsInOrder(wfp, "if (_armed) return true;", "LastError = null;", "OpenEngine();"),
              "a new kill-switch attempt clears the previous attempt's error");

        var proxy = File.ReadAllText(FindSource("SystemProxyService.cs", "Se7enPro\\Services"));
        var writeBackup = Extract(proxy, "private static void WriteBackup", 400);
        Check(!writeBackup.Contains("catch", StringComparison.Ordinal),
              "proxy backup failure propagates instead of being hidden");
        var set = Extract(proxy, "public void Set(int httpProxyPort)", 2600);
        Check(ContainsInOrder(set, "WriteBackup(backup);", "key.SetValue(\"ProxyEnable\""),
              "the original proxy is persisted before Se7en changes the registry");
        var clear = Extract(proxy, "public void Clear()", 5200);
        Check(clear.Contains("current.ProxyServer.StartsWith(\"127.0.0.1:\"", StringComparison.Ordinal)
              && clear.Contains("current.ProxyOverride, StandardBypassList", StringComparison.Ordinal)
              && clear.IndexOf("Removed a leftover Se7en system proxy", StringComparison.Ordinal)
                 < clear.IndexOf("WriteBackup(current);", StringComparison.Ordinal),
              "Clear never saves Se7en's own managed proxy as the original configuration");

        var manager = File.ReadAllText(FindSource("WintunTunManager.cs", "Se7enPro\\Services\\Tun"));
        var routes = File.ReadAllText(FindSource("WintunTunManager.Routes.cs", "Se7enPro\\Services\\Tun"));
        var split = File.ReadAllText(FindSource("WintunTunManager.ProcessSplit.cs", "Se7enPro\\Services\\Tun"));
        var lifecycle = File.ReadAllText(FindSource("WintunTunManager.Lifecycle.cs", "Se7enPro\\Services\\Tun"));
        Check(manager.Contains("private long _routeGeneration;", StringComparison.Ordinal),
              "route teardown has an explicit generation");
        var trackNew = Extract(routes, "WintunRouteApi.RouteEntry TrackNew", 700);
        Check(ContainsInOrder(trackNew, "lock (_routeLock)", "routeGeneration != _routeGeneration", "var entry = addRoute();"),
              "resolved DNS routes are generation-checked and added under the teardown lock");
        var pinBypass = Extract(split, "void PinBypass(IPAddress ip, string source)", 1500);
        Check(ContainsInOrder(pinBypass, "lock (_routeLock)", "routeGeneration != _routeGeneration", "WintunRouteApi.AddRoute"),
              "engine endpoint DNS callbacks cannot add routes after teardown");
        var kill = Extract(lifecycle, "private async Task KillTunAsync", 2700);
        Check(ContainsInOrder(kill, "lock (_routeLock)", "_routeGeneration++;", "_appliedRoutes.Clear();"),
              "teardown invalidates pending DNS callbacks before clearing tracked routes");

        Program.Section("Backend regressions — connection state and process generations");
        var connection = File.ReadAllText(FindSource("ConnectionManager.cs", "Se7enPro\\Services"));
        var drop = Extract(connection, "private async Task HandleConnectionDropAsync", 7200);
        var dropFinally = ExtractLast(drop, "finally", 400);
        Check(drop.Contains("_stateOverride = ConnectionState.Error;", StringComparison.Ordinal)
              && !dropFinally.Contains("_stateOverride = null;", StringComparison.Ordinal),
              "terminal reconnect errors survive the drop handler's finally block");
        var identity = Extract(connection, "private async Task TryRecoverIdentityAfterErrorAsync", 3600);
        Check(identity.Contains("CreateLinkedTokenSource(attemptToken.Value)", StringComparison.Ordinal)
              && identity.Contains("_lifecycleGate.WaitAsync(cts.Token)", StringComparison.Ordinal),
              "identity recovery is linked to the current attempt and serialized by the lifecycle gate");
        Check(ContainsInOrder(identity, "!ReferenceEquals(_active, engine)", "await SafeStopAsync(engine);",
                  "!ReferenceEquals(_active, engine)", "await engine.StartAsync();"),
              "identity recovery revalidates the captured engine before stopping and restarting it");

        var localEngine = File.ReadAllText(FindSource("LocalSocksEngineBase.cs", "Se7enPro\\Services"));
        var readiness = Extract(localEngine, "private void StartReadinessProbe", 3900);
        Check(readiness.Contains("Process proc, int generation", StringComparison.Ordinal)
              && readiness.Contains("ref _readinessProbeGeneration, generation, runningGeneration", StringComparison.Ordinal),
              "readiness probes have generation-owned coordination instead of a shared boolean");
        Check(ContainsInOrder(readiness, "await _lifecycleGate.WaitAsync(ct);",
                  "generation != Volatile.Read(ref _processGeneration)", "proc.Kill(entireProcessTree: true)"),
              "a timed-out probe kills only its captured still-current process");
        Check(readiness.Contains("Interlocked.CompareExchange(ref _readinessProbeGeneration, 0, generation)", StringComparison.Ordinal)
              && ContainsInOrder(readiness, "SetState(ConnectionState.Connected);", "OnTunnelConnected(socksPort"),
              "stale readiness completion cannot clear a newer probe or publish connected state");

        Program.Section("Backend regressions — verified update snapshots and portable rollback");
        var coreUpdate = File.ReadAllText(FindSource("CoreUpdateService.cs", "Se7enPro\\Services"));
        foreach (var method in new[] { "FindAetherExecutable", "FindTorExecutable" })
        {
            var body = Extract(coreUpdate, $"private static string {method}", 900);
            Check(ContainsInOrder(body, "if (EngineTrust.RunFromBundle)", "return File.Exists(appResource) ? appResource : \"\";")
                  && body.IndexOf("if (File.Exists(localCached))", StringComparison.Ordinal)
                     > body.IndexOf("if (EngineTrust.RunFromBundle)", StringComparison.Ordinal),
                  $"{method} never executes a per-user cached binary for an elevated trusted install");
        }
        Check(coreUpdate.Contains("manifestText, \"aether-windows-x86_64.zip\", zipBytes)", StringComparison.Ordinal),
              "the manifest verifies the immutable downloaded byte snapshot");
        Check(coreUpdate.Contains("SafeReplaceFile(executableBytes, destination)", StringComparison.Ordinal)
              && coreUpdate.Contains("SafeReplaceFile(pair.Value, pair.Key)", StringComparison.Ordinal),
              "core installation and rollback use exact byte snapshots rather than staging paths");
        Check(!Extract(coreUpdate, "var backups = new Dictionary<string, byte[]>()", 400)
                  .Contains("catch", StringComparison.Ordinal),
              "an unreadable core backup aborts before any target is replaced");

        var appUpdate = File.ReadAllText(FindSource("AppUpdateService.cs", "Se7enPro\\Services"));
        var portable = Extract(appUpdate, "$backups = @()", 4200);
        Check(portable.Contains("$addedFiles = @()", StringComparison.Ordinal)
              && portable.Contains("Copy-Item -LiteralPath $target -Destination $backup", StringComparison.Ordinal),
              "portable update records replaced and newly added files before applying payload entries");
        Check(ContainsInOrder(portable, "if ($failed -eq 0)", "Restart-App", "} else {",
                  "Copy-Item -LiteralPath $item.Backup -Destination $item.Target",
                  "Remove-Item -LiteralPath $target", "not relaunching"),
              "a partial portable update restores old files, removes additions and stays closed");

        Program.Section("Backend regressions — process ownership, installers and secret logging");
        var lantern = File.ReadAllText(FindSource("LanternEngine.cs", "Se7enPro\\Services\\Lantern"));
        var beforeStart = Extract(lantern, "protected override Task BeforeStartAsync", 1900);
        Check(ContainsInOrder(beforeStart, "p.MainModule?.FileName", "StartupReaper.ShouldReap", "p.Kill"),
              "Lantern only kills daemons whose image path belongs to Se7en");

        foreach (var arch in new[] { "x64", "x86" })
        {
            var installer = File.ReadAllText(FindRepoFile($"installer\\installer_{arch}.iss"));
            Check(!installer.Contains("/IM aether.exe", StringComparison.OrdinalIgnoreCase)
                  && !installer.Contains("/IM tor.exe", StringComparison.OrdinalIgnoreCase),
                  $"{arch} setup does not kill generic Tor or Aether processes");
            Check(installer.Contains("Parameters: \"--cleanup\"", StringComparison.Ordinal)
                  && installer.Contains("Name: \"{localappdata}\\Se7en\"", StringComparison.Ordinal),
                  $"{arch} uninstall restores settings and removes Se7en per-user data");
        }

        var aether = File.ReadAllText(FindSource("AetherEngine.cs", "Se7enPro\\Services"));
        Check(aether.Contains("AetherExtras.RedactUpstreamUrl(upstreamArgs[1])", StringComparison.Ordinal),
              "Aether logs only the redacted upstream URL");
    }

    private static void Check(bool ok, string what) => Program.Check(ok, what);

    private static bool ContainsInOrder(string text, params string[] parts)
    {
        var index = 0;
        foreach (var part in parts)
        {
            index = text.IndexOf(part, index, StringComparison.Ordinal);
            if (index < 0) return false;
            index += part.Length;
        }
        return true;
    }

    private static string Extract(string text, string anchor, int maxChars)
    {
        var index = text.IndexOf(anchor, StringComparison.Ordinal);
        return index < 0 ? "" : text.Substring(index, Math.Min(maxChars, text.Length - index));
    }

    private static string ExtractLast(string text, string anchor, int maxChars)
    {
        var index = text.LastIndexOf(anchor, StringComparison.Ordinal);
        return index < 0 ? "" : text.Substring(index, Math.Min(maxChars, text.Length - index));
    }

    private static string FindSource(string fileName, string relativeFolder) =>
        FindRepoFile(Path.Combine(relativeFolder, fileName));

    private static string FindRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate)) return candidate;
            var fileName = Path.GetFileName(relativePath);
            var parentDir = Path.GetDirectoryName(candidate);
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                var found = Directory.GetFiles(parentDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        throw new FileNotFoundException($"could not locate {relativePath}");
    }
}
