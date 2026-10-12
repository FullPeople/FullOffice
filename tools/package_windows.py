from pathlib import Path
import hashlib
import json
import shutil
import zipfile
import sys

root = Path(__file__).resolve().parents[1]
version = "0.2.0"
output = root / "artifacts/packages" / ("FullOffice-" + version + "-windows-x64")
if output.exists() and not any(flag in sys.argv for flag in ("--refresh-backend", "--refresh-app")):
    raise RuntimeError("Package already exists; choose a new candidate directory")
source = root / "artifacts/self-contained"
required = ["FullOffice.exe", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll", "PresentationFramework.dll", "pdfium.dll"]
for name in required:
    if not (source / name).is_file():
        raise RuntimeError("Self-contained runtime component is missing: " + name)
output.parent.mkdir(parents=True, exist_ok=True)
if not output.exists():
    shutil.copytree(source, output)
elif "--refresh-app" in sys.argv:
    shutil.copytree(source, output, dirs_exist_ok=True)
shutil.copy2(root / ".cache/distribution-build/Release/FullOfficeSetup.exe", output / "FullOfficeSetup.exe")
shutil.copy2(root / ".cache/distribution-build/Release/FullToolsUpdater.exe", output / "FullToolsUpdater.exe")
shutil.copy2(root / "LICENSE", output / "LICENSE.txt")
shutil.copy2(root / "README.md", output / "README.md")
shutil.copytree(root / "licenses", output / "licenses", dirs_exist_ok=True)
runtime_config = json.loads((source / "FullOffice.runtimeconfig.json").read_text(encoding="utf-8"))
assets_path = root / "src/FullOffice.Windows/.cache/obj/project.assets.json"
if not assets_path.is_file():
    assets_path = root / "src/FullOffice.Windows/obj/project.assets.json"
assets = json.loads(assets_path.read_text(encoding="utf-8"))
package_roots = [Path(path) for path in assets["packageFolders"]]
runtime_version = next(framework["version"] for framework in runtime_config["runtimeOptions"]["includedFrameworks"]
    if framework["name"] == "Microsoft.NETCore.App")
for package, filename, target in [
    ("microsoft.netcore.app.runtime.win-x64", "LICENSE.TXT", "dotnet-runtime-LICENSE.txt"),
    ("microsoft.netcore.app.runtime.win-x64", "THIRD-PARTY-NOTICES.TXT", "dotnet-runtime-THIRD-PARTY-NOTICES.txt"),
    ("microsoft.windowsdesktop.app.runtime.win-x64", "LICENSE", "dotnet-windows-desktop-LICENSE.txt"),
]:
    license_file = next((folder / package / runtime_version / filename for folder in package_roots
        if (folder / package / runtime_version / filename).is_file()), None)
    if license_file is None:
        raise RuntimeError("Bundled runtime license is missing: " + package + "/" + filename)
    shutil.copy2(license_file, output / "licenses" / target)
(output / "VERSION.txt").write_text("product=FullOffice\nversion=" + version + "\npackage_version=" + version +
    "\narchitecture=windows-x64\nexecutable=FullOffice.exe\ncomplete=1\n", encoding="utf-8")
files = sorted(p for p in output.rglob("*") if p.is_file() and p.name != "SHA256SUMS.txt")
(output / "SHA256SUMS.txt").write_text("\n".join(hashlib.file_digest(p.open("rb"), "sha256").hexdigest() + "  " +
    p.relative_to(output).as_posix() for p in files) + "\n", encoding="utf-8")
archive_path = output.parent / (output.name + ".zip")
with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for file in output.rglob("*"):
        if file.is_file():
            archive.write(file, file.relative_to(output).as_posix())
report = {"product": "FullOffice", "version": version, "architecture": "windows-x64",
    "files": len(files) + 1, "runtime": "self-contained .NET " + runtime_version + " Windows Desktop", "archive": str(archive_path),
    "bytes": archive_path.stat().st_size, "sha256": hashlib.file_digest(archive_path.open("rb"), "sha256").hexdigest()}
(output.parent / "FullOffice-0.2.0-package.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report), flush=True)
