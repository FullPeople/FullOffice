from pathlib import Path
import argparse
import ctypes
import json
import os
import subprocess
import time
import uuid
import winreg

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/installer-tests" / uuid.uuid4().hex
output.mkdir(parents=True)
installed = output / "安装程序"
shortcut = output / "FullOffice-test.lnk"
identity = "FullOffice-InstallerTest-" + uuid.uuid4().hex
parser = argparse.ArgumentParser()
parser.add_argument("--installer", type=Path, default=root / ".cache/full-office-installer/Release/FullToolsInstaller.exe")
installer = parser.parse_args().installer.resolve()
checks = {}
result = subprocess.run([str(installer), "--quiet", "--install-root", str(installed), "--shortcut-path", str(shortcut),
    "--registry-subkey", identity], timeout=90)
checks["SingleExecutableInstallsCompletePackage"] = result.returncode == 0 and (installed / "current/FullOffice.exe").exists()
checks["RuntimeIncluded"] = (installed / "current/coreclr.dll").exists() and (installed / "current/PresentationFramework.dll").exists()
probe_folder = output / "text-check"
env = dict(os.environ, DOTNET_ROOT=str(output / "no-system-runtime"))
app = subprocess.Popen([str(installed / "current/FullOffice.exe"), "--text-probe", str(root / "tests/fixtures/feature-fixture.pdf"), str(probe_folder)],
    env=env, creationflags=subprocess.CREATE_NO_WINDOW)
app.wait(timeout=55)
report = json.loads((probe_folder / "text-probe.json").read_text(encoding="utf-8"))
checks["InstalledSelfContainedApplicationWorks"] = app.returncode == 0 and report.get("Passed") is True
protected = output / "user-document.txt"
protected.write_text("keep user document", encoding="utf-8")
before = set(Path(os.environ["TEMP"]).glob("FullOffice-Uninstall-*.exe"))
uninstaller = installed / "current/FullOfficeSetup.exe"
process = subprocess.Popen([str(uninstaller), "--uninstall", "--quiet", "--install-root", str(installed),
    "--shortcut-path", str(shortcut), "--registry-subkey", identity], creationflags=subprocess.CREATE_NO_WINDOW)
process.wait(timeout=20)
deadline = time.monotonic() + 35
while installed.exists() and time.monotonic() < deadline:
    time.sleep(0.1)
checks["InstalledUninstallerRemovesApp"] = not installed.exists() and not shortcut.exists()
deadline = time.monotonic() + 15
while set(Path(os.environ["TEMP"]).glob("FullOffice-Uninstall-*.exe")) - before and time.monotonic() < deadline:
    time.sleep(0.1)
checks["TemporaryUninstallerCleaned"] = not (set(Path(os.environ["TEMP"]).glob("FullOffice-Uninstall-*.exe")) - before)
checks["UserDocumentPreserved"] = protected.read_text(encoding="utf-8") == "keep user document"
try:
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Software\\RegisteredApplications") as registry:
        winreg.QueryValueEx(registry, identity)
        checks["DefaultApplicationRegistrationRemoved"] = False
except FileNotFoundError:
    checks["DefaultApplicationRegistrationRemoved"] = True
result = {"Passed": all(checks.values()), "Checks": checks, "TextProbe": report}
(output / "report.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result), flush=True)
if not result["Passed"]:
    raise RuntimeError("Full installer acceptance failed: " + str(output))
