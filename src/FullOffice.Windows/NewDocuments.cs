using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FlowOfficeLab;

static class NewDocumentFiles
{
    public static void Write(string path, string extension)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (extension == ".md") return;
        if (extension == ".pptx") {
            using var source = typeof(NewDocumentFiles).Assembly.GetManifestResourceStream("FullOffice.BlankPresentation")
                ?? throw new IOException("缺少演示文稿模板。");
            source.CopyTo(output); return;
        }
        bool spreadsheet = extension == ".xlsx";
        if (!spreadsheet && extension != ".docx") throw new IOException("不支持这个新建格式。");
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        void Part(string name, string xml) {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
        }
        const string relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        const string office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        string main = spreadsheet ? "xl/workbook.xml" : "word/document.xml";
        string content = spreadsheet ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"
            : "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
        Part("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/" + main + "\" ContentType=\"" + content + "\"/>" +
            (spreadsheet ? "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" : "") + "</Types>");
        Part("_rels/.rels", "<Relationships xmlns=\"" + relationships + "\"><Relationship Id=\"rId1\" Type=\"" + office + "officeDocument\" Target=\"" + main + "\"/></Relationships>");
        if (!spreadsheet) {
            Part(main, "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p/><w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
            return;
        }
        const string sheets = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Part(main, "<workbook xmlns=\"" + sheets + "\" xmlns:r=\"" + office.TrimEnd('/') + "\"><sheets><sheet name=\"工作表1\" sheetId=\"1\" r:id=\"rId1\"/></sheets><calcPr calcMode=\"auto\"/></workbook>");
        Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"" + relationships + "\"><Relationship Id=\"rId1\" Type=\"" + office + "worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"" + office + "styles\" Target=\"styles.xml\"/></Relationships>");
        Part("xl/worksheets/sheet1.xml", "<worksheet xmlns=\"" + sheets + "\"><sheetData/></worksheet>");
        Part("xl/styles.xml", "<styleSheet xmlns=\"" + sheets + "\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
    }
}

public sealed partial class WorkspaceWindow
{
    void AddNewDocumentButtons(Panel parent)
    {
        var row = new WrapPanel { Margin = new(0, 16, 0, 0) };
        foreach (var (label, extension) in new[] { ("新建文档", ".docx"), ("新建表格", ".xlsx"), ("新建演示文稿", ".pptx"), ("新建笔记", ".md") }) {
            var button = ActionButton(label, () => CreateDocument(extension));
            button.Padding = new(18, 10, 18, 10); button.Margin = new(0, 0, 12, 6); row.Children.Add(button);
        }
        parent.Children.Add(row);
    }
    async Task CreateDocument(string extension)
    {
        if (DocumentFormats.IsOffice(extension)) {
            string runtime = Environment.GetEnvironmentVariable("FULLOFFICE_ENGINE_RUNTIME") ?? Path.Combine(AppContext.BaseDirectory, "office-engine");
            if (!File.Exists(Path.Combine(runtime, "FullOfficeOfficeHost.exe"))) throw new IOException("请安装包含文档、表格和演示文稿编辑器的完整版本。");
        }
        var picker = new SaveFileDialog { Title = "创建新文件", FileName = "新文件" + extension, DefaultExt = extension, AddExtension = true,
            Filter = "当前文档格式|*" + extension, OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        string target = Path.GetFullPath(picker.FileName);
        if (!Path.GetExtension(target).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new IOException("请使用 " + extension + " 文件后缀。");
        if (editors.Any(view => view.FilePath.Equals(target, StringComparison.OrdinalIgnoreCase)) ||
            readers.Any(view => view.FilePath.Equals(target, StringComparison.OrdinalIgnoreCase))) throw new IOException("这个文件已在当前窗口打开，请选择其他名称。");
        OfficeRecoveryStore.RejectLinks(target);
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) throw new IOException("目标文件是只读文件，请选择其他名称。");
        byte[]? previous = File.Exists(target) ? await Task.Run(() => Program.HashFile(target)) : null;
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".FullOffice-new-" + Guid.NewGuid().ToString("N") + ".tmp");
        try {
            await Task.Run(() => NewDocumentFiles.Write(temporary, extension));
            if (DocumentFormats.IsOffice(target)) await Task.Run(() => OfficePackage.Validate(temporary, target));
            if (previous != null) {
                if (!File.Exists(target) || !previous.SequenceEqual(await Task.Run(() => Program.HashFile(target)))) throw new IOException("目标文件在创建期间发生了变化，请重新选择。");
                string backups = Path.Combine(Path.GetDirectoryName(target)!, ".FlowOfficeBackups"); Directory.CreateDirectory(backups);
                OfficeRecoveryStore.RejectLinks(Path.Combine(backups, "scope-check"));
                File.SetAttributes(backups, File.GetAttributes(backups) | FileAttributes.Hidden);
                File.Replace(temporary, target, Path.Combine(backups, Path.GetFileName(target) + ".previous"));
            } else File.Move(temporary, target);
            await OpenEditor(target);
        } finally {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { homeStatus.Text = "暂存文件未能清理：" + temporary + "。" + error.Message; }
        }
    }
}
