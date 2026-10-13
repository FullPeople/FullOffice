from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import uuid
import zipfile
import winreg

root = Path(__file__).resolve().parents[1]
out = root / "artifacts/distribution-tests" / uuid.uuid4().hex
out.mkdir(parents=True)
probe = root / ".cache/distribution-build/Release/ExtractionProbe.exe"
backend = root / ".cache/distribution-build/Release/FullOfficeSetup.exe"
checks = {}
def registry_string(path, name=None):
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as registry:
            return winreg.QueryValueEx(registry, name or "")[0]
    except FileNotFoundError:
        return None
user_choice_path = r"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice"
before_choice = (registry_string(user_choice_path, "ProgId"), registry_string(user_choice_path, "Hash"))
office_suffixes = {".docx": ".Word", ".xlsx": ".Spreadsheet", ".pptx": ".Presentation",
                   ".odt": ".Word", ".ods": ".Spreadsheet", ".odp": ".Presentation", ".rtf": ".Word", ".csv": ".Spreadsheet"}
def office_user_choices():
    return {suffix: tuple(registry_string("Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\" + suffix + r"\UserChoice", field)
                         for field in ("ProgId", "Hash")) for suffix in office_suffixes}
before_office_choices = office_user_choices()
for index, (name, entries, accepted) in enumerate([
    ("unicode", [("文档/测试文件.txt", b"original")], True),
    ("traversal", [("../outside.txt", b"bad")], False),
    ("absolute", [("C:/outside.txt", b"bad")], False),
    ("device", [("NUL.txt", b"bad")], False),
    ("duplicates", [("readme.txt", b"a"), ("README.TXT", b"b")], False),
    ("long-traversal", [("safe/" * 110 + "../../outside.txt", b"bad")], False),
    ("nested-directories", [("assets/", b""), ("assets/fonts/", b""), ("assets/fonts/test.txt", b"kept")], True),
    ("long-resource", [("assets/templates/" + "深水城" * 30 + "/" + "presentation-" * 8 + ".txt", b"kept")], True),
]):
    source = out / (name + ".zip")
    with zipfile.ZipFile(source, "w", zipfile.ZIP_DEFLATED) as archive:
        for path, contents in entries:
            archive.writestr(path, contents)
    destination = out / ("FullTools-stage-" + name)
    result = subprocess.run([str(probe), str(source), str(destination)], capture_output=True, text=True)
    checks[name] = (result.returncode == 0) == accepted
    if accepted and result.returncode != 0:
        raise RuntimeError("Accepted extraction failed: " + name + ": " + result.stdout.strip())
    if accepted:
        if name == "unicode": checks["UnicodeBytesPreserved"] = (destination / entries[0][0]).read_bytes() == b"original"
        elif name == "nested-directories": checks["NestedDirectoryFilePreserved"] = (destination / "assets/fonts/test.txt").read_bytes() == b"kept"
        else: checks["LongUnicodeResourceBytesPreserved"] = (destination / entries[0][0]).read_bytes() == b"kept"
checks["NoOutsideWrite"] = not (out.parent / "outside.txt").exists()
package = out / "package"
package.mkdir()
shutil.copy2(root / "artifacts/app/FullOffice.exe", package / "FullOffice.exe")
shutil.copy2(backend, package / "FullOfficeSetup.exe")
(package / "VERSION.txt").write_text("product=FullOffice\nversion=0.2.0\npackage_version=0.2.0\narchitecture=windows-x64\nexecutable=FullOffice.exe\ncomplete=1\n", encoding="utf-8")
def manifest():
    lines = [hashlib.sha256(p.read_bytes()).hexdigest() + "  " + p.relative_to(package).as_posix() for p in package.rglob("*") if p.is_file() and p.name != "SHA256SUMS.txt"]
    (package / "SHA256SUMS.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
manifest()
install = out / "安装目录"
shortcut = out / "FullOffice-test.lnk"
key = "FullOffice-DistributionTest-" + uuid.uuid4().hex
common = ["--quiet", "--install-root", str(install), "--shortcut-path", str(shortcut), "--registry-subkey", key]
result = subprocess.run([str(backend), "--package-root", str(package), "--no-launch", *common], capture_output=True)
checks["FullOfficeInstalls"] = result.returncode == 0 and (install / "current/FullOffice.exe").exists()
checks["StartMenuLinkCreated"] = shortcut.exists()
capability = "Software\\FullPeople\\" + key + "\\Capabilities"
checks["RegisteredDefaultAppCandidate"] = registry_string(r"Software\RegisteredApplications", key) == capability
checks["RegisteredPdfCapability"] = registry_string(capability + r"\FileAssociations", ".pdf") == key + ".Pdf"
checks["OpenCommandQuotesDocumentPath"] = registry_string("Software\\Classes\\" + key + r".Pdf\shell\open\command") == '"' + str(install / "current/FullOffice.exe") + '" "%1"'
checks["UserDefaultChoiceUnchanged"] = before_choice == (registry_string(user_choice_path, "ProgId"), registry_string(user_choice_path, "Hash"))
checks["PartialEditionDoesNotRegisterOfficeFormats"] = all(registry_string(capability + r"\FileAssociations", suffix) is None for suffix in office_suffixes)
engine = root / "artifacts/office-integration/office-engine"
if (engine / "FullOfficeOfficeHost.exe").is_file() and (engine / "ascdocumentscore.dll").is_file():
    # Registration detection uses two genuine runtime files, without claiming this tiny fixture can edit documents.
    (package / "office-engine").mkdir()
    for name in ("FullOfficeOfficeHost.exe", "ascdocumentscore.dll"):
        shutil.copy2(engine / name, package / "office-engine" / name)
    version_path = package / "VERSION.txt"
    version_path.write_text(version_path.read_text().replace("0.2.0", "0.3.0"), encoding="utf-8"); manifest()
    result = subprocess.run([str(backend), "--package-root", str(package), "--no-launch", *common], capture_output=True)
    checks["OfficeRuntimeRegistrationDetectionUpgrades"] = result.returncode == 0
    for suffix, kind in office_suffixes.items():
        checks["RegisteredOfficeCapability" + suffix] = registry_string(capability + r"\FileAssociations", suffix) == key + kind
        checks["OfficeOpenCommandQuoted" + suffix] = registry_string("Software\\Classes\\" + key + kind + r"\shell\open\command") == '"' + str(install / "current/FullOffice.exe") + '" "%1"'
    checks["OfficeUserDefaultChoicesUnchanged"] = office_user_choices() == before_office_choices
    for name in ("FullOfficeOfficeHost.exe", "ascdocumentscore.dll"):
        (package / "office-engine" / name).unlink()
    (package / "office-engine").rmdir()
    version_path.write_text(version_path.read_text().replace("0.3.0", "0.3.1"), encoding="utf-8"); manifest()
    result = subprocess.run([str(backend), "--package-root", str(package), "--no-launch", *common], capture_output=True)
    checks["RemovedOfficeRuntimeClearsUnsupportedAssociations"] = result.returncode == 0 and all(
        registry_string(capability + r"\FileAssociations", suffix) is None for suffix in office_suffixes)
protected = out / "user-document.txt"
protected.write_bytes(b"keep user data")
result = subprocess.run([str(backend), "--uninstall", *common], capture_output=True)
checks["FullOfficeUninstalls"] = result.returncode == 0 and not install.exists() and not shortcut.exists()
checks["DefaultAppCandidateRemoved"] = registry_string(r"Software\RegisteredApplications", key) is None
checks["UninstallPreservesUserDefaultChoice"] = before_choice == (registry_string(user_choice_path, "ProgId"), registry_string(user_choice_path, "Hash"))
checks["UserDocumentPreserved"] = protected.read_bytes() == b"keep user data"
checks["OfficeUserChoicesPreservedAfterUninstall"] = office_user_choices() == before_office_choices
(package / "FullOffice.exe").write_bytes(b"tampered")
result = subprocess.run([str(backend), "--package-root", str(package), "--no-launch", *common], capture_output=True)
checks["TamperedInstallRefused"] = result.returncode != 0 and not install.exists()
report = {"Passed": all(checks.values()), "Checks": checks}
(out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report), flush=True)
if not report["Passed"]:
    raise RuntimeError("Distribution verification failed: " + str(out))
