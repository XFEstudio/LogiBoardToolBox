using System.Text;
using System.Threading.Channels;
using LogiBoardToolBox.Core.Firmware;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.Core.Windows;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class HeroFirmwarePreflightTests
{
    private static HidDeviceInfo Target(string path = "hero&col02#one", ushort product = 0xC08B) =>
        new(path, product, "G502 HERO", "serial", 0xFF00, 2, 20, 20);

    [Test]
    public void SelectionNeverFallsBackToReceiversOrAnAmbiguousMouse()
    {
        var hero = Target();
        Assert.Equal(hero, HeroFirmwarePreflight.SelectTarget([Target("receiver", 0xC547), hero]));
        Assert.Equal(hero.CollectionGroup, HeroFirmwarePreflight.SelectTarget([hero, Target("hero&col01#one")]).CollectionGroup);
        foreach (var list in new[] { new[] { Target("receiver", 0xC547) }, new[] { hero, Target("second#two") } })
            Reject(() => HeroFirmwarePreflight.SelectTarget(list));
    }

    [Test]
    public async Task FullPreflightBacksUpAllPagesAndOnlySendsReadQueries()
    {
        var mouse = new HeroMouse(); var report = new HeroPreflightReport();
        await HeroFirmwarePreflight.CollectAsync(report, Target(), mouse);
        Assert.Equal(0, report.Issues.Count);
        Assert.Equal("G502 HERO", report.Name);
        Assert.Equal("046D:C08B:12345678:FF", report.Identity);
        Assert.Equal(2, report.Firmware.Count);
        Assert.Equal("81.00.B0002", report.Firmware[1].Version);
        Assert.Equal(true, report.KnownApplicationVersionMetadata);
        Assert.Equal(false, report.ApplicationImageHashVerified);
        Assert.Equal(false, report.BootloaderImageBackedUp);
        Assert.Equal(false, report.InstallationVerified);
        Assert.Equal(false, report.ExactLuaHardwareVerified);
        Assert.Equal(true, report.SignedDfuDeclaredAvailable);
        Assert.Equal(true, report.CompleteBoardBackup);
        Assert.Equal(true, report.BoardStateUnchanged);
        Assert.Equal(22, report.Before!.Sectors.Count);
        Assert.Equal(22, report.After!.Sectors.Count);
        Assert.Equal(0, report.BlockedRequests);
        Assert.Equal(mouse.Requests.Count, report.AllowedReadRequests);
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 5 && (r[3] >> 4) is not (0 or 2 or 4 or 5)));
        Assert.Equal(22 * 16 * 2, mouse.SectorReads);
    }

    [Test]
    public async Task ObservedBootloaderBuildThreeIsRetainedWithoutInferringCustomFirmwareSupport()
    {
        var report = new HeroPreflightReport();
        await HeroFirmwarePreflight.CollectAsync(report, Target(), new HeroMouse { BootloaderBuild = 3 });
        Assert.Equal("005531202703001001C08B0000000000", report.Firmware[0].Raw);
        Assert.Equal("01424F548100000300AAE60000000000", report.Firmware[1].Raw);
        Assert.Equal("81.00.B0003", report.Firmware[1].Version);
        Assert.Equal(true, report.KnownApplicationVersionMetadata);
        Assert.Equal(true, report.SignedDfuDeclaredAvailable);
        Assert.Equal(false, report.ApplicationImageHashVerified);
        Assert.Equal(false, report.InstallationVerified);
        Assert.Equal(false, report.ExactLuaHardwareVerified);
    }

    [Test]
    public async Task StateChangingDuringCollectionIsReportedWithoutClaimingSuccessfulBackupComparison()
    {
        var mouse = new HeroMouse { ChangeSecondRead = true }; var report = new HeroPreflightReport();
        await HeroFirmwarePreflight.CollectAsync(report, Target(), mouse);
        Assert.Equal(true, report.CompleteBoardBackup);
        Assert.Equal(false, report.BoardStateUnchanged);
        Assert.Equal(true, report.Differences.Contains("Sector 0x0006"));
        Assert.Equal(true, report.Issues.Count > 0);
        Assert.Equal(false, report.HardwareMutationRequestsSent);
    }

    [Test]
    public async Task UnexpectedLayoutDoesNotReadSectorsAndFailedDfuStatusDoesNotHideTheBackup()
    {
        var changed = new HeroMouse { WrongGeometry = true }; var report = new HeroPreflightReport();
        await HeroFirmwarePreflight.CollectAsync(report, Target(), changed);
        Assert.Equal(0, changed.SectorReads);
        Assert.Equal(true, report.Before is null);
        Assert.Equal(true, report.BoardDescriptionRaw.Length > 0);
        Assert.Equal(true, report.Issues.Any(i => i.StartsWith("board-backup:")));
        var failed = new HeroMouse { DfuError = true }; report = new();
        await HeroFirmwarePreflight.CollectAsync(report, Target(), failed);
        Assert.Equal(true, report.CompleteBoardBackup);
        Assert.Equal(true, report.BoardStateUnchanged);
        Assert.Equal(true, report.SignedDfuStatusRaw is null);
        Assert.Equal(true, report.Issues.Any(i => i.StartsWith("dfu-status:")));
    }

    [Test]
    public async Task WrongTargetNeverSendsAnyRequest()
    {
        var mouse = new HeroMouse();
        try { await HeroFirmwarePreflight.CollectAsync(new(), Target("receiver", 0xC547), mouse); throw new Exception("Expected rejection"); }
        catch (ArgumentException) { }
        Assert.Equal(0, mouse.Requests.Count);
    }

    [Test]
    public async Task GuardBlocksAllMutatingFunctionsAndUnknownEndpointsBeforeSending()
    {
        var mouse = new HeroMouse(); using var guard = new HeroReadOnlyTransport(mouse);
        foreach (var feature in new ushort[] { 0x8100, 0x00C2, 0x00C1, 0x00C3, 0x00D0 })
        {
            var query = Packet(0, 0); query[4] = (byte)(feature >> 8); query[5] = (byte)feature;
            await guard.WriteAsync(query, default); await guard.ReadAsync(default);
        }
        var allowed = mouse.Requests.Count;
        for (byte index = 4; index <= 8; index++)
            for (byte function = 0; function < 16; function++)
            {
                if (index == 4 && function == 0 || index == 5 && function is 0 or 2 or 4 or 5) continue;
                await RejectAsync(() => guard.WriteAsync(Packet(index, function), default));
            }
        for (var i = 0; i < 255; i++)
        {
            var otherDevice = Packet(5, 0); otherDevice[1] = (byte)i;
            await RejectAsync(() => guard.WriteAsync(otherDevice, default));
        }
        foreach (var (sector, offset) in new[] { (16, 0), (0x106, 0), (0, 241), (0, 1), (0xFFFF, 0) })
        {
            var r = Packet(5, 5); r[4] = (byte)(sector >> 8); r[5] = (byte)sector;
            r[6] = (byte)(offset >> 8); r[7] = (byte)offset;
            await RejectAsync(() => guard.WriteAsync(r, default));
        }
        var unknown = Packet(0, 0); unknown[4] = 0x18; unknown[5] = 1;
        await RejectAsync(() => guard.WriteAsync(unknown, default));
        var payload = Packet(5, 0); payload[19] = 1;
        await RejectAsync(() => guard.WriteAsync(payload, default));
        await RejectAsync(() => guard.WriteAsync(Packet(99, 0), default));
        Assert.Equal(allowed, mouse.Requests.Count);
        Assert.Equal(true, guard.BlockedRequests > 300);
    }

    [Test]
    public async Task UnmatchedAndTruncatedFeatureRepliesCannotUnlockAnEndpoint()
    {
        var mouse = new HeroMouse { ManualReplies = true }; using var guard = new HeroReadOnlyTransport(mouse);
        var query = Packet(0, 0); query[4] = 0x81; query[5] = 0;
        await guard.WriteAsync(query, default);
        var reply = Packet(0, 0); reply[4] = 5;
        reply[3] = 2; mouse.Reply(reply); await guard.ReadAsync(default);
        await RejectAsync(() => guard.WriteAsync(Packet(5, 0), default));
        reply[3] = 1; mouse.Reply(reply[..7]); await guard.ReadAsync(default);
        await RejectAsync(() => guard.WriteAsync(Packet(5, 0), default));
        mouse.Reply(reply); await guard.ReadAsync(default);
        await guard.WriteAsync(Packet(5, 0), default);
        Assert.Equal(2, mouse.Requests.Count);
    }

    private static byte[] Packet(byte feature, byte function)
    { var r = new byte[20]; r[0] = 0x11; r[1] = 0xFF; r[2] = feature; r[3] = (byte)((function << 4) | 1); return r; }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }
    private static async Task RejectAsync(Func<Task> action)
    { try { await action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected rejection"); }

    internal sealed class HeroMouse : IHidTransport
    {
        private readonly Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        private readonly Dictionary<ushort, byte> features = new()
        { [0x0001] = 1, [0x0003] = 2, [0x0005] = 3, [0x00C2] = 4, [0x8100] = 5 };
        public List<byte[]> Requests { get; } = [];
        public bool WrongGeometry, DfuError, ChangeSecondRead, ManualReplies;
        public byte BootloaderBuild = 2;
        public Action<byte[]>? OnRequest;
        public byte MemoryVariant;
        public byte? UnitFirstByte;
        public bool CloseOnFirmware, CloseOnEnter, CloseOnRestart;
        public int SectorReads;
        public int InputReportLength => 20;
        public int OutputReportLength => 20;
        public void Reply(byte[] r) => channel.Writer.TryWrite(r);
        public Task WriteAsync(byte[] r, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(r.ToArray());
            OnRequest?.Invoke(r.ToArray());
            if (CloseOnFirmware && r[2] == 2 && r[3] >> 4 == 1 || CloseOnEnter && r[2] == 4 && r[3] >> 4 == 1 ||
                CloseOnRestart && r[2] == 8 && r[3] >> 4 == 5)
            { channel.Writer.TryComplete(new IOException("simulated transport disconnect before reply")); return Task.CompletedTask; }
            if (ManualReplies) return Task.CompletedTask;
            var reply = new byte[20]; r.AsSpan(0, 4).CopyTo(reply);
            var fn = r[3] >> 4;
            if (r[2] == 0 && fn == 1) { reply[4] = 4; reply[5] = 2; reply[6] = r[6]; }
            else if (r[2] == 0)
            {
                var id = (ushort)((r[4] << 8) | r[5]);
                reply[4] = features.GetValueOrDefault(id, id == 0x00C1 ? (byte)6 : id == 0x00C3 ? (byte)7 : id == 0x00D0 ? (byte)8 : (byte)0);
            }
            else if (r[2] == 1 && fn == 0) reply[4] = 5;
            else if (r[2] == 1 && fn == 1)
            { var id = features.FirstOrDefault(p => p.Value == r[4]).Key; reply[4] = (byte)(id >> 8); reply[5] = (byte)id; }
            else if (r[2] == 2 && fn == 0)
            { Convert.FromHexString("0212345678").CopyTo(reply, 4); if (UnitFirstByte is { } unit) reply[5] = unit; }
            else if (r[2] == 2 && fn == 1)
            {
                Convert.FromHexString(r[4] == 0 ? "005531202703001001C08B0000000000" : "01424F548100000200AAE60000000000").CopyTo(reply, 4);
                if (r[4] == 1) reply[11] = BootloaderBuild;
            }
            else if (r[2] == 3 && fn == 0) reply[4] = 9;
            else if (r[2] == 3 && fn == 1) Encoding.ASCII.GetBytes("G502 HERO").AsSpan(r[4]).CopyTo(reply.AsSpan(4));
            else if (r[2] == 4 && DfuError)
            { reply[2] = 0xFF; reply[3] = r[2]; reply[4] = r[3]; reply[5] = 7; }
            else if (r[2] == 5 && fn == 0)
            { Convert.FromHexString("01020105050B1001000A01").CopyTo(reply, 4); if (WrongGeometry) reply[10] = 32; }
            else if (r[2] == 5 && fn == 2) reply[4] = 1;
            else if (r[2] == 5 && fn == 4) reply[5] = 1;
            else if (r[2] == 5 && fn == 5)
            {
                SectorReads++; var sector = (r[4] << 8) | r[5]; var offset = (r[6] << 8) | r[7];
                var data = new byte[256]; data[0] = (byte)sector;
                if (sector == 6) data[1] = MemoryVariant;
                if (ChangeSecondRead && SectorReads > 22 * 16 && sector == 6) data[0] ^= 1;
                SectorChecksum.Seal(data);
                data.AsSpan(offset, 16).CopyTo(reply.AsSpan(4));
            }
            Reply(reply); return Task.CompletedTask;
        }
        public async Task<byte[]> ReadAsync(CancellationToken ct) => await channel.Reader.ReadAsync(ct);
        public void Dispose() => channel.Writer.TryComplete();
    }
}
