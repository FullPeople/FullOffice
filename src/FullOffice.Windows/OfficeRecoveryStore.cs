using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlowOfficeLab;

public sealed record OfficeRecoveryEntry(string Directory, string Source, string WorkingFile,
    DateTime OpenedUtc, DateTime? SavedUtc, bool HasSnapshot);
public sealed record OfficeRecoveryScan(IReadOnlyList<OfficeRecoveryEntry> Entries, IReadOnlyList<string> Warnings);

public sealed class OfficeRecoveryStore
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FullOffice", "EditingSessions");
    readonly string root;
    readonly SemaphoreSlim snapshotGate = new(1, 1);
    public OfficeRecoveryStore(string? root = null) { this.root = Path.GetFullPath(root ?? DefaultRoot); }
    sealed record Record(string Source, DateTime OpenedUtc, int HostPid = 0, long HostStartedUtcTicks = 0,
        DateTime? SavedUtc = null, string? SnapshotSHA256 = null, string? SnapshotFile = null);
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
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough)) {
                await output.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record))).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false); output.Flush(true);
            }
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
        if (record.SnapshotFile != null && (record.SnapshotSHA256 == null || record.SnapshotFile.Length != 46 ||
            !record.SnapshotFile.StartsWith("snapshot-", StringComparison.Ordinal) ||
            !record.SnapshotFile.EndsWith(".data", StringComparison.Ordinal) ||
            !Guid.TryParseExact(record.SnapshotFile.AsSpan(9, 32), "N", out _)))
            throw new IOException("恢复快照的位置无效。");
        return record;
    }
    public async Task SnapshotSavedAsync(string directory, string workingFile)
    {
        await snapshotGate.WaitAsync().ConfigureAwait(false);
        string? snapshot = null; bool published = false;
        try {
            directory = CheckedSession(directory); var record = ReadRecord(directory);
            string expected = Path.Combine(directory, "document", Path.GetFileName(record.Source));
            if (!Path.GetFullPath(workingFile).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("临时文档超出当前会话范围。");
            RejectLinks(workingFile);
            string name = "snapshot-" + Guid.NewGuid().ToString("N") + ".data";
            snapshot = Path.Combine(directory, name); RejectLinks(snapshot);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = new FileStream(workingFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                65536, FileOptions.Asynchronous))
            await using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough)) {
                var buffer = new byte[65536]; int count;
                while ((count = await input.ReadAsync(buffer).ConfigureAwait(false)) != 0) {
                    hash.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false);
                }
                await output.FlushAsync().ConfigureAwait(false); output.Flush(true);
            }
            byte[] snapshotHash = hash.GetHashAndReset();
            byte[] currentHash = await Task.Run(() => Program.HashFile(workingFile)).ConfigureAwait(false);
            if (!snapshotHash.SequenceEqual(currentHash))
                throw new IOException("临时文档在生成恢复快照时发生变化，上一份快照仍保留，请再保存一次。");
            await WriteRecord(directory, record with { SavedUtc = DateTime.UtcNow,
                SnapshotSHA256 = Convert.ToHexString(snapshotHash), SnapshotFile = name }).ConfigureAwait(false);
            published = true;
            if (record.SnapshotFile != null) {
                string previous = Path.Combine(directory, record.SnapshotFile);
                try { RejectLinks(previous); File.Delete(previous); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    Debug.WriteLine("Previous recovery snapshot retained: " + error.Message);
                }
            }
        } finally {
            if (!published && snapshot != null) {
                try { RejectLinks(snapshot); File.Delete(snapshot); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    Debug.WriteLine("Uncommitted recovery snapshot retained: " + error.Message);
                }
            }
            snapshotGate.Release();
        }
    }
    static string RecoveryFile(string directory, Record record) => record.SnapshotFile == null
        ? Path.Combine(directory, "document", Path.GetFileName(record.Source)) : Path.Combine(directory, record.SnapshotFile);
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
                string working = RecoveryFile(directory, record); RejectLinks(working);
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
        string working = RecoveryFile(CheckedSession(entry.Directory), record); RejectLinks(working);
        if (!Path.GetFullPath(entry.WorkingFile).Equals(working, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复文件位置不匹配。");
        await using var input = new FileStream(working, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (record.SnapshotSHA256 != null && !record.SnapshotSHA256.Equals(
            Convert.ToHexString(await SHA256.HashDataAsync(input)), StringComparison.Ordinal))
            throw new IOException("恢复文件校验失败。请保留会话以便排查。");
        input.Position = 0;
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
        var handles = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        try {
            // Reserve deletion rights for the entire session before removing its recovery record.
            // Sharing only deletion rejects existing readers/writers and prevents new ones during cleanup.
            foreach (string path in files.Concat(directories)) {
                RejectLinks(path);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0 && (attributes & FileAttributes.ReadOnly) != 0)
                    throw new IOException("恢复会话中存在只读内容，已保留完整记录。");
                const uint deleteAccess = 0x00010000, shareDeletion = 0x00000004, openExisting = 3,
                    backupSemantics = 0x02000000, openReparsePoint = 0x00200000;
                var handle = CreateRetirementHandle(path, deleteAccess, shareDeletion, 0, openExisting, backupSemantics | openReparsePoint, 0);
                if (handle.IsInvalid) {
                    int error = Marshal.GetLastWin32Error(); handle.Dispose();
                    throw new IOException("恢复会话仍被占用，已保留完整记录。", new System.ComponentModel.Win32Exception(error));
                }
                handles.Add(path, handle);
            }
            foreach (string file in files) { RejectLinks(file); File.Delete(file); handles[file].Dispose(); }
            foreach (string child in directories.Where(item => item != directory).OrderByDescending(item => item.Length)) {
                Directory.Delete(child); handles[child].Dispose();
            }
            // Keep the lease until all document/profile content has been retired.
            lease.Dispose(); File.Delete(lockPath); Directory.Delete(directory); handles[directory].Dispose();
        } finally { foreach (var handle in handles.Values) handle.Dispose(); }
    }
    internal static bool RetirementBlockedBySharing(Exception error) => error is IOException &&
        ((error.HResult & 0xffff) is 32 or 33 || error.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 32 or 33 });
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateRetirementHandle(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
}
