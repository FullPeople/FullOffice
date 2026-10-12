"""Real Windows input test for the app-local Office/PDF workspace.

Requires pywin32, Pillow and psutil, a 96-DPI desktop, and an independently
staged Office runtime. Copies input documents and never edits the originals.
This local UI probe is intentionally separate from cloud compile checks.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import uuid

ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
import psutil
import win32api
import win32clipboard
import win32gui
import win32process
from PIL import ImageGrab

def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("app", type=Path)
    parser.add_argument("docx", type=Path)
    parser.add_argument("pdf", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    source_hashes = [sha(args.docx), sha(args.pdf)]
    output = args.output.resolve() / uuid.uuid4().hex
    output.mkdir(parents=True)
    docx, pdf = output / "office-fixture.docx", output / "reader-fixture.pdf"
    shutil.copy2(args.docx, docx); shutil.copy2(args.pdf, pdf)
    environment = os.environ.copy()
    environment.pop("FULLOFFICE_ENGINE_RUNTIME", None); environment.pop("FULLOFFICE_ENGINE_ROOT", None)
    process = subprocess.Popen([str(args.app.resolve()), str(docx)], env=environment)
    (output / "process.json").write_text(json.dumps({"PID": process.pid}), encoding="utf-8")
    checks, window = {}, None
    try:
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            candidates = []
            win32gui.EnumWindows(lambda h, _: candidates.append(h) if win32process.GetWindowThreadProcessId(h)[1] == process.pid
                and win32gui.IsWindowVisible(h) and win32gui.GetWindowText(h).endswith("FullOffice") else None, None)
            if candidates:
                window = candidates[0]; break
            if process.poll() is not None:
                raise RuntimeError("Owned app exited before its document window appeared")
            time.sleep(.1)
        if window is None:
            raise RuntimeError("Document window did not appear")
        time.sleep(7)
        x, y, right, bottom = win32gui.GetWindowRect(window)
        if (right - x, bottom - y) != (1320, 900) or ctypes.windll.user32.GetDpiForWindow(window) != 96:
            raise RuntimeError("This physical-input probe requires the documented 1320x900 window at 96 DPI")
        win32gui.SetForegroundWindow(window)
        def guard():
            if win32process.GetWindowThreadProcessId(win32gui.GetForegroundWindow())[1] != process.pid:
                raise RuntimeError("Input focus left the owned test app; stopping physical input")
        def click(a, b):
            guard(); win32api.SetCursorPos((x + a, y + b)); win32api.mouse_event(2, 0, 0); win32api.mouse_event(4, 0, 0); time.sleep(.25)
        def key(k):
            guard(); win32api.keybd_event(k, 0, 0, 0); time.sleep(.07); win32api.keybd_event(k, 0, 2, 0)
        def control(k, shift=False):
            guard(); win32api.keybd_event(17, 0, 0, 0)
            if shift: win32api.keybd_event(16, 0, 0, 0)
            try: key(k)
            finally:
                if shift: win32api.keybd_event(16, 0, 2, 0)
                win32api.keybd_event(17, 0, 2, 0)
            time.sleep(.6)
        click(94, 350); click(923, 794); click(650, 480); control(79)
        dialogs = []
        win32gui.EnumWindows(lambda h, _: dialogs.append(h) if win32process.GetWindowThreadProcessId(h)[1] == process.pid
            and win32gui.GetClassName(h) == "#32770" else None, None)
        checks["NativeControlOOpensWorkspaceDialog"] = len(dialogs) == 1
        if len(dialogs) != 1: raise RuntimeError("Office shortcut did not open the workspace file dialog")
        win32clipboard.OpenClipboard()
        try: win32clipboard.EmptyClipboard(); win32clipboard.SetClipboardText(str(pdf))
        finally: win32clipboard.CloseClipboard()
        control(86); key(13); time.sleep(3)
        checks["MixedPDFTabOpened"] = "reader-fixture.pdf" in win32gui.GetWindowText(window)
        control(9); checks["ControlTabFromPDFSelectsOffice"] = "office-fixture.docx" in win32gui.GetWindowText(window)
        click(650, 480); control(9); checks["ControlTabFromOfficeSelectsPDF"] = "reader-fixture.pdf" in win32gui.GetWindowText(window)
        control(9, True); checks["PreviousTabSelectsOffice"] = "office-fixture.docx" in win32gui.GetWindowText(window)
        click(650, 480); control(87); checks["ControlWClosesOfficeTab"] = "reader-fixture.pdf" in win32gui.GetWindowText(window)
        ImageGrab.grab(bbox=win32gui.GetWindowRect(window)).save(output / "mixed-tabs.png")
        checks["OriginalInputsUnchanged"] = source_hashes == [sha(args.docx), sha(args.pdf)]
    except Exception as error:
        checks["Failure"] = str(error)
    finally:
        owned = [psutil.Process(process.pid)] + psutil.Process(process.pid).children(recursive=True) if process.poll() is None else []
        if window is not None and win32gui.IsWindow(window): win32gui.PostMessage(window, 16, 0, 0)
        _, alive = psutil.wait_procs(owned, timeout=10)
        checks["NormalExitWithoutRemainingChildren"] = not alive
    report = {"Passed": all(value is True for value in checks.values()), "Checks": checks}
    (output / "workspace-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({**report, "ReportDirectory": str(output)}))
    return 0 if report["Passed"] else 1

if __name__ == "__main__": raise SystemExit(main())
