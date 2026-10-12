"""Build the AGPL native host against the exact tested Windows core ABI."""
import argparse
import hashlib
from pathlib import Path
import re
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
CORE_SHA = "3d00c5773094ffec137a7b15e862319f211c952786ab7aaac1c3f2a6ab3d3415"

def visual_cpp_tools():
    vswhere = Path("C:/Program Files (x86)/Microsoft Visual Studio/Installer/vswhere.exe")
    install = Path(subprocess.check_output([str(vswhere), "-latest", "-products", "*", "-requires",
        "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"], text=True).strip())
    candidates = sorted((install / "VC/Tools/MSVC").iterdir(), reverse=True)
    return next(path / "bin/Hostx64/x64" for path in candidates if (path / "bin/Hostx64/x64/dumpbin.exe").is_file())

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("engine", type=Path, help="Unmodified ONLYOFFICE Desktop Editors 9.4.0.129 directory")
    args = parser.parse_args()
    core = args.engine.resolve() / "ascdocumentscore.dll"
    with core.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != CORE_SHA:
            raise RuntimeError("Core ABI differs from the tested 9.4.0.129 build; revalidate before changing this pin")
    tools = visual_cpp_tools()
    output = ROOT / ".cache/office-native-host"
    output.mkdir(parents=True, exist_ok=True)
    exports = subprocess.check_output([str(tools / "dumpbin.exe"), "/exports", str(core)], text=True)
    names = re.findall(r"^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)", exports, re.MULTILINE)
    if not names:
        raise RuntimeError("Core has no native API exports")
    definition = output / "core.def"
    definition.write_text("LIBRARY ascdocumentscore.dll\nEXPORTS\n" + "\n".join(names) + "\n", encoding="utf-8")
    library = output / "ascdocumentscore.lib"
    subprocess.run([str(tools / "lib.exe"), "/def:" + str(definition), "/machine:x64", "/out:" + str(library)], check=True)
    subprocess.run(["cmake", "-S", str(ROOT / "src/FullOffice.OfficeEngine.Native"), "-B", str(output),
        "-DENGINE_COMPILE_ONLY=OFF", "-DENGINE_IMPORT_LIBRARY=" + library.as_posix()], check=True)
    subprocess.run(["cmake", "--build", str(output), "--config", "Release", "--parallel", "2"], check=True)
    print(output / "Release/FullOfficeOfficeHost.exe")

if __name__ == "__main__":
    main()
