using System.Diagnostics;
using LogiBoardToolBox.Core.HidPlusPlus;
using LogiBoardToolBox.Core.Lighting;

namespace LogiBoardToolBox.Services;

public sealed class LightsyncSession(HidPlusPlusClient client, HostModeLease? sharedMode = null, IReadOnlyList<byte>? preferredLedOrder = null) : IAsyncDisposable
{
    private readonly LightsyncDevice device = new(client, preferredLedOrder);
    private readonly CancellationTokenSource lifetime = new();
    private SystemAudioMeter? audio;
    private ScreenSampler? screen;
    private MouseActivityObserver? clicks;
    private Rgb[]? latestFrame;
    public IReadOnlyList<Rgb>? LatestFrame => Volatile.Read(ref latestFrame);
    private Task? rendering;
    private byte rgbIndex, spyIndex;
    private volatile bool idle;
    private long lastPress = long.MinValue;
    private readonly Stopwatch time = new();
    private bool ownsMode;
    private readonly HostModeLease hostMode = sharedMode ?? new(client);
    public event EventHandler<Exception>? Faulted;
    public async Task StartAsync(LightingSettings settings)
    {
        settings.Validate();
        try
        {
            if (settings.RequiresScreen) screen = new(settings.MonitorIndex);
            if (settings.RequiresAudio) { audio = new(); audio.Faulted += (_, ex) => { Faulted?.Invoke(this, ex); lifetime.Cancel(); }; }
            var capabilities = await device.ReadAsync();
            if (capabilities.ZoneCount != settings.ZoneColors.Length) throw new InvalidOperationException("灯效颜色数量与当前鼠标灯区不一致，请重新读取设备。");
            rgbIndex = await client.GetFeatureAsync(0x8071);
            if (settings.Effect == LightingEffect.Ripple) spyIndex = await client.GetFeatureAsync(0x8110);
            await hostMode.AcquireAsync(); ownsMode = true;
            await device.ClaimAsync();
            if (settings.Effect == LightingEffect.Ripple) clicks = new(TriggerRipple);
            // Mouse button spy is only observed here; macro sessions can own it separately.
            client.Notification += Notification; client.Disconnected += Disconnected;
            time.Start(); rendering = Task.Run(() => RenderAsync(settings));
        }
        catch { await DisposeAsync(); throw; }
    }
    public void TriggerRipple() => Interlocked.Exchange(ref lastPress, time.ElapsedMilliseconds);
    private void Notification(object? sender, HidNotification n)
    {
        if (rgbIndex != 0 && n.FeatureIndex == rgbIndex && n.Function == 1 && n.Data.Length > 0) idle = n.Data[0] == 0;
        if (spyIndex != 0 && n.FeatureIndex == spyIndex && n.Function == 0 && n.Data.Length >= 2 && (n.Data[0] != 0 || n.Data[1] != 0)) TriggerRipple();
    }
    private void Disconnected(object? sender, Exception ex) { lifetime.Cancel(); Faulted?.Invoke(this, ex); }
    private async Task RenderAsync(LightingSettings settings)
    {
        var previousIdle = false;
        try
        {
            using var frames = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            do
            {
                if (idle != previousIdle) { await device.PowerAsync(!idle, lifetime.Token); previousIdle = idle; }
                if (!idle)
                {
                    var colors = LightingRenderer.Render(settings, time.Elapsed.TotalMilliseconds, screen?.Sample(settings.Regions), audio?.Level ?? 0, Interlocked.Read(ref lastPress));
                    await device.SendFrameAsync(colors, lifetime.Token);
                    Volatile.Write(ref latestFrame, colors);
                }
            } while (await frames.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Faulted?.Invoke(this, ex); }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); client.Notification -= Notification; client.Disconnected -= Disconnected;
        clicks?.Dispose(); clicks = null;
        if (rendering is not null) { await rendering; rendering = null; }
        audio?.Dispose(); audio = null;
        if (!ownsMode) return; ownsMode = false;
        using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await device.ReleaseAsync(recovery.Token); }
        finally
        {
            await hostMode.ReleaseAsync(recovery.Token);
        }
    }
}
