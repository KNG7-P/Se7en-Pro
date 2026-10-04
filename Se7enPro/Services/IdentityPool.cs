using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Se7enPro.Services;

/// <summary>Which registration a pooled identity came from.</summary>
public enum IdentityKind
{
    /// <summary>WireGuard / WARP / WARP-on-WARP.</summary>
    WireGuard,

    /// <summary>MASQUE and MASQUE-in-MASQUE.</summary>
    Masque,
}

/// <summary>One provisioned identity.</summary>
public sealed record PooledIdentity(
    IdentityKind Kind,
    string DeviceId,
    string Toml,
    long CertIssuedAt);

/// <summary>
/// A store of pre-provisioned Cloudflare identities for instant connection.
/// </summary>
public sealed class IdentityPool
{
    private const long CertExpirySkewSeconds = 7 * 86400;
    private const long CertLifetimeSeconds = 365 * 86400;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly ILogger<IdentityPool> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public IdentityPool(ILogger<IdentityPool> logger)
        : this(logger, storePathOverride: null)
    {
    }

    /// <summary>Internal constructor for testing with path override.</summary>
    internal IdentityPool(ILogger<IdentityPool> logger, string? storePathOverride)
    {
        _logger = logger;

        if (!string.IsNullOrEmpty(storePathOverride))
        {
            _path = storePathOverride;
            return;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Se7en");
        _path = Path.Combine(root, "identities", "pool.json");
    }

    /// <summary>Full path of the pool file, for diagnostics and tests.</summary>
    internal string StorePath => _path;

    // ---------------------------------------------------------------- model

    /// <summary>One stored identity plus the bookkeeping rotation depends on.</summary>
    public sealed class Slot
    {
        public string Id { get; set; } = "";
        public IdentityKind Kind { get; set; }
        public string DeviceId { get; set; } = "";
        public string Toml { get; set; } = "";

        /// <summary>Unix seconds the MASQUE certificate was issued. 0 for WireGuard.</summary>
        public long CertIssuedAt { get; set; }

        public long CreatedUtc { get; set; }
        public long LastUsedUtc { get; set; }
        public int UseCount { get; set; }
        public int FailCount { get; set; }

        /// <summary>Non-null once this identity must not be handed out again.</summary>
        public string? RetireReason { get; set; }

        public bool Retired => !string.IsNullOrEmpty(RetireReason);
    }

    private sealed class Store
    {
        public int Version { get; set; } = 1;
        public List<Slot> Slots { get; set; } = new();

        /// <summary>Active identity IDs indexed by working directory.</summary>
        public Dictionary<string, List<string>> ActiveByWorkDir { get; set; } = new();
    }

    // ---------------------------------------------------------------- validity

    /// <summary>Whether a slot may still be handed out at <paramref name="nowUtc"/>.</summary>
    internal static bool IsUsable(Slot slot, long nowUtc)
    {
        if (slot.Retired) return false;
        if (string.IsNullOrEmpty(slot.DeviceId)) return false;
        if (string.IsNullOrEmpty(slot.Toml)) return false;

        if (slot.Kind != IdentityKind.Masque) return true;

        if (slot.CertIssuedAt <= 0) return false;
        if (nowUtc < slot.CertIssuedAt) return false;
        if (nowUtc - slot.CertIssuedAt + CertExpirySkewSeconds >= CertLifetimeSeconds) return false;

        return true;
    }

    /// <summary>Number of spendable identities of a given kind.</summary>
    public int Count(IdentityKind kind, long? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Snapshot().Count(s => s.Kind == kind && IsUsable(s, now));
    }

    /// <summary>Whether a refill is worth attempting for this kind.</summary>
    public bool NeedsRefill(IdentityKind kind, int target, long? nowUtc = null) =>
        target > 0 && Count(kind, nowUtc) < target;

    // ---------------------------------------------------------------- checkout

    /// <summary>Checks out the least-recently-used usable identity.</summary>
    public bool TryCheckout(IdentityKind kind, string workDir, out Slot? slot, long? nowUtc = null)
    {
        slot = null;
        var now = nowUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _gate.Wait();
        try
        {
            var store = LoadUnsafe();
            var candidate = store.Slots
                .Where(s => s.Kind == kind && IsUsable(s, now))
                .OrderBy(s => s.LastUsedUtc)
                .ThenBy(s => s.CreatedUtc)
                .FirstOrDefault();

            if (candidate is null) return false;

            candidate.LastUsedUtc = now;
            candidate.UseCount++;

            if (!store.ActiveByWorkDir.TryGetValue(workDir, out var active))
            {
                active = new List<string>();
                store.ActiveByWorkDir[workDir] = active;
            }

            // Two identities for WARP-on-WARP and MASQUE-in-MASQUE: the primary is
            // already recorded, so only add when this workDir has none.
            if (active.Count == 0)
            {
                active.Add(candidate.Id);
                SaveUnsafe(store);
            }
            else
            {
                // Second leg: same slot twice would make both hops share an identity,
                // which defeats the point of the second hop entirely.
                var second = store.Slots
                    .Where(s => s.Kind == kind && IsUsable(s, now) && !active.Contains(s.Id))
                    .OrderBy(s => s.LastUsedUtc)
                    .ThenBy(s => s.CreatedUtc)
                    .FirstOrDefault();
                if (second is null) return false;

                second.LastUsedUtc = now;
                second.UseCount++;
                active.Add(second.Id);
                SaveUnsafe(store);
            }

            slot = candidate;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Checkout failed");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks identities handed out for a workDir as retired upon connection failure.</summary>
    public void ReportFailure(string workDir, string reason)
    {
        _gate.Wait();
        try
        {
            var store = LoadUnsafe();
            if (!store.ActiveByWorkDir.TryGetValue(workDir, out var ids) || ids.Count == 0) return;

            foreach (var id in ids)
            {
                var slot = store.Slots.FirstOrDefault(s => s.Id == id);
                if (slot is null || slot.Retired) continue;

                slot.RetireReason = reason;
                slot.FailCount++;
                _logger.LogWarning(
                    "[IdentityPool] Retired identity {Id} for {WorkDir}: {Reason}",
                    id, workDir, reason);
            }

            store.ActiveByWorkDir.Remove(workDir);
            SaveUnsafe(store);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Could not record the failure");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears active failure record for a workDir upon successful connection.</summary>
    public void ReportSuccess(string workDir)
    {
        _gate.Wait();
        try
        {
            var store = LoadUnsafe();
            if (store.ActiveByWorkDir.Remove(workDir)) SaveUnsafe(store);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Could not record the success");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- intake

    /// <summary>Adds a freshly provisioned identity to the pool.</summary>
    public bool Add(PooledIdentity material, long? nowUtc = null, int keepPerKind = 0)
    {
        if (string.IsNullOrEmpty(material.DeviceId)) return false;
        if (string.IsNullOrEmpty(material.Toml)) return false;

        var now = nowUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _gate.Wait();
        try
        {
            var store = LoadUnsafe();

            if (store.Slots.Any(s => s.DeviceId == material.DeviceId)) return false;

            store.Slots.Add(new Slot
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                Kind = material.Kind,
                DeviceId = material.DeviceId,
                Toml = material.Toml,
                CertIssuedAt = material.CertIssuedAt,
                CreatedUtc = now,
                LastUsedUtc = 0,
            });

            if (keepPerKind > 0) TrimUnsafe(store, material.Kind, keepPerKind, now);
            SaveUnsafe(store);

            _logger.LogInformation(
                "[IdentityPool] Stored a {Kind} identity; {Count} usable of that kind",
                material.Kind, store.Slots.Count(s => s.Kind == material.Kind && IsUsable(s, now)));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Could not store the identity");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Trims the least valuable identities of a kind once it exceeds <paramref name="keep"/>.</summary>
    public void Trim(IdentityKind kind, int keep, long? nowUtc = null)
    {
        if (keep <= 0) return;
        var now = nowUtc ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        _gate.Wait();
        try
        {
            var store = LoadUnsafe();
            if (TrimUnsafe(store, kind, keep, now)) SaveUnsafe(store);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Could not trim the pool");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TrimUnsafe(Store store, IdentityKind kind, int keep, long now)
    {
        var ofKind = store.Slots.Where(s => s.Kind == kind).ToList();
        if (ofKind.Count <= keep) return false;

        var doomed = ofKind
            .OrderBy(s => s.Retired ? 0 : 1)
            .ThenByDescending(s => s.FailCount)
            .ThenBy(s => s.UseCount)
            .ThenBy(s => s.CreatedUtc)
            .Take(ofKind.Count - keep)
            .Select(s => s.Id)
            .ToHashSet(StringComparer.Ordinal);

        store.Slots.RemoveAll(s => doomed.Contains(s.Id));

        foreach (var active in store.ActiveByWorkDir.Values) active.RemoveAll(id => doomed.Contains(id));

        foreach (var key in store.ActiveByWorkDir
                             .Where(kv => kv.Value.Count == 0)
                             .Select(kv => kv.Key)
                             .ToList())
        {
            store.ActiveByWorkDir.Remove(key);
        }

        return true;
    }

    /// <summary>Snapshot for diagnostics and tests. Never null.</summary>
    public IReadOnlyList<Slot> Snapshot()
    {
        _gate.Wait();
        try { return LoadUnsafe().Slots.ToList(); }
        catch { return Array.Empty<Slot>(); }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- storage

    private Store LoadUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return new Store();

            var stored = File.ReadAllText(_path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(stored)) return new Store();

            var json = ProtectedSecret.Unprotect(stored);
            if (string.IsNullOrWhiteSpace(json)) return new Store();

            return JsonSerializer.Deserialize<Store>(json, JsonOpts) ?? new Store();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Could not read the pool; treating it as empty");
            return new Store();
        }
    }

    private void SaveUnsafe(Store store)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            IdentityFileGuard.RestrictToCurrentUser(dir);

            var json = JsonSerializer.Serialize(store, JsonOpts);
            var protectedText = ProtectedSecret.Protect(json);

            var temp = _path + ".tmp";
            File.WriteAllText(temp, protectedText, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IdentityPool] Could not write the pool");
        }
    }
}