using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

[SupportedOSPlatform("windows")]
internal static class WintunFirewallShell
{

    internal const string RuleName = "Se7enPro TUN QUIC fail-fast";

    internal const string RuleNameV6 = "Se7enPro TUN QUIC fail-fast v6";

    public static async Task InstallQuicBlockAsync(
        IPAddress tunAddress, IPAddress? tunAddressV6, CancellationToken ct)
    {

        await RemoveQuicBlockAsync(ct);

        await RunAsync(ct, "add",
            "advfirewall", "firewall", "add", "rule",
            $"name={RuleName}",
            "dir=out",
            "action=block",
            "protocol=UDP",
            "remoteport=1-52,54-65535",
            $"localip={tunAddress}",
            "profile=any",
            "enable=yes");

        if (tunAddressV6 is null) return;
        await RunAsync(ct, "add",
            "advfirewall", "firewall", "add", "rule",
            $"name={RuleNameV6}",
            "dir=out",
            "action=block",
            "protocol=UDP",
            "remoteport=1-52,54-65535",
            $"localip={tunAddressV6}",
            "profile=any",
            "enable=yes");
    }

    public static async Task RemoveQuicBlockAsync(CancellationToken ct)
    {
        await RunAsync(ct, "delete", "advfirewall", "firewall", "delete", "rule", $"name={RuleName}");
        await RunAsync(ct, "delete", "advfirewall", "firewall", "delete", "rule", $"name={RuleNameV6}");
    }

    internal const string DnsLeakRuleNameUdp = "Se7enPro DNS Leak Block UDP";
    internal const string DnsLeakRuleNameTcp = "Se7enPro DNS Leak Block TCP";

    public static async Task InstallDnsLeakBlockAsync(CancellationToken ct)
    {
        await RemoveDnsLeakBlockAsync(ct);

        await RunAsync(ct, "add",
            "advfirewall", "firewall", "add", "rule",
            $"name={DnsLeakRuleNameUdp}",
            "dir=out",
            "action=block",
            "protocol=UDP",
            "remoteport=53",
            "interfacetype=lan,wireless",
            "profile=any",
            "enable=yes");

        await RunAsync(ct, "add",
            "advfirewall", "firewall", "add", "rule",
            $"name={DnsLeakRuleNameTcp}",
            "dir=out",
            "action=block",
            "protocol=TCP",
            "remoteport=53",
            "interfacetype=lan,wireless",
            "profile=any",
            "enable=yes");
    }

    public static async Task RemoveDnsLeakBlockAsync(CancellationToken ct)
    {
        await RunAsync(ct, "delete", "advfirewall", "firewall", "delete", "rule", $"name={DnsLeakRuleNameUdp}");
        await RunAsync(ct, "delete", "advfirewall", "firewall", "delete", "rule", $"name={DnsLeakRuleNameTcp}");
    }

    public static Task<bool> QuicBlockExistsAsync(CancellationToken ct) =>
        RuleExistsAsync(RuleName, ct);

    public static Task<bool> QuicBlockV6ExistsAsync(CancellationToken ct) =>
        RuleExistsAsync(RuleNameV6, ct);

    private static async Task<bool> RuleExistsAsync(string name, CancellationToken ct)
    {
        var (exitCode, output) = await RunCoreAsync(ct,
            new[] { "advfirewall", "firewall", "show", "rule", $"name={name}" });
        if (exitCode == 0) return true;

        if (output.IndexOf("No rules match", StringComparison.OrdinalIgnoreCase) >= 0
            || output.Trim().Length == 0
            || output.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        throw new InvalidOperationException(
            $"netsh advfirewall firewall show rule name={name} exited {exitCode}: {output.Trim()}");
    }

    private static async Task RunAsync(CancellationToken ct, string verb, params string[] args)
    {
        var (exitCode, output) = await RunCoreAsync(ct, args);

        if (exitCode != 0 && verb != "delete")
        {
            throw new InvalidOperationException(
                $"netsh {string.Join(' ', args)} exited {exitCode}: {output.Trim()}");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCoreAsync(CancellationToken ct, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p is null) throw new InvalidOperationException("Failed to start netsh.exe");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"netsh {string.Join(' ', args)} timed out");
        }

        return (p.ExitCode, await stdout + await stderr);
    }
}
