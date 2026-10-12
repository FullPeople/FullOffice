from pathlib import Path
import hashlib
import io
import json
import urllib.request
import zipfile

root = Path(__file__).resolve().parents[1]
target = root / "src/FullTools.Distribution"
vendor = target / "third_party/miniz"
vendor.mkdir(parents=True, exist_ok=True)
url = "https://github.com/richgel999/miniz/releases/download/3.1.2/miniz-3.1.2.zip"
data = urllib.request.urlopen(url, timeout=30).read()
assert hashlib.sha256(data).hexdigest() == "f0446d863f9c19926ad9483c523fdc42e42b8d4a6a431d27e09d49c79a140d9a"
with zipfile.ZipFile(io.BytesIO(data)) as archive:
    for item in archive.namelist():
        name = Path(item).name
        if name in {"miniz.c", "miniz.h"}:
            (vendor / name).write_bytes(archive.read(item))
assert (vendor / "miniz.c").exists() and (vendor / "miniz.h").exists()
license_data = urllib.request.urlopen("https://raw.githubusercontent.com/richgel999/miniz/77d0dce8627735138c51770d1799a1ef48f2117d/LICENSE", timeout=30).read()
(vendor / "LICENSE").write_bytes(license_data)
(vendor / "provenance.json").write_text(json.dumps({"url": url, "tag": "3.1.2",
    "commit": "77d0dce8627735138c51770d1799a1ef48f2117d", "archive_sha256": hashlib.sha256(data).hexdigest()}, indent=2), encoding="utf-8")
print("Pinned ZIP library refreshed; maintained installation transaction sources were preserved")
