using System.Threading.Channels;
using LogiBoardToolBox.Core.Macros;

namespace LogiBoardToolBox.Core.HidPlusPlus;

public sealed record HostMacroBinding(int Button, bool GShift, MacroProgram Program);

/// <summary>RAM-only button diversion through Mouse Button Spy; restores the captured map on exit.</summary>
public sealed class HostMacroSession(HidPlusPlusClient client, IMacroInputSink input, HostModeLease? sharedMode = null) : IAsyncDisposable
{
    private readonly MacroRuntime runtime = new(input);
    private readonly Channel<ushort> masks = Channel.CreateUnbounded<ushort>(new() { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource lifetime = new();
    private byte spyIndex;
    private byte[]? previousMap;
    private Task? processing;
    private IReadOnlyList<HostMacroBinding> bindings = [];
    private int gshiftButton = -1;
    private bool disposed;
    private readonly HostModeLease hostMode = sharedMode ?? new(client);
    private bool ownsMode;
    public event EventHandler<Exception>? Faulted;
    public async Task StartAsync(IReadOnlyList<HostMacroBinding> assignments, int shiftButton, CancellationToken ct = default)
    {
        if (previousMap is not null || disposed) throw new InvalidOperationException("软件宏会话已启动或已释放。");
        spyIndex = await client.GetFeatureAsync(0x8110, ct).ConfigureAwait(false);
        if (spyIndex == 0) throw new NotSupportedException("此设备不支持 Mouse Button Spy，不能启动软件宏。");
        var count = (await client.FeatureAsync(0x8110, 0, ct: ct).ConfigureAwait(false))[0];
        if (count is < 1 or > 16 || assignments.Any(a => a.Button < 0 || a.Button >= count) || assignments.GroupBy(a => (a.Button, a.GShift)).Any(g => g.Count() != 1))
            throw new InvalidDataException("软件宏按键分配超出范围或重复。");
        if (assignments.Count == 0) throw new InvalidOperationException("请先把宏分配到鼠标按键。");
        if (assignments.Any(a => a.GShift) && shiftButton < 0) throw new InvalidOperationException("请先为普通层分配 G-Shift 按键。");
        if (assignments.Any(a => a.Button == shiftButton)) throw new InvalidOperationException("G-Shift 切换键不能同时作为软件宏触发键。");
        bindings = assignments.ToArray(); gshiftButton = shiftButton;
        previousMap = (await client.FeatureAsync(0x8110, 3, ct: ct).ConfigureAwait(false)).Take(count).ToArray();
        if (previousMap.Length != count) throw new InvalidDataException("原始鼠标映射回复不完整。");
        runtime.Faulted += (_, error) => Faulted?.Invoke(this, error);
        client.Notification += OnNotification; client.Disconnected += OnDisconnected;
        try
        {
            await hostMode.AcquireAsync(ct).ConfigureAwait(false); ownsMode = true;
            var map = new byte[16]; previousMap.CopyTo(map, 0);
            foreach (var a in assignments) map[a.Button] = 0;
            await client.FeatureAsync(0x8110, 4, map, ct).ConfigureAwait(false);
            await client.FeatureAsync(0x8110, 1, ct: ct).ConfigureAwait(false);
            processing = Task.Run(ProcessAsync);
        }
        catch { await DisposeAsync().ConfigureAwait(false); throw; }
    }
    private void OnNotification(object? sender, HidNotification message)
    {
        if (message.FeatureIndex == spyIndex && message.Function == 0 && message.Data.Length >= 2)
            masks.Writer.TryWrite((ushort)((message.Data[0] << 8) | message.Data[1]));
    }
    private void OnDisconnected(object? sender, Exception error)
    {
        lifetime.Cancel(); _ = runtime.StopAllAsync(); Faulted?.Invoke(this, error);
    }
    private async Task ProcessAsync()
    {
        ushort previous = 0;
        try
        {
            await foreach (var mask in masks.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                var shift = gshiftButton >= 0 && (mask & (1 << gshiftButton)) != 0;
                // Resolve the layer at press time. Releases use the same trigger even if G-Shift changed.
                for (var button = 0; button < 16; button++)
                    if ((previous & (1 << button)) != 0 && (mask & (1 << button)) == 0) runtime.Release(button.ToString());
                for (var button = 0; button < 16; button++)
                    if ((mask & (1 << button)) != 0 && (previous & (1 << button)) == 0)
                    {
                        var binding = bindings.FirstOrDefault(a => a.Button == button && a.GShift == shift)
                            ?? (shift ? bindings.FirstOrDefault(a => a.Button == button && !a.GShift) : null);
                        if (binding is not null) _ = runtime.Press(button.ToString(), binding.Program);
                    }
                previous = mask;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Faulted?.Invoke(this, ex); }
        finally { await runtime.StopAllAsync().ConfigureAwait(false); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        client.Notification -= OnNotification; client.Disconnected -= OnDisconnected;
        lifetime.Cancel(); masks.Writer.TryComplete();
        if (processing is not null) await processing.ConfigureAwait(false);
        await runtime.DisposeAsync().ConfigureAwait(false);
        if (previousMap is null) return;
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            await client.FeatureAsync(0x8110, 2, ct: recovery.Token).ConfigureAwait(false);
            var map = new byte[16]; previousMap.CopyTo(map, 0);
            await client.FeatureAsync(0x8110, 4, map, recovery.Token).ConfigureAwait(false);
        }
        finally { previousMap = null; if (ownsMode) { ownsMode = false; await hostMode.ReleaseAsync(recovery.Token).ConfigureAwait(false); } }
    }
}
