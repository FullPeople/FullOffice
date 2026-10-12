"""Build a single-file Windows installer using the maintained native setup backend."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--product", required=True, choices=["FullOffice", "DesktopDice", "MusicBoard"])
parser.add_argument("--version", required=True)
parser.add_argument("--archive", type=Path, required=True)
parser.add_argument("--icon", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
if not args.archive.is_file() or not args.icon.is_file(): parser.error("Archive and icon must exist")
build = root / ".cache" / (args.product.lower() + "-single-installer")
backend = args.product + "Setup.exe"
subprocess.run(["cmake", "-S", str(root / "src/FullTools.Distribution"), "-B", str(build),
    "-DPAYLOAD_ZIP=" + args.archive.resolve().as_posix(), "-DPRODUCT_ID=" + args.product,
    "-DPRODUCT_TITLE=" + args.product, "-DBACKEND_NAME=" + backend,
    "-DBACKEND_USES_EQUALS=" + ("true" if args.product == "DesktopDice" else "false"),
    "-DPRODUCT_ICON=" + args.icon.resolve().as_posix()], check=True)
subprocess.run(["cmake", "--build", str(build), "--config", "Release", "--target", "FullToolsInstaller", "--parallel", "2"], check=True)
args.output.parent.mkdir(parents=True, exist_ok=True)
shutil.copy2(build / "Release/FullToolsInstaller.exe", args.output)
report = {"product": args.product, "version": args.version, "installer": str(args.output), "bytes": args.output.stat().st_size,
    "sha256": hashlib.file_digest(args.output.open("rb"), "sha256").hexdigest(), "platform": "windows-x64"}
args.output.with_suffix(".json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report), flush=True)
