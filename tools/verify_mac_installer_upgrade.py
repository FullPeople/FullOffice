"""Install the actual published baseline and require every old backup record."""
from pathlib import Path
import hashlib,json,os,platform,plistlib,subprocess,sys,uuid

if os.environ.get('GITHUB_ACTIONS')!='true' or os.environ.get('GITHUB_REPOSITORY')!='FullPeople/FullOffice':raise RuntimeError('Requires disposable hosted product runner')
root=Path(__file__).resolve().parents[1];out=root/'artifacts/mac-upgrade-036';out.mkdir(parents=True,exist_ok=False)
app=Path('/Applications/FullOffice.app');receipt='center.dnd.FullOffice.pkg'
if app.exists() or app.is_symlink() or subprocess.run(['pkgutil','--pkg-info',receipt],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0:raise RuntimeError('Existing installation protected')
arch='arm64' if platform.machine()=='arm64' else 'x64'
old_bundle=json.loads((root/'artifacts/upgrade-baseline-reports/macos-full/bundle-manifest.json').read_text())
new_bundle=json.loads((root/'artifacts/upgrade-candidate-reports/macos-full/bundle-manifest.json').read_text())
meta=json.loads((root/'artifacts/upgrade-candidate-reports/mac-package/package.json').read_text())
baseline=root/'artifacts/upgrade-baseline'/f'FullOffice-0.3.5-Setup-macos-{arch}.pkg'
candidate=root/'artifacts/upgrade-candidate'/meta['File']
expected_old={'arm64':'e65d4e1ab4308d482dca0e5324aa36de1332250998ac26ef426302296a765a17','x64':'112dca90fab6188a8cf1ed32a23a9bf6a7d3e1483a768ee642f2fb14aaeb4845'}[arch]
def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
if sha(baseline)!=expected_old or sha(candidate)!=meta['SHA256'] or meta['Version']!='0.3.6' or meta['ProductSource']!='66fc77297edc37db57bd4d7efc3d810e791d5139':raise RuntimeError('Immutable old or accepted new installer differs')
def matches(path,bundle):
    return all((path/name).is_file() and sha(path/name)==value['SHA256'] for name,value in bundle['Files'].items()) and all((path/name).is_symlink() and os.readlink(path/name)==target for name,target in bundle['Links'].items())
def receipts():
    return set(json.loads(subprocess.check_output(['sudo','-n',sys.executable,'-c',"from pathlib import Path;import json;print(json.dumps([str(p) for p in Path('/Library/Application Support/FullOffice/InstallBackups').glob('*/receipt.json')]))"],text=True)))
def install(package,name):
    with (out/(name+'.log')).open('wb') as log:return subprocess.run(['sudo','-n','installer','-pkg',str(package),'-target','/'],stdout=log,stderr=subprocess.STDOUT,timeout=240).returncode
checks={};failure=None
data=Path.home()/'Library/Application Support/FullOffice/EditingSessions'/str(uuid.uuid4());data.mkdir(parents=True,exist_ok=False)
sentinel=data/'preserved.txt';sentinel.write_text('Authored recovery data survives the native upgrade.');sentinel_hash=sha(sentinel)
try:
    checks['ActualPublished035BaselineInstalls']=install(baseline,'baseline')==0 and matches(app,old_bundle)
    checks['ActualBaselineVersionIs035']=plistlib.loads((app/'Contents/Info.plist').read_bytes())['CFBundleShortVersionString']=='0.3.5'
    if not checks['ActualPublished035BaselineInstalls'] or not checks['ActualBaselineVersionIs035']:raise RuntimeError('Actual published baseline failed')
    before=receipts()
    checks['Actual036NativeUpgradeCompletes']=install(candidate,'upgrade')==0 and matches(app,new_bundle)
    checks['ActualInstalledVersionIs036']=plistlib.loads((app/'Contents/Info.plist').read_bytes())['CFBundleShortVersionString']=='0.3.6'
    added=list(receipts()-before);record=json.loads(subprocess.check_output(['sudo','-n','cat',added[0]],text=True)) if len(added)==1 else {}
    expected={name:'file:'+value['SHA256'] for name,value in old_bundle['Files'].items()};expected.update({name:'link:'+target for name,target in old_bundle['Links'].items()})
    recorded=record.get('Files',{})
    (out/'backup-comparison.json').write_text(json.dumps({'ReceiptCount':len(added),'ExpectedEntries':len(expected),'RecordedEntries':len(recorded),'Missing':sorted(set(expected)-set(recorded)),'Extra':sorted(set(recorded)-set(expected)),'Changed':[name for name,value in expected.items() if name in recorded and recorded[name]!=value],'Target':record.get('Target'),'Scope':'Exact complete old built bundle versus actual new installer backup receipt'},indent=2))
    checks['EveryActualPreviousFileAndLinkInBackupReceipt']=len(added)==1 and recorded==expected and record.get('Target')==str(app)
    backup=record.get('Backup','')
    verify="from pathlib import Path;import hashlib,json,os,sys;b=Path(sys.argv[1]);r=Path('/Library/Application Support/FullOffice/InstallBackups');assert b.resolve().is_relative_to(r.resolve());m=json.loads(Path(sys.argv[2]).read_text());ok=all((b/n).is_file() and hashlib.file_digest((b/n).open('rb'),'sha256').hexdigest()==v['SHA256'] for n,v in m['Files'].items()) and all((b/n).is_symlink() and os.readlink(b/n)==t for n,t in m['Links'].items());sys.exit(0 if ok else 1)"
    checks['EveryActualPreviousBackupByteAndLinkIndependentlyMatches']=bool(backup) and subprocess.run(['sudo','-n',sys.executable,'-c',verify,backup,str(root/'artifacts/upgrade-baseline-reports/macos-full/bundle-manifest.json')],timeout=180).returncode==0
    checks['ActualUpgradedBundleSignatureValid']=subprocess.run(['codesign','--verify','--deep','--strict',str(app)],capture_output=True,timeout=90).returncode==0
    checks['ActualUpgradePreservesRecoveryData']=sha(sentinel)==sentinel_hash
    pdf=out/'authored.pdf';subprocess.run(['xcrun','swift',str(root/'tools/create_mac_probe_fixture.swift'),str(pdf)],check=True,timeout=60);original=sha(pdf)
    with (out/'pdf.log').open('wb') as log:result=subprocess.run([str(app/'Contents/MacOS/FullOffice'),'--ui-probe',str(pdf),str(out/'pdf-ui')],stdout=log,stderr=subprocess.STDOUT,timeout=45)
    ui=json.loads((out/'pdf-ui/report.json').read_text())
    checks['ActualUpgradedPDFWindowRunsAndExits']=result.returncode==0 and ui['Passed'] is True and len(ui['Checks'])==8 and all(ui['Checks'].values())
    checks['ActualUpgradedPDFPreservesOriginal']=sha(pdf)==original
except Exception as error:failure=str(error)
finally:
    if app.exists():
        helper=out/'native-trash';report=out/'native-trash.json'
        subprocess.run(['xcrun','swiftc','-target',platform.machine()+'-apple-macosx13.0',str(root/'tools/mac_recycle_installed.swift'),'-o',str(helper)],check=True,timeout=60)
        checks['ActualNativeTrashUninstallCompletes']=subprocess.run(['sudo','-n','env','GITHUB_ACTIONS=true',str(helper),str(report)],capture_output=True,timeout=60).returncode==0 and not app.exists()
        if not app.exists():checks['OwnNativeReceiptRemoved']=subprocess.run(['sudo','-n','pkgutil','--forget',receipt],stdout=subprocess.DEVNULL).returncode==0
    checks['RecoveryDataSurvivesUninstall']=sha(sentinel)==sentinel_hash
    report={'Passed':failure is None and len(checks)==13 and all(checks.values()),'Checks':checks,'Failure':failure,'PackageSource':meta['ProductSource'],'ValidationSource':os.environ['GITHUB_SHA'],'Scope':'Actual public 0.3.5 package to exact accepted 0.3.6 candidate, entire old backup receipt and independent full backup rehash, installed PDF UI, data preservation and native uninstall; not public signed update-menu or physical-device acceptance.'}
    (out/'report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)
raise SystemExit(0 if report['Passed'] else 1)
