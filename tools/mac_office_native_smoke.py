"""Compile and run the actual upstream native editor, using only an authored DOCX."""
from pathlib import Path
import hashlib,json,plistlib,subprocess,time
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
    (host/'Contents/Resources').symlink_to(resources,target_is_directory=True)
    (host/'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleName':'FullOfficeOfficeHost','CFBundleExecutable':'FullOfficeOfficeHost',
        'CFBundleIdentifier':'center.dnd.FullOffice.OfficeHostResearch','CFBundlePackageType':'APPL','NSHighResolutionCapable':True}))
    source=root/'src/FullOffice.OfficeEngine.Native'
    framework_roots=sorted({frameworks,*[p.parent for p in app.rglob('*.framework') if p.is_dir()]})
    build=['xcrun','clang++','-std=c++17','-fblocks','-D_MAC','-DDESKTOP_NO_USE_DYNAMIC_LIBRARY','-DQ_DECL_EXPORT=',
        '-I'+str(source/'third_party/desktop-sdk/ChromiumBasedEditors/lib/include'),'-F'+str(frameworks),
        str(source/'MacOfficeHost.mm'),'-framework','ascdocumentscore','-framework','AppKit',
        *['-Wl,-rpath,'+str(path) for path in framework_roots],'-o',str(mac/'FullOfficeOfficeHost')]
    (out/'native-library-search-paths.json').write_text(json.dumps([p.relative_to(app).as_posix() for p in framework_roots],indent=2))
    helper=host/'Contents/Frameworks/editors_helper.app/Contents/MacOS/editors_helper'
    (out/'native-helper-path.json').write_text(json.dumps({'Exists':helper.is_file(), 'Resolved':str(helper.resolve()),
        'Bytes':helper.stat().st_size if helper.is_file() else None},indent=2))
    subprocess.run(build,check=True,timeout=90)
    fixture=work/'native-probe.docx'
    with ZipFile(fixture,'w',ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr('_rels/.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        z.writestr('word/document.xml','<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:rPr><w:sz w:val="56"/></w:rPr><w:t>MAC_ENGINE_BODY_PROBE_20261013</w:t></w:r></w:p><w:sectPr/></w:body></w:document>')
    original=hashlib.sha256(fixture.read_bytes()).hexdigest()
    started=time.time()
    with (out/'native-host.log').open('wb') as log:
        process=subprocess.Popen([str(mac/'FullOfficeOfficeHost'),str(resources),str(work/'profile'),str(fixture),str(out),str(entries[0])],stdout=log,stderr=subprocess.STDOUT)
        time.sleep(10)
        listing=subprocess.check_output(['ps','-axo','pid=,ppid=,command='],text=True)
        rows=[]
        for line in listing.splitlines():
            parts=line.strip().split(None,2)
            if len(parts)==3:rows.append({'PID':int(parts[0]),'ParentPID':int(parts[1]),'Command':parts[2]})
        owned={process.pid}
        while True:
            expanded=owned|{row['PID'] for row in rows if row['ParentPID'] in owned}
            if expanded==owned:break
            owned=expanded
        (out/'native-owned-processes.json').write_text(json.dumps([row for row in rows if row['PID'] in owned],indent=2))
        try:code=process.wait(timeout=100)
        except subprocess.TimeoutExpired:
            process.kill();process.wait(timeout=15);code=process.returncode
            (out/'native-timeout.json').write_text(json.dumps({'OwnedPid':process.pid,'TimedOut':True}))
    crash_reports=[]
    diagnostic_root=Path.home()/'Library/Logs/DiagnosticReports'
    diagnostics=[*diagnostic_root.glob('FullOfficeOfficeHost*.ips'),*diagnostic_root.glob('editors_helper*.ips')]
    for diagnostic in diagnostics:
        if diagnostic.stat().st_mtime<started:continue
        try:
            decoder=json.JSONDecoder();raw=diagnostic.read_text();_,end=decoder.raw_decode(raw)
            body=json.loads(raw[end:].strip())
            if body.get('pid') not in owned:continue
            crash={'OwnedPid':body.get('pid'),'ProcessName':body.get('procName'),'Exception':body.get('exception'),'Termination':body.get('termination'),
                'FaultingThread':body.get('faultingThread'),'Images':[{'name':p.get('name'),'path':p.get('path'),'arch':p.get('arch')} for p in body.get('usedImages',[])]}
            if isinstance(body.get('faultingThread'),int):crash['Frames']=body['threads'][body['faultingThread']].get('frames')
            crash_reports.append(crash)
        except (ValueError,KeyError,IndexError):continue
    (out/'owned-native-crashes.json').write_text(json.dumps(crash_reports,indent=2))
    checks={'ActualNativeHostClosesNormally':code==0,'OriginalAuthorDocumentUnchanged':hashlib.sha256(fixture.read_bytes()).hexdigest()==original,
            'ActualCompositorScreenshot':(out/'native-office-window.png').is_file()}
    if checks['ActualCompositorScreenshot']:
        recognition=subprocess.run(['xcrun','swift',str(root/'tools/mac_office_ocr.swift'),str(out/'native-office-window.png'),str(out/'recognized-document.json')],timeout=90)
        checks['ActualOfficeBodyRendered']=recognition.returncode==0
    report={'Passed':len(checks)==4 and all(checks.values()),'Checks':checks,'HostExitCode':code,'OwnedPid':process.pid,'Scope':'Actual native ONLYOFFICE Mac interface and converter, generated DOCX body capture; no Office editing/save or final FullOffice integration claim.'}
    (out/'native-host-report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)
    if not report['Passed']:raise RuntimeError('Actual native Office Mac window validation failed')
