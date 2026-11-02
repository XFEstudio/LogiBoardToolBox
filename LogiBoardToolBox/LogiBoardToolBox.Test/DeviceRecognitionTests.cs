using System.Text;
using System.Threading.Channels;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Windows;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class DeviceRecognitionTests
{
    [Test]
    public void RegionalNamesResolveToTheCorrectGenerationRatherThanFirstGenerationGpw()
    {
        foreach (var name in new[] { "GPW4朱雀", "GPW4 朱雀", "罗技 GPW4 朱雀", "PRO 2 LIGHTSPEED" })
            Assert.Equal("Pro2", MouseCatalog.Resolve([], name).Id);
        foreach (var name in new[] { "GPW2金刚", "GPW2 金刚版", "PRO X SUPERLIGHT 2 SE", "G PRO X SUPERLIGHT 2" })
            Assert.Equal("Superlight2", MouseCatalog.Resolve([], name).Id);
        Assert.Equal("Superlight", MouseCatalog.Resolve([], "GPW2").Id);
        Assert.Equal("Superlight2Dex", MouseCatalog.Resolve([], "GPW4 鹰眼").Id);
        Assert.Equal("G402", MouseCatalog.Resolve([0xC07E], "Logitech Gaming Mouse").Id);
        Assert.Equal("Pro2", MouseCatalog.Resolve([0x40A8], "PRO WIRELESS").Id);
        Assert.Equal("Superlight2", MouseCatalog.Resolve([0x40A9], "PRO WIRELESS").Id);
        Assert.Equal(8, MouseCatalog.ById("Pro2")!.ButtonNames.Length);
        Assert.Equal(5, MouseCatalog.ById("Superlight2")!.ButtonNames.Length);
        Assert.Equal(false, MouseCatalog.ById("Superlight2")!.HasLighting);
    }

    [Test]
    public async Task DiscoveryKeepsKnownMiceWithoutOnboardFeatureAndDoesNotWriteSettings()
    {
        using var fake = new IdentityTransport([0x40A8, 0xC09A], "PRO 2 LIGHTSPEED", profiles: false);
        using var client = new HidPlusPlusClient(fake, 1);
        var device = await HidPlusPlusDeviceProbe.ProbeAsync(client, Hid(0xC547));
        Assert.Equal(true, device is not null);
        Assert.Equal("Pro2", device!.Model.Id);
        Assert.Equal(false, device.HasOnboardProfiles);
        Assert.Equal("01020304", device.UnitId);
        Assert.Equal(true, fake.Requests.All(r => r[2] == 0 || r[2] == 1 && r[3] >> 4 == 0 || r[2] == 2 && r[3] >> 4 is 0 or 1));
    }

    [Test]
    public async Task ReceiverDoesNotHideG402OrConfuseSuperlightSeWithOriginalSuperlight()
    {
        foreach (var sample in new[] { (Ids: new ushort[] {0xC07E}, Name: "G402", Expected: "G402"), (Ids: new ushort[] {0x40A9, 0xC09B}, Name: "PRO X SUPERLIGHT 2 SE", Expected: "Superlight2") })
        {
            using var fake = new IdentityTransport(sample.Ids, sample.Name, profiles: true);
            using var client = new HidPlusPlusClient(fake, 1);
            var device = await HidPlusPlusDeviceProbe.ProbeAsync(client, Hid(0xC547));
            Assert.Equal(sample.Expected, device!.Model.Id);
            Assert.Equal(true, device.HasOnboardProfiles);
        }
        Assert.Equal("FF", string.Join(',', HidPlusPlusDeviceProbe.DeviceIndices(Hid(0xC07E)).Select(i => i.ToString("X2"))));
        Assert.Equal(true, HidPlusPlusDeviceProbe.DeviceIndices(Hid(0xC547)).Contains((byte)1));
    }

    [Test]
    public async Task ReceiverRootsAndUnknownHidppPeripheralsAreNotMisidentifiedAsSupportedMice()
    {
        using var fake = new IdentityTransport([0xBEEF], "Unrecognized peripheral", profiles: false);
        using var client = new HidPlusPlusClient(fake, 0xFF);
        Assert.Equal(true, await HidPlusPlusDeviceProbe.ProbeAsync(client, Hid(0xC547)) is null);
        Assert.Equal(true, MouseCatalog.BySlotPrefix("") is null);
        try { GHubMacroImport.Read("{}", MouseCatalog.ById("Pro2")!.SlotPrefix); }
        catch (NotSupportedException) { return; }
        throw new Exception("An unverified G HUB namespace must not silently map another model's buttons.");
    }

    private static HidDeviceInfo Hid(ushort pid) => new("fake-path", pid, "Logitech Receiver", "", 0xFF00, 1, 20, 20);
    private sealed class IdentityTransport(ushort[] productIds, string name, bool profiles) : IHidTransport
    {
        private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Requests { get; } = [];
        public int InputReportLength => 20;
        public int OutputReportLength => 20;
        public Task WriteAsync(byte[] report, CancellationToken ct)
        {
            Requests.Add(report.ToArray());
            var reply = new byte[20]; report.AsSpan(0, 4).CopyTo(reply);
            var fn = report[3] >> 4;
            if (report[2] == 0 && fn == 1) { reply[4] = 2; reply[5] = 0; reply[6] = report[6]; }
            else if (report[2] == 0)
            { var id = (report[4] << 8) | report[5]; reply[4] = (byte)(id == 3 ? 1 : id == 5 ? 2 : id == 0x8100 && profiles ? 3 : 0); }
            else if (report[2] == 1)
            {
                reply[5] = 1; reply[6] = 2; reply[7] = 3; reply[8] = 4;
                reply[10] = (byte)(productIds.Length == 1 ? 8 : 12);
                for (var i = 0; i < productIds.Length; i++) { reply[11 + 2 * i] = (byte)(productIds[i] >> 8); reply[12 + 2 * i] = (byte)productIds[i]; }
            }
            else if (report[2] == 2)
            {
                var bytes = Encoding.UTF8.GetBytes(name);
                if (fn == 0) reply[4] = (byte)bytes.Length;
                else bytes.Skip(report[4]).Take(16).ToArray().CopyTo(reply, 4);
            }
            replies.Writer.TryWrite(reply); return Task.CompletedTask;
        }
        public async Task<byte[]> ReadAsync(CancellationToken ct) => await replies.Reader.ReadAsync(ct);
        public void Dispose() => replies.Writer.TryComplete();
    }
}
