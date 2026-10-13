using System.IO.Compression;
using System.Xml;

namespace FlowOfficeLab;

static class OfficePackage
{
    public static void Validate(string path, string documentPath)
    {
        if (DocumentFormats.TryOpenDocument(documentPath, out var format)) {
            ValidateOpenDocument(path, format); return;
        }
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
    const string OfficeNamespace = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    static void ValidateOpenDocument(string path, (string Mime, string Body) format)
    {
        using var archive = ZipFile.OpenRead(path);
        foreach (string name in new[] { "mimetype", "content.xml" })
            if (archive.Entries.Count(entry => entry.FullName == name) != 1)
                throw new IOException("OpenDocument 文档缺少必要内容或存在重复内容：" + name);
        var mime = archive.GetEntry("mimetype")!;
        if (mime.Length > 512) throw new IOException("OpenDocument 类型记录无效。");
        using (var data = mime.Open())
        using (var text = new StreamReader(data))
            if (text.ReadToEnd().Trim() != format.Mime)
                throw new IOException("文档内容与 OpenDocument 文件格式不匹配。");
        using var content = archive.GetEntry("content.xml")!.Open();
        using var reader = XmlReader.Create(content, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "document-content" || reader.NamespaceURI != OfficeNamespace)
            throw new IOException("OpenDocument 文档正文无效。");
        int bodies = 0, kinds = 0; bool inBody = false;
        while (reader.Read()) {
            if (reader.Depth == 1 && reader.NamespaceURI == OfficeNamespace && reader.LocalName == "body") {
                if (reader.NodeType == XmlNodeType.Element) { ++bodies; inBody = !reader.IsEmptyElement; }
                else if (reader.NodeType == XmlNodeType.EndElement) inBody = false;
            } else if (inBody && reader.Depth == 2 && reader.NodeType == XmlNodeType.Element) {
                if (reader.NamespaceURI != OfficeNamespace || reader.LocalName != format.Body)
                    throw new IOException("OpenDocument 正文类型与文件格式不匹配。");
                ++kinds;
            }
        }
        if (bodies != 1 || kinds != 1) throw new IOException("OpenDocument 文档缺少完整正文。");
    }
}
