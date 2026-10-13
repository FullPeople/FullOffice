"""Exercise real production saves with independently authored files on macOS."""
from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED
import subprocess,shutil
from mac_sources import core_sources

root=Path(__file__).resolve().parents[1]
work=root/'.cache/mac-office-session-checks';work.mkdir(parents=True,exist_ok=False)
fixtures=work/'fixtures';fixtures.mkdir()
for name,text in [('original','Original authored bytes'),('edited','Edited authored bytes'),('later','Second authored edit')]:
    with ZipFile(fixtures/(name+'.docx'),'w',ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr('_rels/.rels','<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        z.writestr('word/document.xml','<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>'+text+'</w:t></w:r></w:p></w:body></w:document>')
for name in ['missing-relationship','wrong-relationship','external-relationship']:
    with ZipFile(fixtures/'edited.docx') as original, ZipFile(fixtures/(name+'.docx'),'w',ZIP_DEFLATED) as modified:
        for entry in original.infolist():
            content=original.read(entry)
            if entry.filename=='_rels/.rels':
                if name=='missing-relationship':continue
                if name=='wrong-relationship':content=content.replace(b'word/document.xml',b'xl/workbook.xml')
                else:content=content.replace(b'Target="',b'TargetMode="External" Target="')
            modified.writestr(entry.filename,content)
shutil.copy2(root/'tools/mac_office_session_checks.swift',work/'main.swift')
exe=work/'office-session-checks'
subprocess.run(['xcrun','swiftc','-swift-version','5',*core_sources(root),str(work/'main.swift'),'-o',str(exe)],check=True,timeout=90)
output=root/'artifacts/mac-office-session-checks';output.parent.mkdir(exist_ok=True)
subprocess.run([str(exe),str(fixtures),str(output)],check=True,timeout=120)
