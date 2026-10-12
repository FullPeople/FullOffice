from pathlib import Path
import json
import subprocess

root = Path(__file__).resolve().parents[1]
probe = root / ".cache/distribution-build/Release/UpdateProbe.exe"
cases = [
    ("0.2.1", "0.2.0", True), ("0.2.0", "0.2.0", False), ("0.1.0", "0.2.0", False),
    ("1.0.0", "1.0.0-rc.1", True), ("1.0.0-rc.2", "1.0.0-rc.1", True),
    ("1.0.0-beta.11", "1.0.0-beta.2", True), ("1.0.0-beta", "1.0.0-alpha", True),
    ("1.0.0-1", "1.0.0-alpha", False), ("1.0.0-beta.2", "1.0.0-beta.11", False),
    ("1.0.0-beta.1", "1.0.0-beta", True), ("1.0.0+build.2", "1.0.0+build.1", False),
    ("1.0.0-", "0.2.0", False), ("1.0.0+", "0.2.0", False), ("01.0.0", "0.2.0", False),
    ("1.0.0-01", "0.2.0", False), ("1.0.0-rc..2", "0.2.0", False), ("1.0.0.1", "0.2.0", False),
]
checks = {}
for candidate, current, expected in cases:
    process = subprocess.run([str(probe), "--newer", candidate, current], timeout=10)
    checks[candidate + ">" + current] = (process.returncode == 0) == expected
report = {"Passed": all(checks.values()), "Checks": checks}
(root / "artifacts/update-version-tests.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report), flush=True)
if not report["Passed"]:
    raise RuntimeError("Version precedence checks failed")
