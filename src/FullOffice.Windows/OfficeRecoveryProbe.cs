using System.Diagnostics;
using System.Text.Json;

namespace FlowOfficeLab;

static class OfficeRecoveryProbe
{
    public static int Run(string output)
    {
        output = Path.GetFullPath(output); Directory.CreateDirectory(output);
        var checks = new Dictionary<string, bool>();
        try {
            string root = Path.Combine(output, "sessions"), directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "document"));
            string source = Path.Combine(output, "original.docx"), working = Path.Combine(directory, "document", "original.docx");
            File.WriteAllText(source, "protected original"); File.WriteAllText(working, "saved working snapshot");
            var store = new OfficeRecoveryStore(root);
            using (var lease = store.Acquire(directory)) {
                store.InitializeAsync(directory, source, 0, 0).GetAwaiter().GetResult();
                store.SnapshotSavedAsync(directory, working).GetAwaiter().GetResult();
                checks["ActiveLeaseNotOfferedForRecovery"] = store.FindAsync().GetAwaiter().GetResult().Count == 0;
                bool busyDenied = false;
                try { store.Retire(directory); } catch (IOException) { busyDenied = true; }
                checks["ActiveLeaseCannotBeRetired"] = busyDenied && File.Exists(working);
            }
            var entries = store.FindAsync().GetAwaiter().GetResult();
            checks["ClosedSnapshotFound"] = entries.Count == 1 && entries[0].HasSnapshot && entries[0].SavedUtc != null;
            var entry = entries.Single(); string recovered = Path.Combine(output, "recovered.docx");
            store.CopySnapshotAsync(entry, recovered).GetAwaiter().GetResult();
            checks["RecoveredSnapshotMatches"] = Program.HashFile(working).SequenceEqual(Program.HashFile(recovered));
            checks["OriginalNeverChanged"] = File.ReadAllText(source) == "protected original";
            bool denied = false;
            try { store.CopySnapshotAsync(entry, recovered).GetAwaiter().GetResult(); } catch (IOException) { denied = true; }
            checks["CopyDoesNotOverwriteExistingDestination"] = denied && File.ReadAllText(recovered) == "saved working snapshot";
            string broken = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, "session.json"), "{\"Source\":null,\"OpenedUtc\":\"2026-10-13T00:00:00Z\"}");
            var scan = store.ScanAsync().GetAwaiter().GetResult();
            checks["MalformedMetadataReportedWithoutHidingValidSnapshot"] = scan.Entries.Count == 1 && scan.Warnings.Count == 1 &&
                File.Exists(Path.Combine(broken, "session.json"));
            File.Delete(Path.Combine(broken, "session.json")); File.Delete(Path.Combine(broken, "session.lock")); Directory.Delete(broken);
            using (var current = Process.GetCurrentProcess()) {
                store.InitializeAsync(directory, source, current.Id, current.StartTime.ToUniversalTime().Ticks).GetAwaiter().GetResult();
                checks["LiveNativeHostNotOffered"] = store.FindAsync().GetAwaiter().GetResult().Count == 0;
                denied = false; try { store.Retire(directory); } catch (IOException) { denied = true; }
                checks["LiveHostCannotBeRetired"] = denied && File.Exists(working);
            }
            store.InitializeAsync(directory, source, 0, 0).GetAwaiter().GetResult();
            store.SnapshotSavedAsync(directory, working).GetAwaiter().GetResult();
            File.WriteAllText(working, "corrupt snapshot");
            string corruptTarget = Path.Combine(output, "corrupt-export.docx");
            denied = false; try { store.CopySnapshotAsync(entry, corruptTarget).GetAwaiter().GetResult(); } catch (IOException) { denied = true; }
            checks["CorruptSnapshotRejectedBeforeExport"] = denied && !File.Exists(corruptTarget);
            string outside = Path.Combine(output, "protected-outside"); Directory.CreateDirectory(outside);
            string marker = Path.Combine(outside, "keep.txt"); File.WriteAllText(marker, "keep");
            denied = false; try { store.Retire(outside); } catch (IOException) { denied = true; }
            checks["OutsideDirectoryCannotBeRetired"] = denied && File.ReadAllText(marker) == "keep";
            string linked = Path.Combine(directory, "linked-profile");
            bool linkCreated = false;
            try { Directory.CreateSymbolicLink(linked, outside); linkCreated = true; }
            catch (UnauthorizedAccessException) { }
            catch (IOException error) when ((error.HResult & 0xffff) == 1314) { }
            if (linkCreated) {
                denied = false; try { store.Retire(directory); } catch (IOException) { denied = true; }
                checks["LinkedContentsCannotBeRetired"] = denied && File.ReadAllText(marker) == "keep";
                Directory.Delete(linked);
            }
            store.Retire(directory);
            checks["InactiveOwnedSessionRetired"] = !Directory.Exists(directory);
            checks["RetirementPreservesOriginalAndRecovery"] = File.ReadAllText(source) == "protected original" && File.ReadAllText(recovered) == "saved working snapshot";
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Values.All(x => x), Checks = checks,
                LinkedPathCheck = linkCreated ? "executed" : "skipped: no symlink permission" }, new JsonSerializerOptions { WriteIndented = true }));
            return checks.Values.All(x => x) ? 0 : 1;
        } catch (Exception error) {
            File.WriteAllText(Path.Combine(output, "failure.json"), JsonSerializer.Serialize(new { Error = error.ToString(), Checks = checks })); return 1;
        }
    }
}
