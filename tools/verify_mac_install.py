"""Install, repair and recycle only this job's product using native macOS services."""
from pathlib import Path
import os,json,hashlib,subprocess,uuid,time,plistlib,sys
from mac_sources import swift_target

if os.environ.get('GITHUB_ACTIONS')!='true':raise RuntimeError('Requires disposable hosted macOS')
root=Path(__file__).resolve().parents[1];artifacts=root/'artifacts'
bundle=json.loads((artifacts/'macos-full/bundle-manifest.json').read_text())
def digest(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def matches(app):
    for name,value in bundle['Files'].items():
        if not (app/name).is_file() or digest(app/name)!=value['SHA256']:return False
    for name,target in bundle['Links'].items():
        if not (app/name).is_symlink() or os.readlink(app/name)!=target:return False
    return True
if len(sys.argv)==3 and sys.argv[1]=='--verify-backup':
    backup=Path(sys.argv[2])
    if not backup.resolve().is_relative_to(Path('/Library/Application Support/FullOffice/InstallBackups').resolve()):raise RuntimeError('Backup outside owned installer area')
    sys.exit(0 if matches(backup) else 1)
if len(sys.argv)==2 and sys.argv[1]=='--list-backup-receipts':
    print(json.dumps([str(p) for p in Path('/Library/Application Support/FullOffice/InstallBackups').glob('*/receipt.json')]))
    sys.exit(0)
def backup_receipts():
    return set(Path(p) for p in json.loads(subprocess.check_output(['sudo','-n','/usr/bin/env','GITHUB_ACTIONS=true',sys.executable,str(Path(__file__).resolve()),'--list-backup-receipts'],text=True)))
out=artifacts/'mac-install-checks';out.mkdir(exist_ok=False)
app=Path('/Applications/FullOffice.app')
if app.exists() or app.is_symlink():raise RuntimeError('Existing application protected; installer test requires a clean target')
receipt='center.dnd.FullOffice.pkg'
if subprocess.run(['pkgutil','--pkg-info',receipt],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0:raise RuntimeError('Existing installer receipt protected')
meta=json.loads((artifacts/'mac-package/package.json').read_text());package=artifacts/'mac-package'/meta['File']
assert digest(package)==meta['SHA256'],'Installer bytes changed'
data=Path.home()/'Library/Application Support/FullOffice/EditingSessions'/str(uuid.uuid4())
data.mkdir(parents=True,exist_ok=False)
sentinel=data/'installer-data-preservation.txt';sentinel.write_text('Authored saved document and recovery data must survive installation and uninstall.\n')
sentinel_hash=digest(sentinel);checks={}
def check(name,value):
    checks[name]=bool(value)
    (out/'report.json').write_text(json.dumps({'Passed':len(checks)==11 and all(checks.values()),'Checks':checks,
        'Scope':'Actual native Installer fresh installation and same-version repair with verified previous-app backup, installed-bundle bytes/signature/PDF UI, administrator-authorized native Trash uninstall and retained authored recovery data. Not signed update, Finder authentication UI or notarized/physical-device acceptance.'},indent=2))
    if not value:raise RuntimeError(name)
with (out/'install.log').open('wb') as log:result=subprocess.run(['sudo','-n','installer','-pkg',str(package),'-target','/'],stdout=log,stderr=subprocess.STDOUT,timeout=180)
check('ActualNativeInstallerCompletes',result.returncode==0 and app.is_dir())
check('InstalledAppMatchesEveryVerifiedBundleFileAndLink',matches(app))
signed=subprocess.run(['codesign','--verify','--deep','--strict',str(app)],capture_output=True,text=True,timeout=90)
(out/'codesign.txt').write_text(signed.stdout+signed.stderr)
check('ActualInstalledBundleSignatureValid',signed.returncode==0)
pdf=out/'authored.pdf';subprocess.run(['xcrun','swift',str(root/'tools/create_mac_probe_fixture.swift'),str(pdf)],check=True,timeout=60)
pdf_hash=digest(pdf);probe=out/'installed-pdf-ui'
with (out/'installed-app.log').open('wb') as log:result=subprocess.run([str(app/'Contents/MacOS/FullOffice'),'--ui-probe',str(pdf),str(probe)],stdout=log,stderr=subprocess.STDOUT,timeout=45)
report=json.loads((probe/'report.json').read_text())
check('ActualInstalledProductPDFWindowRunsAndExitsNormally',result.returncode==0 and report['Passed'] and len(report['Checks'])==8 and all(report['Checks'].values()))
check('InstalledProgramDoesNotChangeAuthoredPDF',digest(pdf)==pdf_hash)
before=backup_receipts()
with (out/'repair.log').open('wb') as log:result=subprocess.run(['sudo','-n','installer','-pkg',str(package),'-target','/'],stdout=log,stderr=subprocess.STDOUT,timeout=240)
check('ActualSameVersionRepairCompletes',result.returncode==0 and matches(app))
after=backup_receipts()
new=list(after-before)
backup=Path(json.loads(subprocess.check_output(['sudo','-n','cat',str(new[0])],text=True))['Backup']) if len(new)==1 else None
verified=backup is not None and subprocess.run(['sudo','-n','/usr/bin/env','GITHUB_ACTIONS=true',sys.executable,str(Path(__file__).resolve()),'--verify-backup',str(backup)]).returncode==0
check('RepairRetainsExactPreviousApplicationBackup',verified)
check('InstallAndRepairPreserveAuthoredRecoveryData',digest(sentinel)==sentinel_hash)
trash=out/'native-trash.json'
helper=out/'native-trash-helper'
subprocess.run(['xcrun','swiftc',*swift_target(),str(root/'tools/mac_recycle_installed.swift'),'-o',str(helper)],check=True,timeout=60)
with (out/'uninstall.log').open('wb') as log:
    result=subprocess.run(['sudo','-n','/usr/bin/env','GITHUB_ACTIONS=true',str(helper),str(trash)],stdout=log,stderr=subprocess.STDOUT,timeout=60)
if result.returncode!=0:raise RuntimeError('Administrator-authorized native Trash uninstall failed; retained report contains the actual service error')
recycled=json.loads(trash.read_text())
check('ActualNativeTrashUninstallRemovesInstalledLocation',recycled['Passed'] and not app.exists())
check('NativeUninstallPreservesAuthoredRecoveryData',digest(sentinel)==sentinel_hash)
check('OwnInstallerReceiptCanBeRemovedAfterUninstall',subprocess.run(['sudo','-n','pkgutil','--forget',receipt],stdout=subprocess.DEVNULL).returncode==0)
