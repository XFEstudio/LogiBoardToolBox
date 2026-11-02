using System.Diagnostics;
using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class LargeLuaTests
{
    [Test]
    public async Task MegabyteSourceImportsConvertsAndExportsWithoutLosingCommentsOrDelays()
    {
        using var scratch = new ScratchDirectory();
        var source = "--[[" + new string('x', 1200000) + "]]\nfunction OnEvent(event,arg) if event=='MOUSE_BUTTON_PRESSED' then PressMouseButton(1) Sleep(59) ReleaseMouseButton(1) Sleep(198) end end\n";
        var path = Path.Combine(scratch.Path, "large.lua");
        await LuaSourceFile.WriteAsync(path, source);
        var imported = await LuaSourceFile.ReadAsync(path);
        Assert.Equal(source, imported.Source);
        var compiled = LuaMacroImport.Convert(imported.Source).CompileProgram().ToOnboard();
        Assert.Equal("41000140003B4200014000C6FF", Convert.ToHexString(compiled.Bytes));
        Assert.Equal(true, source.Length > 1024 * 1024);
    }

    [Test]
    public void LargeConfigurationTableDoesNotConsumeOnboardSpaceUnlessUsedBySelectedEvent()
    {
        var source = "local p={" + string.Join(',', Enumerable.Repeat("1", 10000)) + "}\nfunction OnEvent(event,arg) if event=='MOUSE_BUTTON_PRESSED' then MoveMouseRelative(0,p[10000]) Sleep(59) end end";
        var compiled = LuaMacroImport.Convert(source).CompileProgram().ToOnboard();
        Assert.Equal("610000000140003BFF", Convert.ToHexString(compiled.Bytes));
    }

    [Test]
    public async Task ConversionCanBeCanceledDuringLongStaticComputation()
    {
        using var cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew(); var canceled = false;
        var conversion = Task.Run(() => LuaMacroImport.Convert("function OnEvent(event,arg) local n=0 for i=1,1000000000 do n=n+1 end PressAndReleaseKey('a') end", cancellationToken: cts.Token));
        cts.CancelAfter(30);
        try { await conversion; } catch (OperationCanceledException) { canceled = true; }
        Assert.Equal(true, canceled);
        Assert.Equal(true, watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Test]
    public void PagedSourceEditsPreserveAllOtherTextAndDoNotSplitSurrogatePairs()
    {
        var source = new string('a', LuaEditorDocument.PageCharacters - 1) + "🖱" + new string('b', 90000);
        var document = new LuaEditorDocument(source);
        Assert.Equal(source, string.Concat(Enumerable.Range(0, document.PageCount).Select(document.ReadPage)));
        Assert.Equal(true, document.ReadPage(0).EndsWith("🖱", StringComparison.Ordinal));
        var old = document.ReadPage(1); document.ReplacePage(1, "changed\n" + old);
        Assert.Equal(source.Insert(LuaEditorDocument.PageCharacters + 1, "changed\n"), document.Source);
        Assert.Equal(document.Source, string.Concat(Enumerable.Range(0, document.PageCount).Select(document.ReadPage)));
    }
}
