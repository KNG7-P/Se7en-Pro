using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

internal static class TunRouteJournal
{
    internal readonly record struct Entry(string Destination, byte Prefix, string NextHop, uint Metric);

    private sealed class JournalFile
    {
        public int IfIndex { get; set; }
        public string Adapter { get; set; } = "";
        public List<Entry> Routes { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static string? _lastWritten;
    private static string? _lastPath;

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Se7en", "tun-routes.json");

    internal static string? AdapterAlias(int ifIndex)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.GetIPProperties().GetIPv4Properties()?.Index == ifIndex) return nic.Name;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    internal static void Save(int ifIndex, IReadOnlyList<Entry> routes, string? path = null)
    {
        var target = path ?? DefaultPath;
        try
        {
            if (ifIndex <= 0) return;
            var adapter = AdapterAlias(ifIndex);
            adapter ??= $"ifindex:{ifIndex}";

            var json = JsonSerializer.Serialize(new JournalFile
            {
                IfIndex = ifIndex,
                Adapter = adapter,
                Routes = routes.ToList(),
            });

            lock (Gate)
            {
                if (string.Equals(json, _lastWritten, StringComparison.Ordinal)
                    && string.Equals(target, _lastPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = target + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, target, overwrite: true);
                _lastWritten = json;
                _lastPath = target;
            }
        }
        catch { }
    }

    internal static void Clear(string? path = null)
    {
        var target = path ?? DefaultPath;
        lock (Gate)
        {
            _lastWritten = null;
            _lastPath = null;
            try { if (File.Exists(target)) File.Delete(target); } catch { }
        }
    }

    internal static int RecoverStale(
        Func<int, string?> adapterForIndex,
        Func<int, Entry, bool> tryDelete,
        ILogger? logger = null,
        string? path = null)
    {
        var target = path ?? DefaultPath;
        JournalFile? journal;
        try
        {
            if (!File.Exists(target)) return 0;
            journal = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(target));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "tun route journal unreadable; discarding it");
            Clear(target);
            return 0;
        }

        if (journal is null || journal.IfIndex <= 0 || journal.Routes.Count == 0)
        {
            Clear(target);
            return 0;
        }

        var alias = adapterForIndex(journal.IfIndex);
        if (alias is null || !string.Equals(alias, journal.Adapter, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                "tun route journal: ifIndex {Index} alias mismatch ('{Stored}' vs '{Current}'); "
                + "attempting delete by stored ifIndex",
                journal.IfIndex, journal.Adapter, alias ?? "<unknown>");
            var attempted = 0;
            var failedMismatch = 0;
            foreach (var route in journal.Routes)
            {
                if (!IPAddress.TryParse(route.Destination, out _) || !IPAddress.TryParse(route.NextHop, out _))
                    continue;
                if (tryDelete(journal.IfIndex, route)) attempted++;
                else failedMismatch++;
            }
            if (failedMismatch == 0) Clear(target);
            return attempted;
        }

        var removed = 0;
        var failed = 0;
        foreach (var route in journal.Routes)
        {
            if (!IPAddress.TryParse(route.Destination, out _) || !IPAddress.TryParse(route.NextHop, out _))
                continue;

            if (tryDelete(journal.IfIndex, route)) removed++;
            else failed++;
        }

        if (failed == 0) Clear(target);

        logger?.LogInformation(
            "tun route journal: removed {Removed} leftover route(s) from '{Adapter}'{Note}",
            removed, journal.Adapter,
            failed == 0 ? "" : $"; {failed} could not be removed and will be retried on the next start");
        return removed;
    }
}
