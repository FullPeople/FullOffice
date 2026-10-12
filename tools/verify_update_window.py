"""Exercise the installed updater's actual Win32 window and staged process cleanup."""
from pathlib import Path
import json
import os
import shutil
import subprocess
import time
import uuid
import psutil
import win32con
import win32gui
import win32process

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/update-window-tests" / uuid.uuid4().hex
current = output / "安装目录/current"
current.mkdir(parents=True)
shutil.copy2(root / ".cache/distribution-build/Release/FullToolsUpdater.exe", current / "FullToolsUpdater.exe")
shutil.copy2(root / "artifacts/self-contained/FullOffice.exe", current / "FullOffice.exe")
temporary = output / "临时目录"
temporary.mkdir()
env = dict(os.environ, TEMP=str(temporary), TMP=str(temporary), LOCALAPPDATA=str(output / "profile"))
arguments = [str(current / "FullToolsUpdater.exe"), "--product", "FullOffice", "--version", "0.2.0"]
checks = {}
launcher = subprocess.Popen(arguments, env=env, creationflags=subprocess.CREATE_NO_WINDOW)
launcher.wait(timeout=10)
checks["InstalledLauncherTransfersToTemporaryWorker"] = launcher.returncode == 0

def own_windows():
    owned = []
    def each(hwnd, _):
        if win32gui.GetClassName(hwnd) != "FullTools.Update": return
        try:
            pid = win32process.GetWindowThreadProcessId(hwnd)[1]
            args = psutil.Process(pid).cmdline()
            if any(str(current.parent) in arg for arg in args): owned.append(hwnd)
        except (psutil.NoSuchProcess, psutil.AccessDenied): pass
    win32gui.EnumWindows(each, None)
    return owned

deadline = time.monotonic() + 50
window = None
text = ""
while time.monotonic() < deadline:
    windows = own_windows()
    if windows:
        window = windows[0]
        labels = []
        win32gui.EnumChildWindows(window, lambda hwnd, _: labels.append(win32gui.GetWindowText(hwnd)), None)
        text = " ".join(labels)
        if "更新检查未完成" in text or "最新版本" in text or "发现新版本" in text: break
    time.sleep(0.1)
checks["ActualUpdateWindowAppears"] = window is not None
checks["CheckFinishesWithVisibleResult"] = any(word in text for word in ("更新检查未完成", "最新版本", "发现新版本"))
checks["RunningInstallRemainsUnchanged"] = (current / "FullOffice.exe").exists()
if window: win32gui.PostMessage(window, win32con.WM_CLOSE, 0, 0)
deadline = time.monotonic() + 20
while time.monotonic() < deadline and (own_windows() or list(temporary.glob("FullTools-update-*"))): time.sleep(0.1)
checks["WorkerWindowCloses"] = not own_windows()
checks["TemporaryWorkerAndCacheAreCleaned"] = not list(temporary.glob("FullTools-update-*"))

before = set(temporary.glob("FullTools-update-*"))
launcher = subprocess.Popen(arguments + ["--background"], env=env, creationflags=subprocess.CREATE_NO_WINDOW)
launcher.wait(timeout=10)
deadline = time.monotonic() + 45
while time.monotonic() < deadline and set(temporary.glob("FullTools-update-*")) - before: time.sleep(0.1)
checks["BackgroundCheckDoesNotShowFailureWindow"] = not own_windows()
checks["BackgroundCheckWritesDailyMarker"] = (output / "profile/FullPeople/UpdateChecks/FullOffice.timestamp").is_file()
launcher = subprocess.run(arguments + ["--background"], env=env, timeout=10)
checks["SecondBackgroundCheckSkipsWithinOneDay"] = launcher.returncode == 0 and not list(temporary.glob("FullTools-update-*"))
report = {"Passed": all(checks.values()), "Checks": checks, "VisibleResult": text, "Scope": "actual installed updater launch/check/error UI and cleanup; update installation awaits published feed"}
(output / "report.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
print(json.dumps(report, ensure_ascii=False), flush=True)
if not report["Passed"]: raise RuntimeError("Installed update window checks failed")
