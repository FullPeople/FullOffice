"""Package the self-contained app and the verified optional Office runtime."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import zipfile
import win32api

ROOT = Path(__file__).resolve().parents[1]
CORE_SHA = "3d00c5773094ffec137a7b15e862319f211c952786ab7aaac1c3f2a6ab3d3415"


def sha(path):
    with path.open("rb") as data:
        return hashlib.file_digest(data, "sha256").hexdigest()


def reject_links(directory):
    for path in [directory, *directory.rglob("*")]:
        if path.is_symlink() or path.is_junction():
            raise RuntimeError("Linked package input refused: " + str(path))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", default="0.2.0")
    parser.add_argument("--source", type=Path, default=ROOT / "artifacts/self-contained")
    parser.add_argument("--include-office", action="store_true")
    parser.add_argument("--assets-file", type=Path, default=ROOT / ".cache/obj/project.assets.json")
    parser.add_argument("--refresh-backend", action="store_true")
    parser.add_argument("--refresh-app", action="store_true")
    args = parser.parse_args()
    if not re.fullmatch(r"\d+\.\d+\.\d+", args.version):
        parser.error("Product version must contain three numeric components")
    source = args.source.resolve(strict=True)
    if not source.is_relative_to((ROOT / "artifacts").resolve()):
        raise RuntimeError("Package source must stay inside the owned artifact directory")
    reject_links(source)
    for name in ("FullOffice.exe", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "PresentationFramework.dll", "pdfium.dll"):
        if not (source / name).is_file():
            raise RuntimeError("Self-contained component missing: " + name)
    info = win32api.GetFileVersionInfo(str(source / "FullOffice.exe"), "\\")
    actual_version = ".".join(str(number) for number in (info["FileVersionMS"] >> 16, info["FileVersionMS"] & 65535, info["FileVersionLS"] >> 16))
    if actual_version != args.version:
        raise RuntimeError("Application and package versions differ: " + actual_version)
    runtime_config = json.loads((source / "FullOffice.runtimeconfig.json").read_text(encoding="utf-8"))
    runtime_version = next(item["version"] for item in runtime_config["runtimeOptions"]["includedFrameworks"] if item["name"] == "Microsoft.NETCore.App")
    office_manifest = None
    if args.include_office:
        engine = source / "office-engine"
        if sha(engine / "ascdocumentscore.dll") != CORE_SHA or not (engine / "FullOfficeOfficeHost.exe").is_file():
            raise RuntimeError("Native Office runtime is missing or has a different ABI")
        office_manifest = json.loads((engine / "runtime-manifest.json").read_text(encoding="utf-8"))
        expected = {item["Path"] for item in office_manifest["Files"]}
        actual = {path.relative_to(engine).as_posix() for path in engine.rglob("*") if path.is_file()}
        if actual != expected | {"runtime-manifest.json"}:
            raise RuntimeError("Unrecorded or missing Office runtime files")
        for item in office_manifest["Files"]:
            path = engine / item["Path"]
            if path.stat().st_size != item["Bytes"] or sha(path).lower() != item["SHA256"].lower():
                raise RuntimeError("Office runtime resource mismatch: " + item["Path"])
        print(json.dumps({"VerifiedOfficeFiles": len(expected), "OfficeVersion": office_manifest["Version"]}), flush=True)
    elif (source / "office-engine").exists():
        raise RuntimeError("Office runtime found; use --include-office to verify it explicitly")
    output = ROOT / "artifacts/packages" / f"FullOffice-{args.version}-windows-x64"
    if output.exists() and not (args.refresh_app or args.refresh_backend):
        raise RuntimeError("Candidate already exists; preserve it or select a new version")
    output.parent.mkdir(parents=True, exist_ok=True)
    if not output.exists():
        source_bytes = sum(path.stat().st_size for path in source.rglob("*") if path.is_file())
        if shutil.disk_usage(output.parent).free < source_bytes + 2 * 1024**3:
            raise RuntimeError("Insufficient package and installation headroom")
        shutil.copytree(source, output)
    else:
        reject_links(output)
        if args.refresh_app:
            shutil.copytree(source, output, dirs_exist_ok=True)
    shutil.copy2(ROOT / ".cache/distribution-build/Release/FullOfficeSetup.exe", output / "FullOfficeSetup.exe")
    shutil.copy2(ROOT / ".cache/distribution-build/Release/FullToolsUpdater.exe", output / "FullToolsUpdater.exe")
    shutil.copy2(ROOT / "LICENSE", output / "LICENSE.txt")
    shutil.copy2(ROOT / "README.md", output / "README.md")
    shutil.copytree(ROOT / "licenses", output / "licenses", dirs_exist_ok=True)
    assets = json.loads(args.assets_file.read_text(encoding="utf-8"))
    package_roots = [Path(path) for path in assets["packageFolders"]]
    for package, filename, target in (
        ("microsoft.netcore.app.runtime.win-x64", "LICENSE.TXT", "dotnet-runtime-LICENSE.txt"),
        ("microsoft.netcore.app.runtime.win-x64", "THIRD-PARTY-NOTICES.TXT", "dotnet-runtime-THIRD-PARTY-NOTICES.txt"),
        ("microsoft.windowsdesktop.app.runtime.win-x64", "LICENSE", "dotnet-windows-desktop-LICENSE.txt"),
    ):
        license_file = next((folder / package / runtime_version / filename for folder in package_roots if (folder / package / runtime_version / filename).is_file()), None)
        if license_file is None:
            raise RuntimeError("Missing runtime license: " + package + "/" + filename)
        shutil.copy2(license_file, output / "licenses" / target)
    (output / "VERSION.txt").write_text("product=FullOffice\nversion=" + args.version + "\npackage_version=" + args.version + "\narchitecture=windows-x64\nexecutable=FullOffice.exe\ncomplete=1\n", encoding="utf-8")
    files = sorted(path for path in output.rglob("*") if path.is_file() and path.name != "SHA256SUMS.txt")
    (output / "SHA256SUMS.txt").write_text("\n".join(sha(path) + "  " + path.relative_to(output).as_posix() for path in files) + "\n", encoding="utf-8")
    archive_path = output.parent / (output.name + ".zip")
    with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in files + [output / "SHA256SUMS.txt"]:
            archive.write(path, path.relative_to(output).as_posix())
    report = {"product": "FullOffice", "version": args.version, "architecture": "windows-x64", "files": len(files) + 1,
              "runtime": "self-contained .NET " + runtime_version + " Windows Desktop", "office": office_manifest is not None,
              "officeVersion": office_manifest["Version"] if office_manifest else None, "archive": str(archive_path), "bytes": archive_path.stat().st_size, "sha256": sha(archive_path)}
    (output.parent / f"FullOffice-{args.version}-package.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
