using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Se7enPro.Services;

namespace TunEngineTest;

internal static partial class Program
{
    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "[OK]" : "[FAIL]")} {what}");
        if (!ok) Interlocked.Increment(ref _failures);
    }

    private static async Task<bool> WaitStateAsync(Func<bool> cond, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(100);
        }
        return cond();
    }

    private static async Task WaitAdapterDownAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && WintunRouteApi.IsAdapterUp(TunName))
        {
            await Task.Delay(200);
        }
    }

    private static async Task<string> RoutePrintAsync(string args)
    {
        var psi = new ProcessStartInfo("route.exe", $"print {args}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;
        return await p.StandardOutput.ReadToEndAsync();
    }

    private static bool NrptRuleExists()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient\DnsPolicyConfig");
        return key?.GetSubKeyNames()
            .Any(k => k.Contains("8f4c9d2e", StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static void DumpLogTail()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Psiphon", "logs", "tun2socks.log");
            Console.WriteLine($"--- tun2socks.log tail ({path}) ---");
            foreach (var line in File.ReadLines(path).TakeLast(30))
            {
                Console.WriteLine("    " + line);
            }
        }
        catch { }
    }
}
