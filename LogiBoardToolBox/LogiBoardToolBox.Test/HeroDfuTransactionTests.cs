using System.Buffers.Binary;
using System.Threading.Channels;
using LogiBoardToolBox.Core.Firmware;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Windows;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class HeroDfuTransactionTests
{
    [Test]
    public async Task CompleteTransferRequiresWriteAheadRecordTerminalStatusAndFullReturnedBoardComparison()
    {
        var p = new Platform(); var journal = await p.Journal();
        await p.Transaction().RunAsync(journal, Image(), default);
        Assert.Equal(2820, p.Packets);
        Assert.Equal(HeroDfuPhase.ApplicationVerified, journal.Phase);
        Assert.Equal(true, journal.ApplicationReturnVerified);
        Assert.Equal(false, journal.MustReflashBeforeRestart);
        Assert.Equal(false, journal.CustomFirmwareInstalled);
        Assert.Equal(false, journal.ExactLuaHardwareVerified);
        Assert.Equal(false, journal.ExecutableFlashReadBack);
        Assert.Equal(false, journal.OfficialReference); // The test uses synthetic bytes.
        Assert.Equal(22, journal.Baseline.DfuCycleReturnedSnapshot!.Sectors.Count);
        Assert.Equal(true, p.FirstPacketWasJournaled);
        Assert.Equal(1, p.Restarts);
        Assert.Equal("EnteringDfu,BootloaderValidated,TransferMayHaveStarted,TransferAcknowledged,RestartMayHaveBeenSent,ApplicationVerified",
            string.Join(',', p.Saves));
        var sent = p.Requests.Where(r => r.Mode == "boot" && r.Bytes[2] == 4 && r.Bytes[3] >> 4 < 5).ToArray();
        Assert.Equal(Convert.ToHexString(HeroDfuImageTests.Container()),
            Convert.ToHexString(sent.SelectMany(r => r.Bytes.Skip(4)).ToArray()));
    }

    [Test]
    public async Task FatalStatusIncludingFinalCheckFailureNeverRestartsAnIncompleteImage()
    {
        foreach (var failAt in new[] { 0, 31, 2819 })
        {
            var p = new Platform { FailAt = failAt }; var journal = await p.Journal();
            try { await p.Transaction().RunAsync(journal, Image(), default); throw new Exception("Expected failure"); }
            catch (DfuTransferException error) { Assert.Equal(failAt, error.PacketIndex); }
            Assert.Equal(0, p.Restarts); Assert.Equal(failAt + 1, p.Packets);
            Assert.Equal(true, journal.MustReflashBeforeRestart); Assert.Equal(false, journal.ApplicationReturnVerified);
            Assert.Equal(HeroDfuPhase.Failed, journal.Phase);
            Assert.Equal(HeroDfuPhase.TransferMayHaveStarted, journal.FailedFrom!.Value);
        }
    }

    [Test]
    public async Task PlainPacketAcknowledgementAtEndIsNotFirmwareCompletion()
    {
        var p = new Platform { FinalStatus = 1 }; var journal = await p.Journal();
        try { await p.Transaction().RunAsync(journal, Image(), default); throw new Exception("Expected missing terminal status"); }
        catch (IOException) { }
        Assert.Equal(2820, p.Packets); Assert.Equal(0, p.Restarts);
        Assert.Equal(true, journal.MustReflashBeforeRestart);
    }

    [Test]
    public async Task FailureToPersistFirstPacketStatePreventsAllImageWrites()
    {
        var p = new Platform { FailSaveAt = HeroDfuPhase.TransferMayHaveStarted }; var journal = await p.Journal();
        try { await p.Transaction().RunAsync(journal, Image(), default); throw new Exception("Expected journal error"); }
        catch (IOException) { }
        Assert.Equal(0, p.Packets); Assert.Equal(0, p.Restarts);
        Assert.Equal(true, journal.MustReflashBeforeRestart); // Conservative after attempted write-ahead.
    }

    [Test]
    public async Task FailureToSaveTerminalSuccessPreventsRestartDespiteCompletedTransfer()
    {
        var p = new Platform { FailSaveAt = HeroDfuPhase.TransferAcknowledged }; var journal = await p.Journal();
        try { await p.Transaction().RunAsync(journal, Image(), default); throw new Exception("Expected journal error"); }
        catch (IOException) { }
        Assert.Equal(2820, p.Packets); Assert.Equal(0, p.Restarts);
        Assert.Equal(false, journal.MustReflashBeforeRestart);
        Assert.Equal(false, journal.ApplicationReturnVerified);
    }

    [Test]
    public async Task WrongBootUnitOrUnexpectedBootloaderStopsBeforeFirstImagePacket()
    {
        foreach (var wrongUnit in new[] { false, true })
        {
            var p = new Platform { WrongBootUnit = wrongUnit, WrongBootVersion = !wrongUnit }; var journal = await p.Journal();
            try { await p.Transaction().RunAsync(journal, Image(), default); throw new Exception("Expected target rejection"); }
            catch (IOException) { }
            Assert.Equal(0, p.Packets); Assert.Equal(0, p.Restarts);
            Assert.Equal(false, journal.MustReflashBeforeRestart);
        }
    }

    [Test]
    public async Task ReturnedSettingsDifferenceIsReportedAndNeverOverwritten()
    {
        var p = new Platform { ChangeReturnedMemory = true }; var journal = await p.Journal();
        try { await p.Transaction().RunAsync(journal, Image(), default); throw new Exception("Expected settings mismatch"); }
        catch (IOException) { }
        Assert.Equal(1, p.Restarts); Assert.Equal(false, journal.ApplicationReturnVerified);
        Assert.Equal(true, journal.Baseline.DfuCycleReturnedSnapshot is not null);
        Assert.Equal(false, p.Requests.Any(r => r.Mode == "normal" && r.Bytes[2] == 5 && r.Bytes[3] >> 4 is 1 or 3 or 6 or 7 or 8));
    }

    [Test]
    public async Task CancelAfterFirstPacketDoesNotRestartOrResend()
    {
        using var cancel = new CancellationTokenSource();
        var p = new Platform { OnFirstPacket = cancel.Cancel }; var journal = await p.Journal();
        try { await p.Transaction().RunAsync(journal, Image(), cancel.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Assert.Equal(1, p.Packets); Assert.Equal(0, p.Restarts);
        Assert.Equal(true, journal.MustReflashBeforeRestart);
    }

    [Test]
    public async Task OldReturnOnlyCannotRestartAfterAnyRecordedImageAttempt()
    {
        var p = new Platform(); var journal = await p.Journal(); journal.Baseline.DfuImageDataMayHaveBeenSent = true;
        p.Requests.Clear();
        try { await new HeroDfuRoundTrip(p.Inventory, p.Open).ReturnOnlyAsync(journal.Baseline, default); throw new Exception("Expected return-only refusal"); }
        catch (InvalidOperationException) { }
        Assert.Equal(0, p.Requests.Count);
    }

    [Test]
    public async Task ReferenceRecoveryRejectsSyntheticOrAlteredImageBeforeOpeningHardware()
    {
        var p = new Platform(); var journal = await p.Journal(); p.Requests.Clear();
        try { await p.Transaction().RestoreReferenceAsync(journal, Image(), default); throw new Exception("Expected hash rejection"); }
        catch (InvalidDataException) { }
        Assert.Equal(0, p.Requests.Count);
    }

    private static HeroDfuImage Image() => HeroDfuImage.Parse(HeroDfuImageTests.Container());

    public sealed class Platform
    {
        private static readonly HidDeviceInfo Normal = new("hero#one", 0xC08B, "G502 HERO", "unit", 0xFF00, 2, 20, 20);
        private static readonly HidDeviceInfo Boot = new("boot#one", 0xAAE6, "Bootloader", "", 0xFF00, 2, 20, 20);
        public List<(string Mode, byte[] Bytes)> Requests { get; } = [];
        public List<HeroDfuPhase> Saves { get; } = [];
        public bool WrongBootUnit, WrongBootVersion, ChangeReturnedMemory, FirstPacketWasJournaled;
        public int FailAt = -1, Packets, Restarts;
        public byte FinalStatus = 6;
        public HeroDfuPhase? FailSaveAt;
        public Action? OnFirstPacket;
        private bool boot, returned;
        public IReadOnlyList<HidDeviceInfo> Inventory() => boot ? [Boot] : [Normal];
        public IHidTransport Open(HidDeviceInfo info) => info.ProductId == 0xAAE6 ? new BootMouse(this) : new HeroFirmwarePreflightTests.HeroMouse
        {
            BootloaderBuild = 3, MemoryVariant = returned && ChangeReturnedMemory ? (byte)1 : (byte)0,
            OnRequest = r =>
            {
                Requests.Add(("normal", r));
                if (r[2] == 4 && r[3] >> 4 == 1) boot = true;
            }
        };
        public HeroDfuTransaction Transaction() => new(Inventory, Open, j =>
        {
            Saves.Add(j.Phase);
            if (j.Phase == FailSaveAt) throw new IOException("simulated disk flush failure");
            return Task.CompletedTask;
        }, transitionTimeout: TimeSpan.FromMilliseconds(25));
        public async Task<HeroDfuJournal> Journal()
        {
            var j = new HeroDfuJournal();
            await HeroFirmwarePreflight.CollectAsync(j.Baseline, Normal, Open(Normal));
            Requests.Clear(); return j;
        }

        private sealed class BootMouse(Platform p) : IHidTransport
        {
            private readonly Channel<byte[]> reports = Channel.CreateUnbounded<byte[]>();
            private int streamPackets;
            public int InputReportLength => 20;
            public int OutputReportLength => 20;
            public Task WriteAsync(byte[] r, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); p.Requests.Add(("boot", r.ToArray()));
                var reply = new byte[20]; r.AsSpan(0, 4).CopyTo(reply); var fn = r[3] >> 4;
                ushort[] ids = [0, 1, 3, 5, 0xD0];
                if (r[2] == 0) reply[4] = (byte)Math.Max(0, Array.IndexOf(ids, (ushort)((r[4] << 8) | r[5])));
                else if (r[2] == 1 && fn == 0) reply[4] = 4;
                else if (r[2] == 1 && fn == 1) { reply[4] = (byte)(ids[r[4]] >> 8); reply[5] = (byte)ids[r[4]]; }
                else if (r[2] == 2 && fn == 0)
                {
                    Convert.FromHexString("02123456780008AAE600000000000000").CopyTo(reply, 4);
                    if (p.WrongBootUnit) reply[5] ^= 1;
                }
                else if (r[2] == 2 && fn == 1)
                {
                    Convert.FromHexString(r[4] == 0 ? "0055312027FFFFFF00C08B0000000000" : "01424F548101000301AAE60000000000").CopyTo(reply, 4);
                    if (p.WrongBootVersion && r[4] == 1) reply[11] = 4;
                }
                else if (r[2] == 4 && fn == 5) { p.Restarts++; p.boot = false; p.returned = true; }
                else if (r[2] == 4 && fn < 5)
                {
                    if (fn == 4) streamPackets = 0;
                    var at = streamPackets++; p.Packets++;
                    if (at == 0)
                    {
                        p.FirstPacketWasJournaled = p.Saves.LastOrDefault() == HeroDfuPhase.TransferMayHaveStarted;
                        p.OnFirstPacket?.Invoke();
                    }
                    BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), (uint)at);
                    reply[8] = at == p.FailAt ? (byte)0xA2 : at == 2819 ? p.FinalStatus : (byte)0x81;
                }
                else throw new IOException("Unexpected synthetic boot command");
                reports.Writer.TryWrite(reply); return Task.CompletedTask;
            }
            public async Task<byte[]> ReadAsync(CancellationToken ct) => await reports.Reader.ReadAsync(ct);
            public void Dispose() => reports.Writer.TryComplete();
        }
    }
}
