# Building and releasing Se7en Pro

Everything here works from a clean clone. Two rules to keep in mind:

1. **No secret is ever committed.** The repository ships placeholder Psiphon
   configuration. Real values are injected at build time from environment
   variables and encrypted into the binary.
2. **Releases are made by pushing a tag.** GitHub Actions builds, verifies and
   publishes; you do not upload anything by hand.

---

## 1. Prerequisites

| Requirement | Version | Notes |
| :--- | :--- | :--- |
| Windows | 10 / 11 (x86 or x64) | WPF desktop app |
| [.NET SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | 8.0 or newer | SDK 10 also works |
| [Python](https://python.org/downloads/) | 3.10+ | only for the secret injector |
| `cryptography` (pip) | any | only for the secret injector |
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | 6.x | only for `.exe` installers |

```powershell
git clone https://github.com/KNG7-P/Se7en-Pro.git
cd Se7en-Pro
python -m pip install cryptography
```

---

## 2. Everyday development build

This needs no secrets and no configuration:

```powershell
# framework-dependent, x64
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64

# framework-dependent, x86
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x86
```

Output:

```
Se7enPro\bin\Release\net8.0-windows10.0.19041.0\win-x64\Se7enPro.exe
```

The client starts and every UI feature works. Psiphon resolves no real
propagation channel, because the placeholder branch of `EmbeddedValues.cs` is
compiled. Other protocols (Tor, WARP, Xray, Sing-box, V2Ray) work normally.

---

## 3. Psiphon configuration (secrets)

### 3.1 How it works

```
Services/EmbeddedValues.cs
    #if SE7EN_SECRETS      -> reads Services/BuildSecrets.g.cs (generated)
    #else                  -> placeholders, compiled when no file is present

Se7enPro.csproj
    exists('Services\BuildSecrets.g.cs')  ->  defines SE7EN_SECRETS

tools/generate_build_secrets.py
    env vars  ->  AES-256-GCM  ->  Services/BuildSecrets.g.cs
```

`BuildSecrets.g.cs` is git-ignored. If it is missing, the build silently uses
placeholders, so a fork can always build.

### 3.2 The variables

Every one is a **plain text value**, not base64 — the injector handles the
encoding.

| Variable | Value |
| :--- | :--- |
| `SE7EN_PROPAGATION_CHANNEL_ID` | Psiphon propagation channel id |
| `SE7EN_SPONSOR_ID` | Psiphon sponsor id |
| `SE7EN_CLIENT_VERSION` | client version string, normally `1` |
| `SE7EN_CLIENT_PLATFORM` | `Windows` |
| `SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY` | base64 DER public key that signs the remote server list |
| `SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY` | base64 DER public key that signs server entries |
| `SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY` | base64 public key for feedback uploads |
| `SE7EN_REMOTE_SERVER_LIST_URLS_JSON` | the `RemoteServerListURLs` JSON array |
| `SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON` | the `ObfuscatedServerListRootURLs` JSON array |
| `SE7EN_FEEDBACK_UPLOAD_URLS_JSON` | the `FeedbackUploadURLs` JSON array |

Optional:

| Variable | Value |
| :--- | :--- |
| `SE7EN_SERVER_ENTRIES` | path to a plaintext `server_entries.txt`; encrypted into `Resources/server_entries.bin` and embedded |

### 3.3 Local build with real values

```powershell
$env:SE7EN_PROPAGATION_CHANNEL_ID    = 'your-channel'
$env:SE7EN_SPONSOR_ID                = 'your-sponsor'
$env:SE7EN_CLIENT_VERSION            = '1'
$env:SE7EN_CLIENT_PLATFORM           = 'Windows'
$env:SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY = 'base64-DER-key'
$env:SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY        = 'base64-DER-key'
$env:SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY           = 'base64-key'
$env:SE7EN_REMOTE_SERVER_LIST_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'
$env:SE7EN_FEEDBACK_UPLOAD_URLS_JSON       = '[{"URL":"...","OnlyAfterAttempts":0,"SkipVerify":false}]'

# optional, ~2 MB file
$env:SE7EN_SERVER_ENTRIES = 'C:\secure\server_entries.txt'

python tools\generate_build_secrets.py
dotnet build Se7enPro\Se7enPro.csproj -c Release -r win-x64
```

Then delete the generated file when you are done:

```powershell
Remove-Item Se7enPro\Services\BuildSecrets.g.cs
Remove-Item Se7enPro\Resources\server_entries.bin
```

Both are git-ignored, but removing them keeps a stray `git add -A` honest.

Check what is present without writing anything:

```powershell
python tools\generate_build_secrets.py --check
```

Exit code `0` = all ten values available, `2` = something is missing.

---

## 4. Local release build

Produces the same artifacts CI does, without publishing.

```powershell
# x64 + x86, self-contained, portable zips and installers
.\tools\build-all.ps1 -Arch all -OutDir dist

# one architecture
.\tools\build-all.ps1 -Arch x64 -OutDir dist

# framework-dependent variants only (smaller, needs .NET 8 Desktop Runtime)
.\tools\build-all.ps1 -Arch all -AllVariants
```

Artifacts land in `dist/`:

```
Se7enPro_v1.0.5_Portable_x64.zip              (self-contained)
Se7enPro_v1.0.5_Portable_x64_without_dotnet.zip
Se7enPro_v1.0.5_Portable_x86.zip
Se7enPro_v1.0.5_Portable_x86_without_dotnet.zip
Se7enPro_v1.0.5_Setup_x64.exe                 (needs Inno Setup)
Se7enPro_v1.0.5_Setup_x86.exe
```

Other flags: `-SkipInstaller`, `-SkipPortable`, `-FrameworkDependent`.

If Inno Setup is not installed the script warns and skips the `.exe`; the
zip archives are still produced.

---

## 5. Publishing a release from GitHub

### 5.1 One-time repository setup

**Add the secrets** — *Settings → Secrets and variables → Actions →
New repository secret*. Add the ten variables from section 3.2. Optionally add:

| Secret | Purpose |
| :--- | :--- |
| `SE7EN_SERVER_ENTRIES_B64` | base64 of a plaintext `server_entries.txt` |

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('server_entries.txt')) |
    Set-Clipboard
```

GitHub caps a secret at 48 KB, and a 2 MB file base64-encodes to ~2.7 MB, so
this one does **not** fit. Options:

- skip it — the client fetches the remote server list on first run instead of
  embedding one, which works but is slower on a cold start;
- or keep the plaintext in a private repo / private release asset and have the
  workflow download it. That needs a token, so it is not wired up here.

**Create the `release` environment** — *Settings → Environments → New
environment*, name it `release`. Add the same secrets to it if you want them
scoped, and turn on **Required reviewers** if you want a manual gate. The
release job references this environment, so it is created automatically on the
first run if you skip this step.

The tag `v*` must point at a commit whose `Se7enPro.csproj` already declares
the same `<Version>`, `<AssemblyVersion>` and `<FileVersion>`. The workflow
refuses to publish otherwise.

### 5.2 Cut a release

```powershell
# 1. bump the version in Se7enPro\Se7enPro.csproj
#    <Version>1.0.5</Version>  <AssemblyVersion>1.0.5.0</AssemblyVersion>  <FileVersion>1.0.5.0</FileVersion>
# 2. write the release notes at the top of CHANGELOG.md
# 3. commit, then tag
git add Se7enPro\Se7enPro.csproj CHANGELOG.md
git commit -m "chore(app): release 1.0.5"
git tag -a v1.0.5 -m "Se7en Pro v1.0.5"
git push origin master --tags
```

Actions → **Release** runs automatically. It:

1. checks out with `persist-credentials: false`;
2. verifies the tag matches the csproj version;
3. installs Python, .NET 8 and Inno Setup;
4. runs `--check` and **fails before building** if any secret is missing;
5. injects the secrets, encrypts the optional server list, and asserts that
   `SE7EN_SECRETS` is actually defined — a release can never silently ship
   placeholders;
6. runs `tools/build-all.ps1 -Arch all`;
7. writes `dist/SHA256SUMS.txt`;
8. opens every produced archive and greps for the plaintext channel id and
   sponsor id — a hit aborts the release;
9. publishes the release with `gh release create --verify-tag --latest`, using
   the built-in `GITHUB_TOKEN`.

Re-running is safe: the workflow group is serialised and `gh release create`
fails rather than overwriting an existing tag.

### 5.3 Manual re-run

*Actions → Release → Run workflow*, then type the tag. The tag must already
exist.

### 5.4 Security properties of this setup

| Concern | How it is handled |
| :--- | :--- |
| Secrets in git | never; values only exist as GitHub secrets and as ciphertext in the built binary |
| Secrets in build logs | passed through `env:` only, never echoed; the `--check` gate prints names, not values |
| Secrets in artifacts | an explicit post-build scan aborts the release on a match |
| Placeholder shipped by accident | the release asserts `SE7EN_SECRETS` is defined before building |
| Over-broad token | `permissions: contents: write` on the publish job only; everything else is `read` |
| Token persistence | `persist-credentials: false` on every checkout |
| Untrusted code | builds run only for tags you push, and pull requests never get secrets |
| Duplicate releases | `concurrency` group serialises, `gh` refuses to reuse a tag |
| Dependency updates | Dependabot watches Actions, NuGet and pip monthly |

Actions are referenced by major-version tag rather than commit SHA. To tighten
further, replace e.g. `actions/checkout@v4` with its pinned SHA and add a
comment with the tag it corresponds to.

### 5.5 Uninstalling the key material

GitHub cannot delete a secret's history; rotating is the only option. If a
secret leaks, change the value on the Psiphon side, update the repository
secret, and publish a new release. Because the shipped binary only holds
AES-GCM ciphertext, a leaked *binary* is not a channel takeover, but a leaked
*secret* is — rotate first.

---

## 6. Repository layout

```
Se7enPro/            the WPF client
  Services/          engines, TUN, settings, secret store
  Services/Tun/      Wintun + tun2socks + DNS forwarder
  ViewModels/        MVVM view models
  Views/             pages and dialogs
  Themes/            Material Design 3 palette and styles
  Resources/         bundled binaries, GeoIP data, flags, fonts
installer/           Inno Setup scripts
tools/
  build-all.ps1              the release pipeline
  publish-portable.ps1       portable archive only
  generate_build_secrets.py  secret injector
  TunEngineTest/             TUN/routing test harness
.github/workflows/   ci.yml, release.yml
CHANGELOG.md         release notes, consumed by the release workflow
```

---

## 7. Troubleshooting

**`Could not find file ... Resources\server_entries.bin`** — expected on a
clean checkout. The csproj only embeds it when it exists. Supply it via
`SE7EN_SERVER_ENTRIES`, or ignore it.

**Psiphon shows no region and never connects, everything else works** — the
build used placeholders. Set the environment variables and re-run
`generate_build_secrets.py`, then rebuild.

**`BuildSecrets.g.cs` is present but the client still shows placeholders** — a
stale `obj` directory. `Remove-Item -Recurse -Force Se7enPro\obj` and rebuild.

**Inno Setup not found** — install Inno Setup 6, or pass `-SkipInstaller`.

**Workflow fails at "SE7EN_SECRETS is not defined"** — the generator did not
write the file, or the path is wrong. Run the generator by hand and check
`Se7enPro\Services\BuildSecrets.g.cs` exists.

**Workflow fails at "LEAK: a secret value appears verbatim"** — something
embedded a secret unencrypted. The release is intentionally blocked; do not
bypass this check.