from pathlib import Path
import base64
import json
import subprocess
import uuid

root = Path(__file__).resolve().parents[1]
output = root / "artifacts/update-tests" / uuid.uuid4().hex
output.mkdir(parents=True)
manifest = output / "valid.manifest"
manifest.write_bytes((root / "tests/fixtures/signed-update-test.manifest").read_bytes())
probe = root / ".cache/distribution-build/Release/UpdateProbe.exe"
def verify(path, product="FullOffice"):
    return subprocess.run([str(probe), str(path), product], capture_output=True).returncode == 0
checks = {"PublishedSignatureVerifies": verify(manifest), "WrongProductRefused": not verify(manifest, "MusicBoard")}
lines = manifest.read_bytes().splitlines()
body = base64.b64decode(lines[1]).replace(b"version=0.2.1", b"version=9.9.9")
changed = output / "changed.manifest"
changed.write_bytes(lines[0] + b"\n" + base64.b64encode(body) + b"\n" + lines[2] + b"\n")
checks["ChangedVersionWithoutSignatureRefused"] = not verify(changed)
signature = bytearray(base64.b64decode(lines[2])); signature[0] ^= 1
changed.write_bytes(lines[0] + b"\n" + lines[1] + b"\n" + base64.b64encode(signature) + b"\n")
checks["DamagedSignatureRefused"] = not verify(changed)
changed.write_bytes(b"FULLTOOLS-UPDATE-1\n" + lines[1] + b"\n")
checks["MissingSignatureRefused"] = not verify(changed)
report = {"Passed": all(checks.values()), "Checks": checks}
(output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print(json.dumps(report), flush=True)
if not report["Passed"]:
    raise RuntimeError("Signed update validation failed")
