from pathlib import Path
import json,platform,subprocess

root=Path(__file__).resolve().parents[1]
out=root/'artifacts/mac-install-manifest';out.mkdir(parents=True,exist_ok=False)
main=out/'main.swift';main.write_bytes((root/'tools/mac_install_manifest_checks.swift').read_bytes())
exe=out/'manifest-checks'
subprocess.run(['xcrun','swiftc','-swift-version','5','-D','FULL_OFFICE_INSTALL_MANIFEST_TEST','-target',platform.machine()+'-apple-macosx13.0',str(root/'src/FullOffice.Mac/InstallGuard.swift'),str(main),'-o',str(exe)],check=True,timeout=90)
subprocess.run([str(exe),str(out/'fixture')],check=True,timeout=60)
report=json.loads((out/'fixture/report.json').read_text())
if report['Passed'] is not True or len(report['Checks'])!=4 or not all(report['Checks'].values()):raise RuntimeError('Incomplete actual install manifest checks')
print(json.dumps(report))
