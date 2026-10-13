"""Build a self-contained native Mac candidate from the verified upstream runtime."""
from pathlib import Path
import hashlib,json,os,platform,plistlib,shutil,subprocess
import xml.etree.ElementTree as ET
from mac_sources import full_sources,swift_target
from build_mac_markdown_preview import build as build_markdown_preview

def digest(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()

def build(upstream,root,out):
    if platform.system()!='Darwin':raise RuntimeError('Requires native macOS')
    if shutil.disk_usage(out.parent).free < 6*1024**3:raise RuntimeError('Insufficient self-contained build headroom')
    subprocess.run(['codesign','--verify','--deep','--strict',str(upstream)],check=True,timeout=90)
    out.mkdir(parents=True,exist_ok=False)
    app=out/'FullOffice.app';contents=app/'Contents';mac=contents/'MacOS';resources=contents/'Resources'
    mac.mkdir(parents=True);resources.mkdir()
    host=contents/'Helpers/FullOfficeOfficeHost.app';host_mac=host/'Contents/MacOS';host_mac.mkdir(parents=True)
    host_frameworks=host/'Contents/Frameworks';host_frameworks.mkdir()
    originals=upstream/'Contents/Frameworks'
    for original in originals.iterdir():
        if original.name.startswith('editors_helper') and original.suffix=='.app':continue
        subprocess.run(['ditto',str(original),str(host_frameworks/original.name)],check=True,timeout=240)
    subprocess.run(['ditto',str(upstream/'Contents/Resources'),str(host/'Contents/Resources')],check=True,timeout=240)
    source=root/'src/FullOffice.OfficeEngine.Native'
    framework_roots=sorted({originals,*[p.parent for p in upstream.rglob('*.framework') if p.is_dir()]})
    search=[]
    for path in framework_roots:
        relative=path.relative_to(upstream/'Contents')
        search.append('-Wl,-rpath,@executable_path/../'+relative.as_posix())
    compile=['xcrun','clang++','-std=c++17','-fblocks','-mmacosx-version-min=13.0','-D_MAC','-DDESKTOP_NO_USE_DYNAMIC_LIBRARY','-DQ_DECL_EXPORT=',
        '-I'+str(source/'third_party/desktop-sdk/ChromiumBasedEditors/lib/include'),'-F'+str(host_frameworks),
        str(source/'MacOfficeHost.mm'),'-framework','ascdocumentscore','-framework','AppKit',*search,'-o',str(host_mac/'FullOfficeOfficeHost')]
    subprocess.run(compile,check=True,timeout=90)
    (out/'host-load-commands.txt').write_text(subprocess.check_output(['otool','-l',str(host_mac/'FullOfficeOfficeHost')],text=True))
    (host/'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleName':'FullOffice 文档编辑','CFBundleExecutable':'FullOfficeOfficeHost',
        'CFBundleIdentifier':'center.dnd.FullOffice.OfficeHost','CFBundlePackageType':'APPL','NSHighResolutionCapable':True}))
    helper=out/'compiled-editors-helper';command=compile.copy();command[command.index(str(source/'MacOfficeHost.mm'))]=str(source/'MacOfficeHelper.cpp');command[-1]=str(helper)
    command=[item for item in command if not item.startswith('-Wl,-rpath,')]
    for path in framework_roots:
        relative=os.path.relpath(path,originals).replace(os.sep,'/')
        command.insert(-2,'-Wl,-rpath,@executable_path/../../..'+('' if relative=='.' else '/'+relative))
    subprocess.run(command,check=True,timeout=90)
    (out/'helper-load-commands.txt').write_text(subprocess.check_output(['otool','-l',str(helper)],text=True))
    for index,name in enumerate(['editors_helper','editors_helper (Renderer)','editors_helper (GPU)']):
        original=originals/(name+'.app');target=host_frameworks/(name+'.app');target_mac=target/'Contents/MacOS';target_mac.mkdir(parents=True)
        info=plistlib.loads((original/'Contents/Info.plist').read_bytes());info['CFBundleIdentifier']='center.dnd.FullOffice.Helper'+str(index)
        (target/'Contents/Info.plist').write_bytes(plistlib.dumps(info));shutil.copy2(helper,target_mac/name)
        subprocess.run(['codesign','--force','--sign','-',str(target)],check=True,timeout=30)
    sources=root/'src/FullOffice.Mac'
    version=ET.parse(root/'src/FullOffice.Windows/FullOffice.Windows.csproj').findtext('./PropertyGroup/Version')
    if version is None or len(version.split('.'))!=3 or not all(part.isascii() and part.isdigit() for part in version.split('.')):
        raise RuntimeError('Product version is invalid')
    subprocess.run(['xcrun','swiftc','-swift-version','5',*swift_target(),'-O',*full_sources(root),'-framework','AppKit','-framework','PDFKit','-o',str(mac/'FullOffice')],check=True,timeout=120)
    (out/'main-load-commands.txt').write_text(subprocess.check_output(['otool','-l',str(mac/'FullOffice')],text=True))
    build_markdown_preview(root,app)
    shutil.copy2(sources/'Assets/update-public-key.der', resources/'update-public-key.der')
    shutil.copy2(sources/'Assets/update-public-key-provenance.json', resources/'update-public-key-provenance.json')
    iconset=out/'FullOffice.iconset';iconset.mkdir()
    for size in [16,32,128,256,512]:
        for retina in [False,True]:
            pixels=size*(2 if retina else 1);name=f'icon_{size}x{size}'+('@2x' if retina else '')+'.png'
            subprocess.run(['sips','-z',str(pixels),str(pixels),str(root/'src/FullOffice.Windows/Assets/icon-256.png'),'--out',str(iconset/name)],check=True,stdout=subprocess.DEVNULL)
    subprocess.run(['iconutil','-c','icns',str(iconset),'-o',str(resources/'FullOffice.icns')],check=True)
    info={'CFBundleName':'FullOffice','CFBundleDisplayName':'FullOffice','CFBundleIdentifier':'center.dnd.FullOffice',
        'CFBundleExecutable':'FullOffice','CFBundlePackageType':'APPL','CFBundleShortVersionString':version,'CFBundleVersion':'1',
        'CFBundleIconFile':'FullOffice.icns','NSHighResolutionCapable':True,'LSMinimumSystemVersion':'13.0',
        'CFBundleDocumentTypes':[{'CFBundleTypeName':'FullOffice 文档','CFBundleTypeRole':'Editor','LSHandlerRank':'Alternate',
            'CFBundleTypeExtensions':['docx','xlsx','pptx','odt','ods','odp','rtf','csv','md','markdown','txt']},
            {'CFBundleTypeName':'PDF','CFBundleTypeRole':'Viewer','LSHandlerRank':'Alternate','CFBundleTypeExtensions':['pdf']}],
        'NSHumanReadableCopyright':'2026 FullPeople. Office component: ONLYOFFICE; see bundled licenses.'}
    (contents/'Info.plist').write_bytes(plistlib.dumps(info))
    shutil.copy2(root/'LICENSE',resources/'LICENSE.txt');shutil.copy2(source/'LICENSE',resources/'Office-AGPL-3.0.txt')
    shutil.copy2(source/'NOTICE.txt',resources/'Office-NOTICE.txt')
    # Sign only our own bundles. Preserve vendor binaries and their existing signatures.
    subprocess.run(['codesign','--force','--sign','-',str(host)],check=True,timeout=90)
    subprocess.run(['codesign','--force','--sign','-',str(app)],check=True,timeout=90)
    subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True,timeout=90)
    manifest={};links={}
    for directory,dirs,files in os.walk(app,followlinks=False):
        for name in [*dirs,*files]:
            path=Path(directory)/name
            if path.is_symlink():
                if not path.resolve().is_relative_to(app.resolve()):raise RuntimeError('External bundle link: '+str(path))
                links[path.relative_to(app).as_posix()]=os.readlink(path)
            elif path.is_file():manifest[path.relative_to(app).as_posix()]={'Bytes':path.stat().st_size,'SHA256':digest(path)}
    original_core=originals/'ascdocumentscore.framework/Versions/A/ascdocumentscore'
    copied_core=host_frameworks/'ascdocumentscore.framework/Versions/A/ascdocumentscore'
    if digest(original_core)!=digest(copied_core):raise RuntimeError('Pinned vendor core bytes changed')
    record={'Built':True,'Architecture':platform.machine(),'ProductSource':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
        'Bundle':str(app),'CoreSHA256':digest(copied_core),'Files':manifest,'Links':links,'Signature':'ad-hoc; not Apple notarized',
        'Scope':'Self-contained candidate, copied verified vendor runtime, rebuilt pinned helpers and real product main; execution and installation still require acceptance.'}
    (out/'bundle-manifest.json').write_text(json.dumps(record,indent=2),encoding='utf-8')
    print(json.dumps({'Built':True,'Files':len(manifest),'Bytes':sum(v['Bytes'] for v in manifest.values()),'Architecture':platform.machine()}),flush=True)
    return app
