"""Exercise actual Markdown editing and the lazily loaded preview in the built app."""
from pathlib import Path
import os,json,hashlib,subprocess

if os.environ.get('GITHUB_ACTIONS')!='true':raise RuntimeError('Requires disposable hosted macOS')
root=Path(__file__).resolve().parents[1];out=root/'artifacts/mac-text-ui';out.mkdir(parents=True,exist_ok=False)
text='# MARKDOWN_BODY_20261014\n\n**中文测试** 🐉\n\n| 名称 | 数量 |\n| --- | --- |\n| 怪物 | 2 |\n\n```js\nconst value = 2;\n```\n\n![本地图像](local.svg)\n\n<script>window.__fullOfficeAttack=true</script>\n'
data=b'\xef\xbb\xbf'+text.replace('\n','\r\n').encode()
source=out/'original.md';source.write_bytes(data)
(out/'local.svg').write_text('<svg xmlns="http://www.w3.org/2000/svg" width="160" height="50"><rect width="160" height="50" rx="8" fill="#5473ed"/><text x="10" y="30" fill="white">LOCAL_IMAGE</text></svg>')
edited=text+'\nMAC_TEXT_EDIT_SAVED_20261014\n';alternate=edited+'\nALTERNATE_ONLY_20261014\n'
def digest(text):return hashlib.sha256(b'\xef\xbb\xbf'+text.replace('\n','\r\n').encode()).hexdigest()
(out/'fixture.json').write_text(json.dumps({'Source':str(source),'Root':str(out/'sessions'),'Text':text,'Edited':edited,'Alternate':alternate,
    'OriginalSHA256':hashlib.sha256(data).hexdigest(),'EditedSHA256':digest(edited),'AlternateSHA256':digest(alternate)},indent=2))
work=root/'.cache/mac-recovery-ui'
subprocess.run(['xcodebuild','test-without-building','-project',str(root/'tests/mac-recovery-ui/RecoveryUI.xcodeproj'),
    '-scheme','RecoveryUI','-destination','platform=macOS','-derivedDataPath',str(work/'build'),'CODE_SIGNING_ALLOWED=NO',
    '-only-testing:RecoveryUI/TextUITests','-resultBundlePath',str(work/'text-results.xcresult')],check=True,timeout=360)
report=json.loads((out/'report.json').read_text())
if not report['Passed'] or len(report['Checks'])!=17 or not all(report['Checks'].values()):raise RuntimeError('Actual text UI checks did not all pass')
