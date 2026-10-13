"""Independently verify real compositor PNG pixels, alongside native metadata checks."""
from pathlib import Path
import hashlib,json
from PIL import Image

root=Path(__file__).resolve().parents[1]/'artifacts/macos-preview/probe'
native=json.loads((root/'report.json').read_text())
image=Image.open(root/'window.png').convert('RGB')
if native['WindowWidth']<=0:raise RuntimeError('Actual window width unavailable')
ratio=image.width/native['WindowWidth'];left=max(0,int(native['SidebarRight']*ratio)+12)
top=int(80*ratio);right=image.width-12;bottom=image.height-12
if left>=right or top>=bottom:raise RuntimeError('Actual PDF viewport crop is invalid')
pixels=image.crop((left,top,right,bottom)).getdata()
count=sum(1 for red,green,blue in pixels if red<102 and green<102 and blue>178)
checks={**native['Checks'],'AuthoredPageMarkerActuallyVisible':count>500}
report={'Passed':native['Passed'] and len(checks)==9 and all(checks.values()),'Checks':checks,
        'Architecture':native['Architecture'],'VisiblePageMarkerPixels':count,
        'CapturedPNG_SHA256':hashlib.sha256((root/'window.png').read_bytes()).hexdigest(),
        'Scope':'Eight native checks plus independent PNG verification of visible authored page marker in the actual compositor window; read-only Mac preview, no Office editing or real-user-document/device assertion.'}
(root/'capture-validation.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
print(json.dumps(report),flush=True)
raise SystemExit(0 if report['Passed'] else 1)
