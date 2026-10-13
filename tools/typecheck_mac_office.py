"""Compile production Mac UI and save components before fetching the native runtime."""
from pathlib import Path
import subprocess,shutil,sys

root=Path(__file__).resolve().parents[1]
work=root/'.cache/mac-office-typecheck';work.mkdir(parents=True,exist_ok=False)
sources=root/'src/FullOffice.Mac'
shared=[str(sources/'OfficeFileSession.swift'),str(sources/'OfficeCoordinator.swift'),str(sources/'OfficeRecovery.swift')]
subprocess.run(['xcrun','swiftc','-typecheck','-swift-version','5',*shared,str(sources/'ProductProbe.swift'),str(sources/'main.swift')],check=True,timeout=90)
for name in ['ProcessOfficeProbe.swift','mac_office_session_checks.swift']:
    original=sources/name if name=='ProcessOfficeProbe.swift' else root/'tools'/name
    case=work/name;case.mkdir();shutil.copy2(original,case/'main.swift')
    subprocess.run(['xcrun','swiftc','-typecheck','-swift-version','5',*shared,str(case/'main.swift')],check=True,timeout=90)
subprocess.run(['xcrun','clang++','-fsyntax-only','-std=c++17','-fblocks','-D_MAC','-DDESKTOP_NO_USE_DYNAMIC_LIBRARY','-DQ_DECL_EXPORT=',
    '-I'+str(root/'src/FullOffice.OfficeEngine.Native/third_party/desktop-sdk/ChromiumBasedEditors/lib/include'),
    str(root/'src/FullOffice.OfficeEngine.Native/MacOfficeHost.mm')],check=True,timeout=90)
