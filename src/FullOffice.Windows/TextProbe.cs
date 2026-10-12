using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace FlowOfficeLab;
public sealed partial class WorkspaceWindow
{
    async Task TextProbe(string pdfFixture, string directory)
    {
        Directory.CreateDirectory(directory); var checks = new Dictionary<string, bool>();
        try {
            string path = Path.Combine(directory, "notes.md");
            string original = "# FullOffice Markdown 验证\r\n\r\n中文 **粗体** 与 *斜体*。\r\n\r\n3. 第三项\r\n4. 第四项\r\n\r\n| 名称 | 值 |\r\n| --- | --- |\r\n| 属性 | 7 |\r\n\r\n```text\r\n未执行的代码\r\n```\r\n";
            await File.WriteAllTextAsync(path, original, new UTF8Encoding(true));
            await OpenPaths(new[] { path }); var view = activeEditor as TextDocumentView ?? throw new IOException("Text tab did not open");
            await view.Ready;
            checks["MarkdownOpenedThroughWorkspace"] = view.DocumentText == original && editors.Count == 1;
            checks["CommonMarkHeadingRendered"] = view.PreviewDocument?.Blocks.FirstBlock is Paragraph p && p.FontSize > 20;
            checks["MarkdownTableRendered"] = view.PreviewDocument?.Blocks.OfType<System.Windows.Documents.Table>().Any() == true;
            await OpenPaths(new[] { path }); checks["DuplicateTextFileReusesTab"] = editors.Count == 1 && activeEditor == view;
            view.DocumentText += "\r\n新增笔记。\r\n";
            checks["TextEditMarksUnsaved"] = view.IsModified;
            UiInputProbe.Click(this, saveButton);
            for (int i = 0; i < 100 && (view.IsModified || view.IsSaving); ++i) await Task.Delay(20);
            checks["HeaderSaveWritesOriginalFile"] = (await File.ReadAllTextAsync(path)).Contains("新增笔记。") && !view.IsModified;
            checks["Utf8BomPreserved"] = (await File.ReadAllBytesAsync(path)).AsSpan().StartsWith(new byte[] { 239, 187, 191 });
            checks["PreviousVersionBackupPreserved"] = await File.ReadAllTextAsync(Path.Combine(directory, ".FlowOfficeBackups", "notes.md.previous")) == original;
            var pdf = await OpenDocument(pdfFixture) ?? throw new IOException("PDF tab did not open");
            checks["PdfAndMarkdownShareWorkspace"] = readers.Count == 1 && editors.Count == 1 && active == pdf;
            CycleDocuments(false); checks["CycleReturnsToTextTab"] = activeEditor == view;
            CycleDocuments(true); checks["CycleReturnsToPdfTab"] = active == pdf;
            SelectEditor(view); CaptureWorkspace(Path.Combine(directory, "markdown-workspace.png"));
            byte[] saved = await File.ReadAllBytesAsync(path);
            view.DocumentText += "保留未保存的内容"; File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            checks["ReadOnlySaveRejected"] = !await view.SaveAsync();
            checks["RejectedSaveRetainsEditorChanges"] = view.IsModified && view.DocumentText.Contains("保留未保存的内容") && saved.SequenceEqual(await File.ReadAllBytesAsync(path));
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            checks["FailedSaveCanRetry"] = await view.SaveAsync();
            view.DocumentText += "外部修改冲突"; await File.AppendAllTextAsync(path, "outside change");
            checks["ExternalModificationRejected"] = !await view.SaveAsync() && view.IsModified && (await File.ReadAllTextAsync(path)).EndsWith("outside change");
            File.WriteAllText(Path.Combine(directory, "text-probe.json"), JsonSerializer.Serialize(new { Passed = checks.Values.All(v => v), Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        } catch (Exception error) { File.WriteAllText(Path.Combine(directory, "text-probe.json"), JsonSerializer.Serialize(new { Error = error.ToString(), Checks = checks })); }
        finally { closingApproved = true; Close(); }
    }
}
