using LogiBoardToolBox.Core.Windows;

/// <summary>Temporarily changes only the documented SOFTWARE_PRESENT notification bit.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class ReceiverNotificationScope : IAsyncDisposable
{
    private readonly WindowsHidTransport transport;
    private byte[]? original;
    private bool writeAttempted;
    public List<string> Trace { get; } = [];
    public ReceiverNotificationScope(LogitechDevice device)
    {
        var hid = WindowsHidTransport.EnumerateLogitechDevices().Single(d => d.CollectionGroup == device.Hid.CollectionGroup && d.OutputReportLength == 7);
        transport = new(hid);
    }
    public async Task<byte[]> ReadAsync() => (await ExchangeAsync(0x81, [0, 0, 0]))[4..7];
    public async Task ClearSoftwarePresentAsync()
    {
        original = await ReadAsync();
        var desired = (byte[])original.Clone(); desired[1] &= 0xF7;
        if (!desired.SequenceEqual(original))
        {
            writeAttempted = true;
            await ExchangeAsync(0x80, desired);
            if (!(await ReadAsync()).SequenceEqual(desired)) throw new IOException("通知位读回不一致。");
        }
    }
    private async Task<byte[]> ExchangeAsync(byte command, byte[] value)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        byte[] request = [0x10, 0xFF, command, 0, .. value]; Trace.Add("TX " + Convert.ToHexString(request));
        await transport.WriteAsync(request, timeout.Token);
        while (true)
        {
            var reply = await transport.ReadAsync(timeout.Token); Trace.Add("RX " + Convert.ToHexString(reply));
            if (reply.Length < 7 || reply[1] != 0xFF) continue;
            if (reply[2] == command && reply[3] == 0) return reply;
            if (reply[2] == 0x8F && reply[3] == command && reply[4] == 0)
                throw new IOException($"接收器通知操作错误 {reply[5]:X2}。");
        }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (writeAttempted && original is not null)
            {
                var current = await ReadAsync();
                current[1] = (byte)((current[1] & 0xF7) | (original[1] & 8));
                await ExchangeAsync(0x80, current);
                if (!(await ReadAsync()).SequenceEqual(current)) throw new IOException("接收器软件通知位未恢复。");
            }
        }
        finally { transport.Dispose(); }
    }
}
