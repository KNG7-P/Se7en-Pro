using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

[SupportedOSPlatform("windows")]
internal static class WintunDnsShell
{
    public static async Task SetAdapterDnsAsync(string adapterName, string serverIp)
    {
        await RunAsync("ipv4", "set", adapterName, serverIp);
        try { await RunAsync("ipv6", "set", adapterName, serverIp); }
        catch {  }
    }

    public static async Task ClearAdapterDnsAsync(string adapterName)
    {
        try { await RunAsync("ipv4", "delete", adapterName, null); } catch { }
        try { await RunAsync("ipv6", "delete", adapterName, null); } catch { }
    }

    private static async Task RunAsync(string family, string verb, string adapterName, string? serverIp)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("interface");
        psi.ArgumentList.Add(family);
        if (verb == "set")
        {
            psi.ArgumentList.Add("set");
            psi.ArgumentList.Add("dnsservers");
            psi.ArgumentList.Add($"name={adapterName}");
            psi.ArgumentList.Add("source=static");
            psi.ArgumentList.Add($"address={serverIp}");
            psi.ArgumentList.Add("register=primary");
            psi.ArgumentList.Add("validate=no");
        }
        else
        {
            psi.ArgumentList.Add("delete");
            psi.ArgumentList.Add("dnsservers");
            psi.ArgumentList.Add($"name={adapterName}");
            psi.ArgumentList.Add("all");
        }

        using var p = Process.Start(psi)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"netsh {family} {verb} dnsservers timed out");
        }
    }
}
