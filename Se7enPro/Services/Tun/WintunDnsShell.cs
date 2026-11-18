using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

[SupportedOSPlatform("windows")]
internal static class WintunDnsShell
{
    private const string NrptRuleGuid = "{6F1F6C2A-5B9E-4C1B-9A7E-2D5E3B8C4A10}";
    private const string NrptKeyPath = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig\" + NrptRuleGuid;
    private const string DnsClientPolicyKey = @"Software\Policies\Microsoft\Windows NT\DNSClient";
    private const string DnsCacheParametersKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";

    public static void EnableNrptPolicy(string dnsServers)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(NrptKeyPath, writable: true);
            if (key != null)
            {
                key.SetValue("Version", 2, Microsoft.Win32.RegistryValueKind.DWord);
                key.SetValue("Name", new[] { "." }, Microsoft.Win32.RegistryValueKind.MultiString);
                key.SetValue("GenericDNSServers", dnsServers, Microsoft.Win32.RegistryValueKind.String);
                key.SetValue("ConfigOptions", 8, Microsoft.Win32.RegistryValueKind.DWord);
            }

            try
            {
                using var k1 = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(DnsClientPolicyKey, writable: true);
                k1?.SetValue("DisableSmartNameResolution", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
            try
            {
                using var k2 = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(DnsCacheParametersKey, writable: true);
                k2?.SetValue("DisableParallelAandAAAA", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }

            WintunRouteApi.FlushDnsCache();
            _ = Task.Run(() =>
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"),
                        Arguments = "/registerdns",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    p?.WaitForExit(5000);
                }
                catch { }
            });
        }
        catch { }
    }

    public static void DisableNrptPolicy()
    {
        try
        {
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(NrptKeyPath, throwOnMissingSubKey: false);
            try
            {
                using var k1 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DnsClientPolicyKey, writable: true);
                k1?.DeleteValue("DisableSmartNameResolution", throwOnMissingValue: false);
            }
            catch { }
            try
            {
                using var k2 = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(DnsCacheParametersKey, writable: true);
                k2?.DeleteValue("DisableParallelAandAAAA", throwOnMissingValue: false);
            }
            catch { }

            WintunRouteApi.FlushDnsCache();
        }
        catch { }
    }

    public static async Task SetAdapterDnsAsync(string adapterName, string serverIp)
    {
        await Task.WhenAll(
            RunAsync("ipv4", "set", adapterName, serverIp),
            RunAsync("ipv6", "delete", adapterName, null));
    }

    public static async Task ClearAdapterDnsAsync(string adapterName)
    {
        await Task.WhenAll(
            RunAsync("ipv4", "delete", adapterName, null),
            RunAsync("ipv6", "delete", adapterName, null));
    }

    public static async Task SetAdapterMtuAsync(string adapterName, int mtu)
    {
        var args4 = new List<string> { "interface", "ipv4", "set", "subinterface", adapterName, $"mtu={mtu}", "store=active" };
        var args6 = new List<string> { "interface", "ipv6", "set", "subinterface", adapterName, $"mtu={mtu}", "store=active" };
        await Task.WhenAll(
            RunNetshAsync(args4, "netsh ipv4 set subinterface mtu"),
            RunNetshAsync(args6, "netsh ipv6 set subinterface mtu"));
    }

    public static Task EnableAdapterAsync(string adapterName) =>
        RunNetshAsync(
            new List<string> { "interface", "set", "interface", $"name={adapterName}", "admin=enabled" },
            "netsh interface set admin=enabled");

    public static async Task SetAdapterMetricAsync(string adapterName, int metric)
    {
        await Task.WhenAll(
            RunMetricAsync("ipv4", adapterName, metric),
            RunMetricAsync("ipv6", adapterName, metric));
    }

    public static async Task ResetAdapterMetricAsync(string adapterName)
    {
        await Task.WhenAll(
            RunMetricAsync("ipv4", adapterName, 0),
            RunMetricAsync("ipv6", adapterName, 0));
    }

    private static async Task RunAsync(string family, string verb, string adapterName, string? serverIp)
    {
        var args = new List<string> { "interface", family, verb, "dnsservers", $"name={adapterName}" };
        if (verb == "set")
        {
            args.Add("source=static");
            args.Add($"address={serverIp}");
            args.Add("register=primary");
            args.Add("validate=no");
        }
        else
        {
            args.Add("all");
        }

        await RunNetshAsync(args, $"netsh {family} {verb} dnsservers");
    }

    private static async Task RunMetricAsync(string family, string adapterName, int metric)
    {
        var args = new List<string>
        {
            "interface", family, "set", "interface", adapterName, $"metric={metric}",
        };
        await RunNetshAsync(args, $"netsh {family} set interface metric");
    }

    private static async Task RunNetshAsync(List<string> args, string description)
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

        
        
        psi.FileName = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

        using var p = Process.Start(psi);
        if (p is null) throw new InvalidOperationException("Could not start netsh.");

        
        
        
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{description} timed out");
        }

        
        
        
        
        
        
        
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{description} failed (exit {p.ExitCode}): " +
                $"{stderr.GetAwaiter().GetResult().Trim()} {stdout.GetAwaiter().GetResult().Trim()}".Trim());
        }
    }
}
