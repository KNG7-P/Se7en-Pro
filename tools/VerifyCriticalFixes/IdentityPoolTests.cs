using System;
using System.IO;
using System.Linq;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

/// <summary>
/// Tests for the identity pool.
///
/// The store does no networking, so all of this runs offline against a temp directory.
/// That is the point of the design: rotation, validity and failure attribution are the
/// parts that decide whether the pool actually helps when the registration endpoint is
/// blocked, and they are the parts a screenshot cannot check.
///
/// Nothing here touches api.cloudflareclient.com or writes anywhere near the real pool.
/// </summary>
internal static class IdentityPoolTests
{
    private const long Day = 86400;

    internal static int Run()
    {
        var failures = 0;
        var root = Path.Combine(Path.GetTempPath(), "se7en-pool-tests-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Program.Section("Identity pool — validity");
            failures += TestValidity();

            Program.Section("Identity pool — rotation");
            failures += TestRotation(root);

            Program.Section("Identity pool — failure attribution");
            failures += TestFailureAttribution(root);

            Program.Section("Identity pool — capacity and trim");
            failures += TestTrim(root);

            Program.Section("Identity pool — encrypted at rest");
            failures += TestAtRest(root);

            Program.Section("Identity pool — survives a corrupt store");
            failures += TestCorruptStore(root);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }

        return failures;
    }

    // ---------------------------------------------------------------- cases

    private static int TestValidity()
    {
        var f = 0;
        var now = 1_700_000_000;

        Check(IdentityPool.IsUsable(SlotOf(Warp("d1", now)), now), "a fresh WireGuard identity is usable");
        Check(IdentityPool.IsUsable(SlotOf(Warp("d2", 0)), now),
              "a WireGuard identity with no expiry is still usable years later");
        Check(IdentityPool.IsUsable(SlotOf(Warp("d3", 0)), now + 400 * Day),
              "WireGuard does not expire on a one-year horizon");

        var retired = SlotOf(Warp("d4", now));
        retired.RetireReason = "blocked";
        Check(!IdentityPool.IsUsable(retired, now),
              "a retired WireGuard identity is not usable");
        Check(!IdentityPool.IsUsable(SlotOf(Warp("", now)), now),
              "an identity with no device id is not usable");
        var noMaterial = SlotOf(Warp("d5", now));
        noMaterial.Toml = "";
        Check(!IdentityPool.IsUsable(noMaterial, now),
              "an identity with no material is not usable");

        // MASQUE carries a certificate with a real lifetime, and handing out a
        // nearly-expired one fails later rather than now.
        Check(IdentityPool.IsUsable(SlotOf(Masque("m1", now)), now), "a fresh MASQUE identity is usable");
        Check(IdentityPool.IsUsable(SlotOf(Masque("m2", now)), now + 300 * Day),
              "a MASQUE identity is still usable at 300 days");
        Check(!IdentityPool.IsUsable(SlotOf(Masque("m3", now)), now + 365 * Day),
              "a MASQUE identity is dead at 365 days");
        Check(!IdentityPool.IsUsable(SlotOf(Masque("m4", now)), now + 360 * Day),
              "the expiry skew bites before the real expiry");
        Check(!IdentityPool.IsUsable(SlotOf(Masque("m5", 0)), now),
              "a MASQUE identity with no issue time is not usable");
        Check(!IdentityPool.IsUsable(SlotOf(Masque("m6", now + 10)), now),
              "a MASQUE identity issued in the future is not usable");

        return f;
    }

    private static int TestRotation(string root)
    {
        var f = 0;
        var now = 1_700_000_000;
        var pool = MakePool(root, "rot");

        for (var i = 0; i < 3; i++)
            pool.Add(Warp("rot-d" + i, now), now);

        Check(pool.Count(IdentityKind.WireGuard, now) == 3, "three identities are counted",
              pool.Count(IdentityKind.WireGuard, now).ToString());

        // Least-recently-used first: a never-used slot outranks a used one, so a fresh
        // pool is handed out in turn rather than always reusing the same identity.
        Check(pool.TryCheckout(IdentityKind.WireGuard, "wd1", out var a, now) && a!.DeviceId == "rot-d0",
              "the first checkout hands out the first identity", a?.DeviceId ?? "(none)");
        Check(pool.TryCheckout(IdentityKind.WireGuard, "wd2", out var b, now + 1) && b!.DeviceId == "rot-d1",
              "the second checkout hands out a DIFFERENT identity", b?.DeviceId ?? "(none)");
        Check(pool.TryCheckout(IdentityKind.WireGuard, "wd3", out var c, now + 2) && c!.DeviceId == "rot-d2",
              "the third checkout hands out the third", c?.DeviceId ?? "(none)");

        // A fourth checkout wraps around, and wraps to the least recently used.
        Check(pool.TryCheckout(IdentityKind.WireGuard, "wd4", out var d, now + 3) && d!.DeviceId == "rot-d0",
              "once the pool is exhausted it wraps to the least recently used",
              d?.DeviceId ?? "(none)");

        // Rotation must actually move usage, otherwise spreading the load - the whole
        // point - would not happen.
        var used = pool.Snapshot().Where(s => s.UseCount > 0).Select(s => s.UseCount).ToList();
        Check(used.Count == 3 && used.Max() - used.Min() <= 1,
              "usage is spread evenly across the pool",
              string.Join(",", pool.Snapshot().Select(s => s.UseCount)));

        // Kinds are kept apart: a WireGuard identity must never satisfy a MASQUE checkout.
        var mixed = MakePool(root, "mix");
        mixed.Add(Warp("mix-w", now), now);
        Check(!mixed.TryCheckout(IdentityKind.Masque, "wd", out _, now),
              "a WireGuard identity is never handed out for MASQUE");
        Check(mixed.TryCheckout(IdentityKind.WireGuard, "wd", out _, now),
              "and it IS handed out for WireGuard");

        return f;
    }

    private static int TestFailureAttribution(string root)
    {
        var f = 0;
        var now = 1_700_000_000;
        var pool = MakePool(root, "fail");

        for (var i = 0; i < 3; i++) pool.Add(Warp("f-d" + i, now), now);

        pool.TryCheckout(IdentityKind.WireGuard, "work-a", out var used, now);
        pool.TryCheckout(IdentityKind.WireGuard, "work-b", out var other, now);

        Check(pool.Count(IdentityKind.WireGuard, now) == 3, "all three are usable before the failure");

        pool.ReportFailure("work-a", "blocked by the carrier");

        Check(pool.Count(IdentityKind.WireGuard, now) == 2,
              "a failed identity stops counting as usable",
              pool.Count(IdentityKind.WireGuard, now).ToString());

        var retired = pool.Snapshot().First(s => s.DeviceId == used!.DeviceId);
        Check(retired.Retired, "the identity used by the failed workDir is retired");
        Check(retired.RetireReason == "blocked by the carrier", "the reason is recorded",
              retired.RetireReason ?? "(none)");
        Check(retired.FailCount == 1, "the failure is counted");

        // The other workDir's identity must be untouched - attributing the failure
        // broadly would retire identities that demonstrably still work.
        var survivor = pool.Snapshot().First(s => s.DeviceId == other!.DeviceId);
        Check(!survivor.Retired, "an identity in use by a different workDir is NOT retired");

        // The next connect must not be handed the dead one again.
        pool.TryCheckout(IdentityKind.WireGuard, "work-c", out var next, now + 10);
        Check(next!.DeviceId != used!.DeviceId,
              "the next connect is not handed the retired identity", next.DeviceId);

        // A success clears the active mapping without retiring anything.
        pool.TryCheckout(IdentityKind.WireGuard, "work-d", out var good, now + 20);
        pool.ReportSuccess("work-d");
        var stillGood = pool.Snapshot().First(s => s.DeviceId == good!.DeviceId);
        Check(!stillGood.Retired, "a successful connect retires nothing");
        Check(stillGood.UseCount >= 1, "but the use is still counted for rotation");

        return f;
    }

    private static int TestTrim(string root)
    {
        var f = 0;
        var now = 1_700_000_000;
        var pool = MakePool(root, "trim");

        for (var i = 0; i < 5; i++) pool.Add(Warp($"t-d{i}", now + i), now + i);

        pool.TryCheckout(IdentityKind.WireGuard, "w1", out _, now + 10);
        pool.TryCheckout(IdentityKind.WireGuard, "w2", out _, now + 11);
        pool.TryCheckout(IdentityKind.WireGuard, "w3", out _, now + 12);

        pool.Trim(IdentityKind.WireGuard, keep: 3, now + 20);
        Check(pool.Count(IdentityKind.WireGuard, now + 20) == 3,
              "trim caps the pool at the requested size",
              pool.Count(IdentityKind.WireGuard, now + 20).ToString());

        // Trimming must drop the LEAST valuable. The identities that have been handed out and
        // worked are the ones with evidence behind them, so they are the ones to keep -
        // dropping them to make room for untried ones would discard every proven entry.
        var kept = pool.Snapshot().Where(s => s.Kind == IdentityKind.WireGuard).ToList();
        Check(kept.Sum(s => s.UseCount) == 3,
              "the identities with recorded use are the ones kept",
              string.Join(",", kept.Select(s => $"{s.DeviceId}:{s.UseCount}")));
        Check(kept.All(s => s.UseCount > 0),
              "an untried identity is dropped before a tried one",
              string.Join(",", kept.Select(s => $"{s.DeviceId}:{s.UseCount}")));

        // Kinds trim independently.
        var mixed = MakePool(root, "trim2");
        for (var i = 0; i < 4; i++) mixed.Add(Warp($"x-w{i}", now), now);
        for (var i = 0; i < 2; i++) mixed.Add(Masque($"x-m{i}", now), now);
        mixed.Trim(IdentityKind.WireGuard, keep: 2, now);
        Check(mixed.Count(IdentityKind.WireGuard, now) == 2, "WireGuard is trimmed to two");
        Check(mixed.Count(IdentityKind.Masque, now) == 2, "MASQUE is untouched by a WireGuard trim");

        // Adding beyond the cap trims immediately.
        var capped = MakePool(root, "trim3");
        for (var i = 0; i < 4; i++)
            capped.Add(Warp($"y-d{i}", now), now, keepPerKind: 2);
        Check(capped.Count(IdentityKind.WireGuard, now) == 2,
              "Add with keepPerKind caps the pool in one step",
              capped.Count(IdentityKind.WireGuard, now).ToString());

        // A retired identity is always the first to go, however well-used it was.
        var retiredFirst = MakePool(root, "trim4");
        for (var i = 0; i < 3; i++) retiredFirst.Add(Warp($"z-d{i}", now), now);
        retiredFirst.TryCheckout(IdentityKind.WireGuard, "w1", out var good1, now + 1);
        retiredFirst.TryCheckout(IdentityKind.WireGuard, "w2", out var good2, now + 2);
        retiredFirst.ReportFailure("w1", "blocked");

        // Both used identities are now equal on UseCount, so only the retirement can decide
        // which one is dropped when the pool is over capacity.
        Check(good1!.UseCount == good2!.UseCount,
              "the test case has two equally-used identities, so retirement decides",
              $"{good1.UseCount} vs {good2.UseCount}");

        retiredFirst.Trim(IdentityKind.WireGuard, keep: 2, now + 3);

        var survivors = retiredFirst.Snapshot();
        Check(survivors.All(s => s.DeviceId != good1!.DeviceId),
              "a retired identity is trimmed first even when it was well used",
              string.Join(",", survivors.Select(s => $"{s.DeviceId}:{(s.Retired ? "retired" : "ok")}")));
        Check(survivors.Any(s => s.DeviceId == good2!.DeviceId),
              "the healthy identity is kept");
        Check(survivors.Count == 2, "and the pool is at the requested size", survivors.Count.ToString());

        return f;
    }

    private static int TestAtRest(string root)
    {
        var f = 0;
        var now = 1_700_000_000;
        var pool = MakePool(root, "crypt");

        var secret = "SUPERSECRETKEYMATERIAL-abcdef0123456789";
        pool.Add(Warp("c-d1", now) with { Toml = $"wg_private_key = \"{secret}\"" }, now);

        var onDisk = File.ReadAllText(pool.StorePath);

        Check(!onDisk.Contains(secret, StringComparison.Ordinal),
              "the private key is NOT readable in the pool file on disk");
        Check(!onDisk.Contains("wg_private_key", StringComparison.Ordinal),
              "not even the field name appears in the clear");
        Check(onDisk.StartsWith("dpapi:v1:", StringComparison.Ordinal),
              "the pool file is DPAPI-protected", onDisk[..Math.Min(24, onDisk.Length)]);

        // And it round-trips: the data survives because DPAPI is encryption, not hashing.
        var reopened = MakePool(root, "crypt");
        Check(reopened.Count(IdentityKind.WireGuard, now) == 1,
              "the identity is readable again in the same session");
        Check(reopened.TryCheckout(IdentityKind.WireGuard, "wd", out var slot, now),
              "and can be checked out");
        Check(slot!.Toml.Contains(secret, StringComparison.Ordinal),
              "the material round-trips intact");

        return f;
    }

    private static int TestCorruptStore(string root)
    {
        var f = 0;
        var now = 1_700_000_000;
        var pool = MakePool(root, "corrupt");
        pool.Add(Warp("k-d1", now), now);

        File.WriteAllText(pool.StorePath, "this is not the pool file at all");

        // A pool is a cache. Being unable to read it must degrade to "empty", never to a
        // refusal to connect.
        Check(pool.Count(IdentityKind.WireGuard, now) == 0,
              "a corrupt pool reads as empty");
        Check(!pool.TryCheckout(IdentityKind.WireGuard, "wd", out _, now),
              "a corrupt pool hands out nothing");
        Check(pool.Snapshot().Count == 0, "Snapshot is safe on a corrupt pool");

        // And it recovers: a fresh Add over the wreckage works.
        Check(pool.Add(Warp("k-d2", now), now), "the pool can be repopulated after corruption");
        Check(pool.Count(IdentityKind.WireGuard, now) == 1, "and works again");

        return f;
    }

    // ---------------------------------------------------------------- helpers

    private static IdentityPool MakePool(string root, string name) =>
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityPool>.Instance,
            Path.Combine(root, name, "pool.json"));

    private static PooledIdentity Warp(string deviceId, long createdUtc) =>
        new(IdentityKind.WireGuard, deviceId, $"device_id = \"{deviceId}\"", CertIssuedAt: 0);

    private static PooledIdentity Masque(string deviceId, long issuedAt) =>
        new(IdentityKind.Masque, deviceId, $"device_id = \"{deviceId}\"", issuedAt);

    /// <summary>A stored slot, as the pool keeps it, for the validity rules.</summary>
    private static IdentityPool.Slot SlotOf(PooledIdentity m) => new()
    {
        Id = m.DeviceId,
        Kind = m.Kind,
        DeviceId = m.DeviceId,
        Toml = m.Toml,
        CertIssuedAt = m.CertIssuedAt,
    };

    private static void Check(bool ok, string what, string? detail = null) => Program.Check(ok, what, detail);
}