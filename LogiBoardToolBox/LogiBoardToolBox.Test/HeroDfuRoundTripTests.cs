using LogiBoardToolBox.Core.Firmware;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Windows;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class HeroDfuRoundTripTests
{
    [Test]
    public async Task UpgradeModeRoundTripRechecksIdentityAndEveryBoardByteWithoutSendingImages()
    {
        var platform = new Platform(); var report = await platform.Preflight(); platform.Requests.Clear();
        await platform.Cycle().RunAsync(report, default);
        Assert.Equal(true, report.DfuCycleVerified); Assert.Equal(true, report.DfuCycleBootloaderObserved);
        Assert.Equal(true, report.DfuCycleStartAttempted); Assert.Equal(true, report.DfuCycleRestartAttempted);
        Assert.Equal(true, report.HardwareMutationRequestsSent); Assert.Equal(false, report.InstallationVerified);
        Assert.Equal(1, platform.Requests.Count(r => r[2] == 4 && r[3] >> 4 == 1));
        Assert.Equal(1, platform.Requests.Count(r => r[2] == 8 && r[3] >> 4 == 5));
        Assert.Equal(false, platform.Requests.Any(r => r[2] == 8 && r[3] >> 4 < 5));
        Assert.Equal(22, report.DfuCycleReturnedSnapshot!.Sectors.Count);
        Assert.Equal(true, report.DfuCycleBootloaderFeatures.Count > 0);
        Assert.Equal(true, report.DfuCycleBootloaderFirmware.Count > 0);
    }

    [Test]
    public async Task ExistingBootloaderOrChangedTargetStopsBeforeStartDfu()
    {
        foreach (var conflict in new[] { true, false })
        {
            var platform = new Platform(); var report = await platform.Preflight();
            platform.ExistingBootloader = conflict; platform.ChangedIdentity = !conflict;
            platform.Requests.Clear();
            try { await platform.Cycle().RunAsync(report, default); throw new Exception("Expected identity rejection"); }
            catch (Exception error) when (error is IOException or InvalidOperationException) { }
            Assert.Equal(false, report.DfuCycleStartAttempted);
            Assert.Equal(false, platform.Requests.Any(r => r[2] != 0 && (r[3] >> 4) == 5 || r[2] == 4 && (r[3] >> 4) == 1));
        }
    }

    [Test]
    public async Task ChangedConfigurationAfterReturnIsKeptInReportAndNeverMarkedAsVerified()
    {
        var platform = new Platform { ChangeReturnedMemory = true }; var report = await platform.Preflight();
        try { await platform.Cycle().RunAsync(report, default); throw new Exception("Expected changed board failure"); }
        catch (IOException) { }
        Assert.Equal(false, report.DfuCycleVerified);
        Assert.Equal(true, report.DfuCycleReturnedSnapshot is not null);
        Assert.Equal(false, report.Before!.Sectors[6].SequenceEqual(report.DfuCycleReturnedSnapshot!.Sectors[6]));
        Assert.Equal(false, platform.Requests.Any(r => r[2] == 5 && r[3] >> 4 is 1 or 3 or 6 or 7 or 8));
    }

    [Test]
    public async Task MissingReturnDoesNotClaimSuccessOrKeepIssuingRestartCommands()
    {
        var platform = new Platform { StayInBootloader = true }; var report = await platform.Preflight();
        try { await platform.Cycle().RunAsync(report, default); throw new Exception("Expected missing normal device"); }
        catch (TimeoutException) { }
        Assert.Equal(false, report.DfuCycleVerified); Assert.Equal(true, report.DfuCycleRestartAttempted);
        Assert.Equal(1, platform.Requests.Count(r => r[2] == 8 && r[3] >> 4 == 5));
    }

    [Test]
    public async Task ControlBoundaryNeverPermitsProgramDataStartImageOrWrongMagic()
    {
        foreach (var role in new[] { HeroDfuControlRole.Application, HeroDfuControlRole.Bootloader })
        {
            var mouse = new HeroFirmwarePreflightTests.HeroMouse();
            using var boundary = new HeroDfuControlTransport(mouse, role, 0xFF);
            using var client = new HidPlusPlusClient(boundary, 0xFF);
            var index = await client.GetFeatureAsync(role == HeroDfuControlRole.Application ? (ushort)0xC2 : (ushort)0xD0);
            var queries = mouse.Requests.Count;
            for (byte fn = 0; fn < 16; fn++)
            {
                if (role == HeroDfuControlRole.Application && fn == 0 || role == HeroDfuControlRole.Bootloader && fn == 5) continue;
                try { await client.CallAsync(index, fn); throw new Exception("Expected control boundary rejection"); }
                catch (InvalidOperationException) { }
            }
            Assert.Equal(queries, mouse.Requests.Count);
            Assert.Equal(0, boundary.ControlRequests);
        }
    }

    [Test]
    public async Task ReturnOnlyReloadsSavedBaselineAndNeverRequestsEntryOrSendsAnImage()
    {
        var platform = new Platform(); var first = await platform.Preflight();
        var json = System.Text.Json.JsonSerializer.Serialize(first);
        var baseline = System.Text.Json.JsonSerializer.Deserialize<HeroPreflightReport>(json, new System.Text.Json.JsonSerializerOptions
        { PreferredObjectCreationHandling = System.Text.Json.Serialization.JsonObjectCreationHandling.Populate })!;
        Assert.Equal(2, baseline.Firmware.Count); Assert.Equal(22, baseline.Before!.Sectors.Count);
        platform.inBootloader = true; platform.Requests.Clear();
        await platform.Cycle().ReturnOnlyAsync(baseline, default);
        Assert.Equal(true, baseline.DfuCycleVerified); Assert.Equal(true, baseline.DfuCycleReturnOnly);
        Assert.Equal(false, baseline.DfuCycleStartAttempted);
        Assert.Equal(false, platform.Requests.Any(r => r[2] == 4 && r[3] >> 4 == 1));
        Assert.Equal(false, platform.Requests.Any(r => r[2] == 8 && r[3] >> 4 < 5));
        Assert.Equal(1, platform.Requests.Count(r => r[2] == 8 && r[3] >> 4 == 5));
    }

    [Test]
    public async Task ReturnOnlyDoesNotTouchAnOnlineNormalHero()
    {
        var platform = new Platform(); var report = await platform.Preflight(); platform.Requests.Clear();
        try { await platform.Cycle().ReturnOnlyAsync(report, default); throw new Exception("Expected normal-mode rejection"); }
        catch (InvalidOperationException) { }
        Assert.Equal(0, platform.Requests.Count);
    }

    [Test]
    public async Task DisconnectBeforeControlReplyStillRequiresObservedReturnAndFullReadback()
    {
        foreach (var enter in new[] { true, false })
        {
            var platform = new Platform { DisconnectEnter = enter, DisconnectRestart = !enter };
            var report = await platform.Preflight();
            await platform.Cycle().RunAsync(report, default);
            Assert.Equal(true, report.DfuCycleVerified);
            Assert.Equal(true, report.DfuCycleEvents.Any(e => e.Contains("连接已中断")));
        }
    }

    [Test]
    public async Task OptionalBootInfoReadFailureCannotPoisonTheReturnConnection()
    {
        var platform = new Platform { BootInfoFailure = true }; var report = await platform.Preflight();
        await platform.Cycle().RunAsync(report, default);
        Assert.Equal(true, report.DfuCycleVerified);
        Assert.Equal(true, report.DfuCycleEvents.Any(e => e.Contains("可选信息读取未完成")));
    }

    private sealed class Platform
    {
        private static readonly HidDeviceInfo Normal = new("hero#one", 0xC08B, "G502 HERO", "unit", 0xFF00, 2, 20, 20);
        private static readonly HidDeviceInfo Boot = new("boot#one", 0xAAE6, "Bootloader", "", 0xFF00, 2, 20, 20);
        public List<byte[]> Requests { get; } = [];
        public bool ExistingBootloader, ChangedIdentity, ChangeReturnedMemory, StayInBootloader;
        public bool DisconnectEnter, DisconnectRestart, BootInfoFailure;
        internal bool inBootloader;
        private bool returned;
        private IReadOnlyList<HidDeviceInfo> Inventory() => inBootloader ? [Boot] : ExistingBootloader ? [Normal, Boot] : [Normal];
        private IHidTransport Open(HidDeviceInfo info) => new HeroFirmwarePreflightTests.HeroMouse
        {
            BootloaderBuild = 3,
            UnitFirstByte = ChangedIdentity ? (byte)0xAB : null,
            MemoryVariant = returned && ChangeReturnedMemory ? (byte)1 : (byte)0,
            CloseOnEnter = DisconnectEnter && info.ProductId == 0xC08B,
            CloseOnRestart = DisconnectRestart && info.ProductId == 0xAAE6,
            CloseOnFirmware = BootInfoFailure && info.ProductId == 0xAAE6,
            OnRequest = r =>
            {
                Requests.Add(r);
                if (info.ProductId == 0xC08B && r[2] == 4 && r[3] >> 4 == 1) inBootloader = true;
                if (info.ProductId == 0xAAE6 && r[2] == 8 && r[3] >> 4 == 5)
                { returned = true; if (!StayInBootloader) inBootloader = false; }
            }
        };
        public HeroDfuRoundTrip Cycle() => new(Inventory, Open, transitionTimeout: TimeSpan.FromMilliseconds(25));
        public async Task<HeroPreflightReport> Preflight()
        { var report = new HeroPreflightReport(); await HeroFirmwarePreflight.CollectAsync(report, Normal, Open(Normal)); return report; }
    }
}
