"""Compile and run the actual upstream native editor, using only an authored DOCX."""
from pathlib import Path
import hashlib,json,plistlib,subprocess
from zipfile import ZipFile,ZIP_DEFLATED

def exercise(app,out,root):
    resources=app/'Contents/Resources';frameworks=app/'Contents/Frameworks'
    entries=[p for p in resources.rglob('index.html') if p.as_posix().endswith('/apps/api/documents/index.html')]
    if len(entries)!=1:
        (out/'resource-entries.json').write_text(json.dumps({'Entries':[p.relative_to(app).as_posix() for p in entries],'Root':[p.name for p in resources.iterdir()]},indent=2))
        raise RuntimeError('Expected one native editor entry point')
    if not (resources/'converter').is_dir():raise RuntimeError('Upstream converter directory differs')
    work=root/'.cache/mac-office-native';work.mkdir(parents=True,exist_ok=False)
    host=work/'FullOfficeOfficeHost.app';mac=host/'Contents/MacOS';mac.mkdir(parents=True)
    (host/'Contents/Frameworks').symlink_to(frameworks,target_is_directory=True)
    (host/'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleName':'FullOfficeOfficeHost','CFBundleExecutable':'FullOfficeOfficeHost',
        'CFBundleIdentifier':'center.dnd.FullOffice.OfficeHostResearch','CFBundlePackageType':'APPL','NSHighResolutionCapable':True}))
    source=root/'src/FullOffice.OfficeEngine.Native'
    build=['xcrun','clang++','-std=c++17','-fblocks','-D_MAC','-DDESKTOP_NO_USE_DYNAMIC_LIBRARY','-DQ_DECL_EXPORT=',
        '-I'+str(source/'third_party/desktop-sdk/ChromiumBasedEditors/lib/include'),'-F'+str(frameworks),
        str(source/'MacOfficeHost.mm'),'-framework','ascdocumentscore','-framework','AppKit','-Wl,-rpath,'+str(frameworks),'-o',str(mac/'FullOfficeOfficeHost')]
    subprocess.run(build,check=True,timeout=90)
    fixture=work/'native-probe.docx'
    with ZipFile(fixture,'w',ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr('_rels/.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        z.writestr('word/document.xml','<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:rPr><w:sz w:val="56"/></w:rPr><w:t>MAC_ENGINE_BODY_PROBE_20261013</w:t></w:r></w:p><w:sectPr/></w:body></w:document>')
    original=hashlib.sha256(fixture.read_bytes()).hexdigest()
    with (out/'native-host.log').open('wb') as log:
        result=subprocess.run([str(mac/'FullOfficeOfficeHost'),str(resources),str(work/'profile'),str(fixture),str(out),str(entries[0])],stdout=log,stderr=subprocess.STDOUT,timeout=100)
    checks={'ActualNativeHostClosesNormally':result.returncode==0,'OriginalAuthorDocumentUnchanged':hashlib.sha256(fixture.read_bytes()).hexdigest()==original,
            'ActualCompositorScreenshot':(out/'native-office-window.png').is_file()}
    if checks['ActualCompositorScreenshot']:
        recognition=subprocess.run(['xcrun','swift',str(root/'tools/mac_office_ocr.swift'),str(out/'native-office-window.png'),str(out/'recognized-document.json')],timeout=90)
        checks['ActualOfficeBodyRendered']=recognition.returncode==0
    report={'Passed':len(checks)==4 and all(checks.values()),'Checks':checks,'Scope':'Actual native ONLYOFFICE Mac interface and converter, generated DOCX body capture; no Office editing/save or final FullOffice integration claim.'}
    (out/'native-host-report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)
    if not report['Passed']:raise RuntimeError('Actual native Office Mac window validation failed')
