"""Verify upstream Mac runtime and inspect its real native editor integration ABI."""
from pathlib import Path
import hashlib,json,platform,subprocess,urllib.request,time,shlex
import argparse

parser=argparse.ArgumentParser();parser.add_argument('--native-window',action='store_true')
parser.add_argument('--native-kind',choices=['docx','xlsx'],default='docx')
parser.add_argument('--lazy-host',action='store_true');args=parser.parse_args()

root=Path(__file__).resolve().parents[1]
architecture=platform.machine()
assets={
    'arm64':('ONLYOFFICE-arm.dmg',542306356,'e965be2222609add6b5a70baa2a8cdb599402491fb2925825d9039dcb154beb4'),
    'x86_64':('ONLYOFFICE-x86_64.dmg',555347165,'43ac517493c0c316f268ce4b7dc3810b77a7aefe83c0edc1655476d9f21681d2'),
}
name,size,expected=assets[architecture]
work=root/'.cache/mac-office-abi';work.mkdir(parents=True,exist_ok=False)
out=root/'artifacts/mac-office-abi';out.mkdir(parents=True,exist_ok=False)
dmg=work/name;digest=hashlib.sha256();count=0
url='https://github.com/ONLYOFFICE/DesktopEditors/releases/download/v9.4.0/'+name
with urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'FullOffice-runtime-research/1.0'}),timeout=60) as response,dmg.open('xb') as stream:
    if response.status!=200 or not response.geturl().startswith('https://'):raise RuntimeError('Runtime HTTPS verification failed')
    while block:=response.read(1024*1024):
        count+=len(block)
        if count>size:raise RuntimeError('Runtime exceeds fixed expected size')
        digest.update(block);stream.write(block)
if count!=size or digest.hexdigest()!=expected:raise RuntimeError('Upstream runtime digest differs')
mount=work/'mounted';mount.mkdir()
subprocess.run(['hdiutil','attach','-readonly','-nobrowse','-mountpoint',str(mount),str(dmg)],check=True,timeout=90,stdout=subprocess.DEVNULL)
try:
    apps=list(mount.glob('*.app'))
    if len(apps)!=1:raise RuntimeError('Unexpected upstream bundle count')
    app=apps[0]
    checked=subprocess.run(['codesign','--verify','--deep','--strict',str(app)],capture_output=True,text=True,timeout=60)
    structure=[p.relative_to(app).as_posix() for folder in ['Contents/MacOS','Contents/Frameworks'] if (app/folder).exists() for p in (app/folder).rglob('*') if p.is_file() and (p.suffix=='.dylib' or p.name.endswith('Framework') or p.parent.name=='MacOS')]
    cores=[p for p in app.rglob('*') if p.is_file() and ('ascdocumentscore' in p.name.lower())]
    report={'RuntimeVerified':True,'Architecture':architecture,'Asset':name,'Bytes':count,'SHA256':expected,'BundleSignatureVerified':checked.returncode==0,'CoreLibraries':[p.relative_to(app).as_posix() for p in cores],'RelevantBundleStructure':structure,
            'Scope':'Read-only upstream Mac binary ABI inspection, no user data, no claim of FullOffice Office editor execution.'}
    for index,core in enumerate(cores):
        symbols=subprocess.run(['nm','-gU',str(core)],capture_output=True,text=True,timeout=60)
        demangled=subprocess.run(['c++filt'],input=symbols.stdout,capture_output=True,text=True,timeout=30)
        wanted=[line for line in demangled.stdout.splitlines() if any(token in line for token in ['CCefViewWrapper','CAscApplicationManager','CApplicationCEF','NSAscApplication','ExecuteInAllFrames','OpenLocalFile'])]
        (out/f'core-{index}-integration-symbols.txt').write_text('\n'.join(wanted),encoding='utf-8')
        deps=subprocess.run(['otool','-L',str(core)],capture_output=True,text=True,timeout=30)
        (out/f'core-{index}-dependencies.txt').write_text(deps.stdout,encoding='utf-8')
        report.setdefault('CoreSymbolCounts',[]).append(len(wanted))
        if args.lazy_host:
            # Inspect the actual pinned binary's close implementation rather
            # than assuming the published headers describe every Mac path.
            disassembly=subprocess.run(['lldb','--batch','-o','target create '+shlex.quote(str(core)),
                '-o','disassemble -n "CAscApplicationManager::DestroyCefView(int, bool)" -c 120'],
                capture_output=True,text=True,timeout=40)
            (out/f'core-{index}-close-disassembly.txt').write_text(disassembly.stdout+disassembly.stderr,encoding='utf-8')
    (out/'report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps({k:report[k] for k in ['RuntimeVerified','Architecture','BundleSignatureVerified','CoreLibraries','CoreSymbolCounts'] if k in report}),flush=True)
    if args.native_window:
        from mac_office_native_smoke import exercise
        exercise(app,out,root,args.native_kind,args.lazy_host)
finally:
    for attempt in range(10):
        cleanup=subprocess.run(['hdiutil','detach',str(mount)],capture_output=True,text=True,timeout=15)
        if cleanup.returncode==0:break
        if cleanup.returncode!=16:break
        time.sleep(1)
    (out/'mount-cleanup.json').write_text(json.dumps({'Detached':cleanup.returncode==0,'ExitCode':cleanup.returncode,'Attempts':attempt+1,'Error':cleanup.stderr[:1000]},indent=2))
    if cleanup.returncode:print(json.dumps({'OwnedMountCleanupDeferred':True,'ExitCode':cleanup.returncode}),flush=True)
