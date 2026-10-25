using System.Threading.Channels;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.Test;

/// <summary>In-memory HID++ firmware. Test runs never enumerate or write physical devices.</summary>
internal sealed class FakeMouse : IHidTransport
{
    private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
    private readonly byte[] description;
    private int writingSector;
    private int writingSize;
    private readonly List<byte> writingData = [];
    internal Dictionary<int, byte[]> Sectors { get; }
    internal byte Mode { get; private set; } = 2;
    internal ushort Active { get; private set; }
    internal List<int> ReadOffsets { get; } = [];
    internal int WriteStarts { get; private set; }
    internal int? FailOnceAtSector { get; set; }
    internal byte EndError { get; set; } = 2;
    internal bool InjectUnrelated { get; set; }
    internal bool SpySupported { get; set; }
    internal bool SpyActive { get; set; }
    internal bool FailSpyStop { get; set; }
    internal byte[] SpyMap { get; private set; } = [1, 2, 3, 0, 0, 0, 0, 0, 0, 0, 0];
    internal List<byte[]> Requests { get; } = [];
    public int InputReportLength => 20;
    public int OutputReportLength => 20;
    internal FakeMouse(MemorySnapshot snapshot)
    {
        description = snapshot.Description; Sectors = snapshot.Sectors.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone());
    }
    public Task WriteAsync(byte[] request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.ToArray());
        var reply = new byte[20]; request.AsSpan(0, 4).CopyTo(reply);
        var feature = request[2]; var function = request[3] >> 4;
        if (InjectUnrelated)
        {
            var eventReport = (byte[])reply.Clone(); eventReport[3] = 0; replies.Writer.TryWrite(eventReport);
            var otherDevice = (byte[])reply.Clone(); otherDevice[1] = 2; replies.Writer.TryWrite(otherDevice);
        }
        if (feature == 0)
        {
            if (function == 0)
            {
                var id = (ushort)((request[4] << 8) | request[5]);
                reply[4] = id == 0x8100 ? (byte)2 : id == 0x8110 && SpySupported ? (byte)3 : (byte)0;
            }
            if (function == 1) { reply[4] = 4; reply[5] = 2; reply[6] = request[6]; }
        }
        else if (feature == 3)
        {
            if (function == 0) reply[4] = 11;
            if (function == 1) SpyActive = true;
            if (function == 2) { if (FailSpyStop) Error(reply, request, 5); else SpyActive = false; }
            if (function == 3) SpyMap.CopyTo(reply, 4);
            if (function == 4) SpyMap = request[4..15];
        }
        else if (feature == 2)
        {
            switch (function)
            {
                case 0: description.CopyTo(reply, 4); break;
                case 1: Mode = request[4]; if (Mode == 2) Active = 0; else if (Active == 0) Active = 1; break;
                case 2: reply[4] = Mode; break;
                case 3:
                    Active = (ushort)((request[4] << 8) | request[5]);
                    if (Active == 0) throw new InvalidOperationException("Cannot activate sector zero.");
                    break;
                case 4: reply[4] = (byte)(Active >> 8); reply[5] = (byte)Active; break;
                case 5:
                    var sector = (request[4] << 8) | request[5]; var offset = (request[6] << 8) | request[7];
                    ReadOffsets.Add(offset);
                    if (!Sectors.TryGetValue(sector, out var data) || offset + 16 > data.Length) Error(reply, request, 2);
                    else data.AsSpan(offset, 16).CopyTo(reply.AsSpan(4));
                    break;
                case 6:
                    WriteStarts++; writingSector = (request[4] << 8) | request[5]; writingSize = (request[8] << 8) | request[9]; writingData.Clear(); break;
                case 7: writingData.AddRange(request.AsSpan(4, 16).ToArray()); break;
                case 8:
                    Sectors[writingSector] = writingData.Take(writingSize).ToArray();
                    if (FailOnceAtSector == writingSector) { FailOnceAtSector = null; Error(reply, request, EndError); }
                    break;
                default: Error(reply, request, 2); break;
            }
        }
        replies.Writer.TryWrite(reply);
        return Task.CompletedTask;
    }
    private static void Error(byte[] reply, byte[] request, byte error)
    { reply[2] = 0xFF; reply[3] = request[2]; reply[4] = request[3]; reply[5] = error; }
    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken) => await replies.Reader.ReadAsync(cancellationToken);
    public void Dispose() => replies.Writer.TryComplete();
}
