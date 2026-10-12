"""Exercise real HTTPS transport against a pinned, public upstream license file."""
from pathlib import Path
import hashlib
import json
import subprocess
import uuid

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/update-download-tests" / uuid.uuid4().hex
output.mkdir(parents=True)
probe = root / ".cache/distribution-build/Release/UpdateDownloadProbe.exe"
reference = (root / "src/FullTools.Distribution/third_party/miniz/LICENSE").read_bytes()
digest = hashlib.sha256(reference).hexdigest()
url = "https://raw.githubusercontent.com/richgel999/miniz/77d0dce8627735138c51770d1799a1ef48f2117d/LICENSE"
checks = {}
def run(url, name, size=len(reference), sha=digest):
    process = subprocess.run([str(probe), url, str(output / name), str(size), sha], capture_output=True, text=True, timeout=45)
    print(process.stdout.strip(), flush=True)
    return process.returncode == 0
checks["RealHttpsVerifiedDownload"] = run(url, "valid.txt") and (output / "valid.txt").read_bytes() == reference
checks["WrongDigestRefused"] = not run(url, "digest.txt", sha="0" * 64)
checks["WrongSignedSizeRefused"] = not run(url, "size.txt", size=len(reference) - 1)
checks["HttpRefusedBeforeDownload"] = not run("http://example.com/license", "plain.txt") and not (output / "plain.txt").exists()
checks["EmbeddedCredentialsRefusedBeforeDownload"] = not run("https://user:pass@example.com/license", "credentials.txt") and not (output / "credentials.txt").exists()
checks["ExistingFilePreserved"] = not run(url, "valid.txt") and (output / "valid.txt").read_bytes() == reference
checks["InvalidSizeRefusedBeforeDownload"] = not run(url, "zero.txt", size=0) and not (output / "zero.txt").exists()
report = {"Passed": all(checks.values()), "Checks": checks, "Network": "actual public HTTPS upstream, not a local fixture"}
(output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report), flush=True)
if not report["Passed"]:
    raise RuntimeError("HTTPS update download checks failed")
