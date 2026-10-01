#!/usr/bin/env python3
"""
Build-time secret injector for Se7en Pro.

Reads the Psiphon embedded values from environment variables, encrypts each one
with AES-256-GCM under the key that Services/SecretStore.cs reconstructs at
runtime, and writes the ciphertext to:

    Se7enPro/Services/BuildSecrets.g.cs

That file is git-ignored and is picked up by the csproj, which also defines
SE7EN_SECRETS so Services/EmbeddedValues.cs compiles the injected branch
instead of the placeholder branch. A checkout without this file still builds.

Required environment variables (all optional -- a missing one becomes a
placeholder, so a fork can build and CI can compile-check without secrets):

    SE7EN_PROPAGATION_CHANNEL_ID
    SE7EN_SPONSOR_ID
    SE7EN_CLIENT_VERSION
    SE7EN_CLIENT_PLATFORM
    SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY
    SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY
    SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY
    SE7EN_REMOTE_SERVER_LIST_URLS_JSON
    SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON
    SE7EN_FEEDBACK_UPLOAD_URLS_JSON

Optional:

    SE7EN_SERVER_ENTRIES   path to a plaintext server_entries.txt; when set it
                           is encrypted into Resources/server_entries.bin

Usage:

    python tools/generate_build_secrets.py
    python tools/generate_build_secrets.py --check

--check writes nothing and fails with exit code 2 if any value is missing, so
it can be used as a release gate.

Dependencies: cryptography (pip install cryptography)
"""

import argparse
import hashlib
import hmac
import os
import secrets
import sys
from pathlib import Path

try:
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
except ImportError:
    raise SystemExit("Please `pip install cryptography` first")

ROOT = Path(__file__).resolve().parent.parent
PROJ = ROOT / "Se7enPro"
OUT_CS = PROJ / "Services" / "BuildSecrets.g.cs"
OUT_BIN = PROJ / "Resources" / "server_entries.bin"

# Order must match EmbeddedValues.cs.
VALUES = [
    "PropagationChannelId",
    "SponsorId",
    "ClientVersion",
    "ClientPlatform",
    "RemoteServerListSignaturePublicKey",
    "ServerEntrySignaturePublicKey",
    "FeedbackEncryptionPublicKey",
    "RemoteServerListUrlsJson",
    "ObfuscatedServerListRootUrlsJson",
    "FeedbackUploadUrlsJson",
]

ENV = {
    "PropagationChannelId": "SE7EN_PROPAGATION_CHANNEL_ID",
    "SponsorId": "SE7EN_SPONSOR_ID",
    "ClientVersion": "SE7EN_CLIENT_VERSION",
    "ClientPlatform": "SE7EN_CLIENT_PLATFORM",
    "RemoteServerListSignaturePublicKey": "SE7EN_REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY",
    "ServerEntrySignaturePublicKey": "SE7EN_SERVER_ENTRY_SIGNATURE_PUBLIC_KEY",
    "FeedbackEncryptionPublicKey": "SE7EN_FEEDBACK_ENCRYPTION_PUBLIC_KEY",
    "RemoteServerListUrlsJson": "SE7EN_REMOTE_SERVER_LIST_URLS_JSON",
    "ObfuscatedServerListRootUrlsJson": "SE7EN_OBFUSCATED_SERVER_LIST_ROOT_URLS_JSON",
    "FeedbackUploadUrlsJson": "SE7EN_FEEDBACK_UPLOAD_URLS_JSON",
}

# The three key shares committed in Services/SecretStore.cs. They are
# obfuscation material, not credentials: the shipped binary has to carry them
# to decrypt anything at all, so publishing them adds no exposure. Only the
# values below are secret, and only the ciphertext is ever committed or built.
ALPHA = bytes([
    0x16, 0xF7, 0xE3, 0x02, 0xBD, 0x9F, 0x80, 0xFB, 0x15, 0x35, 0x4E, 0x05,
    0x7D, 0x29, 0x33, 0x26, 0xB0, 0x2C, 0x4A, 0xCF, 0xE6, 0xB9, 0x2E, 0xE7,
    0x62, 0x20, 0x08, 0xB1, 0xC2, 0x39, 0x74, 0x10,
])
BETA = bytes([
    0xB3, 0xA3, 0x1F, 0xED, 0x26, 0x5F, 0x2F, 0x64, 0xC5, 0x09, 0x56, 0x7A,
    0xB2, 0xFA, 0x45, 0x6D, 0x8F, 0x98, 0xD2, 0x40, 0xD6, 0xBD, 0x41, 0x8E,
    0xF4, 0x9C, 0x9F, 0x73, 0x0A, 0xCC, 0x9D, 0x84,
])
GAMMA = bytes([
    0x6A, 0x63, 0x7D, 0x72, 0x2D, 0xAD, 0xDB, 0x21, 0x8B, 0x07, 0xCA, 0x6B,
    0x00, 0xA8, 0x8F, 0xB9, 0x5B, 0xE8, 0x57, 0x70, 0x08, 0x99, 0xE2, 0x9A,
    0x29, 0x26, 0x82, 0x0E, 0xB8, 0x8B, 0x7E, 0x4E,
])


def derive_key(alpha: bytes, beta: bytes, gamma: bytes) -> bytes:
    salt = bytes(a ^ b for a, b in zip(alpha, beta))
    return hmac.new(salt, gamma, hashlib.sha256).digest()


def encrypt(key: bytes, plaintext: bytes) -> bytes:
    nonce = secrets.token_bytes(12)
    return nonce + AESGCM(key).encrypt(nonce, plaintext, None)


def fmt_bytes(name: str, data: bytes) -> str:
    lines = []
    for i in range(0, len(data), 16):
        chunk = data[i:i + 16]
        lines.append("        " + ", ".join(f"0x{b:02X}" for b in chunk))
    body = ",\n".join(lines)
    return f"    internal static byte[] {name} => new byte[]\n    {{\n{body}\n    }};"


def read_values() -> tuple[dict[str, str], list[str]]:
    values: dict[str, str] = {}
    missing: list[str] = []
    for name in VALUES:
        raw = os.environ.get(ENV[name], "")
        raw = raw.strip()
        if not raw:
            missing.append(ENV[name])
            continue
        values[name] = raw
    return values, missing


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true",
                    help="verify all secrets are present without writing files")
    args = ap.parse_args()

    values, missing = read_values()

    if args.check:
        if missing:
            print("Missing required environment variables:", file=sys.stderr)
            for m in missing:
                print(f"  - {m}", file=sys.stderr)
            return 2
        print("All build secrets present.")
        return 0

    key = derive_key(ALPHA, BETA, GAMMA)

    fields = []
    for name in VALUES:
        if name in values:
            blob = encrypt(key, values[name].encode("utf-8"))
        else:
            blob = encrypt(key, name.encode("utf-8"))
        fields.append(fmt_bytes(name, blob))

    body = "\n\n".join(fields)

    OUT_CS.parent.mkdir(parents=True, exist_ok=True)
    OUT_CS.write_text(
        "// <auto-generated>Generated by tools/generate_build_secrets.py. Do not edit.\n"
        "// Never commit this file. It holds the build-time injected Psiphon\n"
        "// configuration in encrypted form.</auto-generated>\n"
        "\n"
        "namespace Se7enPro.Services;\n"
        "\n"
        "internal static class BuildSecrets\n"
        "{\n"
        f"{body}\n"
        "}\n",
        encoding="utf-8",
    )
    print(f"Wrote {OUT_CS}")

    if missing:
        print("Injected placeholders for: " + ", ".join(missing), file=sys.stderr)
        print("The build will succeed but Psiphon will not resolve a real channel.",
              file=sys.stderr)

    entries = os.environ.get("SE7EN_SERVER_ENTRIES", "").strip()
    if entries:
        src = Path(entries)
        if not src.is_file():
            print(f"SE7EN_SERVER_ENTRIES not found: {src}", file=sys.stderr)
            return 1
        blob = encrypt(key, src.read_bytes())
        OUT_BIN.parent.mkdir(parents=True, exist_ok=True)
        OUT_BIN.write_bytes(blob)
        print(f"Wrote {OUT_BIN} ({len(blob)} bytes)")
    elif OUT_BIN.exists():
        print(f"{OUT_BIN} exists and will be embedded as-is.")

    aes = AESGCM(key)
    print("Round-trip verification:")
    ok = True
    for name in VALUES:
        expected = values.get(name, name)
        blob = encrypt(key, expected.encode("utf-8"))
        got = aes.decrypt(blob[:12], blob[12:], None).decode("utf-8")
        if got != expected:
            ok = False
            print(f"  {name}: MISMATCH")
    print("  all values decrypt correctly" if ok else "  FAILED")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())