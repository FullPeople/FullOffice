"""Operate the published old app's actual menu and retained signed package."""
from pathlib import Path
import hashlib,json,os,platform,shutil,subprocess

if os.environ.get('GITHUB_ACTIONS')!='true' or os.environ.get('GITHUB_REPOSITORY')!='FullPeople/FullOffice':raise RuntimeError('Requires disposable hosted product runner')
root=Path(__file__).resolve().parents[1];out=root/'artifacts/mac-public-update-ui';out.mkdir(parents=True,exist_ok=False)
app=Path('/Applications/FullOffice.app');receipt='center.dnd.FullOffice.pkg'
if app.exists() or app.is_symlink() or subprocess.run(['pkgutil','--pkg-info',receipt],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0:raise RuntimeError('Existing application protected')
if subprocess.run(['pgrep','-x','Installer'],stdout=subprocess.DEVNULL).returncode==0:raise RuntimeError('Existing system Installer protected')
arch='arm64' if platform.machine()=='arm64' else 'x64'
baseline=root/'artifacts/upgrade-baseline'/f'FullOffice-0.3.5-Setup-macos-{arch}.pkg'
old_hash={'arm64':'e65d4e1ab4308d482dca0e5324aa36de1332250998ac26ef426302296a765a17','x64':'112dca90fab6188a8cf1ed32a23a9bf6a7d3e1483a768ee642f2fb14aaeb4845'}[arch]
new_hash={'arm64':'fb312c4798a9725a26a86471f3f51e691196f3fd4c19e80ad1af8a742f2352ea','x64':'e71bdfcdf1a640ad102c807955e6d53c3c6569f231aa9efc317e852c208bfe13'}[arch]
def sha(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
if sha(baseline)!=old_hash:raise RuntimeError('Actual public old installer differs')
source=out/'authored.md';source.write_text('# Preserved original update guard body\n',encoding='utf-8')
sessions=out/'sessions';sessions.mkdir()
downloads=Path.home()/'Library/Application Support/FullOffice/Updates'
if downloads.exists():raise RuntimeError('Existing update cache protected; requires fresh runner')
(out/'fixture.json').write_text(json.dumps({'Source':str(source),'Root':str(sessions),'OriginalSHA256':sha(source),'ExpectedSHA256':new_hash,'DownloadsRoot':str(downloads)}))
work=root/'.cache/mac-public-update-ui';work.mkdir(parents=True,exist_ok=False)
build=['xcodebuild','-project',str(root/'tests/mac-recovery-ui/RecoveryUI.xcodeproj'),'-scheme','RecoveryUI','-destination','platform=macOS','-derivedDataPath',str(work/'build'),'CODE_SIGNING_ALLOWED=NO']
subprocess.run([build[0],'build-for-testing',*build[1:]],check=True,timeout=240)
try:
    with (out/'baseline-install.log').open('wb') as log:subprocess.run(['sudo','-n','installer','-pkg',str(baseline),'-target','/'],stdout=log,stderr=subprocess.STDOUT,check=True,timeout=240)
    register='/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
    subprocess.run([register,'-f',str(app)],check=True,timeout=20)
    subprocess.run([build[0],'test-without-building',*build[1:],'-only-testing:RecoveryUI/UpdateUITests','-resultBundlePath',str(work/'results.xcresult')],check=True,timeout=2100)
    report=json.loads((out/'report.json').read_text())
    if report['Passed'] is not True or len(report['Checks'])!=14 or not all(report['Checks'].values()):raise RuntimeError('Actual published menu checks incomplete')
    retained=json.loads((out/'actual-download.json').read_text());package=Path(retained['Package'])
    if not package.resolve().is_relative_to(downloads.resolve()) or sha(package)!=new_hash or retained['SHA256']!=new_hash:raise RuntimeError('Actual menu download bytes differ')
    target=root/'artifacts/upgrade-candidate'/f'FullOffice-0.3.6-Setup-macos-{arch}.pkg';target.parent.mkdir(parents=True,exist_ok=True)
    if target.exists():raise RuntimeError('Existing upgrade package protected')
    shutil.copy2(package,target)
    print(json.dumps({'ActualPublishedMenuChecks':14,'DownloadedPackageSHA256':sha(target),'Source':os.environ['GITHUB_SHA']}))
finally:
    if app.exists():
        helper=out/'native-trash';trash=out/'native-trash.json'
        subprocess.run(['xcrun','swiftc','-target',platform.machine()+'-apple-macosx13.0',str(root/'tools/mac_recycle_installed.swift'),'-o',str(helper)],check=True,timeout=60)
        removed=subprocess.run(['sudo','-n','env','GITHUB_ACTIONS=true',str(helper),str(trash)],capture_output=True,timeout=60)
        if removed.returncode!=0 or app.exists():raise RuntimeError('Owned baseline cleanup failed')
        subprocess.run(['sudo','-n','pkgutil','--forget',receipt],check=True,stdout=subprocess.DEVNULL)
