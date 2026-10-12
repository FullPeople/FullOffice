"""Create authenticated update manifests; private signing material stays outside repositories."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import struct
import win32crypt
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

root = Path(__file__).resolve().parents[1]
private_root = root.parent / ".release-signing"
private_root.mkdir(exist_ok=True)
key_path = private_root / "fulltools-update-rsa.dpapi"
if key_path.exists():
    pem = win32crypt.CryptUnprotectData(key_path.read_bytes(), None, None, None, 0)[1]
    key = serialization.load_pem_private_key(pem, password=None)
else:
    if (root / "src/FullTools.Distribution/UpdatePublicKey.h").exists():
        raise RuntimeError("Publisher private key is missing. Restore it securely; never replace an existing public verification key.")
    key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
    pem = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
    key_path.write_bytes(win32crypt.CryptProtectData(pem, "FullTools release signing", None, None, None, 0))
public = key.public_key().public_numbers()
exponent = public.e.to_bytes((public.e.bit_length() + 7) // 8, "big")
modulus = public.n.to_bytes((public.n.bit_length() + 7) // 8, "big")
blob = struct.pack("<6I", 0x31415352, key.key_size, len(exponent), len(modulus), 0, 0) + exponent + modulus
public_folder = root / "src/FullTools.Distribution"
(public_folder / "UpdatePublicKey.h").write_text("#pragma once\n#include <cstdint>\ninline constexpr std::uint8_t update_public_key[] = {" +
    ",".join(str(b) for b in blob) + "};\n", encoding="utf-8")
parser = argparse.ArgumentParser()
parser.add_argument("--product", choices=["FullOffice", "DesktopDice", "MusicBoard"])
parser.add_argument("--version")
parser.add_argument("--installer", type=Path)
parser.add_argument("--url")
parser.add_argument("--output", type=Path)
args = parser.parse_args()
if args.product:
    if not args.version or not args.installer or not args.url or not args.output:
        parser.error("All manifest fields are required")
    if "\n" in args.version or "\n" in args.url or not args.url.startswith("https://"):
        parser.error("Invalid update fields")
    body = ("product=" + args.product + "\nversion=" + args.version + "\narchitecture=windows-x64\nurl=" + args.url +
        "\nsha256=" + hashlib.file_digest(args.installer.open("rb"), "sha256").hexdigest() +
        "\nbytes=" + str(args.installer.stat().st_size) + "\n").encode("utf-8")
    signature = key.sign(body, padding.PSS(mgf=padding.MGF1(hashes.SHA256()), salt_length=32), hashes.SHA256())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(b"FULLTOOLS-UPDATE-1\n" + base64.b64encode(body) + b"\n" + base64.b64encode(signature) + b"\n")
    print(json.dumps({"product": args.product, "version": args.version, "output": str(args.output), "bytes": args.installer.stat().st_size}))
else:
    print("Public update verification key prepared; private key protected with Windows DPAPI")
