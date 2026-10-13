"""Compile and run the actual upstream native editor, using only an authored DOCX."""
from pathlib import Path
import hashlib,json,plistlib,subprocess,time,os,shutil
from zipfile import ZipFile,ZIP_DEFLATED
import xml.etree.ElementTree as ET

def exercise(app,out,root,kind='docx',lazy=False):
    resources=app/'Contents/Resources';frameworks=app/'Contents/Frameworks'
    entries=[p for p in resources.rglob('index.html') if p.as_posix().endswith('/apps/api/documents/index.html')]
    if len(entries)!=1:
        (out/'resource-entries.json').write_text(json.dumps({'Entries':[p.relative_to(app).as_posix() for p in entries],'Root':[p.name for p in resources.iterdir()]},indent=2))
        raise RuntimeError('Expected one native editor entry point')
    if not (resources/'converter').is_dir():raise RuntimeError('Upstream converter directory differs')
    work=root/'.cache/mac-office-native';work.mkdir(parents=True,exist_ok=False)
    host=work/'FullOfficeOfficeHost.app';mac=host/'Contents/MacOS';mac.mkdir(parents=True)
    host_frameworks=host/'Contents/Frameworks';host_frameworks.mkdir()
    for original in frameworks.iterdir():
        if original.name.startswith('editors_helper') and original.suffix=='.app':continue
        (host_frameworks/original.name).symlink_to(original,target_is_directory=original.is_dir())
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
    if lazy:
        bridge_build=build.copy()
        bridge_build[bridge_build.index(str(source/'MacOfficeHost.mm'))]=str(source/'MacOfficeBridge.mm')
        bridge=host_frameworks/'FullOfficeOfficeBridge.dylib'
        bridge_build[-1]=str(bridge);bridge_build.insert(2,'-dynamiclib')
        subprocess.run(bridge_build,check=True,timeout=90)
        subprocess.run(['xcrun','swiftc','-swift-version','5',str(root/'src/FullOffice.Mac/LazyOfficeProbe.swift'),
            '-framework','AppKit','-framework','PDFKit','-o',str(mac/'FullOfficeOfficeHost')],check=True,timeout=90)
    else:subprocess.run(build,check=True,timeout=90)
    helper_binary=work/'rebuilt-editors-helper'
    helper_build=build.copy();helper_build[helper_build.index(str(source/'MacOfficeHost.mm'))]=str(source/'MacOfficeHelper.cpp')
    helper_build[-1]=str(helper_binary)
    subprocess.run(helper_build,check=True,timeout=90)
    helpers=[]
    for index,name in enumerate(['editors_helper','editors_helper (Renderer)','editors_helper (GPU)']):
        original=frameworks/(name+'.app')
        target=host_frameworks/(name+'.app');target_mac=target/'Contents/MacOS';target_mac.mkdir(parents=True)
        info=plistlib.loads((original/'Contents/Info.plist').read_bytes())
        info['CFBundleIdentifier']='center.dnd.FullOffice.OfficeHostResearch.Helper'+str(index)
        (target/'Contents/Info.plist').write_bytes(plistlib.dumps(info))
        __import__('shutil').copy2(helper_binary,target_mac/name)
        subprocess.run(['codesign','--force','--sign','-',str(target)],check=True,timeout=30)
        helpers.append({'Name':name,'Executable':str(target_mac/name),'SHA256':hashlib.sha256((target_mac/name).read_bytes()).hexdigest()})
    (out/'native-helper-path.json').write_text(json.dumps({'RebuiltPinnedSDKHelper':True,'Helpers':helpers},indent=2))
    fixture=work/('native-probe.'+kind)
    with ZipFile(fixture,'w',ZIP_DEFLATED) as z:
        if kind=='docx':
            z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
            z.writestr('_rels/.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
            z.writestr('word/document.xml','<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:rPr><w:sz w:val="56"/></w:rPr><w:t>MAC_ENGINE_BODY_PROBE_20261013</w:t></w:r></w:p><w:sectPr/></w:body></w:document>')
        else:
            z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>')
            z.writestr('_rels/.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
            z.writestr('xl/workbook.xml','<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Calculation" sheetId="1" r:id="rId1"/></sheets></workbook>')
            z.writestr('xl/_rels/workbook.xml.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>')
            z.writestr('xl/worksheets/sheet1.xml','<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetViews><sheetView workbookViewId="0"><selection activeCell="A1" sqref="A1"/></sheetView></sheetViews><cols><col min="2" max="2" width="60" customWidth="1"/></cols><sheetData><row r="1"><c r="A1"><v>1</v></c><c r="B1" t="inlineStr"><is><t>MAC_ENGINE_BODY_PROBE_20261013</t></is></c></row><row r="2"><c r="A2"><v>2</v></c></row><row r="3"><c r="A3"><f>SUM(A1:A2)</f><v>3</v></c></row></sheetData></worksheet>')
    original=hashlib.sha256(fixture.read_bytes()).hexdigest()
    working=work/('working-probe.'+kind);shutil.copy2(fixture,working)
    if lazy:
        pdf=work/'lazy-probe.pdf'
        subprocess.run(['xcrun','swift',str(root/'tools/create_mac_probe_fixture.swift'),str(pdf)],check=True,timeout=60)
        pdf_hash=hashlib.sha256(pdf.read_bytes()).hexdigest()
        with (out/'lazy-host.log').open('wb') as log:
            process=subprocess.Popen([str(mac/'FullOfficeOfficeHost'),str(pdf),str(bridge),str(resources),str(work/'lazy-profile'),str(working),str(out)],stdout=log,stderr=subprocess.STDOUT)
            try:code=process.wait(timeout=120)
            except subprocess.TimeoutExpired:process.kill();process.wait(timeout=15);code=process.returncode
        report=json.loads((out/'lazy-host-report.json').read_text()) if (out/'lazy-host-report.json').is_file() else {'Passed':False,'Checks':{}}
        checks=report['Checks'];checks['ActualLazyAppClosesNormally']=code==0
        checks['AuthorPDFUnchanged']=hashlib.sha256(pdf.read_bytes()).hexdigest()==pdf_hash
        checks['AuthorOfficeDocumentUnchanged']=hashlib.sha256(fixture.read_bytes()).hexdigest()==original
        if (out/'lazy-office-window.png').is_file():
            checks['ActualLazyOfficeBodyRendered']=subprocess.run(['xcrun','swift',str(root/'tools/mac_office_ocr.swift'),str(out/'lazy-office-window.png'),str(out/'lazy-office-recognized.json')],timeout=90).returncode==0
        report['Passed']=report['Passed'] and len(checks)==10 and all(checks.values());report['HostExitCode']=code
        (out/'lazy-host-report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)
        if not report['Passed']:raise RuntimeError('Actual deferred Office integration failed')
        return
    started=time.time()
    with (out/'native-host.log').open('wb') as log:
        process=subprocess.Popen([str(mac/'FullOfficeOfficeHost'),str(resources),str(work/'profile'),str(working),str(out),str(entries[0])],
            env=dict(os.environ,FULLOFFICE_MAC_NATIVE_EDIT_PROBE='1'),stdout=log,stderr=subprocess.STDOUT)
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
    with ZipFile(working) as saved:
        if kind=='docx':
            text=''.join(ET.fromstring(saved.read('word/document.xml')).itertext())
            checks['NativeInputActuallySavedToWorkingDocument']='MAC_NATIVE_EDIT_SAVED_20261013' in text
        else:
            ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
            sheet=ET.fromstring(saved.read('xl/worksheets/sheet1.xml'))
            cells={c.get('r'):c for c in sheet.findall('.//s:c',ns)}
            values={name:cells[name].findtext('s:v',namespaces=ns) for name in ['A1','A2','A3']}
            checks['NativeInputActuallySavedToWorkingDocument']=values['A1']=='10' and values['A2']=='2'
            checks['DependentFormulaActuallyRecalculated']=values['A3']=='12'
            checks['OriginalFormulaPreserved']=cells['A3'].findtext('s:f',namespaces=ns)=='SUM(A1:A2)'
            (out/'independent-spreadsheet-values.json').write_text(json.dumps(values,indent=2))
        checks['SavedDocumentPackageValid']=saved.testzip() is None
    host_log=(out/'native-host.log').read_text(errors='replace')
    checks['ActualNativeDirtyAndSaveEvents']='DIRTY 1' in host_log and 'SAVED OK' in host_log and 'DOCUMENT_CONTENT_READY' in host_log
    shutil.copy2(working,out/('saved-working-probe.'+kind))
    report={'Passed':len(checks)==(7 if kind=='docx' else 9) and all(checks.values()),'Checks':checks,'Format':kind,'HostExitCode':code,'OwnedPid':process.pid,
        'Scope':'Actual native ONLYOFFICE Mac editor, authored '+kind.upper()+' and dispatched AppKit input events, native save request and independent package verification; not physical keyboard, complex workbook or final FullOffice integration acceptance.'}
    (out/'native-host-report.json').write_text(json.dumps(report,indent=2));print(json.dumps(report),flush=True)
    if not report['Passed']:
        # Independent upstream application control: same signed read-only bundle,
        # same authored document and desktop; this cannot make the host pass.
        if __import__('os').environ.get('GITHUB_ACTIONS')!='true':
            raise RuntimeError('Upstream reference requires the disposable hosted runner')
        installed_reference=Path.home()/'Applications/FullOfficeOnlyOfficeReference.app'
        if installed_reference.exists():raise RuntimeError('Reference installation path is not pristine')
        installed_reference.parent.mkdir(exist_ok=True)
        # Run an ordinary installed bundle. The signed DMG copy otherwise stops
        # at "Move to Applications folder?", which does not test its editor.
        subprocess.run(['ditto',str(app),str(installed_reference)],check=True,timeout=180)
        subprocess.run(['codesign','--verify','--deep','--strict',str(installed_reference)],check=True,timeout=60)
        app=installed_reference
        (out/'upstream-reference-installation.json').write_text(json.dumps({'Bundle':str(app),'SignatureVerifiedAfterCopy':True,
            'Scope':'One dedicated user Applications bundle in the disposable hosted VM; original read-only DMG unmodified'},indent=2))
        bundle_id=plistlib.loads((app/'Contents/Info.plist').read_bytes())['CFBundleIdentifier']
        # Sparkle first launch otherwise blocks this control behind its update prompt.
        # Set only this fresh hosted VM's upstream preference, before the first launch.
        subprocess.run(['defaults','write',bundle_id,'SUEnableAutomaticChecks','-bool','NO'],check=True,timeout=15)
        (out/'upstream-reference-preferences.json').write_text(json.dumps({'BundleIdentifier':bundle_id,'SUEnableAutomaticChecks':False,
            'Scope':'Disposable hosted reference only; not user settings or production update configuration'},indent=2))
        with (out/'upstream-reference.log').open('wb') as log:
            reference=subprocess.Popen([str(app/'Contents/MacOS/ONLYOFFICE'),str(fixture)],stdout=log,stderr=subprocess.STDOUT)
            time.sleep(40)
            if reference.poll() is None:
                capture=subprocess.run(['xcrun','swift',str(root/'tools/mac_office_reference.swift'),str(reference.pid),str(app),str(fixture),str(out/'upstream-reference-window.png')],capture_output=True,text=True,timeout=75)
                (out/'upstream-reference-control.json').write_text(json.dumps({'ExitCode':capture.returncode,'Stdout':capture.stdout,'Stderr':capture.stderr},indent=2))
                try:reference.wait(timeout=15)
                except subprocess.TimeoutExpired:reference.kill();reference.wait(timeout=15)
        result={'OwnedPid':reference.pid,'ExitCode':reference.returncode,'OriginalAuthorDocumentUnchanged':hashlib.sha256(fixture.read_bytes()).hexdigest()==original,
            'Scope':'Official signed upstream application control on the same authored document; not FullOffice acceptance'}
        if (out/'upstream-reference-window.png').is_file():
            result['UpstreamBodyRendered']=subprocess.run(['xcrun','swift',str(root/'tools/mac_office_ocr.swift'),str(out/'upstream-reference-window.png'),str(out/'upstream-recognized-document.json')],timeout=90).returncode==0
        (out/'upstream-reference-report.json').write_text(json.dumps(result,indent=2))
        raise RuntimeError('Actual native Office Mac window validation failed; independent upstream control recorded')
