using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Tesseract;

namespace FlowOfficeLab;

static class PortableOcr
{
    static string CheckedRequest(string directory)
    {
        string full = Path.GetFullPath(directory), name = Path.GetFileName(full);
        if (!Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith("FullOffice-OCR-", StringComparison.Ordinal) || !Guid.TryParseExact(name[15..], "N", out _))
            throw new IOException("文字识别请求目录超出范围。");
        OfficeRecoveryStore.RejectLinks(Path.Combine(full, "input.png")); return full;
    }
    public static async Task<string> RecognizeAsync(BitmapSource source, CancellationToken token, Action<Process>? started = null)
    {
        token.ThrowIfCancellationRequested();
        string directory = Path.Combine(Path.GetTempPath(), "FullOffice-OCR-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); directory = CheckedRequest(directory);
        string input = Path.Combine(directory, "input.png"), result = Path.Combine(directory, "result.json");
        Process? process = null;
        try {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            await using (var output = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(output);
            var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("无法定位识别组件。")) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            start.ArgumentList.Add("--ocr-host"); start.ArgumentList.Add(directory);
            string runtime = Path.Combine(AppContext.BaseDirectory, "office-engine");
            start.Environment["PATH"] = runtime + Path.PathSeparator + (start.Environment["PATH"] ?? "");
            process = Process.Start(start) ?? throw new IOException("无法启动文字识别组件。");
            started?.Invoke(process);
            Task<string> errors = process.StandardError.ReadToEndAsync(), outputText = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(40), token);
            await outputText; await errors;
            if (process.ExitCode != 0 || !File.Exists(result)) throw new IOException("文字识别未完成，组件退出代码 " + process.ExitCode + "。当前文档可以继续使用。");
            if (new FileInfo(result).Length > 16 * 1024 * 1024) throw new IOException("识别结果超出范围。");
            var glyphs = JsonSerializer.Deserialize<List<PdfGlyph>>(await File.ReadAllTextAsync(result, token)) ?? throw new IOException("识别结果无效。");
            token.ThrowIfCancellationRequested(); return TextLayout.Reflow(glyphs);
        } finally {
            if (process is { HasExited: false }) {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync();
            }
            process?.Dispose();
            foreach (string name in new[] { "input.png", "result.json", "error.txt" }) {
                string path = Path.Combine(directory, name); if (File.Exists(path)) File.Delete(path);
            }
            Directory.Delete(directory);
        }
    }
    public static int Run(string directory)
    {
        try {
            directory = CheckedRequest(directory);
            string input = Path.Combine(directory, "input.png"), result = Path.Combine(directory, "result.json");
            if (new FileInfo(input).Length > 32 * 1024 * 1024) throw new IOException("识别图片过大。");
            string models = Environment.GetEnvironmentVariable("FULLOFFICE_OCR_MODELS_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "ocr-data");
            foreach (var (name, digest) in new[] {
                ("chi_sim.traineddata", "A5FCB6F0DB1E1D6D8522F39DB4E848F05984669172E584E8D76B6B3141E1F730"),
                ("eng.traineddata", "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2") }) {
                if (!Convert.ToHexString(Program.HashFile(Path.Combine(models, name))).Equals(digest, StringComparison.Ordinal))
                    throw new IOException("文字识别模型校验失败。");
            }
            using var engine = new TesseractEngine(models, "chi_sim+eng", EngineMode.LstmOnly);
            using var image = Pix.LoadFromMemory(File.ReadAllBytes(input));
            if (image.Width > 5000 || image.Height > 5000) throw new IOException("识别图片尺寸过大。");
            using var page = engine.Process(image, PageSegMode.Auto);
            using var iterator = page.GetIterator(); iterator.Begin();
            var glyphs = new List<PdfGlyph>(); string? previous = null; int previousTop = -1;
            do {
                string text = iterator.GetText(PageIteratorLevel.Word)?.Trim() ?? "";
                if (text.Length == 0 || !iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var word)) continue;
                if (!iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var line)) line = word;
                if (line.Y1 != previousTop) previous = null;
                if (previous?.Length > 0 && previous[^1] < 128 && text[0] < 128) text = " " + text;
                glyphs.Add(new(text, (double)word.X1 / image.Width, (double)line.Y1 / image.Height,
                    (double)word.Width / image.Width, (double)line.Height / image.Height));
                previous = text.Trim(); previousTop = line.Y1;
            } while (iterator.Next(PageIteratorLevel.Word));
            using var output = new FileStream(result, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(output, glyphs); return 0;
        } catch (Exception error) {
            Console.Error.WriteLine("OCR component failed: " + error.Message); return 1;
        }
    }
}
