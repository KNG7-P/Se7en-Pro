using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Se7enPro.Models;
using Se7enPro.Services;
using Se7enPro.ViewModels;

namespace VerifyCriticalFixes;

internal static class AutoSaveTests
{

    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(1400);

    internal static async Task<int> RunAsync()
    {
        Program.Section("DNS — row state machine");
        var failures = TestRowState();

        Program.Section("DNS — auto-save");
        failures += await TestAutoSaveAsync();

        Program.Section("DNS — strict-mode warning");
        failures += TestTunnelWarning();

        return failures;
    }

    private static int TestRowState()
    {
        var f = 0;
        var changes = 0;
        var row = NewRow(DnsTransport.Udp, () => changes++);

        Check(row.State == DnsFieldState.Empty, "a new row starts Empty");
        Check(!row.HasEntries && row.CountText.Length == 0, "an empty row shows no count");
        Check(row.Problem.Length == 0, "an empty row has no problem text");

        row.Text = "9.9.9.9";
        Check(row.State == DnsFieldState.Ok, "a valid entry puts the row in Ok", row.State.ToString());
        Check(row.Entries.Count == 1, "the entry is parsed");
        Check(row.CountText == "1", "the count badge shows one");
        Check(!row.HasProblem, "a valid entry raises no problem");

        row.Text = "9.9.9.9, 149.112.112.112";
        Check(row.Entries.Count == 2, "two entries are parsed", row.Entries.Count.ToString());
        Check(row.CountText == "2", "the count badge shows two", row.CountText);

        row.Text = "tls://dns.google";
        Check(row.HasProblem, "a tls:// entry under plain UDP is flagged");
        Check(row.State == DnsFieldState.Problem, "the row is in Problem", row.State.ToString());
        Check(row.Problem.Contains("tls://"), "the problem text names the offending entry", row.Problem);
        Check(row.Problem.Contains("DoT"), "the problem text says which list it belongs in", row.Problem);

        row.Text = "9.9.9.9, nonsense!!, 1.0.0.1";
        Check(row.Entries.Count == 2, "valid neighbours survive a bad entry", row.Entries.Count.ToString());
        Check(row.HasProblem, "the bad entry is still reported");

        row.Text = string.Join(",", Enumerable.Range(1, DnsSettings.MaxEntriesPerList + 4).Select(i => $"9.9.9.{i % 250 + 1}"));
        Check(row.HasProblem, "more entries than the cap is flagged, not silently cut");
        Check(row.Entries.Count <= DnsSettings.MaxEntriesPerList,
              "never more entries than the cap", row.Entries.Count.ToString());

        row.Text = "";
        Check(row.State == DnsFieldState.Empty, "clearing the row returns it to Empty", row.State.ToString());
        Check(row.Entries.Count == 0, "clearing the row drops the parsed entries");

        Check(changes > 0, "every edit notified the owner so it can schedule a save",
              changes.ToString());

        return f;
    }

    private static async Task<int> TestAutoSaveAsync()
    {
        var f = 0;
        var svc = new FakeSettingsService();
        var vm = new DnsSettingsViewModel(svc);

        vm.UdpRow.Text = "9.9.9.9";
        vm.UdpRow.Text = "9.9.9.9,";
        vm.UdpRow.Text = "9.9.9.9, 1";
        vm.UdpRow.Text = "9.9.9.9, 149.112.112.112";

        await Task.Delay(150);
        Check(svc.SaveCount == 0,
              "no write happens while typing is still in progress", svc.SaveCount.ToString());

        await Task.Delay(Wait);
        Check(svc.SaveCount == 1,
              "a burst of keystrokes results in exactly one write", svc.SaveCount.ToString());
        Check(svc.Settings.CustomDnsUdp == "9.9.9.9, 149.112.112.112",
              "the value written is the settled one, not an intermediate one",
              svc.Settings.CustomDnsUdp);
        Check(vm.IsSavedFlash, "the confirmation appears after an automatic save");

        await Task.Delay(TimeSpan.FromSeconds(3.2));
        Check(!vm.IsSavedFlash, "the confirmation fades without anything being clicked");

        var before = svc.SaveCount;
        vm.UdpRow.Text = "9.9.9.9, 149.112.112.112  ";
        await Task.Delay(Wait);
        Check(svc.SaveCount == before,
              "an edit that changes nothing does not write", svc.SaveCount.ToString());

        vm.UdpRow.Text = "";
        await Task.Delay(Wait);
        Check(svc.Settings.CustomDnsUdp == "",
              "deleting the text clears the stored value", svc.Settings.CustomDnsUdp);
        Check(svc.SaveCount == before + 1, "the deletion was written", svc.SaveCount.ToString());

        before = svc.SaveCount;
        vm.DotRow.Text = "tls://dns.google";
        await Task.Delay(Wait);
        Check(svc.Settings.CustomDnsDot == "tls://dns.google",
              "the DoT list persists on its own", svc.Settings.CustomDnsDot);

        vm.DohRow.Text = "https://cloudflare-dns.com/dns-query";
        await Task.Delay(Wait);
        Check(svc.Settings.CustomDnsDoh == "https://cloudflare-dns.com/dns-query",
              "the DoH list persists on its own", svc.Settings.CustomDnsDoh);

        before = svc.SaveCount;
        vm.StrictMode = true;
        await Task.Delay(60);
        Check(svc.SaveCount == before + 1,
              "the strict toggle saves at once rather than after the debounce",
              svc.SaveCount.ToString());
        Check(svc.Settings.CustomDnsStrict, "strict is persisted");

        before = svc.SaveCount;
        vm.UdpRow.Text = "not a resolver";
        await Task.Delay(Wait);
        Check(svc.SaveCount == before,
              "a malformed entry is not written, so the last good resolver list survives",
              svc.SaveCount.ToString());
        Check(vm.UdpRow.Text == "not a resolver",
              "the typed text stays in the box so the user can fix it");
        Check(svc.Settings.CustomDnsUdp == "",
              "the stored list is untouched", svc.Settings.CustomDnsUdp);
        Check(vm.UdpRow.HasProblem, "and it is flagged as unusable");
        Check(vm.UdpRow.Entries.Count == 0,
              "a malformed entry contributes no resolver to the plan");

        before = svc.SaveCount;
        vm.ResetCommand.Execute(null);
        await Task.Delay(60);
        Check(svc.Settings.CustomDnsUdp == "" && svc.Settings.CustomDnsDot == ""
              && svc.Settings.CustomDnsDoh == "" && !svc.Settings.CustomDnsStrict,
              "Reset clears all three lists and strict mode");
        Check(svc.SaveCount == before + 1, "Reset is a single write", svc.SaveCount.ToString());

        before = svc.SaveCount;
        svc.Settings.CustomDnsUdp = "9.9.9.9";
        svc.Settings.CustomDnsDoh = "doh:dns.quad9.net";
        vm.Load();
        await Task.Delay(Wait);
        Check(vm.UdpRow.Text == "9.9.9.9", "Load repopulates the UDP row", vm.UdpRow.Text);
        Check(svc.SaveCount == before,
              "Load does not write, so reading settings cannot trigger a save loop",
              svc.SaveCount.ToString());

        Check(vm.UdpRow.State == DnsFieldState.Ok, "the loaded UDP row validates");
        Check(vm.DohRow.State == DnsFieldState.Ok, "the loaded DoH row validates");
        Check(vm.DohRow.Entries.Count == 1, "the loaded DoH row parsed its entry");
        Check(vm.DotRow.State == DnsFieldState.Empty, "the untouched DoT row stays Empty");
        Check(vm.Summary.Contains("UDP", StringComparison.Ordinal)
              && vm.Summary.Contains("DoH", StringComparison.Ordinal),
              "the summary counts both transports", vm.Summary);

        return f;
    }

    private static int TestTunnelWarning()
    {
        var f = 0;
        var svc = new FakeSettingsService();
        var vm = new DnsSettingsViewModel(svc);

        Check(!vm.HasTunnelWarning, "no warning by default");

        vm.StrictMode = true;
        Check(!vm.HasTunnelWarning, "strict alone with no entries raises no warning");

        vm.DohRow.Text = "https://dns.quad9.net/dns-query";
        Check(vm.HasTunnelWarning,
              "strict with only an encrypted resolver warns: TUN mode has nothing to dial");

        vm.UdpRow.Text = "9.9.9.9";
        Check(!vm.HasTunnelWarning,
              "adding a plain-UDP entry clears the warning");

        vm.UdpRow.Text = "";
        Check(vm.HasTunnelWarning, "removing it brings the warning back");

        vm.StrictMode = false;
        Check(!vm.HasTunnelWarning, "turning strict off clears the warning");

        return f;
    }

    private static DnsFieldRow NewRow(DnsTransport transport, Action onChanged) =>
        new(transport, () => "label", () => "hint", () => "placeholder", _ => onChanged());

    private static void Check(bool ok, string what, string? detail = null) => Program.Check(ok, what, detail);

    private sealed class FakeSettingsService : ISettingsService
    {
        public UserSettings Settings { get; } = new();

        public int SaveCount { get; private set; }

        public event EventHandler? SettingsChanged;

        public void Load() { }

        public bool Save()
        {
            SaveCount++;

            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public bool SaveQuietly()
        {
            SaveCount++;
            return true;
        }

        public void ResetToDefaults() => Save();
    }
}