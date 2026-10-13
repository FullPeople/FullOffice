using System.Diagnostics;
using System.Text.Json;

namespace FlowOfficeLab;

public sealed record OfficeRecoveryEntry(string Directory, string Source, string WorkingFile,
    DateTime OpenedUtc, DateTime? SavedUtc, bool HasSnapshot);
public sealed record OfficeRecoveryScan(IReadOnlyList<OfficeRecoveryEntry> Entries, IReadOnlyList<string> Warnings);

public sealed class OfficeRecoveryStore
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullOffice", "EditingSessions");
    readonly string root;
    public OfficeRecoveryStore(string? root = null) { this.root = Path.GetFullPath(root ?? DefaultRoot); }
    sealed record Record(string Source, DateTime OpenedUtc, int HostPid = 0, long HostStartedUtcTicks = 0,
        DateTime? SavedUtc = null, string? SnapshotSHA256 = null);
    internal static void RejectLinks(string path)
    {
        for (var item = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); item != null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复目录中存在链接，无法安全处理。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("恢复文件是链接，无法安全处理。");
    }
    string CheckedSession(string path)
    {
        string full = Path.GetFullPath(path);
        if (!Path.GetDirectoryName(full)!.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new IOException("恢复目录超出范围。");
        RejectLinks(Path.Combine(full, "session.json")); return full;
    }
    public FileStream Acquire(string directory)
    {
        string path = Path.Combine(CheckedSession(directory), "session.lock"); RejectLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public async Task InitializeAsync(string directory, string source, int hostPid, long hostStartedUtcTicks)
    {
        directory = CheckedSession(directory);
        var record = new Record(Path.GetFullPath(source), DateTime.UtcNow, hostPid, hostStartedUtcTicks);
        await WriteRecord(directory, record);
    }
    async Task WriteRecord(string directory, Record record)
    {
        string target = Path.Combine(directory, "session.json"), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        RejectLinks(target);
        try {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record));
            if (File.Exists(target)) File.Move(temporary, target, true); else File.Move(temporary, target);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    Record ReadRecord(string directory)
    {
        string path = Path.Combine(CheckedSession(directory), "session.json");
        if (new FileInfo(path).Length > 65536) throw new IOException("恢复记录过大。");
        var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(path)) ?? throw new IOException("恢复记录无效。");
        if (string.IsNullOrWhiteSpace(record.Source) || !Path.IsPathFullyQualified(record.Source) ||
            !DocumentFormats.IsOffice(record.Source) || record.OpenedUtc == default || record.HostPid < 0 ||
            (record.SnapshotSHA256 != null && (record.SavedUtc == null || record.SnapshotSHA256.Length != 64 ||
             !record.SnapshotSHA256.All(Uri.IsHexDigit)))) throw new IOException("恢复记录缺少有效的文档信息。");
        return record;
    }
    public async Task SnapshotSavedAsync(string directory, string workingFile)
    {
        var record = ReadRecord(directory);
        string hash = await Task.Run(() => Convert.ToHexString(Program.HashFile(workingFile)));
        await WriteRecord(directory, record with { SavedUtc = DateTime.UtcNow, SnapshotSHA256 = hash });
    }
    static bool HostAlive(Record record)
    {
        if (record.HostPid == 0) return false;
        try { using var process = Process.GetProcessById(record.HostPid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == record.HostStartedUtcTicks; }
        catch (ArgumentException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
        catch (InvalidOperationException) { return false; }
    }
    public async Task<IReadOnlyList<OfficeRecoveryEntry>> FindAsync() => (await ScanAsync()).Entries;
    public Task<OfficeRecoveryScan> ScanAsync() => Task.Run(() => {
        var entries = new List<OfficeRecoveryEntry>();
        var warnings = new List<string>();
        if (!Directory.Exists(root)) return new OfficeRecoveryScan(entries, warnings);
        RejectLinks(Path.Combine(root, "scope-check"));
        foreach (string directory in Directory.EnumerateDirectories(root)) {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try {
                using var lease = Acquire(directory); var record = ReadRecord(directory);
                if (HostAlive(record)) continue;
                string working = Path.Combine(directory, "document", Path.GetFileName(record.Source)); RejectLinks(working);
                if (!File.Exists(working)) throw new IOException("临时文档已不存在。");
                entries.Add(new(directory, record.Source, working, record.OpenedUtc, record.SavedUtc, record.SnapshotSHA256 != null));
            } catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException) {
                warnings.Add(Path.GetFileName(directory) + "：" + error.Message);
            }
        }
        return new OfficeRecoveryScan(entries.OrderByDescending(item => item.SavedUtc ?? item.OpenedUtc).ToList(), warnings);
    });
    public async Task CopySnapshotAsync(OfficeRecoveryEntry entry, string destination)
    {
        RejectLinks(destination);
        using var lease = Acquire(entry.Directory); var record = ReadRecord(entry.Directory);
        if (HostAlive(record)) throw new IOException("文档编辑器仍在运行，请稍后再恢复。");
        string working = Path.Combine(CheckedSession(entry.Directory), "document", Path.GetFileName(record.Source)); RejectLinks(working);
        if (!Path.GetFullPath(entry.WorkingFile).Equals(working, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复文件位置不匹配。");
        if (record.SnapshotSHA256 != null && !record.SnapshotSHA256.Equals(
            await Task.Run(() => Convert.ToHexString(Program.HashFile(working))), StringComparison.Ordinal))
            throw new IOException("恢复文件校验失败。请保留会话以便排查。");
        await using var input = new FileStream(working, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await input.CopyToAsync(output); await output.FlushAsync();
    }
    public void Retire(string directory)
    {
        directory = CheckedSession(directory);
        using var lease = Acquire(directory);
        if (HostAlive(ReadRecord(directory))) throw new IOException("正在运行的文档不能清理。");
        var pending = new Stack<string>(); pending.Push(directory);
        var files = new List<string>(); var directories = new List<string>();
        string lockPath = Path.Combine(directory, "session.lock");
        while (pending.TryPop(out string? current)) {
            directories.Add(current);
            foreach (string entry in Directory.EnumerateFileSystemEntries(current)) {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("会话内存在链接，无法清理。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else if (!entry.Equals(lockPath, StringComparison.OrdinalIgnoreCase)) files.Add(entry);
            }
        }
        foreach (string file in files) { RejectLinks(file); File.Delete(file); }
        foreach (string child in directories.Where(item => item != directory).OrderByDescending(item => item.Length)) Directory.Delete(child);
        // Keep the lease until all document/profile content has been retired.
        // A new lease at the final boundary prevents lock deletion or the nonrecursive directory deletion.
        lease.Dispose(); File.Delete(lockPath); Directory.Delete(directory);
    }
}
