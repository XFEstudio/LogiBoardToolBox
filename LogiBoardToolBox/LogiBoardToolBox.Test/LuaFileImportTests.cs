using System.Text;
using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class LuaFileImportTests
{
    [Test]
    public async Task Utf8ChineseTextAndCaseInsensitiveLuaNameLoadWithoutExecution()
    {
        using var scratch = new ScratchDirectory();
        var path = Path.Combine(scratch.Path, "左键连点.LUA");
        const string source = "-- 中文注释，保留换行及录制延迟\r\nerror('导入时绝不能执行')\r\nSleep(59)\n";
        await File.WriteAllBytesAsync(path, new UTF8Encoding(false, true).GetBytes(source));
        var imported = await LuaSourceFile.ReadAsync(path);
        Assert.Equal("左键连点", imported.Name);
        Assert.Equal(source, imported.Source);
    }

    [Test]
    public async Task Utf8BomIsRecognizedAndRemovedFromSource()
    {
        using var scratch = new ScratchDirectory();
        var path = Path.Combine(scratch.Path, "bom.lua");
        const string source = "-- UTF-8 的中文\nfunction OnEvent(event,arg) end";
        var encoding = new UTF8Encoding(true, true);
        await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray());
        Assert.Equal(source, (await LuaSourceFile.ReadAsync(path)).Source);
    }

    [Test]
    public async Task Utf16LittleAndBigEndianBomPreserveSourceExactly()
    {
        using var scratch = new ScratchDirectory();
        const string source = "-- 中文和表情 🖱\r\nfunction OnEvent(event,arg) Sleep(198) end\r\n";
        foreach (var bigEndian in new[] { false, true })
        {
            var path = Path.Combine(scratch.Path, bigEndian ? "big.lua" : "little.lua");
            var encoding = new UnicodeEncoding(bigEndian, true, true);
            await File.WriteAllBytesAsync(path, encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray());
            Assert.Equal(source, (await LuaSourceFile.ReadAsync(path)).Source);
        }
    }

    [Test]
    public async Task SourceCharacterBoundaryIsAcceptedAndLargerSourcesAreRejected()
    {
        using var scratch = new ScratchDirectory();
        var path = Path.Combine(scratch.Path, "limit.lua");
        var bytes = Enumerable.Repeat((byte)' ', LuaSourceFile.MaximumSourceCharacters).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal(LuaSourceFile.MaximumSourceCharacters, (await LuaSourceFile.ReadAsync(path)).Source.Length);
        await File.WriteAllBytesAsync(path, bytes.Append((byte)' ').ToArray());
        await Reject(path);
    }

    [Test]
    public async Task OtherExtensionsAndDisguisedExecutableNamesAreRejected()
    {
        using var scratch = new ScratchDirectory();
        foreach (var name in new[] { "script.txt", "script.lua.exe", "script" })
        {
            var path = Path.Combine(scratch.Path, name);
            await File.WriteAllTextAsync(path, "function OnEvent(event,arg) end");
            await Reject(path);
        }
    }

    [Test]
    public async Task InvalidUtf8AndMalformedUtf16AreRejectedWithoutReplacementCharacters()
    {
        using var scratch = new ScratchDirectory();
        var samples = new[]
        {
            new byte[] { 0xC3, 0x28 },
            new byte[] { 0xEF, 0xBB, 0xBF, 0xFF },
            new byte[] { 0xFF, 0xFE, 0x61 },
            new byte[] { 0xFE, 0xFF, 0xD8, 0x00 },
            new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x61, 0x00, 0x00, 0x00 },
            new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x61 },
        };
        for (var index = 0; index < samples.Length; index++)
        {
            var path = Path.Combine(scratch.Path, index + ".lua");
            await File.WriteAllBytesAsync(path, samples[index]);
            await Reject(path);
        }
    }

    [Test]
    public async Task CanceledImportDoesNotReadTheScript()
    {
        using var scratch = new ScratchDirectory();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var canceled = false;
        try { await LuaSourceFile.ReadAsync(Path.Combine(scratch.Path, "not-created.lua"), cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Assert.Equal(true, canceled);
    }

    private static async Task Reject(string path)
    {
        var rejected = false;
        try { await LuaSourceFile.ReadAsync(path); }
        catch (InvalidDataException) { rejected = true; }
        Assert.Equal(true, rejected);
    }

    [Test]
    public async Task ExportWritesUtf8BomAndRoundTripsUnicodeAndMixedNewlinesWithoutExecution()
    {
        using var scratch = new ScratchDirectory();
        var path = Path.Combine(scratch.Path, "连点导出.LUA");
        const string source = "-- 中文与表情 🖱\r\nerror('导出绝不能执行脚本')\n\nSleep(59)\r\n  ";
        await LuaSourceFile.WriteAsync(path, source);
        var encoding = new UTF8Encoding(true, true);
        var expectedBytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
        Assert.Equal(Convert.ToHexString(expectedBytes), Convert.ToHexString(await File.ReadAllBytesAsync(path)));
        var imported = await LuaSourceFile.ReadAsync(path);
        Assert.Equal("连点导出", imported.Name); Assert.Equal(source, imported.Source);
        Assert.Equal(0, Directory.EnumerateFiles(scratch.Path, ".LogiBoardToolBox-lua-*.tmp").Count());
    }

    [Test]
    public async Task ExportOverwritesOnlyTheExplicitlyProvidedFile()
    {
        using var scratch = new ScratchDirectory();
        var selected = Path.Combine(scratch.Path, "selected.lua");
        var neighbor = Path.Combine(scratch.Path, "neighbor.lua");
        await File.WriteAllTextAsync(selected, "old selected source");
        await File.WriteAllTextAsync(neighbor, "untouched source");
        const string updated = "function OnEvent(event,arg)\n  Sleep(198)\nend\n";
        await LuaSourceFile.WriteAsync(selected, updated);
        Assert.Equal(updated, (await LuaSourceFile.ReadAsync(selected)).Source);
        Assert.Equal("untouched source", await File.ReadAllTextAsync(neighbor));
        Assert.Equal(2, Directory.EnumerateFiles(scratch.Path).Count());
    }

    [Test]
    public async Task ExportSourceLimitIncludesAllPagesAndRejectsOversizedUnicodeBeforeOverwriting()
    {
        using var scratch = new ScratchDirectory();
        var path = Path.Combine(scratch.Path, "size.lua");
        var maximumAscii = new string(' ', LuaSourceFile.MaximumSourceCharacters);
        await LuaSourceFile.WriteAsync(path, maximumAscii);
        Assert.Equal((long)LuaSourceFile.MaximumSourceCharacters + 3, new FileInfo(path).Length);
        Assert.Equal(maximumAscii, (await LuaSourceFile.ReadAsync(path)).Source);
        var before = await File.ReadAllBytesAsync(path);
        foreach (var source in new[] { maximumAscii + " ", new string('鼠', LuaSourceFile.MaximumSourceCharacters + 1) })
        {
            var rejected = false;
            try { await LuaSourceFile.WriteAsync(path, source); }
            catch (InvalidDataException) { rejected = true; }
            Assert.Equal(true, rejected);
            Assert.Equal(Convert.ToHexString(before), Convert.ToHexString(await File.ReadAllBytesAsync(path)));
        }
        Assert.Equal(0, Directory.EnumerateFiles(scratch.Path, ".LogiBoardToolBox-lua-*.tmp").Count());
    }

    [Test]
    public async Task CanceledExportPreservesExistingFileAndLeavesNoTemporaryFile()
    {
        using var scratch = new ScratchDirectory();
        var path = Path.Combine(scratch.Path, "cancel.lua");
        await File.WriteAllTextAsync(path, "original text");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var canceled = false;
        try { await LuaSourceFile.WriteAsync(path, "replacement", cancellation.Token); }
        catch (OperationCanceledException) { canceled = true; }
        Assert.Equal(true, canceled); Assert.Equal("original text", await File.ReadAllTextAsync(path));
        Assert.Equal(1, Directory.EnumerateFiles(scratch.Path).Count());
    }

    [Test]
    public async Task FailedExportRenameCleansItsTemporaryFile()
    {
        using var scratch = new ScratchDirectory();
        var path = Path.Combine(scratch.Path, "directory.lua");
        Directory.CreateDirectory(path);
        var failed = false;
        try { await LuaSourceFile.WriteAsync(path, "source"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        Assert.Equal(true, failed); Assert.Equal(true, Directory.Exists(path));
        Assert.Equal(0, Directory.EnumerateFiles(scratch.Path).Count());
    }

    [Test]
    public async Task ExportRejectsWrongExtensionAndInvalidUnicodeWithoutReplacingExistingData()
    {
        using var scratch = new ScratchDirectory();
        foreach (var (name, source) in new[] { ("wrong.txt", "source"), ("bad.lua", "\uD800") })
        {
            var path = Path.Combine(scratch.Path, name);
            await File.WriteAllTextAsync(path, "original");
            var rejected = false;
            try { await LuaSourceFile.WriteAsync(path, source); }
            catch (InvalidDataException) { rejected = true; }
            Assert.Equal(true, rejected); Assert.Equal("original", await File.ReadAllTextAsync(path));
        }
        Assert.Equal(0, Directory.EnumerateFiles(scratch.Path, ".LogiBoardToolBox-lua-*.tmp").Count());
    }
}
