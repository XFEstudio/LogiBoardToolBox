using System.Text;

namespace LogiBoardToolBox.Core.Macros;

/// <summary>Loads Lua text without parsing or executing any of its contents.</summary>
public sealed record LuaSourceFile(string Name, string Source)
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public const int MaximumSourceCharacters = 4 * 1024 * 1024;

    public static void ValidateSource(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumSourceCharacters)
            throw new InvalidDataException("Lua 源码不能超过 4 MiB 字符；这与鼠标的板载存储容量无关。");
    }

    public static async Task<LuaSourceFile> ReadAsync(string path, CancellationToken ct = default)
    {
        ValidatePath(path);
        ct.ThrowIfCancellationRequested();

        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaximumFileBytes) throw TooLarge();
        using var contents = new MemoryStream((int)file.Length);
        var buffer = new byte[8192];
        while (true)
        {
            var read = await file.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            // Enforce the limit while reading too, rather than trusting a stale file length.
            if (contents.Length + read > MaximumFileBytes) throw TooLarge();
            contents.Write(buffer, 0, read);
        }
        ct.ThrowIfCancellationRequested();
        var bytes = contents.ToArray();
        var source = await Task.Run(() => Decode(bytes), ct).ConfigureAwait(false);
        ValidateSource(source);
        return new(Path.GetFileNameWithoutExtension(path), source);
    }

    /// <summary>Exports text as UTF-8 with BOM, replacing only the explicitly supplied destination.</summary>
    public static async Task WriteAsync(string path, string source, CancellationToken ct = default)
    {
        ValidatePath(path);
        ArgumentNullException.ThrowIfNull(source);
        ValidateSource(source);
        ct.ThrowIfCancellationRequested();
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        var preamble = encoding.GetPreamble();
        if (source.Length > MaximumFileBytes - preamble.Length) throw TooLarge();
        byte[] bytes;
        try
        {
            if (encoding.GetByteCount(source) > MaximumFileBytes - preamble.Length) throw TooLarge();
            bytes = encoding.GetBytes(source);
        }
        catch (EncoderFallbackException error)
        { throw new InvalidDataException("Lua 源码包含无法编码的字符，未导出文件。", error); }
        ct.ThrowIfCancellationRequested();

        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        var temporary = Path.Combine(directory, ".LogiBoardToolBox-lua-" + Guid.NewGuid().ToString("N") + ".tmp");
        var temporaryCreated = false;
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                await file.WriteAsync(preamble, ct).ConfigureAwait(false);
                await file.WriteAsync(bytes, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            // A canceled or failed write never truncates the user's existing script.
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            // This is a single private file created beside the selected target, never a directory.
            try { if (temporaryCreated && File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择扩展名为 .lua 的脚本文件。");
    }

    private static string Decode(byte[] bytes)
    {
        // Check UTF-32 first because its little-endian BOM starts with the UTF-16 BOM.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 })
            || bytes.AsSpan().StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            throw new InvalidDataException("Lua 导入不支持 UTF-32，请将文件另存为 UTF-8。");
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var offset = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) offset = 3;
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
        { encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true); offset = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
        { encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true); offset = 2; }
        try { return encoding.GetString(bytes, offset, bytes.Length - offset); }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("Lua 文件编码无效。请使用 UTF-8 或带 BOM 的 UTF-16，文件中的无效字节不会被自动替换。", error);
        }
    }

    private static InvalidDataException TooLarge() => new("Lua 文件不能超过 16 MiB；这与鼠标的板载存储容量无关。");
}
