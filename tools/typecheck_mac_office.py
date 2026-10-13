"""Compile production Mac UI and save components before fetching the native runtime."""
from pathlib import Path
import subprocess,shutil,sys
from mac_sources import core_sources,full_sources

root=Path(__file__).resolve().parents[1]
work=root/'.cache/mac-office-typecheck';work.mkdir(parents=True,exist_ok=False)
sources=root/'src/FullOffice.Mac'
shared=core_sources(root)
subprocess.run(['xcrun','swiftc','-typecheck','-swift-version','5',*full_sources(root)],check=True,timeout=90)
for name in ['ProcessOfficeProbe.swift','mac_office_session_checks.swift','mac_recovery_ui_fixture.swift']:
    original=sources/name if name=='ProcessOfficeProbe.swift' else root/'tools'/name
    case=work/name;case.mkdir();shutil.copy2(original,case/'main.swift')
    subprocess.run(['xcrun','swiftc','-typecheck','-swift-version','5',*shared,str(case/'main.swift')],check=True,timeout=90)
subprocess.run(['xcrun','clang++','-fsyntax-only','-std=c++17','-fblocks','-D_MAC','-DDESKTOP_NO_USE_DYNAMIC_LIBRARY','-DQ_DECL_EXPORT=',
    '-I'+str(root/'src/FullOffice.OfficeEngine.Native/third_party/desktop-sdk/ChromiumBasedEditors/lib/include'),
    str(root/'src/FullOffice.OfficeEngine.Native/MacOfficeHost.mm')],check=True,timeout=90)
subprocess.run(['xcrun','clang++','-fsyntax-only','-fobjc-arc','-fblocks',str(sources/'MarkdownPreview.mm')],check=True,timeout=90)
