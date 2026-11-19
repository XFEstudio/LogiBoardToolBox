using System.Buffers.Binary;
using System.Threading.Channels;
using LogiBoardToolBox.Core.Firmware;
using LogiBoardToolBox.Core.HidPlusPlus;
using XFEExtension.NetCore.XUnit.Assertions;
using XFEExtension.NetCore.XUnit.Attributes;

namespace LogiBoardToolBox.Test;

[TestFixture]
public class HidPlusPlusDfuTransferTests
{
    [Test]
    public async Task FullStreamKeepsEveryByteAndWindowWithoutEnteringDfuOrResetting()
    {
        var raw = HeroDfuImageTests.Container(); var image = HeroDfuImage.Parse(raw);
        var mouse = new DfuMouse(); using var client = new HidPlusPlusClient(mouse, 0xFF);
        var result = await HidPlusPlusDfuTransfer.SendAsync(client, image);
        Assert.Equal(2820, result.AcknowledgedPackets);
        Assert.Equal((byte)0x06, result.LastStatus.RawStatus);
        Assert.Equal(false, result.InstallationVerified);
        Assert.Equal(false, result.RestartRequested);
        var packets = mouse.Requests.Where(r => r[2] == 6).ToArray();
        Assert.Equal(2820, packets.Length);
        Assert.Equal(Convert.ToHexString(raw), Convert.ToHexString(packets.SelectMany(r => r.Skip(4)).ToArray()));
        Assert.Equal("4,1,2,3,0,1,2,3,0", string.Join(',', packets.Take(9).Select(r => r[3] >> 4)));
        Assert.Equal(false, packets.Any(r => (r[3] >> 4) == 5));
        Assert.Equal(1, mouse.Requests.Count(r => r[2] == 0));
        var copy = image.CopyPacket(0); copy[0] ^= 1;
        Assert.Equal((byte)0, image.CopyPacket(0)[0]);
    }

    [Test]
    public async Task NoBootloaderInterfaceOnlyQueriesItsPresenceAndNeverChangesApplicationMode()
    {
        var mouse = new DfuMouse { HasDfu = false }; using var client = new HidPlusPlusClient(mouse, 0xFF);
        try { await HidPlusPlusDfuTransfer.SendAsync(client, HeroDfuImage.Parse(HeroDfuImageTests.Container())); throw new Exception("Expected missing DFU"); }
        catch (NotSupportedException) { }
        Assert.Equal(1, mouse.Requests.Count); Assert.Equal(0, mouse.Packets);
        Assert.Equal("00D0", Convert.ToHexString(mouse.Requests[0].AsSpan(4, 2)));
    }

    [Test]
    public async Task BusyCompletionQueuedBeforeReplyIsNotLostAndUnrelatedFeaturesAreIgnored()
    {
        var mouse = new DfuMouse { BusyAt = 0, EarlyCompletion = true, UnrelatedNotice = true };
        using var client = new HidPlusPlusClient(mouse, 0xFF);
        var result = await HidPlusPlusDfuTransfer.SendAsync(client, HeroDfuImage.Parse(HeroDfuImageTests.Container()));
        Assert.Equal(2820, result.AcknowledgedPackets); Assert.Equal(2820, mouse.Packets);
    }

    [Test]
    public async Task BusyFailuresStopWithoutSendingTheNextPacketRegardlessOfCounterChanges()
    {
        foreach (var mismatch in new[] { false, true })
        {
            var mouse = new DfuMouse { BusyAt = 0, BusyCompletionStatus = 0xA2, WrongCounter = mismatch };
            using var client = new HidPlusPlusClient(mouse, 0xFF);
            try { await HidPlusPlusDfuTransfer.SendAsync(client, HeroDfuImage.Parse(HeroDfuImageTests.Container())); throw new Exception("Expected DFU rejection"); }
            catch (DfuTransferException error)
            {
                Assert.Equal(0, error.PacketIndex);
                Assert.Equal((byte)0x22, error.Status!.Code);
            }
            Assert.Equal(1, mouse.Packets);
        }
    }

    [Test]
    public async Task BusyCompletionMayAdvanceTheDiagnosticCounter()
    {
        var mouse = new DfuMouse { BusyAt = 0, WrongCounter = true };
        using var client = new HidPlusPlusClient(mouse, 0xFF);
        var result = await HidPlusPlusDfuTransfer.SendAsync(client, HeroDfuImage.Parse(HeroDfuImageTests.Container()));
        Assert.Equal(2820, result.AcknowledgedPackets);
    }

    [Test]
    public async Task DeviceErrorsAbortAtTheExactPacketWithoutReplayingStatefulWrites()
    {
        foreach (byte status in new byte[] { 0x14, 0x1F, 0x20, 0x22, 0xFF })
        {
            var mouse = new DfuMouse { FailAt = 8, FailureStatus = status }; using var client = new HidPlusPlusClient(mouse, 0xFF);
            try { await HidPlusPlusDfuTransfer.SendAsync(client, HeroDfuImage.Parse(HeroDfuImageTests.Container())); throw new Exception("Expected rejection"); }
            catch (DfuTransferException error)
            { Assert.Equal(8, error.PacketIndex); Assert.Equal(status, error.Status!.RawStatus); }
            Assert.Equal(9, mouse.Packets);
        }
    }

    [Test]
    public async Task BusyTimeoutAndCallerCancellationDoNotResetOrRetry()
    {
        var mouse = new DfuMouse { BusyAt = 0, NoCompletion = true }; using var client = new HidPlusPlusClient(mouse, 0xFF);
        var image = HeroDfuImage.Parse(HeroDfuImageTests.Container());
        try { await HidPlusPlusDfuTransfer.SendAsync(client, image, eventTimeout: TimeSpan.FromMilliseconds(20)); throw new Exception("Expected timeout"); }
        catch (DfuTransferException error) { Assert.Equal(true, error.InnerException is TimeoutException); }
        Assert.Equal(1, mouse.Packets);
        mouse = new DfuMouse { BusyAt = 0, NoCompletion = true }; using var second = new HidPlusPlusClient(mouse, 0xFF);
        using var cancellation = new CancellationTokenSource();
        var pending = HidPlusPlusDfuTransfer.SendAsync(second, image, ct: cancellation.Token);
        await mouse.FirstPacket.Task.WaitAsync(TimeSpan.FromSeconds(2)); cancellation.Cancel();
        try { await pending; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Assert.Equal(1, mouse.Packets);
        Assert.Equal(false, mouse.Requests.Any(r => r[2] == 6 && r[3] >> 4 == 5));
    }

    [Test]
    public async Task TruncatedStatusNotificationOverflowAndDisconnectCannotAdvanceTheStream()
    {
        foreach (var mouse in new[]
        {
            new DfuMouse { ShortReplyAt = 0 },
            new DfuMouse { BusyAt = 0, EarlyCompletion = true, ExtraBusyNotices = 80 },
            new DfuMouse { BusyAt = 0, NoCompletion = true, DisconnectAt = 0 }
        })
        {
            using var client = new HidPlusPlusClient(mouse, 0xFF);
            try { await HidPlusPlusDfuTransfer.SendAsync(client, HeroDfuImage.Parse(HeroDfuImageTests.Container())); throw new Exception("Expected transfer failure"); }
            catch (DfuTransferException error) { Assert.Equal(0, error.PacketIndex); }
            Assert.Equal(1, mouse.Packets);
        }
    }

    [Test]
    public async Task ConcurrentStreamsOnOneClientAreSerializedAsCompleteStreams()
    {
        var mouse = new DfuMouse(); using var client = new HidPlusPlusClient(mouse, 0xFF);
        var raw = HeroDfuImageTests.Container(); var image = HeroDfuImage.Parse(raw);
        var results = await Task.WhenAll(HidPlusPlusDfuTransfer.SendAsync(client, image), HidPlusPlusDfuTransfer.SendAsync(client, image));
        Assert.Equal(true, results.All(r => r.AcknowledgedPackets == 2820));
        var bytes = mouse.Requests.Where(r => r[2] == 6).SelectMany(r => r.Skip(4)).ToArray();
        Assert.Equal(Convert.ToHexString(raw.Concat(raw).ToArray()), Convert.ToHexString(bytes));
    }

    [Test]
    public void StatusParsingMasksOnlyTheFlagBitAndRejectsTruncatedReplies()
    {
        foreach (byte code in new byte[] { 1, 2, 5, 6 })
            Assert.Equal(DfuStatusKind.Success, new DfuStatus(0, (byte)(code | 0x80)).Kind);
        foreach (byte code in new byte[] { 3, 0x19, 0x23 }) Assert.Equal(DfuStatusKind.Busy, new DfuStatus(0, code).Kind);
        Assert.Equal(DfuStatusKind.Failure, new DfuStatus(0, 0x22).Kind);
        var parsed = DfuStatus.Parse(Convert.FromHexString("123456788100"));
        Assert.Equal(0x12345678u, parsed.DeviceCounter); Assert.Equal((byte)1, parsed.Code);
        try { DfuStatus.Parse(new byte[4]); throw new Exception("Expected short reply rejection"); }
        catch (InvalidDataException) { }
    }

    private sealed class DfuMouse : IHidTransport
    {
        private readonly Channel<byte[]> reports = Channel.CreateUnbounded<byte[]>();
        public List<byte[]> Requests { get; } = [];
        public TaskCompletionSource FirstPacket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HasDfu = true, EarlyCompletion, WrongCounter, UnrelatedNotice, NoCompletion;
        public int BusyAt = -1, FailAt = -1, ShortReplyAt = -1, DisconnectAt = -1, ExtraBusyNotices, Packets;
        public byte BusyCompletionStatus = 1, FailureStatus = 0x22;
        public int InputReportLength => 20;
        public int OutputReportLength => 20;
        public Task WriteAsync(byte[] r, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add(r.ToArray());
            var reply = new byte[20]; r.AsSpan(0, 4).CopyTo(reply);
            if (r[2] == 0) { reply[4] = HasDfu ? (byte)6 : (byte)0; reports.Writer.TryWrite(reply); return Task.CompletedTask; }
            var at = Packets++; var counter = (uint)at + 100;
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), counter);
            reply[8] = at == FailAt ? FailureStatus : at == BusyAt ? (byte)3 : at == 2819 ? (byte)6 : (byte)0x81;
            if (at == ShortReplyAt) { reply = reply[..7]; reply[0] = 0x10; }
            if (at == BusyAt && !NoCompletion && EarlyCompletion) Complete();
            reports.Writer.TryWrite(reply);
            if (at == BusyAt && !NoCompletion && !EarlyCompletion) Complete();
            if (at == DisconnectAt) reports.Writer.TryComplete(new IOException("simulated cable disconnect"));
            FirstPacket.TrySetResult();
            return Task.CompletedTask;
            void Complete()
            {
                var notice = reply.ToArray(); notice[3] = 0xA0; notice[8] = BusyCompletionStatus;
                BinaryPrimitives.WriteUInt32BigEndian(notice.AsSpan(4, 4), counter + (WrongCounter ? 1u : 0u));
                if (UnrelatedNotice) { var unrelated = notice.ToArray(); unrelated[2] = 9; unrelated[8] = 0x22; reports.Writer.TryWrite(unrelated); }
                for (var i = 0; i < ExtraBusyNotices; i++) { var busy = notice.ToArray(); busy[8] = 0x19; reports.Writer.TryWrite(busy); }
                reports.Writer.TryWrite(notice);
            }
        }
        public async Task<byte[]> ReadAsync(CancellationToken ct) => await reports.Reader.ReadAsync(ct);
        public void Dispose() => reports.Writer.TryComplete();
    }
}
