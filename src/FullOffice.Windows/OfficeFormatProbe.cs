using System.IO.Compression;
using System.Text;
using System.Xml;

namespace FlowOfficeLab;

static class OfficeFormatProbe
{
    public static void Check(string output, Dictionary<string, bool> checks)
    {
        string rich = Path.Combine(output, "rich-text"); Directory.CreateDirectory(rich);
        foreach (var (name, data) in new[] {
            ("basic", "{\\rtf1\\ansi Text {\\b bold} \\u20013? \\u25991?}"),
            ("escaped", "{\\rtf1 Literal \\{brace\\} and \\\\ slash \\'7b}"),
            ("binary", "{\\rtf1{\\pict\\bin3 {\\}}}"),
            ("large-ignored-parameter", "{\\rtf1\\unknown123456789012345678901234567890 Test}") }) {
            string path = Path.Combine(rich, name + ".rtf"); File.WriteAllText(path, data, new UTF8Encoding(false));
            byte[] before = Program.HashFile(path); OfficePackage.Validate(path, path);
            checks["RichTextValidAndUnchanged" + name] = before.SequenceEqual(Program.HashFile(path));
        }
        foreach (var (name, data) in new[] {
            ("wrong-header", "plain text"), ("truncated", "{\\rtf1{\\b missing}"),
            ("bad-hex", "{\\rtf1\\'xz}"), ("short-binary", "{\\rtf1\\bin50 x}"),
            ("negative-binary", "{\\rtf1\\bin-3 xyz}"), ("extra-root", "{\\rtf1 One}{Two}"),
            ("wrong-version", "{\\rtf12 Wrong}") }) {
            string path = Path.Combine(rich, name + ".rtf"); File.WriteAllText(path, data, new UTF8Encoding(false));
            checks["RichTextMalformedRejected" + name] = Denied(() => OfficePackage.Validate(path, path));
        }
        string root = Path.Combine(output, "open-document"); Directory.CreateDirectory(root);
        foreach (var (extension, mime, body) in new[] {
            (".odt", "application/vnd.oasis.opendocument.text", "text"),
            (".ods", "application/vnd.oasis.opendocument.spreadsheet", "spreadsheet"),
            (".odp", "application/vnd.oasis.opendocument.presentation", "presentation") }) {
            string path = Path.Combine(root, "valid" + extension);
            Write(path, mime, Content(body)); byte[] before = Program.HashFile(path);
            OfficePackage.Validate(path, path);
            checks["OpenDocumentValidAndUnchanged" + extension] = before.SequenceEqual(Program.HashFile(path));
            checks["OpenDocumentRecognizedWithoutCaseSensitivity" + extension] = DocumentFormats.IsOffice(path.ToUpperInvariant());
        }
        string word = Path.Combine(root, "valid.odt");
        checks["OpenDocumentCrossFormatRejected"] = Denied(() => OfficePackage.Validate(word, "wrong.ods"));
        string wrongBody = Path.Combine(root, "wrong-body.odt");
        Write(wrongBody, "application/vnd.oasis.opendocument.text", Content("spreadsheet"));
        checks["OpenDocumentBodyTypeMismatchRejected"] = Denied(() => OfficePackage.Validate(wrongBody, wrongBody));
        string wrongNamespace = Path.Combine(root, "wrong-namespace.odt");
        Write(wrongNamespace, "application/vnd.oasis.opendocument.text", Content("text").Replace("urn:oasis:names:tc:opendocument:xmlns:office:1.0", "urn:foreign"));
        checks["OpenDocumentForeignRootRejected"] = Denied(() => OfficePackage.Validate(wrongNamespace, wrongNamespace));
        string external = Path.Combine(root, "external.odt");
        Write(external, "application/vnd.oasis.opendocument.text", "<!DOCTYPE office:document-content [<!ENTITY unsafe SYSTEM 'file:///should-not-be-read'>]>" + Content("text"));
        checks["OpenDocumentExternalDtdRejected"] = Denied(() => OfficePackage.Validate(external, external));
        string truncated = Path.Combine(root, "truncated.odt");
        Write(truncated, "application/vnd.oasis.opendocument.text", Content("text")[..^10]);
        checks["OpenDocumentTruncatedXmlRejected"] = Denied(() => OfficePackage.Validate(truncated, truncated));
        string duplicate = Path.Combine(root, "duplicate.odt");
        Write(duplicate, "application/vnd.oasis.opendocument.text", Content("text"), duplicate: true);
        checks["OpenDocumentAmbiguousDuplicateRejected"] = Denied(() => OfficePackage.Validate(duplicate, duplicate));
        string noBody = Path.Combine(root, "no-body.odt");
        Write(noBody, "application/vnd.oasis.opendocument.text", Content("text").Replace("<office:text/>", ""));
        checks["OpenDocumentMissingBodyRejected"] = Denied(() => OfficePackage.Validate(noBody, noBody));
    }
    static string Content(string body) => "<office:document-content xmlns:office='urn:oasis:names:tc:opendocument:xmlns:office:1.0' office:version='1.3'><office:body><office:" + body + "/></office:body></office:document-content>";
    static void Write(string path, string mime, string content, bool duplicate = false)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var writer = new StreamWriter(archive.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false))) writer.Write(mime);
        using (var writer = new StreamWriter(archive.CreateEntry("content.xml").Open(), new UTF8Encoding(false))) writer.Write(content);
        if (duplicate) using (var writer = new StreamWriter(archive.CreateEntry("content.xml").Open(), new UTF8Encoding(false))) writer.Write(content);
    }
    static bool Denied(Action action)
    {
        try { action(); return false; }
        catch (Exception error) when (error is IOException or XmlException) { return true; }
    }
}
