"""Create authenticated update manifests; private signing material stays outside repositories."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import struct
from urllib.parse import urlsplit
import win32crypt
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--product", choices=["FullOffice", "DesktopDice", "MusicBoard"])
parser.add_argument("--version")
parser.add_argument("--architecture", choices=["windows-x64", "macos-arm64", "macos-x64"], default="windows-x64")
parser.add_argument("--installer", type=Path)
parser.add_argument("--url")
parser.add_argument("--output", type=Path)
parser.add_argument("--key-file", type=Path, help="Existing DPAPI key outside the repository; never created or replaced")
args = parser.parse_args()
if args.product:
    if not args.version or not args.installer or not args.url or not args.output:
        parser.error("All manifest fields are required")
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version) or any(int(x) > 65535 for x in args.version.split(".")):
        parser.error("Invalid version")
    url = urlsplit(args.url)
    if url.scheme != "https" or not url.hostname or url.username or url.password or url.fragment or any(c in args.url for c in "\r\n"):
        parser.error("Invalid update URL")
    suffix = ".exe" if args.architecture == "windows-x64" else ".pkg"
    if not url.path.lower().endswith(suffix) or args.installer.suffix.lower() != suffix:
        parser.error("Installer format does not match the target platform")
    if not args.installer.is_file() or args.installer.is_symlink() or not 0 < args.installer.stat().st_size <= 4 * 1024**3:
        parser.error("Invalid installer")
key_path = args.key_file or root.parent / ".release-signing/fulltools-update-rsa.dpapi"
if args.key_file and (not key_path.is_file() or key_path.resolve().is_relative_to(root.resolve())):
    parser.error("Explicit signing key must already exist outside the repository")
if key_path.exists():
    pem = win32crypt.CryptUnprotectData(key_path.read_bytes(), None, None, None, 0)[1]
    key = serialization.load_pem_private_key(pem, password=None)
    del pem
else:
    if (root / "src/FullTools.Distribution/UpdatePublicKey.h").exists():
        raise RuntimeError("Publisher private key is missing. Restore it securely; never replace an existing public verification key.")
    key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    key_path.parent.mkdir(exist_ok=True)
    key_path.write_bytes(win32crypt.CryptProtectData(pem, "FullTools release signing", None, None, None, 0))
    del pem
if not isinstance(key, rsa.RSAPrivateKey) or key.key_size != 3072:
    raise RuntimeError("Unexpected publisher key")
public = key.public_key().public_numbers()
exponent = public.e.to_bytes((public.e.bit_length() + 7) // 8, "big")
modulus = public.n.to_bytes((public.n.bit_length() + 7) // 8, "big")
blob = struct.pack("<6I", 0x31415352, key.key_size, len(exponent), len(modulus), 0, 0) + exponent + modulus
public_folder = root / "src/FullTools.Distribution"
public_header = public_folder / "UpdatePublicKey.h"
if public_header.exists():
    match = re.search(r"update_public_key\[\]\s*=\s*\{([^}]+)\}", public_header.read_text(encoding="utf-8"))
    if not match or bytes(int(b) for b in match[1].split(",")) != blob:
        raise RuntimeError("Signing key does not match the existing public verification key")
else:
    public_folder.mkdir(parents=True, exist_ok=True)
    public_header.write_text("#pragma once\n#include <cstdint>\ninline constexpr std::uint8_t update_public_key[] = {" +
        ",".join(str(b) for b in blob) + "};\n", encoding="utf-8")
if args.product:
    with args.installer.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    body = ("product=" + args.product + "\nversion=" + args.version + "\narchitecture=" + args.architecture + "\nurl=" + args.url +
        "\nsha256=" + digest +
        "\nbytes=" + str(args.installer.stat().st_size) + "\n").encode("utf-8")
    signature = key.sign(body, padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=32), hashes.SHA256())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(b"FULLTOOLS-UPDATE-1\n" + base64.b64encode(body) + b"\n" + base64.b64encode(signature) + b"\n")
    print(json.dumps({"product": args.product, "version": args.version, "architecture": args.architecture, "output": str(args.output), "bytes": args.installer.stat().st_size}))
else:
    print("Public update verification key prepared; private key protected with Windows DPAPI")
