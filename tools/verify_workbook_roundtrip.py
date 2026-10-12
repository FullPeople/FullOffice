import argparse
import hashlib
import json
import re
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath
from openpyxl.formula.translate import Translator

ns = {"m":"http://schemas.openxmlformats.org/spreadsheetml/2006/main", "r":"http://schemas.openxmlformats.org/officeDocument/2006/relationships"}
def local(tag): return tag.rsplit("}",1)[-1]
def expression(text): return re.sub(r"_xlfn\.|_xlws\.","",text or "")
def scopes(text):
    result=[]
    for area in (text or "").split():
        area=area.replace("$","").upper()
        area=re.sub(r"^([A-Z]+)1:([A-Z]+)1048576$",r"\1:\2",area)
        area=re.sub(r"^A([0-9]+):XFD([0-9]+)$",r"\1:\2",area)
        result.append(area)
    return tuple(sorted(result))
def rules(sheet):
    validations=[]
    defaults={"type":"none","operator":"between","errorStyle":"stop","imeMode":"noControl","allowBlank":"0",
        "showDropDown":"0","showInputMessage":"0","showErrorMessage":"0","errorTitle":"","error":"","promptTitle":"","prompt":""}
    for node in sheet.iter():
        if local(node.tag)!="dataValidation": continue
        values={key:node.get(key,value) for key,value in defaults.items()}
        for key in ("allowBlank","showDropDown","showInputMessage","showErrorMessage"):
            values[key]="1" if values[key] in ("1","true") else "0"
        scope=node.get("sqref") or next((item.text for item in node if local(item.tag)=="sqref"),"")
        formulas={local(item.tag):expression("".join(item.itertext())) for item in node if local(item.tag) in ("formula1","formula2")}
        validations.append((tuple(sorted(values.items())),scopes(scope),formulas.get("formula1",""),formulas.get("formula2","")))
    conditional=[]
    represented=set()
    for node in sheet.iter():
        if local(node.tag)=="cfRule" and node.get("priority") is not None:
            represented.update((item.text or "").lower() for item in node.iter() if local(item.tag)=="id")
    for group in sheet.iter():
        if local(group.tag)!="conditionalFormatting":continue
        scope=group.get("sqref") or next((item.text for item in group if local(item.tag)=="sqref"),"")
        for rule in group:
            if local(rule.tag)!="cfRule" or (rule.get("id","").lower() in represented and rule.get("priority") is None):continue
            formulas=[]
            for item in rule.iter():
                if local(item.tag) in ("formula","f"):formulas.append(expression(item.text))
                elif local(item.tag)=="cfvo" and item.get("type")=="formula" and item.get("val") is not None:formulas.append(expression(item.get("val")))
            conditional.append((rule.get("type",""),rule.get("priority",""),rule.get("operator",""),
                rule.get("stopIfTrue","0"),scopes(scope),tuple(sorted(formulas))))
    return sorted(validations),sorted(conditional)
def inspect(path):
    result = {"sheets":[], "errors":{}, "formulas":{}, "values":{}, "images":[]}
    with zipfile.ZipFile(path) as z:
        wb = ET.fromstring(z.read("xl/workbook.xml"))
        rels = {entry.get("Id"):entry.get("Target") for entry in ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))}
        strings = []
        if "xl/sharedStrings.xml" in z.namelist():
            strings = ["".join(si.itertext()) for si in ET.fromstring(z.read("xl/sharedStrings.xml"))]
        for index, item in enumerate(wb.find("m:sheets",ns)):
            target = rels[item.get("{"+ns["r"]+"}id")]
            name = target.lstrip("/") if target.startswith("/") else str(PurePosixPath("xl") / target)
            sheet = ET.fromstring(z.read(name))
            validation_rules,conditional_rules=rules(sheet)
            result["sheets"].append({"name":item.get("name"), "merged":sorted(node.get("ref") for node in sheet.findall("m:mergeCells/m:mergeCell",ns)),
                "validations":validation_rules, "conditional":conditional_rules,
                "protected":sheet.find("m:sheetProtection",ns) is not None})
            cells = sheet.findall("m:sheetData/m:row/m:c",ns)
            shared = {f.get("si"):(cell.get("r"),f.text) for cell in cells for f in cell.findall("m:f",ns)
                if f.get("t")=="shared" and f.text}
            for cell in cells:
                key = str(index)+":"+cell.get("r")
                value = cell.find("m:v",ns)
                text = value.text if value is not None else None
                kind = cell.get("t")
                if kind=="s" and text is not None: text = strings[int(text)]
                elif kind=="inlineStr": text = "".join(cell.find("m:is",ns).itertext())
                formula = cell.find("m:f",ns)
                if formula is not None:
                    expression = formula.text or ""
                    if formula.get("t")=="shared" and not expression:
                        origin, expression = shared[formula.get("si")]
                        expression = Translator("="+expression,origin=origin).translate_formula(cell.get("r"))[1:]
                    result["formulas"][key] = re.sub(r"_xlfn\.|_xlws\.", "", expression)
                elif text is not None and text!="": result["values"][key]=text
                if kind=="e":result["errors"][key]=text
        result["images"] = sorted(hashlib.sha256(z.read(name)).hexdigest() for name in z.namelist() if name.startswith("xl/media/") and not name.endswith("/"))
    return result
def main():
    parser=argparse.ArgumentParser(description="Compare workbook content after real editor roundtrip")
    parser.add_argument("source",type=Path)
    parser.add_argument("saved",type=Path)
    parser.add_argument("report",type=Path)
    parser.add_argument("--changed-cell",action="append",default=[])
    args=parser.parse_args()
    a,b=inspect(args.source),inspect(args.saved)
    changes={key for key in set(a["values"])|set(b["values"]) if a["values"].get(key)!=b["values"].get(key)}
    checks={
        "SheetNamesAndOrderPreserved":[s["name"] for s in a["sheets"]]==[s["name"] for s in b["sheets"]],
        "MergedCellsPreserved":[s["merged"] for s in a["sheets"]]==[s["merged"] for s in b["sheets"]],
        "DataValidationRulesAndRangesPreserved":[s["validations"] for s in a["sheets"]]==[s["validations"] for s in b["sheets"]],
        "ConditionalRuleTargetsAndFormulasPreserved":[s["conditional"] for s in a["sheets"]]==[s["conditional"] for s in b["sheets"]],
        "SheetProtectionPreserved":[s["protected"] for s in a["sheets"]]==[s["protected"] for s in b["sheets"]],
        "OriginalImagesPreserved":a["images"]==b["images"],
        "NoNewFormulaErrors":all(a["errors"].get(k)==v for k,v in b["errors"].items()),
        "OriginalFormulasPreserved":a["formulas"]==b["formulas"],
        "OnlyExpectedInputsChanged":changes==set(args.changed_cell)
    }
    report={"Passed":all(checks.values()),"Checks":checks,"OriginalFormulaCount":len(a["formulas"]),"SavedFormulaCount":len(b["formulas"]),"OriginalErrorCount":len(a["errors"]),"SavedErrorCount":len(b["errors"]),"ChangedValueAddresses":sorted(changes)}
    args.report.parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
    print(json.dumps(report))
    return 0 if report["Passed"] else 1

if __name__=="__main__":raise SystemExit(main())
