using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using LogiBoardToolBox.Core.Diagnostics;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class DiagnosticPackageTests
{
    [Test]
    public void UnwritableJournalReportsFailureWithoutThrowingIntoDeviceCode()
    {
        using var scratch = new ScratchDirectory(); var root = Path.Combine(scratch.Path, "blocked");
        File.WriteAllText(root, "keep this file");
        var session = new DiagnosticSession(root);
        session.Record("hid", "TX", new { Report = "1100000000000000" });
        Assert.Equal(true, session.LastError is not null); Assert.Equal(0, session.CaptureFiles().Count);
        Assert.Equal("keep this file", File.ReadAllText(root));
    }

    [Test]
    public async Task PackageKeepsRawSnapshotDraftsLuaTraceAndCompleteException()
    {
        using var scratch = new ScratchDirectory();
        var session = new DiagnosticSession(Path.Combine(scratch.Path, "logs"));
        var error = Failure(); error.Data["Binding"] = "00060000";
        session.Record("hid", "RX", new { Report = "11012A57FF" });
        session.Record("write", "failed", new { Sector = 6 }, error);
        var source = Path.Combine(scratch.Path, "macros.json"); await File.WriteAllTextAsync(source, "宏与延迟 59/198/25/10");
        var files = session.CaptureFiles().Append(new DiagnosticFile(source, "attachments/macros.json")).ToArray();
        var path = Path.Combine(scratch.Path, "diagnostics.zip");
        var result = await DiagnosticPackage.ExportAsync(path, new { Snapshot = Fixture.Create(), LuaSource = "PressKey(30)", Draft = "未保存内容" }, files);
        Assert.Equal(0, result.Issues.Count);
        using var zip = ZipFile.OpenRead(path);
        using var context = JsonDocument.Parse(await Text(zip, "context.json"));
        Assert.Equal("PressKey(30)", context.RootElement.GetProperty("LuaSource").GetString());
        Assert.Equal(19, context.RootElement.GetProperty("Snapshot").GetProperty("Sectors").EnumerateObject().Count());
        var log = await Text(zip, files[0].EntryName);
        Assert.Equal(true, log.Contains("DiagnosticPackageTests.Failure"));
        using var last = JsonDocument.Parse(log.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
        Assert.Equal("00060000", last.RootElement.GetProperty("ExceptionData").GetProperty("Binding").GetString());
        Assert.Equal("宏与延迟 59/198/25/10", await Text(zip, "attachments/macros.json"));
        using var manifest = JsonDocument.Parse(await Text(zip, "manifest.json"));
        foreach (var entry in manifest.RootElement.GetProperty("Files").EnumerateArray())
        {
            using var input = zip.GetEntry(entry.GetProperty("EntryName").GetString()!)!.Open();
            Assert.Equal(entry.GetProperty("Sha256").GetString(), Convert.ToHexString(await SHA256.HashDataAsync(input)));
        }
    }

    [Test]
    public async Task ExportFreezesLogAtCompleteRecordAndDoesNotIncludeNeighbourFiles()
    {
        using var scratch = new ScratchDirectory(); var session = new DiagnosticSession(Path.Combine(scratch.Path, "logs"));
        session.Record("stage", "before export"); var files = session.CaptureFiles();
        session.Record("stage", "after snapshot"); await File.WriteAllTextAsync(Path.Combine(scratch.Path, "unrelated.txt"), "not diagnostic input");
        var path = Path.Combine(scratch.Path, "frozen.zip"); await DiagnosticPackage.ExportAsync(path, new { }, files);
        using var zip = ZipFile.OpenRead(path); var log = await Text(zip, files[0].EntryName);
        Assert.Equal(false, log.Contains("after snapshot")); Assert.Equal(true, log.EndsWith('\n'));
        Assert.Equal(false, zip.Entries.Any(e => e.FullName.Contains("unrelated")));
    }

    [Test]
    public async Task CancelledExportPreservesExistingZipAndRemovesOnlyItsTemporaryFile()
    {
        using var scratch = new ScratchDirectory(); var path = Path.Combine(scratch.Path, "existing.zip");
        await File.WriteAllTextAsync(path, "keep previous export"); var cancelled = new CancellationToken(true);
        try { await DiagnosticPackage.ExportAsync(path, new { }, [], cancelled); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Assert.Equal("keep previous export", await File.ReadAllTextAsync(path));
        Assert.Equal(1, Directory.GetFiles(scratch.Path).Length);
    }

    [Test]
    public async Task MissingAttachmentIsExplicitlyReportedWithoutHidingOtherEvidence()
    {
        using var scratch = new ScratchDirectory(); var path = Path.Combine(scratch.Path, "partial.zip");
        var result = await DiagnosticPackage.ExportAsync(path, new { Error = "sector 6" },
            [new(Path.Combine(scratch.Path, "missing.log"), "attachments/missing.log")], collectionIssues: ["older directory unavailable"]);
        Assert.Equal(2, result.Issues.Count);
        using var zip = ZipFile.OpenRead(path); using var manifest = JsonDocument.Parse(await Text(zip, "manifest.json"));
        Assert.Equal(2, manifest.RootElement.GetProperty("Issues").GetArrayLength());
        Assert.Equal(true, zip.GetEntry("context.json") is not null);
    }

    [Test]
    public async Task TraversalAndDuplicateArchiveNamesAreRejectedBeforeWriting()
    {
        using var scratch = new ScratchDirectory();
        foreach (var entries in new[] { new[] { "../outside.json" }, new[] { "C:/root.json" }, new[] { "manifest.json" }, new[] { "a.log", "A.log" } })
        {
            try { await DiagnosticPackage.ExportAsync(Path.Combine(scratch.Path, "bad.zip"), new { }, entries.Select(e => new DiagnosticFile("irrelevant", e)).ToArray()); throw new Exception("Expected invalid name."); }
            catch (ArgumentException) { }
        }
        Assert.Equal(0, Directory.GetFiles(scratch.Path).Length);
    }

    private static Exception Failure()
    {
        try { throw new IOException("outer", new InvalidDataException("CRC mismatch on sector 6")); }
        catch (Exception error) { return error; }
    }
    private static async Task<string> Text(ZipArchive zip, string name)
    { using var reader = new StreamReader(zip.GetEntry(name)!.Open()); return await reader.ReadToEndAsync(); }
}
