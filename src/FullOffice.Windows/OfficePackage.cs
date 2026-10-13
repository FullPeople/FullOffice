using System.IO.Compression;
using System.Xml;

namespace FlowOfficeLab;

static class OfficePackage
{
    public static void Validate(string path, string documentPath)
    {
        string content = DocumentFormats.OfficeContentEntry(documentPath);
        string root = content switch { "word/document.xml" => "document", "xl/workbook.xml" => "workbook", _ => "presentation" };
        using var archive = ZipFile.OpenRead(path);
        foreach (var (name, expectedRoot) in new[] { ("[Content_Types].xml", "Types"), (content, root) }) {
            using var data = (archive.GetEntry(name) ?? throw new IOException("文档缺少必要内容：" + name)).Open();
            using var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != expectedRoot)
                throw new IOException("文档内容与文件格式不匹配。");
            while (reader.Read()) { }
        }
    }
}
