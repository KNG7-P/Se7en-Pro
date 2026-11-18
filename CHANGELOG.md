# 📝 Se7en Pro — Changelog

## [1.0.8]

Major protocol expansion (Foxy, Lantern, Veep), Aether 2.4.0 update, 2 new TUN engines (Zeptun & Mihomo), new multi-hop Chain modes, MTU scanner, and major stability/performance optimizations.

### 🚀 Highlights & Features
- **Foxy Method**: Added Firefox VPN (Fastly MASQUE HTTP/2) protocol with HelloSplit TLS bypass, multi-account management & automatic quota failover, plus direct in-app sign-in without requiring a separate VPN.
- **Lantern & Veep Methods**: Added Lantern and Veep protocol engines with automatic server selection and resilient health checks.
- **Aether Core 2.4.0 & WARP Key Fetching**: Updated Aether core to `2.4.0` and added standalone WARP key acquisition directly through any active app protocol.
- **2 New TUN Engines**: Added `Zeptun` and `Mihomo` alongside `tun2socks` for system-wide Wintun routing.
- **New Chain Methods**: Added multiple new multi-hop chained methods (including Psiphon over Foxy, Psiphon over Lantern, Lantern over WARP, Veep over WARP, and more).
- **MTU Configuration & Scanner**: Added MTU tuning and live MTU scanning for Shard and Aether methods.

### 🔧 Fixes & Improvements
- **Split Tunneling Fixes**: Resolved per-app/domain split tunneling routing and DNS leaks across TUN modes.
- **Lag & Performance Optimizations**: Reduced UI/data-plane lag, batched HTTP/2 `WINDOW_UPDATE`s, eliminated 600s chained drops, and fixed reported stability issues.

---

## [1.0.7]

Automatic egress endpoint and geo-location resolution, live country flag indicator in dashboard, Tor pluggable transport path space fixes, and DNS resolver diagnostics UI repair.

### 🚀 Highlights & Features
- **Dynamic Egress Location & IP Resolver**: Multi-provider egress probe (`EndpointProbeHelper`) automatically querying via the active local SOCKS port (Cloudflare trace, Country.is, Ipwho.is, Ip-api.com) upon connection.
- **Live Country Flag Indicator**: Connected exit node's country flag and localized country name are now displayed directly inside the dashboard endpoint tile and telemetry badge.
- **Protocol-Adaptive Region Selector**: The region selection box in the dashboard now dynamically appears only for protocols supporting exit region customization (e.g. Psiphon, Tor).

### 🔧 Fixes & Improvements
- **Tor Pluggable Transport Path Fix**: Resolved issues with pluggable transport executable paths containing spaces on Windows by normalizing to 8.3 short paths without illegal manual quotes in `torrc`.
- **Custom DNS Diagnostics Button**: Fixed the `Test Resolvers` button in DNS settings which was previously disabled due to an incorrect inverse boolean converter binding.
- **Multi-Protocol Exit Synchronization**: Seamless exit endpoint probing wired into V2Ray, Tor, Psiphon, and Local SOCKS engine workflows.
- **In-App Updater ACL & Staging Fallback**: Hardened update staging directory permissions to include active user SID alongside Administrators and SYSTEM, adding reliable fallback staging directories for non-elevated downloads.
- **Accurate Update Flavor Selection**: In-app updater now accurately detects and downloads the exact runtime flavor matching the currently running instance (Setup vs Portable, x64 vs x86, with vs without bundled .NET runtime).
