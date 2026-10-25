using System.Threading.Channels;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.Macros;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

internal sealed class FeatureMouse : IHidTransport
{
    private readonly Channel<byte[]> reports = Channel.CreateUnbounded<byte[]>();
    public List<byte[]> Requests { get; } = [];
    private readonly Dictionary<ushort, byte> features = new() { [0x0003] = 2, [0x1004] = 3, [0x2202] = 7, [0x8061] = 14, [0x8071] = 9, [0x8081] = 10, [0x8090] = 11, [0x8110] = 13, [0x8100] = 12 };
    public int InputReportLength => 20;
    public int OutputReportLength => 20;
    public ushort X = 800, Y = 1200;
    public byte Rate = 3, Mode = 2;
    public bool BadNextDpiRead;
    public bool HeroUnavailable, Hits;
    public byte ModeStatus = 0xA1;
    public byte[] Startup = [0, 0, 1, 1, 11, 22, 33, 44, 55, 66];
    public byte[] Map = [1, 2, 3, 4, 0, 5, 0, 0, 0, 0, 0];
    public byte[] Control = [0, 1, 2];
    public Task WriteAsync(byte[] request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Add(request.ToArray());
        var reply = new byte[20]; request.AsSpan(0, 4).CopyTo(reply);
        var function = request[3] >> 4; var feature = request[2];
        if (feature == 0)
        { var id = (ushort)((request[4] << 8) | request[5]); reply[4] = id == 0x1B0C && Hits ? (byte)15 : features.GetValueOrDefault(id); }
        else if (feature == 2)
        { if (function == 0) reply[4] = 1; if (function == 1) Convert.FromHexString("004D504D270100220140992903652500").CopyTo(reply, 4); }
        else if (feature == 3 && function == 1)
        { reply[4] = 65; reply[5] = 4; reply[6] = 1; }
        else if (feature == 7)
        {
            if (function == 1) { reply[5] = 5; reply[6] = 13; }
            if (function == 2)
            {
                var stream = Convert.FromHexString("0064E00100C8E00201F4E00503E8E00A07D0E03264000000");
                reply[5] = request[5]; reply[6] = request[6];
                var start = request[6] * 13; stream.AsSpan(start, Math.Min(13, stream.Length - start)).CopyTo(reply.AsSpan(7));
            }
            if (function == 5)
            {
                var x = BadNextDpiRead ? 123 : X; BadNextDpiRead = false;
                reply[5] = (byte)(x >> 8); reply[6] = (byte)x; reply[7] = 3; reply[8] = 32;
                reply[9] = (byte)(Y >> 8); reply[10] = (byte)Y; reply[11] = 3; reply[12] = 32;
            }
            if (function == 6) { X = (ushort)((request[5] << 8) | request[6]); Y = (ushort)((request[7] << 8) | request[8]); }
            if (function == 8)
            {
                if (HeroUnavailable) { reply[2] = 0xFF; reply[3] = request[2]; reply[4] = request[3]; reply[5] = 5; }
                else new byte[] { 0, 75, 0, 125, 3, 32, 3, 32 }.CopyTo(reply, 4);
            }
        }
        else if (feature == 14)
        { if (function is 0 or 1) { reply[4] = 0xFF; reply[5] = 15; } if (function == 2) reply[4] = Rate; if (function == 3) Rate = request[4]; }
        else if (feature == 9)
        {
            if (function == 0)
            {
                if (request[4] == 255) { reply[6] = 1; reply[8] = 1; }
                else if (request[5] == 255) { reply[4] = request[4]; reply[8] = 4; }
                else { reply[4] = request[4]; reply[5] = request[5]; reply[7] = new byte[] { 0, 1, 10, 3 }[request[5]]; }
            }
            if (function == 5) { if (request[4] == 1) Control = request[4..7]; Control.CopyTo(reply, 4); }
            if (function == 3) { if (request[4] == 1) { Startup = request[4..14]; Startup[0] = 0; } Startup.CopyTo(reply, 4); }
        }
        else if (feature == 10)
        {
            if (function == 0) { reply[5] = request[5]; if (request[5] == 0) { reply[6] = 255; reply[7] = 1; } }
            else request.AsSpan(4, 16).CopyTo(reply.AsSpan(4));
        }
        else if (feature == 12)
        { if (function == 2) reply[4] = Mode; if (function == 1) Mode = request[4]; if (function == 4) reply[5] = 1; }
        else if (feature == 11)
        { if (function == 0) reply[5] = ModeStatus; if (function == 1) ModeStatus = (byte)((ModeStatus & ~request[7]) | (request[5] & request[7])); }
        else if (feature == 13)
        {
            if (function == 0) reply[4] = 11;
            if (function == 3) Map.CopyTo(reply, 4);
            if (function == 4) Map = request[4..15];
        }
        reports.Writer.TryWrite(reply); return Task.CompletedTask;
    }
    public void Notify(byte feature, ushort mask)
    { var data = new byte[20]; data[0] = 0x11; data[1] = 1; data[2] = feature; data[4] = (byte)(mask >> 8); data[5] = (byte)mask; reports.Writer.TryWrite(data); }
    public async Task<byte[]> ReadAsync(CancellationToken ct) => await reports.Reader.ReadAsync(ct);
    public void Dispose() => reports.Writer.TryComplete();
}

[TestFixture]
public class FeatureTests
{
    [Test]
    public async Task BatteryAndFirmwareUseCapturedBcdVersionWithoutSendingSettings()
    {
        using var mouse = new FeatureMouse(); using var client = new HidPlusPlusClient(mouse, 1);
        var status = await DeviceStatusReader.ReadAsync(client);
        Assert.Equal(65, status.Battery!.Value); Assert.Equal((byte)1, status.Charging); Assert.Equal("27.1.22", status.Firmware);
        Assert.Equal(false, mouse.Requests.Any(r => r[2] is not (0 or 2 or 3)));
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 2 && r[3] >> 4 > 1 || r[2] == 3 && r[3] >> 4 != 1));
    }
    [Test]
    public async Task ExtendedDpiPagingAndDualReportRatesMatchCapturedInterface()
    {
        using var mouse = new FeatureMouse(); using var client = new HidPlusPlusClient(mouse, 1);
        var performance = new PerformanceController(client, true); var state = await performance.ReadAsync();
        Assert.Equal(true, state.IndependentAxes); Assert.Equal(25600, state.Maximum); Assert.Equal((ushort)1200, state.CurrentY);
        Assert.Equal("125,250,500,1000", string.Join(',', state.ActualRates));
        await performance.ApplyAsync(1600, 2400, 500);
        Assert.Equal((ushort)1600, mouse.X); Assert.Equal((ushort)2400, mouse.Y); Assert.Equal((byte)2, mouse.Rate);
        Assert.Equal(true, mouse.Requests.Any(r => r[2] == 7 && r[3] >> 4 == 2 && r[6] == 1));
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 12 && r[3] >> 4 is >= 6 and <= 8));
    }
    [Test]
    public async Task FailedDpiReadbackRestoresOriginalDpiAndRate()
    {
        using var mouse = new FeatureMouse(); using var client = new HidPlusPlusClient(mouse, 1);
        var performance = new PerformanceController(client, true); await performance.ReadAsync(); mouse.BadNextDpiRead = true;
        try { await performance.ApplyAsync(1600, 2400, 500); throw new Exception("Expected readback error"); } catch (IOException) { }
        Assert.Equal((ushort)800, mouse.X); Assert.Equal((ushort)1200, mouse.Y); Assert.Equal((byte)3, mouse.Rate);
    }
    [Test]
    public async Task LedFrameWritesEachMappedZoneAndCommitsOnlyToRam()
    {
        using var mouse = new FeatureMouse(); using var client = new HidPlusPlusClient(mouse, 1);
        var light = new LightsyncDevice(client, LightsyncDevice.G502XZoneMap); var caps = await light.ReadAsync();
        Assert.Equal(8, caps.LedIds.Count); await light.ClaimAsync();
        await light.SendFrameAsync(Enumerable.Range(0, 8).Select(i => new Rgb((byte)i, 22, 33)).ToArray());
        var frames = mouse.Requests.Where(r => r[2] == 10 && r[3] >> 4 == 1).ToArray();
        Assert.Equal(2, frames.Length); Assert.Equal("3,7,2,4,8,6,5,1", string.Join(',', frames.SelectMany(r => new[] { r[4], r[8], r[12], r[16] })));
        var commit = mouse.Requests.Last(r => r[2] == 10 && r[3] >> 4 == 7); Assert.Equal((byte)0, commit[4]);
        await light.ReleaseAsync(); Assert.Equal((byte)1, mouse.Control[1]); Assert.Equal((byte)2, mouse.Control[2]);
    }
    private sealed class Sink : IMacroInputSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<MacroInput> Events { get; } = new();
        public void Send(MacroInput input) => Events.Enqueue(input);
    }
    [Test]
    public async Task UnsolicitedButtonNotificationsTriggerSequenceWithoutPendingRequest()
    {
        using var mouse = new FeatureMouse(); using var client = new HidPlusPlusClient(mouse, 1); var sink = new Sink();
        var original = mouse.Map.ToArray();
        await using (var host = new HostMacroSession(client, sink))
        {
            await host.StartAsync([new(3, false, MacroCompiler.CompileProgram("tap A", MacroRepeat.Sequence, "tap B\ndelay 20", "tap C"))], -1);
            Assert.Equal((byte)0, mouse.Map[3]); mouse.Notify(13, 8);
            using var timeout = new CancellationTokenSource(2000);
            while (!sink.Events.Any(e => e.Code == 5 && e.Down)) await Task.Delay(1, timeout.Token);
            mouse.Notify(13, 0);
            while (!sink.Events.Any(e => e.Code == 6 && !e.Down)) await Task.Delay(1, timeout.Token);
        }
        Assert.Equal(true, mouse.Map.SequenceEqual(original)); Assert.Equal((byte)2, mouse.Mode);
        Assert.Equal(1, sink.Events.Count(e => e.Code == 4 && e.Down)); Assert.Equal(1, sink.Events.Count(e => e.Code == 6 && e.Down));
    }
}
