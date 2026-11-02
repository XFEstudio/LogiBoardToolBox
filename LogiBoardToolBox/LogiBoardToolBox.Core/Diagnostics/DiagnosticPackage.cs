using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogiBoardToolBox.Core.Diagnostics;

public sealed record DiagnosticExportResult(string Path, int Files, IReadOnlyList<string> Issues);

public static class DiagnosticPackage
{
    public static async Task<DiagnosticExportResult> ExportAsync(string path, object context,
        IReadOnlyList<DiagnosticFile> files, CancellationToken ct = default, IReadOnlyList<string>? collectionIssues = null)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("诊断包请保存为 .zip 文件。", nameof(path));
        // Validate before creating anything; archive entries must not escape their directory.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "context.json", "manifest.json", "README.txt" };
        foreach (var file in files)
            if (file.EntryName.Contains('\\') || file.EntryName.Split('/').Any(s => s is "" or "." or ".." || s.Contains(':')) || !names.Add(file.EntryName))
                throw new ArgumentException("诊断包条目名称无效或重复。");
        var parent = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        var temporary = System.IO.Path.Combine(parent, $".LogiBoard-diagnostics-{Guid.NewGuid():N}.tmp");
        var issues = collectionIssues?.ToList() ?? [];
        var entries = new List<object>();
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
                await AddJson("context.json", context);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var full = System.IO.Path.GetFullPath(file.Path);
                        if (full.Equals(path, StringComparison.OrdinalIgnoreCase) || full.Equals(temporary, StringComparison.OrdinalIgnoreCase)) continue;
                        RejectLinks(full);
                        await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
                        var length = file.Length ?? input.Length;
                        if (length < 0 || input.Length < length) throw new IOException("文件在导出前已被截短。");
                        var entry = zip.CreateEntry(file.EntryName, CompressionLevel.Optimal);
                        await using var target = entry.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[65536]; long copied = 0;
                        while (copied < length)
                        {
                            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), ct);
                            if (count == 0) throw new EndOfStreamException("文件在导出期间被截短。");
                            await target.WriteAsync(buffer.AsMemory(0, count), ct); hash.AppendData(buffer, 0, count); copied += count;
                        }
                        entries.Add(new { file.EntryName, Bytes = copied, Sha256 = Convert.ToHexString(hash.GetHashAndReset()) });
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { issues.Add(file.EntryName + ": " + error.Message); }
                }
                await AddJson("manifest.json", new { Format = "LogiBoardToolBox.Diagnostics.v1", At = DateTimeOffset.Now,
                    Environment = DiagnosticSession.EnvironmentInfo(), Files = entries, Issues = issues,
                    HardwareWrites = false, InputGenerated = false });
                var readme = zip.CreateEntry("README.txt");
                await using var readmeStream = readme.Open();
                await readmeStream.WriteAsync(Encoding.UTF8.GetBytes("LogiBoardToolBox 诊断包\ncontext.json：设备、原始板载快照、逐扇区校验、编辑草稿、宏/Lua 和完整错误。\nsessions/：逐条操作、HID++ 收发和异常调用栈；分卷不删除本会话记录。\nattachments/：软件配置、自动备份和历史日志。\nmanifest.json：文件校验值及无法导出的文件；Issues 非空表示有缺失。\n导出只读取软件已有数据，不切换设备模式、不写入鼠标、不生成按键。\n宏及 Lua 源码包含在包内。\n"), ct);
                async Task AddJson(string name, object value)
                {
                    var entry = zip.CreateEntry(name);
                    await using var target = entry.Open();
                    await JsonSerializer.SerializeAsync(target, value, new JsonSerializerOptions { WriteIndented = true }, ct);
                }
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return new(path, entries.Count, issues);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null;
            current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("不跟随诊断文件或目录链接。");
    }
}
