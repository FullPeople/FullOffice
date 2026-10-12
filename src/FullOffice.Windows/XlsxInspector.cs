using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;

namespace FlowOfficeLab;

public static class XlsxInspector
{
    // This is a streaming workload inspector, not a spreadsheet editor or calculator.
    public static object Inspect(string path){
        var timer=Stopwatch.StartNew();using var archive=ZipFile.OpenRead(path);
        var functions=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);var sheets=new List<object>();
        long compressed=archive.Entries.Sum(x=>x.CompressedLength),expanded=archive.Entries.Sum(x=>x.Length);
        var xmlSettings=new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,IgnoreWhitespace=false,IgnoreComments=true,MaxCharactersInDocument=2L*1024*1024*1024};
        foreach(var entry in archive.Entries.Where(x=>Regex.IsMatch(x.FullName,@"^xl/worksheets/sheet\d+\.xml$"))){
            using var input=entry.Open();using var xml=XmlReader.Create(input,xmlSettings);long cells=0,formulas=0,rows=0,merged=0,conditional=0,missingCache=0,emptyStringCache=0;string dimension="";
            while(xml.Read()){
                if(xml.NodeType!=XmlNodeType.Element)continue;
                switch(xml.LocalName){
                    case "dimension":dimension=xml.GetAttribute("ref")??"";break;
                    case "row":rows++;break;
                    case "mergeCell":merged++;break;
                    case "conditionalFormatting":conditional++;break;
                    case "c":
                        cells++;bool formula=false,cached=false,hasValue=false;string cellType=xml.GetAttribute("t")??"n";
                        using(var cell=xml.ReadSubtree())while(cell.Read()){
                            if(cell.NodeType!=XmlNodeType.Element)continue;
                            if(cell.LocalName=="f"){
                                formula=true;formulas++;
                                using var f=cell.ReadSubtree();var parts=new System.Text.StringBuilder();while(f.Read())if(f.NodeType==XmlNodeType.Text||f.NodeType==XmlNodeType.CDATA)parts.Append(f.Value);
                                foreach(Match match in Regex.Matches(parts.ToString(),@"(?<![A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_.]*)\s*\(")){string key=match.Groups[1].Value;functions[key]=functions.GetValueOrDefault(key)+1;}
                            }else if(cell.LocalName=="v"){hasValue=true;using var value=cell.ReadSubtree();while(value.Read())if((value.NodeType==XmlNodeType.Text||value.NodeType==XmlNodeType.CDATA||value.NodeType==XmlNodeType.Whitespace||value.NodeType==XmlNodeType.SignificantWhitespace)&&!string.IsNullOrEmpty(value.Value))cached=true;}
                        }
                        if(formula&&(!hasValue||(!cached&&cellType!="str")))missingCache++;
                        if(formula&&hasValue&&cellType=="str"&&!cached)emptyStringCache++;break;
                }
            }
            sheets.Add(new{Part=entry.FullName,Dimension=dimension,Rows=rows,Cells=cells,Formulas=formulas,FormulaCellsMissingCachedValue=missingCache,FormulaCellsWithEmptyStringCache=emptyStringCache,MergedRanges=merged,ConditionalFormattingGroups=conditional,ExpandedBytes=entry.Length});
        }
        return new{File=Path.GetFileName(path),FileBytes=new FileInfo(path).Length,ZipCompressedBytes=compressed,ZipExpandedBytes=expanded,InspectMilliseconds=timer.Elapsed.TotalMilliseconds,Sheets=sheets,FormulaFunctions=functions,ContainsVba=archive.Entries.Any(x=>x.FullName.EndsWith("vbaProject.bin",StringComparison.OrdinalIgnoreCase)),ExternalLinks=archive.Entries.Count(x=>Regex.IsMatch(x.FullName,@"^xl/externalLinks/externalLink\d+\.xml$")),PivotTables=archive.Entries.Count(x=>Regex.IsMatch(x.FullName,@"^xl/pivotTables/pivotTable\d+\.xml$")),Charts=archive.Entries.Count(x=>Regex.IsMatch(x.FullName,@"^xl/charts/chart\d+\.xml$")),Scope="Structure inspection only; no formula evaluation, editor performance, or save compatibility claim."};
    }
}
