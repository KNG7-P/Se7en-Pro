using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

[SupportedOSPlatform("windows")]
internal static class WintunDnsShell
{

    public static async Task SetAdapterDnsAsync(string adapterName, string serverIp, string? serverIpV6)
    {
        await RunAsync("ipv4", "set", adapterName, serverIp);

        if (serverIpV6 is not null)
        {
            try { await RunAsync("ipv6", "set", adapterName, serverIpV6); }
            catch {  }
        }
        else
        {

            try { await RunAsync("ipv6", "delete", adapterName, null); } catch { }
        }
    }

    public static async Task ClearAdapterDnsAsync(string adapterName)
    {
        try { await RunAsync("ipv4", "delete", adapterName, null); } catch { }
        try { await RunAsync("ipv6", "delete", adapterName, null); } catch { }
    }

    public static void SetSmartNameResolution(bool disable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
                @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient");
            if (disable)
            {
                key?.SetValue("DisableSmartNameResolution", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            else
            {
                key?.DeleteValue("DisableSmartNameResolution", throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public static async Task SetInterfaceMetricAsync(string adapterName, int metric)
    {
        await SetInterfaceOptionAsync("ipv4", adapterName, $"metric={metric}");
        await SetInterfaceOptionAsync("ipv6", adapterName, $"metric={metric}");
    }

    public static async Task DisableDuplicateAddressDetectionAsync(string adapterName)
    {
        await SetInterfaceOptionAsync("ipv4", adapterName, "dadtransmits=0");
        await SetInterfaceOptionAsync("ipv6", adapterName, "dadtransmits=0");
    }

    private static async Task SetInterfaceOptionAsync(string family, string adapterName, string option)
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
        psi.ArgumentList.Add("set");
        psi.ArgumentList.Add("interface");
        psi.ArgumentList.Add(adapterName);
        psi.ArgumentList.Add(option);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } }
        }
        catch { }
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
