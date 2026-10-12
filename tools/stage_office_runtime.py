"""Stage an independent, pinned Office runtime for local release validation."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
from build_office_host import CORE_SHA, ROOT, visual_cpp_tools

def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("engine", type=Path)
    parser.add_argument("app", type=Path, help="An existing task-owned published app under artifacts")
    args = parser.parse_args()
    engine, app = args.engine.resolve(), args.app.resolve()
    if not app.is_relative_to((ROOT / "artifacts").resolve()) or not (app / "FullOffice.exe").is_file():
        raise RuntimeError("Destination must be an existing task-owned FullOffice artifact")
    if sha(engine / "ascdocumentscore.dll") != CORE_SHA:
        raise RuntimeError("Runtime core does not match the tested ABI")
    destination = app / "office-engine"
    if destination.exists():
        raise RuntimeError("Office runtime already exists; use a fresh app candidate")
    bridge = ROOT / ".cache/office-native-host/Release/FullOfficeOfficeHost.exe"
    if not bridge.is_file():
        raise RuntimeError("Build the native Office host first")
    folders = ["converter", "editors", "fonts", "dictionaries", "locales"]
    files = [file for name in folders for file in (engine / name).rglob("*") if file.is_file()]
    if any(file.is_symlink() or file.is_junction() for file in files):
        raise RuntimeError("Linked runtime resources are not supported")
    required_bytes = sum(file.stat().st_size for file in files) + 512 * 1024 * 1024
    if shutil.disk_usage(app).free < required_bytes + 1024 * 1024 * 1024:
        raise RuntimeError("Insufficient staging headroom")
    destination.mkdir()
    dumpbin = visual_cpp_tools() / "dumpbin.exe"
    pending = [engine / "ascdocumentscore.dll", engine / "editors_helper.exe"]
    seen = set()
    while pending:
        source = pending.pop()
        if source.name.lower() in seen:
            continue
        seen.add(source.name.lower())
        shutil.copy2(source, destination / source.name)
        dependencies = subprocess.check_output([str(dumpbin), "/dependents", str(source)], text=True)
        for name in re.findall(r"^\s+([A-Za-z0-9._-]+\.dll)\s*$", dependencies, re.MULTILINE):
            match = next((folder / name for folder in (engine, engine / "converter") if (folder / name).is_file()), None)
            if match is not None:
                pending.append(match)
    for name in ["chrome_100_percent.pak", "chrome_200_percent.pak", "icudtl.dat", "resources.pak",
                 "snapshot_blob.bin", "v8_context_snapshot.bin", "LICENSE.txt"]:
        shutil.copy2(engine / name, destination / name)
    for name in folders:
        shutil.copytree(engine / name, destination / name)
    for name in ["libEGL.dll", "libGLESv2.dll", "d3dcompiler_47.dll", "vk_swiftshader.dll", "vk_swiftshader_icd.json", "vulkan-1.dll"]:
        if (engine / name).is_file():
            shutil.copy2(engine / name, destination / name)
    shutil.copy2(bridge, destination / bridge.name)
    records = [{"Path": path.relative_to(destination).as_posix(), "Bytes": path.stat().st_size, "SHA256": sha(path)}
        for path in sorted(destination.rglob("*")) if path.is_file()]
    manifest = {"Version": "9.4.0.129", "CoreSHA256": CORE_SHA,
        "SDKRevision": "6bd2051287a496ac1504214caca69a5729625696", "CoreRevision": "b18715ae811aaa5cd9dae2dd9a4b9412d38b8b7b", "Files": records}
    (destination / "runtime-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"Files": len(records), "Bytes": sum(record["Bytes"] for record in records), "Destination": str(destination)}))

if __name__ == "__main__":
    main()
