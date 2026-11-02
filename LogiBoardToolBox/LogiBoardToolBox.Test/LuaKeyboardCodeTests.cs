using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class LuaKeyboardCodeTests
{
    [Test]
    public void GHubNumbersAreScanCodesRatherThanAsciiOrHidUsages()
    {
        Assert.Equal((byte)0x04, LuaKeyboardCodes.ToHidUsage(30)); // documented PressKey(30) = A
        Assert.Equal((byte)0x1E, LuaKeyboardCodes.ToHidUsage(2)); // top-row 1, not HID post-fail
        Assert.Equal((byte)0x20, LuaKeyboardCodes.ToHidUsage(4)); // top-row 3, not A
        Assert.Equal((byte)0x2C, LuaKeyboardCodes.ToHidUsage(57));
        Assert.Equal((byte)0x31, LuaKeyboardCodes.ToHidUsage(0x2B));
        Assert.Equal((byte)0x58, LuaKeyboardCodes.ToHidUsage(0x11C));
        Assert.Equal((byte)0x28, LuaKeyboardCodes.ToHidUsage(0x1C));
    }

    [Test]
    public void DocumentedExtendedModifiersNavigationAndFunctionCodesAreNotTruncated()
    {
        Assert.Equal((byte)0xE0, LuaKeyboardCodes.ToHidUsage(0x1D));
        Assert.Equal((byte)0xE4, LuaKeyboardCodes.ToHidUsage(0x11D));
        Assert.Equal((byte)0xE3, LuaKeyboardCodes.ToHidUsage(0x15B));
        Assert.Equal((byte)0xE7, LuaKeyboardCodes.ToHidUsage(0x15C));
        Assert.Equal((byte)0x4A, LuaKeyboardCodes.ToHidUsage(0x147));
        Assert.Equal((byte)0x46, LuaKeyboardCodes.ToHidUsage(0x137));
        Assert.Equal((byte)0x48, LuaKeyboardCodes.ToHidUsage(0x146));
        Assert.Equal((byte)0x68, LuaKeyboardCodes.ToHidUsage(0x64));
        Assert.Equal((byte)0x73, LuaKeyboardCodes.ToHidUsage(0x76));
        foreach (var code in new[] { -1, 0, 0x7F, 0xE01D, 0xFFFF })
        { Assert.Equal(false, LuaKeyboardCodes.TryToHidUsage(code, out _)); Reject(() => LuaKeyboardCodes.ToHidUsage(code)); }
        foreach (var code in new[] { 1.5, double.NaN, double.PositiveInfinity }) Reject(() => LuaKeyboardCodes.ToHidUsage(code));
    }

    [Test]
    public async Task SoftwareLuaProducesIdenticalEventsForNumericAndNamedKeys()
    {
        var numeric = await Execute("30, 2, 0x11D");
        var named = await Execute("\"a\", \"1\", \"rctrl\"");
        Assert.Equal(true, numeric.SequenceEqual(named));
        Assert.Equal("4,30,228,228,30,4", string.Join(',', numeric.Select(e => e.Code)));
        Assert.Equal("True,True,True,False,False,False", string.Join(',', numeric.Select(e => e.Down)));
    }

    [Test]
    public void ExportInverseUsesOnlyVerifiedScanCodesAndPreservesEveryMappedUsage()
    {
        Assert.Equal(30, LuaKeyboardCodes.FromHidUsage(0x04));
        Assert.Equal(2, LuaKeyboardCodes.FromHidUsage(0x1E));
        Assert.Equal(0x11C, LuaKeyboardCodes.FromHidUsage(0x58));
        Assert.Equal(0x11D, LuaKeyboardCodes.FromHidUsage(0xE4));
        var verified = 0;
        for (var scanCode = 1; scanCode <= 0x1FF; scanCode++)
        {
            if (!LuaKeyboardCodes.TryToHidUsage(scanCode, out var usage)) continue;
            Assert.Equal(usage, LuaKeyboardCodes.ToHidUsage(LuaKeyboardCodes.FromHidUsage(usage)));
            verified++;
        }
        Assert.Equal(true, verified > 100);
        foreach (var usage in new byte[] { 0, 1, 0x55, 0xFF })
        {
            Assert.Equal(false, LuaKeyboardCodes.TryFromHidUsage(usage, out _));
            Reject(() => LuaKeyboardCodes.FromHidUsage(usage));
        }
    }

    private static async Task<MacroInput[]> Execute(string keys)
    {
        var sink = new InputSink(); await using var lua = new LuaMacroRuntime(sink);
        await lua.StartAsync("function OnEvent(event,arg) if event == 'MOUSE_BUTTON_PRESSED' and arg == 1 then PressAndReleaseKey(" + keys + ") end end");
        lua.MouseButton(1, true);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sink.Count < 6 && DateTime.UtcNow < deadline) await Task.Delay(1);
        lua.MouseButton(1, false);
        Assert.Equal(6, sink.Count);
        return sink.Events.ToArray();
    }
    private sealed class InputSink : IMacroInputSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<MacroInput> Events { get; } = new();
        public int Count => Events.Count;
        public void Send(MacroInput input) => Events.Enqueue(input);
    }
    private static void Reject(Action action)
    { try { action(); } catch (FormatException) { return; } throw new Exception("Expected unsupported scan code to be rejected."); }
}
