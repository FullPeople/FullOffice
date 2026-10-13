"""Run real menu, alert, system Save panel and native editor through XCTest."""
from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED
import os,subprocess,shutil,sys,json

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
fixtures=work/'authored-files';fixtures.mkdir(exist_ok=False)
for name,text in [('original','ORIGINAL_BODY_20261014'),('edited','RECOVERED_BODY_20261014')]:
    with ZipFile(fixtures/(name+'.docx'),'w',ZIP_DEFLATED) as zipped:
        zipped.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        zipped.writestr('_rels/.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        zipped.writestr('word/document.xml','<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:rPr><w:sz w:val="40"/></w:rPr><w:t>'+text+'</w:t></w:r></w:p><w:sectPr/></w:body></w:document>')
subprocess.run([str(work/'fixture'),str(fixtures),str(out),str(app)],check=True,timeout=60)
register='/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
for bundle in [app,app/'Contents/Helpers/FullOfficeOfficeHost.app']:
    subprocess.run([register,'-f',str(bundle)],check=True,timeout=20)
subprocess.run([build[0],'test-without-building',*build[1:],'-resultBundlePath',str(work/'results.xcresult')],check=True,timeout=360)
report=json.loads((out/'report.json').read_text())
if not report['Passed'] or len(report['Checks'])!=13 or not all(report['Checks'].values()):
    raise RuntimeError('Actual recovery UI checks did not all execute and pass')
