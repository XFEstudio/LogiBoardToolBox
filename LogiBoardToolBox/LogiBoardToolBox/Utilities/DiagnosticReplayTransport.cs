using System.Threading.Channels;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.Utilities;

/// <summary>Opt-in UI verification only. This type never opens a Windows device.</summary>
internal sealed class DiagnosticReplayTransport : IHidTransport
{
    private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
    private readonly byte[] description;
    private int writingSector, writingSize;
    private readonly List<byte> buffer = [];
    internal Dictionary<int, byte[]> Sectors { get; }
    internal int WriteStarts { get; private set; }
    private byte mode;
    private ushort active;
    public int InputReportLength => 20;
    public int OutputReportLength => 20;
    internal DiagnosticReplayTransport(MemorySnapshot snapshot)
    {
        if (!UiVerification.IsCatalogRequested) throw new InvalidOperationException("仅用于后台离线验收。");
        description = (byte[])snapshot.Description.Clone(); mode = snapshot.Mode; active = snapshot.ActiveSector;
        Sectors = snapshot.Sectors.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone());
    }
    public Task WriteAsync(byte[] request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var reply = new byte[20]; request.AsSpan(0, 4).CopyTo(reply);
        var function = request[3] >> 4;
        if (request[2] == 0)
        { if (function == 0) reply[4] = request[4] == 0x81 && request[5] == 0 ? (byte)2 : (byte)0; }
        else if (request[2] == 2)
        {
            var sector = (request[4] << 8) | request[5]; var offset = (request[6] << 8) | request[7];
            switch (function)
            {
                case 0: description.CopyTo(reply, 4); break;
                case 1: mode = request[4]; break;
                case 2: reply[4] = mode; break;
                case 3: active = (ushort)sector; break;
                case 4: reply[4] = (byte)(active >> 8); reply[5] = (byte)active; break;
                case 5:
                    if (Sectors.TryGetValue(sector, out var data) && offset + 16 <= data.Length) data.AsSpan(offset, 16).CopyTo(reply.AsSpan(4));
                    else { reply[2] = 0xFF; reply[3] = request[2]; reply[4] = request[3]; reply[5] = 2; }
                    break;
                case 6: WriteStarts++; writingSector = sector; writingSize = (request[8] << 8) | request[9]; buffer.Clear(); break;
                case 7: buffer.AddRange(request.AsSpan(4, 16).ToArray()); break;
                case 8: Sectors[writingSector] = buffer.Take(writingSize).ToArray(); break;
            }
        }
        replies.Writer.TryWrite(reply); return Task.CompletedTask;
    }
    public async Task<byte[]> ReadAsync(CancellationToken ct) => await replies.Reader.ReadAsync(ct);
    public void Dispose() => replies.Writer.TryComplete();
}
