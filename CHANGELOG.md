# Changelog

All notable changes to Se7en Pro are recorded here. The release workflow
publishes this file as the body of the GitHub release, so only the section for
the version being shipped should be present at the top.

## 1.0.5

The WPF client replaces the previous Flutter-based 1.0.4 line as the shipped
product. The engine layer is unchanged in spirit, but everything below shipped
after 1.0.4 is now in the release.

### Removed
- The Auto connection method and MASQUE-in-Masque as a selectable protocol.
  WireGuard (WARP) is the default method; Auto no longer probes the transport or
  falls back on its own.
- All Linux/Avalonia port and cross-build leftovers.
- The Flutter client and the headless daemon/IPC split introduced in 1.0.4.
  The client is a single WPF process again.

### Changed
- Secrets are no longer committed. The Psiphon propagation channel, sponsor id,
  signature/feedback keys and remote server-list URLs are injected at build time
  from GitHub Actions secrets and encrypted into the binary; the repository only
  ever contains placeholders.
- Release publishing is fully automated: pushing a `v*` tag builds x64 and x86,
  produces portable archives, installers and SHA-256 checksums, verifies that no
  plaintext secret reached an asset, and publishes the GitHub release.
- ReadyToRun is enabled for the publish path so first launch does not pay for a
  cold JIT of the whole UI stack.

### Fixed
- Kill switch is enforced on unexpected tunnel teardown instead of only on a
  clean disconnect, so an IP cannot leak through a dropped route.
- Update payloads are signature-verified and restricted to known hosts before
  anything is fetched or executed.
- TUN bring-up and teardown race that could black-hole DNS is closed.
- SHARD, Tor and chained-engine defects around disposal and restart.
- `ss://` node handling, upstream proxy chaining and the readiness probe.
- Proxy password field is editable again and the chain hop pins are respected.
- Log list auto-scroll now actually follows the tail, and the list only refreshes
  while the page is on screen.
- Portable (non-installed) builds no longer break TUN mode during elevation.
- Tray icon updates reliably on connect/disconnect.

### Performance
- First-launch black screen shortened; per-tick waste removed from the hot
  paths; log rendering throttled off-screen.
- Aether MASQUE and SHARD engines reworked for faster handshake and node reuse.
- The last successful route is reused and dropped tunnels recover automatically.