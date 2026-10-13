"""Run the actual self-contained product app, then independently verify its saved files."""
from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED
import hashlib,json,os,shutil,signal,subprocess
import xml.etree.ElementTree as ET

def fixture(path,kind):
    with ZipFile(path,'w',ZIP_DEFLATED) as z:
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

def run(app,root,out):
    if os.environ.get('GITHUB_ACTIONS')!='true':raise RuntimeError('Actual app probe requires the disposable hosted environment')
    out.mkdir(parents=True,exist_ok=False)
    pdf=out/'authored.pdf';subprocess.run(['xcrun','swift',str(root/'tools/create_mac_probe_fixture.swift'),str(pdf)],check=True,timeout=60)
    pdf_hash=hashlib.sha256(pdf.read_bytes()).hexdigest();reports=[]
    for kind in ['docx','xlsx']:
        case=out/kind;case.mkdir();author=case/('author.'+kind);fixture(author,kind)
        before=hashlib.sha256(author.read_bytes()).hexdigest();working=case/('selected.'+kind);shutil.copy2(author,working)
        probe=case/'probe'
        with (case/'product.log').open('wb') as log:
            process=subprocess.Popen([str(app/'Contents/MacOS/FullOffice'),'--office-ui-probe',str(pdf),str(working),str(probe)],
                stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
            try:code=process.wait(timeout=140)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid,signal.SIGKILL);process.wait(timeout=15);code=process.returncode
        report=json.loads((probe/'product-report.json').read_text()) if (probe/'product-report.json').is_file() else {'Passed':False,'Checks':{}}
        checks=report['Checks'];checks['ActualMainExitsNormally']=code==0
        checks['AuthorOfficeFileUnchanged']=hashlib.sha256(author.read_bytes()).hexdigest()==before
        checks['AuthorPDFUnchanged']=hashlib.sha256(pdf.read_bytes()).hexdigest()==pdf_hash
        image=probe/'native-office-window.png'
        checks['ActualOfficeBodyRendered']=image.is_file() and subprocess.run(['xcrun','swift',str(root/'tools/mac_office_ocr.swift'),str(image),str(case/'recognized.json')],timeout=90).returncode==0
        with ZipFile(working) as saved:
            checks['SavedZIPValid']=saved.testzip() is None
            if kind=='docx':checks['NativeTypedTextActuallySaved']='MAC_NATIVE_EDIT_SAVED_20261013' in ''.join(ET.fromstring(saved.read('word/document.xml')).itertext())
            else:
                ns={'s':'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
                cells={c.get('r'):c for c in ET.fromstring(saved.read('xl/worksheets/sheet1.xml')).findall('.//s:c',ns)}
                values={name:cells[name].findtext('s:v',namespaces=ns) for name in ['A1','A2','A3']}
                checks['NativeInputActuallySaved']=values['A1']=='10' and values['A2']=='2'
                checks['FormulaActuallyRecalculated']=values['A3']=='12'
                checks['OriginalFormulaPreserved']=cells['A3'].findtext('s:f',namespaces=ns)=='SUM(A1:A2)'
                (case/'independent-values.json').write_text(json.dumps(values,indent=2))
        report['Passed']=report['Passed'] and len(checks)==(20 if kind=='docx' else 22) and all(checks.values())
        report['ExitCode']=code;report['Format']=kind
        (case/'report.json').write_text(json.dumps(report,indent=2));reports.append(report)
        print(json.dumps({'Format':kind,'Passed':report['Passed'],'Checks':checks}),flush=True)
        if not report['Passed']:raise RuntimeError('Actual self-contained product app failed '+kind)
    (out/'report.json').write_text(json.dumps({'Passed':all(r['Passed'] for r in reports),'Results':reports,'Scope':'Actual full product app after upstream mount detached; independent native edits, original saves and PDF navigation. Not installer/update acceptance.'},indent=2))
