"""Public signed package transport and real 0.3.4-to-0.3.5 native upgrade."""
from pathlib import Path
import hashlib,json,os,platform,plistlib,subprocess,sys,time,uuid

if os.environ.get('GITHUB_ACTIONS')!='true' or os.environ.get('GITHUB_REPOSITORY')!='FullPeople/FullOffice':
    raise RuntimeError('Requires disposable hosted product runner')
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/mac-public-update';out.mkdir(parents=True,exist_ok=False)
app=Path('/Applications/FullOffice.app');receipt='center.dnd.FullOffice.pkg'
if app.exists() or app.is_symlink() or subprocess.run(['pkgutil','--pkg-info',receipt],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0:
    raise RuntimeError('Existing installation protected')
arch='arm64' if platform.machine()=='arm64' else 'x64'
old_hash={'arm64':'338caf2796269c12f11c19a0647999aa42252ce540f2ef9043cc51ca196c3c6c','x64':'cb64cb666b6ff005e139881b320a00926278cad593a10511f0e6f2fe84ee4e61'}[arch]
baseline=root/'artifacts/mac-update-baseline'/f'FullOffice-0.3.4-Setup-macos-{arch}.pkg'
def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
if sha(baseline)!=old_hash:raise RuntimeError('Actual baseline package differs')
old_bundle=json.loads((root/'artifacts/mac-update-baseline-reports/macos-full/bundle-manifest.json').read_text())
new_bundle=json.loads((root/'artifacts/mac-update-candidate-reports/macos-full/bundle-manifest.json').read_text())
def matches(path,bundle):
    return all((path/name).is_file() and sha(path/name)==record['SHA256'] for name,record in bundle['Files'].items()) and all((path/name).is_symlink() and os.readlink(path/name)==target for name,target in bundle['Links'].items())
def receipt_paths():
    return set(json.loads(subprocess.check_output(['sudo','-n',sys.executable,'-c',"from pathlib import Path;import json;print(json.dumps([str(p) for p in Path('/Library/Application Support/FullOffice/InstallBackups').glob('*/receipt.json')]))"],text=True)))
def install(package,name):
    with (out/(name+'.log')).open('wb') as log:return subprocess.run(['sudo','-n','installer','-pkg',str(package),'-target','/'],stdout=log,stderr=subprocess.STDOUT,timeout=240).returncode
work=out/'compile';work.mkdir();(work/'main.swift').write_bytes((root/'tools/mac_public_update.swift').read_bytes())
source=root/'src/FullOffice.Mac';exe=work/'public-update'
subprocess.run(['xcrun','swiftc','-swift-version','5','-target',platform.machine()+'-apple-macosx13.0',*[str(source/name) for name in ['TextCodec.swift','OfficeFileSession.swift','MacUpdateManifest.swift','MacUpdateFeed.swift','MacUpdateDownload.swift']],str(work/'main.swift'),'-o',str(exe)],check=True,timeout=90)
with (out/'public-download.log').open('wb') as log:subprocess.run([str(exe),str(root),str(out)],stdout=log,stderr=subprocess.STDOUT,check=True,timeout=1320)
network=json.loads((out/'network-report.json').read_text());package=Path(network['Package'])
if network['Passed'] is not True or not package.resolve().is_relative_to((out/'downloads').resolve()):raise RuntimeError('Public package evidence incomplete')
checks=dict(network['Checks']);failure=None;running=None
data=Path.home()/'Library/Application Support/FullOffice/EditingSessions'/str(uuid.uuid4());data.mkdir(parents=True,exist_ok=False)
sentinel=data/'public-update-preservation.txt';sentinel.write_text('Authored saved document and recovery settings must survive.\n');sentinel_hash=sha(sentinel)
try:
    checks['ActualBaselineNativeInstall']=install(baseline,'baseline-install')==0 and matches(app,old_bundle)
    if not checks['ActualBaselineNativeInstall']:raise RuntimeError('Baseline native installation failed')
    checks['ActualBaselineVersionIs034']=plistlib.loads((app/'Contents/Info.plist').read_bytes())['CFBundleShortVersionString']=='0.3.4'
    before=receipt_paths()
    # Only this owned, authored test application is stopped after the running-app guard fault.
    running=subprocess.Popen([str(app/'Contents/MacOS/FullOffice'),'--recovery-ui-root',str(data)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    time.sleep(3)
    if running.poll() is not None:raise RuntimeError('Actual baseline app did not stay running for update guard')
    checks['ActualRunningApplicationBlocksNativeUpgrade']=install(package,'running-guard')!=0 and matches(app,old_bundle) and receipt_paths()==before
    running.terminate();running.wait(timeout=20);running=None
    if not checks['ActualRunningApplicationBlocksNativeUpgrade']:raise RuntimeError('Running application guard failed')
    checks['ActualDownloadedNativeUpgradeCompletes']=install(package,'public-upgrade')==0 and matches(app,new_bundle)
    checks['ActualInstalledVersionIs035']=plistlib.loads((app/'Contents/Info.plist').read_bytes())['CFBundleShortVersionString']=='0.3.5'
    new_receipts=list(receipt_paths()-before)
    record=json.loads(subprocess.check_output(['sudo','-n','cat',new_receipts[0]],text=True)) if len(new_receipts)==1 else {}
    # InstallGuard records every old file/link and verifies the actual backup internally.
    expected={name:'file:'+value['SHA256'] for name,value in old_bundle['Files'].items()}
    expected.update({name:'link:'+target for name,target in old_bundle['Links'].items()})
    recorded=record.get('Files',{})
    comparison={'ReceiptCount':len(new_receipts),'Target':record.get('Target'),'ExpectedTarget':str(app),
        'ExpectedEntries':len(expected),'RecordedEntries':len(recorded),
        'Missing':sorted(set(expected)-set(recorded)), 'Extra':sorted(set(recorded)-set(expected)),
        'Changed':[{'Path':name,'Expected':value,'Recorded':recorded.get(name)} for name,value in expected.items() if name in recorded and recorded[name]!=value],
        'Backup':record.get('Backup'),'Scope':'Unmodified backup assertion diagnostics; actual built baseline manifest and actual installer receipt, no user files.'}
    (out/'backup-comparison.json').write_text(json.dumps(comparison,indent=2))
    checks['UpgradeRetainsVerifiedActualPreviousAppBackup']=len(new_receipts)==1 and record.get('Files')==expected and record.get('Target')==str(app)
    checks['ActualInstalledBundleSignatureValid']=subprocess.run(['codesign','--verify','--deep','--strict',str(app)],capture_output=True,timeout=90).returncode==0
    checks['RecoveryDataAndSettingsSurviveUpgrade']=sha(sentinel)==sentinel_hash
    pdf=out/'authored.pdf';subprocess.run(['xcrun','swift',str(root/'tools/create_mac_probe_fixture.swift'),str(pdf)],check=True,timeout=60);original=sha(pdf)
    with (out/'installed-pdf.log').open('wb') as log:result=subprocess.run([str(app/'Contents/MacOS/FullOffice'),'--ui-probe',str(pdf),str(out/'installed-pdf-ui')],stdout=log,stderr=subprocess.STDOUT,timeout=45)
    ui=json.loads((out/'installed-pdf-ui/report.json').read_text())
    checks['ActualUpgradedPDFWindowAndNormalExit']=result.returncode==0 and ui['Passed'] is True and len(ui['Checks'])==8 and all(ui['Checks'].values())
    checks['UpgradedApplicationPreservesAuthoredPDF']=sha(pdf)==original
except Exception as error:failure=str(error)
finally:
    if running is not None and running.poll() is None:running.terminate();running.wait(timeout=20)
    if app.exists():
        helper=out/'native-trash-helper';trash=out/'native-trash.json'
        subprocess.run(['xcrun','swiftc','-target',platform.machine()+'-apple-macosx13.0',str(root/'tools/mac_recycle_installed.swift'),'-o',str(helper)],check=True,timeout=60)
        removed=subprocess.run(['sudo','-n','env','GITHUB_ACTIONS=true',str(helper),str(trash)],capture_output=True,timeout=60)
        checks['ActualUpgradedAppNativeTrashUninstall']=removed.returncode==0 and not app.exists()
        if not app.exists():checks['OwnInstallerReceiptRemoved']=subprocess.run(['sudo','-n','pkgutil','--forget',receipt],stdout=subprocess.DEVNULL).returncode==0
    checks['RecoveryDataSurvivesUninstall']=sha(sentinel)==sentinel_hash
    report={'Passed':failure is None and all(checks.values()),'Checks':checks,'Failure':failure,'Architecture':arch,
        'ProductSource':os.environ['GITHUB_SHA'],'Scope':'Actual published signature, full HTTPS package, original 0.3.4 baseline, running-app guard, native 0.3.5 upgrade, exact old-app backup receipt, installed PDF UI, settings preservation and administrator native Trash uninstall. Excludes product update-menu interaction, Finder authentication, notarization and physical devices.'}
    (out/'report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)
raise SystemExit(0 if report['Passed'] else 1)
