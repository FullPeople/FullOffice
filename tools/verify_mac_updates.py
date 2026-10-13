"""Run production Mac signature and streaming checks before downloading the Office engine."""
from pathlib import Path
import hashlib,json,re,shutil,subprocess
root=Path(__file__).resolve().parents[1]
assets=root/'src/FullOffice.Mac/Assets'
provenance=json.loads((assets/'update-public-key-provenance.json').read_text(encoding='utf-8'))
header=(root/provenance['Source']).read_text(encoding='utf-8')
match=re.search(r'update_public_key\[\]\s*=\s*\{([^}]+)\}',header)
blob=bytes(int(b) for b in match[1].split(','))
assert hashlib.sha256(blob).hexdigest()==provenance['PublicCNG_SHA256']
assert hashlib.sha256((assets/'update-public-key.der').read_bytes()).hexdigest()==provenance['PublicDER_SHA256']
work=root/'.cache/mac-update-checks';work.mkdir(parents=True,exist_ok=False)
shutil.copy2(root/'tools/mac_update_checks.swift',work/'main.swift')
exe=work/'update-checks'
source=root/'src/FullOffice.Mac'
subprocess.run(['xcrun','swiftc','-swift-version','5',str(source/'TextCodec.swift'),str(source/'OfficeFileSession.swift'),str(source/'MacUpdateManifest.swift'),str(source/'MacUpdateFeed.swift'),str(source/'MacUpdateDownload.swift'),str(work/'main.swift'),'-o',str(exe)],check=True,timeout=90)
subprocess.run([str(exe),str(root),str(root/'artifacts/mac-update-checks')],check=True,timeout=120)
