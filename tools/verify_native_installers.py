"""Exercise the real MusicBoard and DesktopDice single-file installers in owned roots."""
from pathlib import Path
import argparse
import json
import subprocess
import time
import uuid
import winreg
import win32api

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument("--product", choices=["DesktopDice", "MusicBoard"], required=True)
parser.add_argument("--package-version", help="Candidate archive and installer suffix; product version is checked independently")
options = parser.parse_args()
product = options.product
version = "0.2.0" if product == "DesktopDice" else "0.3.0"
package_version = options.package_version or version
repository = root.parent / product
package = repository / "dist" / (product + "-" + package_version + "-windows-x64")
installer = repository / "dist" / (product + "-" + package_version + "-Setup-windows-x64.exe")
output = root / "artifacts/native-installer-tests" / (product + "-" + uuid.uuid4().hex)
output.mkdir(parents=True)
installed = output / "安装目录"
checks = {}
if product == "DesktopDice":
    shortcut = output / "shortcut.lnk"
    registry = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopDice.Tests.SingleFile." + uuid.uuid4().hex
    args = ["--quiet", "--install-root", str(installed), "--evidence-root", str(output), "--shortcut-path", str(shortcut), "--registry-subkey", registry]
else:
    registry = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\MusicBoard"
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, registry):
            raise RuntimeError("Existing MusicBoard registration must be preserved; do not run this clean-registration test")
    except FileNotFoundError:
        pass
    args = ["--quiet", "--install-root", str(installed)]
(output / "test-scope.json").write_text(json.dumps({"product": product, "registry": registry, "install_root": str(installed)}), encoding="utf-8")
result = subprocess.run([str(installer), *args], capture_output=True, timeout=120)
main = installed / ("DesktopDice.exe" if product == "DesktopDice" else "current/MusicBoard.exe")
updater = main.parent / "FullToolsUpdater.exe"
checks["SingleFileInstallsFullProduct"] = result.returncode == 0 and main.is_file()
checks["UpdaterInstalledBesideMainExecutable"] = updater.is_file()
if not checks["SingleFileInstallsFullProduct"]:
    report = {"Passed": False, "Product": product, "Checks": checks, "InstallerExitCode": result.returncode,
        "Evidence": str(output)}
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report), flush=True)
    raise RuntimeError("Single-file installation failed before registration")
with winreg.OpenKey(winreg.HKEY_CURRENT_USER, registry) as key:
    checks["VersionRegistered"] = winreg.QueryValueEx(key, "DisplayVersion")[0] == package_version
if product == "DesktopDice":
    info = win32api.GetFileVersionInfo(str(main), "\\")
    actual = ".".join(str(number) for number in (info["FileVersionMS"] >> 16, info["FileVersionMS"] & 65535, info["FileVersionLS"] >> 16))
    checks["InstalledExecutableVersionInfoMatches"] = actual == version
else:
    version_result = subprocess.run([str(main), "--version"], capture_output=True, timeout=15)
    checks["InstalledExecutableReportsCorrectVersion"] = version_result.returncode == 0 and version.encode() in version_result.stdout
protected = output / "user-document.txt"
protected.write_text("keep user data", encoding="utf-8")
if product == "DesktopDice":
    uninstaller = output / "app-data/installer/DesktopDiceSetup.exe"
    command = [str(uninstaller), "--diagnostic-uninstall", "--quiet", "--package-root=" + str(package),
        "--install-root=" + str(installed), "--evidence-root=" + str(output), "--shortcut-path=" + str(shortcut), "--registry-subkey=" + registry]
else:
    uninstaller = installed / "current/MusicBoardSetup.exe"
    command = [str(uninstaller), "--uninstall", "--quiet", "--install-root", str(installed)]
subprocess.run(command, timeout=35)
deadline = time.monotonic() + 35
while installed.exists() and time.monotonic() < deadline: time.sleep(0.1)
checks["InstalledUninstallerRemovesProduct"] = not installed.exists()
try:
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, registry): checks["RegistrationRemoved"] = False
except FileNotFoundError:
    checks["RegistrationRemoved"] = True
checks["UserDocumentPreserved"] = protected.read_text(encoding="utf-8") == "keep user data"
report = {"Passed": all(checks.values()), "Product": product, "Checks": checks}
(output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report), flush=True)
if not report["Passed"]: raise RuntimeError("Native single-file installer workflow failed")
