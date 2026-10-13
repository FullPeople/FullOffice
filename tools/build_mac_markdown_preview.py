from pathlib import Path
import hashlib,json,plistlib,subprocess

def build(root,app):
    source=root/'src/FullOffice.Mac';assets=source/'Assets'
    provenance=json.loads((assets/'markdown-it-provenance.json').read_text(encoding='utf-8'))
    for name,expected in provenance['Files'].items():
        if hashlib.sha256((assets/name).read_bytes()).hexdigest()!=expected:raise RuntimeError('Markdown parser provenance mismatch: '+name)
    bundle=app/'Contents/PlugIns/FullOfficeMarkdownPreview.bundle'
    binary=bundle/'Contents/MacOS';resources=bundle/'Contents/Resources'
    binary.mkdir(parents=True,exist_ok=False);resources.mkdir()
    subprocess.run(['xcrun','clang++','-bundle','-fobjc-arc','-fblocks','-mmacosx-version-min=13.0',
        str(source/'MarkdownPreview.mm'),'-framework','AppKit','-framework','WebKit','-o',str(binary/'FullOfficeMarkdownPreview')],check=True,timeout=90)
    script=(assets/'markdown-it-14.1.0.min.js').read_text(encoding='utf-8').replace('</script','<\\/script')
    template=(assets/'preview.html').read_text(encoding='utf-8')
    if template.count('/*MARKDOWN_IT*/')!=1:raise RuntimeError('Invalid preview template')
    (resources/'preview.html').write_text(template.replace('/*MARKDOWN_IT*/',script),encoding='utf-8')
    for name in ['markdown-it-LICENSE.txt','markdown-it-provenance.json']:(resources/name).write_bytes((assets/name).read_bytes())
    (bundle/'Contents/Info.plist').write_bytes(plistlib.dumps({'CFBundleName':'FullOfficeMarkdownPreview',
        'CFBundleIdentifier':'center.dnd.FullOffice.MarkdownPreview','CFBundleExecutable':'FullOfficeMarkdownPreview',
        'CFBundlePackageType':'BNDL','NSPrincipalClass':'FullOfficeMarkdownPreviewController'}))
    subprocess.run(['codesign','--force','--sign','-',str(bundle)],check=True,timeout=30)
