"""Build an honest, unsigned native macOS PDF reading research bundle."""
from pathlib import Path
import hashlib,json,platform,plistlib,shutil,subprocess,sys

if sys.platform!='darwin':raise RuntimeError('Requires native macOS compiler and frameworks')
root=Path(__file__).resolve().parents[1];out=root/'artifacts/macos-preview'
out.mkdir(parents=True,exist_ok=False)
app=out/'FullOffice.app';mac=app/'Contents/MacOS';resources=app/'Contents/Resources'
mac.mkdir(parents=True);resources.mkdir()
sdk=subprocess.check_output(['xcrun','--sdk','macosx','--show-sdk-path'],text=True).strip()
subprocess.run(['xcrun','swiftc','-swift-version','5','-O','-sdk',sdk,'-target',platform.machine()+'-apple-macos13.0',
    str(root/'src/FullOffice.Mac/OfficeFileSession.swift'),str(root/'src/FullOffice.Mac/OfficeCoordinator.swift'),
    str(root/'src/FullOffice.Mac/ProductProbe.swift'),
    str(root/'src/FullOffice.Mac/main.swift'),'-framework','AppKit','-framework','PDFKit','-o',str(mac/'FullOffice')],check=True)
iconset=out/'FullOffice.iconset';iconset.mkdir()
icon=root/'src/FullOffice.Windows/Assets/icon-256.png'
for size in [16,32,128,256,512]:
    for retina in [False,True]:
        pixels=size*(2 if retina else 1);name=f'icon_{size}x{size}'+('@2x' if retina else '')+'.png'
        subprocess.run(['sips','-z',str(pixels),str(pixels),str(icon),'--out',str(iconset/name)],check=True,stdout=subprocess.DEVNULL)
subprocess.run(['iconutil','-c','icns',str(iconset),'-o',str(resources/'FullOffice.icns')],check=True)
info={'CFBundleName':'FullOffice','CFBundleDisplayName':'FullOffice','CFBundleIdentifier':'center.dnd.FullOffice.MacPreview',
      'CFBundleExecutable':'FullOffice','CFBundlePackageType':'APPL','CFBundleShortVersionString':'0.3.3','CFBundleVersion':'1',
      'CFBundleIconFile':'FullOffice.icns','NSHighResolutionCapable':True,'LSMinimumSystemVersion':'13.0',
      'NSHumanReadableCopyright':'2026 FullPeople. MIT; macOS PDF reading research preview.'}
(app/'Contents/Info.plist').write_bytes(plistlib.dumps(info))
shutil.copy2(root/'LICENSE',resources/'LICENSE.txt')
subprocess.run(['codesign','--force','--deep','--sign','-',str(app)],check=True)
subprocess.run(['codesign','--verify','--deep','--strict',str(app)],check=True)
subprocess.run(['ditto','-c','-k','--sequesterRsrc','--keepParent',str(app),str(out/f'FullOffice-macos-reading-preview-{platform.machine()}.zip')],check=True)
print(json.dumps({'Built':True,'Architecture':platform.machine(),'Bundle':str(app),'Scope':'Unsigned native read-only PDF research preview; not the full Windows product.'}))
