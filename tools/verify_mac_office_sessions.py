"""Exercise real production saves with independently authored files on macOS."""
from pathlib import Path
from zipfile import ZipFile,ZIP_DEFLATED
import subprocess,shutil

root=Path(__file__).resolve().parents[1]
work=root/'.cache/mac-office-session-checks';work.mkdir(parents=True,exist_ok=False)
fixtures=work/'fixtures';fixtures.mkdir()
for name,text in [('original','Original authored bytes'),('edited','Edited authored bytes'),('later','Second authored edit')]:
    with ZipFile(fixtures/(name+'.docx'),'w',ZIP_DEFLATED) as z:
        z.writestr('[Content_Types].xml','<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        z.writestr('word/document.xml','<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>'+text+'</w:t></w:r></w:p></w:body></w:document>')
shutil.copy2(root/'tools/mac_office_session_checks.swift',work/'main.swift')
exe=work/'office-session-checks'
subprocess.run(['xcrun','swiftc','-swift-version','5',str(root/'src/FullOffice.Mac/OfficeFileSession.swift'),str(root/'src/FullOffice.Mac/OfficeRecovery.swift'),str(work/'main.swift'),'-o',str(exe)],check=True,timeout=90)
output=root/'artifacts/mac-office-session-checks';output.parent.mkdir(exist_ok=True)
subprocess.run([str(exe),str(fixtures),str(output)],check=True,timeout=120)
