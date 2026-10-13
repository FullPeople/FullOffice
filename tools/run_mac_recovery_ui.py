"""Run real menu, alert, system Save panel and native editor through XCTest."""
from pathlib import Path
import os,subprocess,shutil,sys

if os.environ.get('GITHUB_ACTIONS')!='true':raise RuntimeError('Requires disposable hosted macOS')
root=Path(__file__).resolve().parents[1]
out=root/'artifacts/mac-recovery-ui'
work=root/'.cache/mac-recovery-ui';work.mkdir(parents=True,exist_ok=True)
build=['xcodebuild','-project',str(root/'tests/mac-recovery-ui/RecoveryUI.xcodeproj'),
    '-scheme','RecoveryUI','-destination','platform=macOS','-derivedDataPath',str(work/'build'),'CODE_SIGNING_ALLOWED=NO']
if '--compile-only' in sys.argv:
    subprocess.run([build[0],'build-for-testing',*build[1:]],check=True,timeout=240)
    sys.exit(0)
shutil.copy2(root/'tools/mac_recovery_ui_fixture.swift',work/'main.swift')
subprocess.run(['xcrun','swiftc','-swift-version','5',str(root/'src/FullOffice.Mac/OfficeFileSession.swift'),
    str(root/'src/FullOffice.Mac/OfficeRecovery.swift'),str(work/'main.swift'),'-o',str(work/'fixture')],check=True,timeout=90)
app=root/'artifacts/macos-full/FullOffice.app'
subprocess.run([str(work/'fixture'),str(root/'.cache/mac-office-session-checks/fixtures'),str(out),str(app)],check=True,timeout=60)
register='/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
for bundle in [app,app/'Contents/Helpers/FullOfficeOfficeHost.app']:
    subprocess.run([register,'-f',str(bundle)],check=True,timeout=20)
subprocess.run([build[0],'test-without-building',*build[1:],'-resultBundlePath',str(work/'results.xcresult')],check=True,timeout=360)
