using System.Security.Cryptography;
using LogiBoardToolBox.Core.Diagnostics;

namespace LogiBoardToolBox.Core.HidPlusPlus;

public sealed class HidPlusPlusException(byte code, string message) : IOException(message)
{
    public byte Code { get; } = code;
}

/// <summary>HID++ 2.0 request/reply matching, including receiver routing and software IDs.</summary>
public sealed class HidPlusPlusClient(IHidTransport transport, byte deviceIndex) : IDisposable
{
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ushort, byte> features = new() { [0] = 0 };
    private int softwareId = RandomNumberGenerator.GetInt32(1, 15);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private Task? reader;
    private TaskCompletionSource<byte[]>? pending;
    private byte pendingFeature, pendingAddress;
    private Exception? readError;
    public bool HasConnectionError { get { lock (sync) return readError is not null; } }
    public event EventHandler<HidNotification>? Notification;
    public event EventHandler<Exception>? Disconnected;
    public byte DeviceIndex { get; } = deviceIndex;
    public string DiagnosticId { get; } = Guid.NewGuid().ToString("N");
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(1600);
    public Action<string>? Trace { get; set; }

    public async Task<byte[]> CallAsync(byte featureIndex, byte function, byte[]? parameters = null,
        CancellationToken cancellationToken = default)
    {
        parameters ??= [];
        if (function > 15 || parameters.Length > 16)
            throw new ArgumentOutOfRangeException(nameof(parameters));
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TaskCompletionSource<byte[]> completion;
            lock (sync)
            {
                if (readError is not null) throw new IOException("鼠标连接已中断。", readError);
                if (lifetime.IsCancellationRequested) throw new ObjectDisposedException(nameof(HidPlusPlusClient));
                reader ??= Task.Run(ReadLoopAsync);
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var address = (byte)((function << 4) | (softwareId = softwareId % 15 + 1));
            var request = new byte[transport.OutputReportLength];
            if (request.Length < 20) throw new NotSupportedException("HID 接口不支持 HID++ 长报文。");
            request[0] = 0x11;
            request[1] = DeviceIndex;
            request[2] = featureIndex;
            request[3] = address;
            parameters.CopyTo(request, 4);
            try
            {
                lock (sync) { pendingFeature = featureIndex; pendingAddress = address; pending = completion; }
                TraceReport("TX", request);
                await transport.WriteAsync(request, timeout.Token).ConfigureAwait(false);
                return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                DiagnosticLog.Record("hid-error", "timeout", new { DeviceIndex, featureIndex, function, address, Parameters = Convert.ToHexString(parameters) });
                throw new TimeoutException("鼠标未响应。唤醒或重新连接鼠标后可重试。");
            }
            catch (Exception error)
            {
                DiagnosticLog.Record("hid-error", "request failed", new { DeviceIndex, featureIndex, function, address, Parameters = Convert.ToHexString(parameters) }, error);
                throw;
            }
            finally { lock (sync) if (ReferenceEquals(pending, completion)) pending = null; }
        }
        finally { requestGate.Release(); }
    }

    public async Task<byte> GetFeatureAsync(ushort id, CancellationToken ct = default)
    {
        if (features.TryGetValue(id, out var known)) return known;
        var reply = await CallAsync(0, 0, [(byte)(id >> 8), (byte)id], ct).ConfigureAwait(false);
        features[id] = reply[0];
        return reply[0];
    }

    public bool IsNotificationFor(ushort feature, HidNotification notification)
        => features.TryGetValue(feature, out var index) && index != 0 && index == notification.FeatureIndex;

    public async Task<byte[]> FeatureAsync(ushort id, byte function, byte[]? parameters = null, CancellationToken ct = default)
    {
        var index = await GetFeatureAsync(id, ct).ConfigureAwait(false);
        if (index == 0 && id != 0) throw new NotSupportedException($"设备未提供 HID++ 功能 0x{id:X4}。");
        return await CallAsync(index, function, parameters, ct).ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        var nonce = (byte)RandomNumberGenerator.GetInt32(1, 256);
        var response = await CallAsync(0, 1, [0, 0, nonce], ct).ConfigureAwait(false);
        return response.Length >= 3 && response[0] >= 2 && response[2] == nonce;
    }

    public async Task<string> ReadNameAsync(CancellationToken ct = default)
    {
        if (await GetFeatureAsync(0x0005, ct).ConfigureAwait(false) == 0) return "Logitech HID++ Mouse";
        var size = (await FeatureAsync(0x0005, 0, ct: ct).ConfigureAwait(false))[0];
        var bytes = new List<byte>();
        while (bytes.Count < size)
        {
            var fragment = await FeatureAsync(0x0005, 1, [(byte)bytes.Count], ct).ConfigureAwait(false);
            bytes.AddRange(fragment.Take(size - bytes.Count));
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var report = await transport.ReadAsync(lifetime.Token).ConfigureAwait(false);
                TraceReport("RX", report);
                if (report.Length < 7 || report[1] != DeviceIndex || report[0] is not (0x10 or 0x11 or 0x12)) continue;
                var size = report[0] == 0x10 ? 7 : report[0] == 0x11 ? 20 : 64;
                if (report.Length < size) continue;
                TaskCompletionSource<byte[]>? target = null;
                Exception? error = null;
                lock (sync)
                {
                    if (pending is not null)
                    {
                        if (report[2] is 0xFF or 0x8F && report[3] == pendingFeature && report[4] == pendingAddress)
                        { target = pending; error = new HidPlusPlusException(report[5], $"HID++ 错误 0x{report[5]:X2}（功能 {pendingFeature:X2}/{pendingAddress >> 4:X1}）。"); }
                        else if (report[2] == pendingFeature && report[3] == pendingAddress) target = pending;
                    }
                }
                if (target is not null)
                {
                    if (error is not null) target.TrySetException(error); else target.TrySetResult(report[4..size]);
                }
                else if ((report[3] & 15) == 0)
                {
                    // Subscribers must queue work; they must never block the reply reader.
                    try { Notification?.Invoke(this, new(report[2], (byte)(report[3] >> 4), report[4..size])); }
                    catch (Exception ex) { Trace?.Invoke("Notification: " + ex.Message); }
                }
            }
        }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DiagnosticLog.Record("hid-error", "reader disconnected", new { DeviceIndex }, ex);
            var connectionError = ex is IOException ? ex : new IOException("鼠标 HID 接口连接已中断。", ex);
            lock (sync) { readError = connectionError; pending?.TrySetException(connectionError); }
            Disconnected?.Invoke(this, connectionError);
        }
    }
    private void TraceReport(string direction, byte[] report)
    {
        var hex = Convert.ToHexString(report);
        DiagnosticLog.Record("hid", direction, new { DiagnosticId, DeviceIndex, Transport = transport.GetType().FullName, Report = hex });
        Trace?.Invoke(direction + " " + hex);
    }
    public void Dispose()
    {
        if (lifetime.IsCancellationRequested) return;
        lifetime.Cancel(); lock (sync) pending?.TrySetCanceled(); transport.Dispose();
    }
}

public sealed record HidNotification(byte FeatureIndex, byte Function, byte[] Data);
