using System.Diagnostics;
using System.Text.Json;

namespace FlowOfficeLab;

static class PortableOcrProbe
{
    public static int Run(string fixture, string output)
    {
        Directory.CreateDirectory(output); var checks = new Dictionary<string, bool>();
        try {
            byte[] before = Program.HashFile(fixture);
            using var pdf = new PdfEngine(); pdf.OpenAsync(fixture).GetAwaiter().GetResult();
            var source = pdf.RenderAsync(0, 2200, CancellationToken.None, allowLarger: true).GetAwaiter().GetResult().Image;
            var initial = Directory.GetDirectories(Path.GetTempPath(), "FullOffice-OCR-*").ToHashSet();
            int child = 0; var elapsed = Stopwatch.StartNew();
            using (var cancelled = new CancellationTokenSource()) {
                bool interrupted = false;
                try { PortableOcr.RecognizeAsync(source, cancelled.Token, process => { child = process.Id; cancelled.Cancel(); }).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { interrupted = true; }
                checks["CancellationAfterRealChildStarts"] = child != 0 && interrupted;
            }
            bool exited = false;
            try { using var process = Process.GetProcessById(child); exited = process.HasExited; }
            catch (ArgumentException) { exited = true; }
            checks["CancelledChildExited"] = exited;
            checks["CancellationRemovesPrivateRequestFiles"] = Directory.GetDirectories(Path.GetTempPath(), "FullOffice-OCR-*").All(initial.Contains);
            string broken = Path.Combine(output, "broken-models"); Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "chi_sim.traineddata"), "broken");
            string? previous = Environment.GetEnvironmentVariable("FULLOFFICE_OCR_MODELS_ROOT"); bool failed = false;
            try {
                Environment.SetEnvironmentVariable("FULLOFFICE_OCR_MODELS_ROOT", Path.GetFullPath(broken));
                try { PortableOcr.RecognizeAsync(source, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException) { failed = true; }
            } finally { Environment.SetEnvironmentVariable("FULLOFFICE_OCR_MODELS_ROOT", previous); }
            checks["BrokenModelIsolatedFromParent"] = failed;
            string text = PortableOcr.RecognizeAsync(source, CancellationToken.None).GetAwaiter().GetResult();
            checks["ChineseRecognitionWorksAfterChildFailure"] = text.Count(character => character >= '\u4E00' && character <= '\u9FFF') > 20;
            checks["PrivateImagesAndTextRemovedAfterAllRequests"] = Directory.GetDirectories(Path.GetTempPath(), "FullOffice-OCR-*").All(initial.Contains);
            checks["PDFNeverChanged"] = before.SequenceEqual(Program.HashFile(fixture));
            var report = new { Passed = checks.Values.All(value => value), Checks = checks, Milliseconds = elapsed.Elapsed.TotalMilliseconds,
                Scope = "Actual packaged OCR child; cancellation after process start; corrupted-model isolation and subsequent Chinese OCR; generated PDF fixture; no source text retained" };
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return checks.Values.All(value => value) ? 0 : 1;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new { Error = error.ToString(), Checks = checks })); return 1;
        }
    }
}
