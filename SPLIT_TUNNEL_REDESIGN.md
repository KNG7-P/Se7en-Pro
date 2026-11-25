# Split Tunnel Redesign — Implementation Report

Target: `Se7en-Pro` (WPF .NET 8, Windows VPN client). Reference only for routing/profile
architecture: [Throne](https://github.com/throneproj/Throne) — nothing was copied.

---

## 1. What was wrong

The old implementation had **no per-app mode** at all:

```
UserSettings.SplitTunnelMode      // "include" | "exclude"   ← one global switch
UserSettings.SplitTunnelEntries   // List<SplitTunnelEntry>  ← one shared list
```

Every consumer derived the same four lists from that single collection
(`SplitRules.ClassifySplitEntries`). Consequences:

* There was no Block mode and no per-app choice — an entry was either in or out of the
  single global bucket.
* Adding an app "to the tunnel" mutated the one shared list, so apps previously added as
  "bypass" were re-interpreted by the same classification pass. That is the reported
  *"adding an app to Through Tunnel pulls the Bypass apps with it"* bug: one mutable list,
  consumed in one direction.
* No profiles, so no way to keep two routing sets apart.
* Splitting happened only at the route/DNS level; an app's decision was never enforced
  per process.

`SplitRules.ClassifySplitEntries` and both legacy settings were removed. The only trace
left is `LegacySplitTunnelEntry`, used once by the migration.

---

## 2. Architecture

```
UserSettings
 ├─ SplitTunnelEnabled              (bool)
 ├─ ActiveSplitTunnelProfileId      (string)  ← restored on start
 └─ SplitTunnelProfiles : List<SplitTunnelProfile>
        ├─ Id / Name
        ├─ DefaultOutbound         "proxy" | "direct" | "block"
        ├─ Bypass : List<SplitTunnelRule>    ← independent list
        ├─ Tunnel : List<SplitTunnelRule>    ← independent list
        └─ Block  : List<SplitTunnelRule>    ← independent list
```

`SplitTunnelRule` = `Kind` (`app` / `process` / `domain` / `ip`) + `Value` + `DisplayName`.

```
SplitTunnelProfileStore   (static, the only mutator)
        ↓ revision counter
SplitRoutingPlanner.Build(settings) → SplitRoutingPlan   (immutable snapshot)
        ↓
   ┌────────────┬──────────────┬───────────────┬────────────────────┐
   │ Mihomo YAML│ WFP filters  │ TUN routes    │ DNS policy         │
   │ (mihomo)   │ (block/bypass│ (bypass pins) │ (bypass resolvers) │
   │            │  on tun2socks│               │                    │
   │            │  /zeptun)    │               │                    │
   └────────────┴──────────────┴───────────────┴────────────────────┘
```

### Key types

| Type | File | Role |
|---|---|---|
| `SplitTunnelProfile`, `SplitTunnelRule` | `Models/SplitTunnelProfiles.cs` | persisted model |
| `SplitTunnelProfileStore` | `Services/Tun/SplitTunnelProfileStore.cs` | **only** mutator; normalisation, isolation guarantees, max limits (32 profiles / 512 rules) |
| `SplitRoutingPlanner`, `SplitRoutingPlan`, `SplitRuleCategory` | `Services/Tun/SplitRoutingPlan.cs` | builds an immutable plan; resolves a process to a category |
| `WfpSplitRouter` | `Services/Tun/WfpSplitRouter.cs` | real WFP enforcement for tun2socks/zeptun |
| `MihomoConfigBuilder` | `Services/Tun/MihomoConfigBuilder.cs` | native process/domain/IP rules for mihomo TUN |
| `SplitTunnelMigration` | `Services/Tun/SplitTunnelMigration.cs` | one-time conversion of the old flat list |

---

## 3. How the three modes are isolated

**Data level**

* Three separate `List<SplitTunnelRule>` instances per profile. They are never aliased:
  a test asserts `!ReferenceEquals` for all three pairs and that no rule instance exists
  in two lists.
* `SplitTunnelProfileStore.SetApplication(...)` is the only way a rule changes mode. It
  locates the *current owner* across all three lists, removes it there, then adds it to the
  target list. A target therefore exists in exactly one mode.
* Re-adding to the same mode is a no-op (`AlreadyThere`), so no duplicates.
* `NormalizeAll` (run on load) collapses a hand-edited duplicate deterministically:
  Block wins, then Tunnel, then Bypass. `SanitizeList` also removes rules owned elsewhere.
* Every visible "move" in the UI (`MoveRule`, `AssignApp*`) routes through that same call.
  Nothing ever moves implicitly.

**Plan level**

* `SplitRoutingPlanner.Build` emits rules in the fixed order **Block → Tunnel → Bypass**,
  and drops any later duplicate key, so precedence is deterministic and Block always wins.
* The plan is immutable and content-hashed (`plan.Hash`).

**Engine level** — each mode has a different enforcement mechanism, so they cannot
contaminate each other:

| Mode | tun2socks / zeptun | mihomo TUN |
|---|---|---|
| **Through Tunnel** | no filter — the TUN catch-all route owns the process | `PROCESS-PATH/NAME,…,Se7enProxy` |
| **Bypass** | `BLOCK` at `FWPM_LAYER_ALE_CONNECT_REDIRECT_V4/V6` (cancels the redirect, traffic continues over the physical NIC); falls back to per-connection route pinning if the redirect layer is unavailable | `…,Se7enDirect` (a `direct` outbound bound to the real interface) |
| **Block** | `BLOCK` at `ALE_AUTH_CONNECT_V4/V6` **and** `DATAGRAM_DATA_V4/V6`, weight above the kill-switch permits | `…,REJECT` |

Block is dropped at both the connect layer (no new flow) and the datagram layer (an
already-open UDP socket stops too). Because Block filters sit above the kill-switch
permits, an allow rule elsewhere cannot re-permit a blocked app.

---

## 4. How profile routing works

1. `SettingsService.Load()` deserialises profiles, runs the one-time migration if the old
   keys are present, then `NormalizeAll` repairs anything inconsistent.
2. `ActiveSplitTunnelProfileId` is restored, and `ResolveActive` falls back to the first
   profile if the pinned one disappeared.
3. `WintunTunManager.BuildSplitPlan()` memoises `SplitRoutingPlanner.Build(settings)` on a
   cheap fingerprint (`store revision | enabled | active id | fallback bucket`), so the
   250 ms process monitor and the DNS path reuse one instance instead of rebuilding.
4. `ReconcileAsync` compares `plan.Hash`; on change it applies WFP enforcement, then
   re-applies routes, or regenerates the mihomo YAML (which restarts the core). When the
   adapter is not running, the WFP filters are disarmed.
5. The default outbound only decides unmatched traffic: `MATCH,Se7enProxy` /
   `MATCH,Se7enDirect` / `MATCH,REJECT`. It never overrides an explicit rule.

**Domain and IP rules follow their owning mode.** Only `Bypass` domains/CIDRs are resolved
against the local resolver and pinned to the physical gateway. Through-Tunnel entries stay
on the tunnel path (the engine resolves them), and Block entries are rejected before they
are dialled. The system-proxy bypass list is built from `BypassDomains`/`DirectIpCidrs`
only.

---

## 5. UI

`Views/SplitTunnelPage.xaml` (614 lines) + `ViewModels/SplitTunnelViewModel.cs`:

* **Status strip** — coloured dot, one sentence, system-wide switch (admin) or
  *Restart as Administrator*.
* **Profile bar** — enable switch, profile combo (outbound icon + rule count), rename,
  delete, new-profile field, create.
* **Left card** — default-outbound segmented control (Tunnel / Direct / Block), three mode
  tabs with live counts, an inline custom-rule panel (process / domain / IP), the active
  mode's rule list from **one shared template**, empty state.
* **Right card** — a permanent, searchable application list: icon, name, file name, and a
  one-click add or a coloured mode pill with unassign.

Removed: picker dialog flow, duplicate per-mode templates, dead commands, and the
`ConverterParameter=""` that produced XAML error `MC3041`.

---

## 6. Files

**Added (7)**

| File | Lines |
|---|---|
| `Se7enPro/Models/SplitTunnelProfiles.cs` | 72 |
| `Se7enPro/Services/Tun/SplitRoutingPlan.cs` | 328 |
| `Se7enPro/Services/Tun/SplitTunnelProfileStore.cs` | 360 |
| `Se7enPro/Services/Tun/SplitTunnelMigration.cs` | 87 |
| `Se7enPro/Services/Tun/WfpSplitRouter.cs` | 513 |
| `Se7enPro/Converters/SplitPaneConverters.cs` | 36 |
| `tools/VerifyCriticalFixes/SplitTunnelProfileTests.cs` | 668 |

**Modified (26)** — `UserSettings`, `SettingsService`, `SplitRules` (obsolete code
removed), `MihomoConfigBuilder`, `ConnectionManager`, `InstalledAppsProvider`,
`WintunTunManager{,.Helpers,.Lifecycle,.ProcessSplit,.Reconcile,.Routes,.Session,.SplitDns}`,
`SplitTunnelViewModel`, `SplitTunnelPage.xaml(.cs)`, the three string dictionaries,
`ContractTests`, `MihomoSingBoxModalTests`, `Program`, `VerifyCriticalFixes.csproj`,
`README.md`, `CHANGELOG.md`.

`git diff --stat`: **26 files changed, 1748 insertions(+), 1209 deletions(-)**.
`WintunRouteApi.cs` is byte-identical to the original — an earlier UDP-table helper was
removed once WFP datagram filtering made it unnecessary.

---

## 7. Tests

`tools/VerifyCriticalFixes/SplitTunnelProfileTests.cs`, wired into `Program.cs`, and also
run through an offline harness (same source files, `net8.0`) because the project itself
targets WPF. **116 assertions, all passing.**

| Required scenario | Covered by |
|---|---|
| 1. Bypass isolation | two apps in Bypass resolve to Direct; other lists stay empty |
| 2. Through Tunnel isolation | adding to Tunnel leaves both Bypass apps untouched |
| 3. Block isolation | blocked app resolves to Block; the other three unaffected |
| 4. Moving apps | explicit move removes from source, adds to target, no duplicate; kind preserved |
| 5. Profile isolation | two profiles, 25 rapid switches, rule snapshots identical before/after |
| 6. Persistence across restart | real `SettingsService.Save()` → new instance `Load()`; rules, switches, defaults and active id restored |
| 7. Default outbound | Proxy/Direct/Block defaults for unmatched traffic; explicit rules still win |
| 8. Real engine rules | mihomo YAML: Block→Direct→Proxy ordering, `MATCH` last, no target under two actions |
| Edge cases | empty profile, duplicate names, invalid input rejected, case-insensitive path merge, name-vs-path rules, per-mode clear, three-way duplicate collapse to Block, uninstalled app, rapid toggling |
| Regression | the reported bug: lists never shared, never replaced, no aliased rules |
| Migration | exclude→Bypass/Proxy, include→Tunnel/Direct, runs exactly once |
| Mode-correct domain/IP | only Bypass domains/CIDRs eligible for direct pinning; Tunnel stays proxied; Block withdrawn from every direct path |

Also updated: `ContractTests` (new page surface: three modes, default outbound, app
assign/unassign, shared row template, restart-as-admin visibility) and
`MihomoSingBoxModalTests` (now drives the real planner instead of the removed
`splitDomains:` parameter).

### Real-network verification — **not performed**

Stated plainly rather than omitted: this work was done in a Linux container.

* WPF cannot run (`Microsoft.WindowsDesktop.App` is Windows-only) — the app was never
  launched.
* `wintun.dll`, the WFP engine, route manipulation and the TUN data path all require
  Windows + Administrator.
* Therefore Direct / Proxy / Block were **not** confirmed against a live network. What was
  verified is everything up to the kernel boundary: the immutable plan, the exact YAML the
  mihomo core consumes, the exact WFP layer GUIDs/weights, and the route/DNS inputs.

---

## 8. Build status

```
dotnet build Se7enPro/Se7enPro.csproj -c Release -r win-x64 --self-contained false
→ Build succeeded.
  3 warnings: App.xaml.cs(168), LocalSocksEngineBase.cs(397), Shard/ShardEngine.cs(199)
  (all CS1998, all pre-existing, all in files this change does not touch)
```

* Zero warnings introduced by this change; verified with `--no-incremental`.
* `dotnet build tools/VerifyCriticalFixes/…` → succeeded, no warnings.
* `dotnet format whitespace --verify-no-changes` → clean on every file added here (the repo
  as a whole is not format-clean; that is pre-existing).
* No `.csproj` changes to the app project; test project only gains the new unit links.

---

## 9. Remaining limitations

1. **Windows-only verification gap** — see above. A reviewer on Windows should run the
   app with a real tunnel and check: a Bypass app stays outside the tunnel during
   download, a Block app cannot open a connection *and* loses an already-open UDP flow,
   and a Through-Tunnel app's traffic exits through the proxy.
2. **`WFP DATAGRAM_DATA` filters** — on Windows builds that refuse that layer, Block still
   stops every new flow but an already-open UDP socket may survive briefly. The failure is
   recorded in `LastError` and written to the diagnostic log rather than hidden.
3. **Bypass without a physical default route** — no direct outbound exists to bind to.
   mihomo YAML would silently proxy those apps, so a diagnostic now reports it and the TUN
   start fails instead of quietly changing the mode.
4. **`direct` outbound binding** — `Se7enDirect` binds `interface-name` to the physical NIC.
   This is correct for a single-uplink machine; multi-homed setups with per-app source
   routing are out of scope.
5. **Name-only rules** — an `.exe` without an absolute path needs a running instance or a
   cached app scan to be resolved to a WFP `appId`. Unresolvable names are matched by the
   process monitor instead, which is per-connection rather than kernel-level.
6. **Not committed** — the work sits as uncommitted changes on `master` in
   `/home/user/Se7en-Pro`; no branch or commit was created.

---

## 10. Throne reference

Used for two decisions only, both re-expressed in Se7en's own model:

* **three-way per-app classification** (direct / proxy / reject) rather than a single
  include/exclude switch — confirmed the mode split is the right shape;
* **profiles as the isolation boundary**, with the active profile selected by id and each
  profile owning its own rule sets.

Not copied: Throne's GO/Clash config structures, its process-matching implementation, its
UI, and its global "bypass China/Iran" style preset lists — the last is explicitly out of
scope for this work.
