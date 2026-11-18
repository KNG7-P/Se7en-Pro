using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

internal static class RouteJournalTests
{
    private static readonly List<TunRouteJournal.Entry> Routes = new()
    {
        new("162.159.192.0", 18, "172.31.16.1", 0),
        new("188.114.96.0", 22, "172.31.16.1", 0),
        new("104.28.221.222", 32, "172.31.16.1", 0),
    };

    public static int Run()
    {
        Program.Section("Route journal — crash recovery for TUN bypass routes");

        var failures = 0;
        void Check(bool ok, string what, string? detail = null)
        {
            Program.Check(ok, what, detail);
            if (!ok) failures++;
        }

        var (ifIndex, alias) = RealAdapter();
        if (ifIndex <= 0)
        {
            Program.Check(true, "skipped — no usable adapter on this runner");
            return failures;
        }
        Check(true, $"found a real adapter to journal against ({alias}, ifIndex {ifIndex})");

        var dir = Path.Combine(Path.GetTempPath(), "se7en-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {

            var path = Path.Combine(dir, "tun-routes.json");
            TunRouteJournal.Save(ifIndex, Routes, path);
            Check(File.Exists(path), "Save writes the journal file");
            var json = File.ReadAllText(path);
            Check(json.Contains("162.159.192.0", StringComparison.Ordinal)
                  && json.Contains(alias, StringComparison.Ordinal),
                  "the journal records the destination and the adapter that carried it");
            Check(!File.Exists(path + ".tmp"), "no temporary file is left behind");

            var none = Path.Combine(dir, "none.json");
            TunRouteJournal.Save(0, Routes, none);
            Check(!File.Exists(none), "an unknown interface index writes nothing");

            var deleted = new List<TunRouteJournal.Entry>();
            var removed = TunRouteJournal.RecoverStale(
                _ => alias,
                (index, entry) =>
                {
                    if (index != ifIndex) return false;
                    deleted.Add(entry);
                    return true;
                },
                null,
                path);
            Check(removed == Routes.Count, $"recovery removes every journaled route ({removed}/{Routes.Count})");
            Check(deleted.Count == Routes.Count, "every entry was offered to the deleter");
            Check(!File.Exists(path), "the journal is deleted after a fully successful recovery");

            Check(TunRouteJournal.RecoverStale(_ => alias, (_, _) => true, null, Path.Combine(dir, "absent.json")) == 0,
                  "a missing journal is a no-op");

            var mismatch = Path.Combine(dir, "mismatch.json");
            TunRouteJournal.Save(ifIndex, Routes, mismatch);
            var touched = 0;
            var count = TunRouteJournal.RecoverStale(
                _ => "Some Other Adapter",
                (_, _) => { touched++; return true; },
                null,
                mismatch);
            Check(count == Routes.Count && touched == Routes.Count,
                  "a renumbered index still deletes every journaled route");
            Check(!File.Exists(mismatch), "and the journal is cleared after successful deletion");

            var gone = Path.Combine(dir, "gone.json");
            TunRouteJournal.Save(ifIndex, Routes, gone);
            var goneTouched = 0;
            var goneRemoved = TunRouteJournal.RecoverStale(
                _ => null,
                (_, _) => { goneTouched++; return true; },
                null,
                gone);
            Check(goneRemoved == Routes.Count && goneTouched == Routes.Count,
                  "an absent adapter still deletes by the stored interface index");
            Check(!File.Exists(gone), "and its journal is cleared after successful deletion");

            var partial = Path.Combine(dir, "partial.json");
            TunRouteJournal.Save(ifIndex, Routes, partial);
            var attempt = 0;
            var keptRemoved = TunRouteJournal.RecoverStale(
                _ => alias,
                (_, _) => ++attempt != 2,
                null,
                partial);
            Check(keptRemoved == Routes.Count - 1,
                  $"a failed delete is not reported as removed ({keptRemoved}/{Routes.Count})");
            Check(File.Exists(partial), "the journal is kept when a delete fails, so startup can retry");

            var finalRemoved = TunRouteJournal.RecoverStale(_ => alias, (_, _) => true, null, partial);
            Check(finalRemoved == Routes.Count, "the retry replays every entry, including the deleted ones");
            Check(!File.Exists(partial), "and clears the journal once nothing fails");

            var corrupt = Path.Combine(dir, "corrupt.json");
            File.WriteAllText(corrupt, "{ this is not json");
            Check(TunRouteJournal.RecoverStale(_ => alias, (_, _) => true, null, corrupt) == 0,
                  "a corrupt journal is ignored instead of throwing");
            Check(!File.Exists(corrupt), "and is removed");

            var empty = Path.Combine(dir, "empty.json");
            File.WriteAllText(empty, "{\"IfIndex\":1,\"Adapter\":\"x\",\"Routes\":[]}");
            Check(TunRouteJournal.RecoverStale(_ => alias, (_, _) => true, null, empty) == 0,
                  "an empty journal does nothing");

            var clearPath = Path.Combine(dir, "clear.json");
            TunRouteJournal.Save(ifIndex, Routes, clearPath);
            TunRouteJournal.Clear(clearPath);
            Check(!File.Exists(clearPath), "Clear removes the journal");
            TunRouteJournal.Clear(clearPath);
            Check(!File.Exists(clearPath), "Clear on an absent journal is a no-op");

            var routes = Source("Tun/WintunTunManager.Routes.cs");
            Check(routes.Contains("PersistRouteJournal();"),
                  "ApplyRoutesAsync journals the bypass set it just installed");
            Check(Source("Tun/WintunTunManager.SplitDns.cs").Contains("PersistRouteJournal();"),
                  "split DNS host routes are journaled as they are pinned");
            Check(Source("Tun/WintunTunManager.ProcessSplit.cs").Split("PersistRouteJournal();").Length - 1 >= 2,
                  "engine and per-app host routes are journaled too");
            Check(Source("Tun/WintunTunManager.Lifecycle.cs").Contains("TunRouteJournal.Clear();"),
                  "a clean stop removes the journal with the routes");
            var app = File.ReadAllText(FindFile(Path.Combine("Se7enPro", "App.xaml.cs")));
            var recover = app.IndexOf("RecoverStaleRoutes", StringComparison.Ordinal);
            var autoConnect = app.IndexOf("StartAutoConnectAsync", StringComparison.Ordinal);
            Check(recover >= 0, "the app recovers a crashed session's journal at startup");
            Check(recover >= 0 && autoConnect > recover,
                  "recovery runs before auto-connect can install its own routes");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        return failures;
    }

    private static (int Index, string Alias) RealAdapter()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var index = nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                    if (index > 0) return (index, nic.Name);
                }
                catch { }
            }
        }
        catch { }
        return (0, "");
    }

    private static string Source(string relativeUnderServices)
    {
        var path = FindFile(Path.Combine("Se7enPro", "Services", relativeUnderServices));
        return File.ReadAllText(path);
    }

    private static string FindFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd('\\'));
        }
        throw new FileNotFoundException($"could not locate {relative}");
    }
}
