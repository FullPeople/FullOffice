"""Prepare the native Installer candidate only after full product checks pass."""
from pathlib import Path
import hashlib,json,os,platform,plistlib,shutil,subprocess

if platform.system()!='Darwin':raise RuntimeError('Requires native macOS packaging')
root=Path(__file__).resolve().parents[1];artifacts=root/'artifacts'
for name,count in [('mac-product-ui',115),('mac-office-session-checks',55),('mac-recovery-ui',13),('mac-text-ui',17)]:
    report=json.loads((artifacts/name/'report.json').read_text())
    checks=[value for item in report['Results'] for value in item['Checks'].values()] if 'Results' in report else list(report['Checks'].values())
    if not report['Passed'] or len(checks)!=count or not all(checks):raise RuntimeError('Incomplete product gate: '+name)
app=artifacts/'macos-full/FullOffice.app'
subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True,timeout=90)
bundle=json.loads((artifacts/'macos-full/bundle-manifest.json').read_text())
for relative,value in bundle['Files'].items():
    with (app/relative).open('rb') as stream:actual=hashlib.file_digest(stream,'sha256').hexdigest()
    if actual!=value['SHA256']:raise RuntimeError('Built bundle changed: '+relative)
if shutil.disk_usage(artifacts).free<4*1024**3:raise RuntimeError('Insufficient package headroom')
out=artifacts/'mac-package';out.mkdir(exist_ok=False)
payload=out/'payload';payload.mkdir()
subprocess.run(['ditto',str(app),str(payload/'FullOffice.app')],check=True,timeout=240)
scripts=out/'scripts';scripts.mkdir()
subprocess.run(['xcrun','swiftc','-swift-version','5',str(root/'src/FullOffice.Mac/InstallGuard.swift'),'-o',str(scripts/'FullOfficeInstallGuard')],check=True,timeout=90)
preinstall=scripts/'preinstall';preinstall.write_text('#!/bin/bash\nset -eu\nSCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"\nexec "$SCRIPT_DIR/FullOfficeInstallGuard" "${3:-/}"\n')
preinstall.chmod(0o755)
components=out/'components.plist'
subprocess.run(['pkgbuild','--analyze','--root',str(payload),str(components)],check=True,timeout=60)
entries=plistlib.loads(components.read_bytes())
for item in entries:
    item['BundleIsRelocatable']=False
    if item.get('RootRelativeBundlePath')=='FullOffice.app':
        item['BundleIsVersionChecked']=False;item['BundleOverwriteAction']='upgrade'
components.write_bytes(plistlib.dumps(entries))
version=plistlib.loads((app/'Contents/Info.plist').read_bytes())['CFBundleShortVersionString']
arch='arm64' if platform.machine()=='arm64' else 'x64'
package=out/f'FullOffice-{version}-Setup-macos-{arch}.pkg'
subprocess.run(['pkgbuild','--root',str(payload),'--component-plist',str(components),'--identifier','center.dnd.FullOffice.pkg',
    '--version',version,'--install-location','/Applications','--scripts',str(scripts),str(package)],check=True,timeout=300)
with package.open('rb') as stream:sha=hashlib.file_digest(stream,'sha256').hexdigest()
record={'Built':True,'Product':'FullOffice','Version':version,'Architecture':'macos-'+arch,'ProductSource':bundle['ProductSource'],
    'File':package.name,'Bytes':package.stat().st_size,'SHA256':sha,'BundleCoreSHA256':bundle['CoreSHA256'],
    'Signature':'Candidate only; app ad-hoc signed, package not Developer ID signed or notarized',
    'Scope':'Native Installer container with running-app guard and verified previous-app backup. Actual installation, repair, launch and uninstall checks still required.'}
(out/'package.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
print(json.dumps(record),flush=True)
