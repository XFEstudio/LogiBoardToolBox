namespace LogiBoardToolBox.Core.HidPlusPlus;

/// <summary>Shares one host-mode lifetime between macros and lighting, independent of stop order.</summary>
public sealed class HostModeLease(HidPlusPlusClient client)
{
    private readonly SemaphoreSlim gate = new(1);
    private int owners;
    private byte oldMode;
    private byte[] oldActive = [];
    public async Task AcquireAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (owners == 0)
            {
                oldMode = (await client.FeatureAsync(0x8100, 2, ct: ct).ConfigureAwait(false))[0];
                oldActive = await client.FeatureAsync(0x8100, 4, ct: ct).ConfigureAwait(false);
                await client.FeatureAsync(0x8100, 1, [2], ct).ConfigureAwait(false);
            }
            owners++;
        }
        finally { gate.Release(); }
    }
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (owners == 0 || --owners != 0) return;
            if (oldMode == 1) await OnboardModeTransition.StopButtonSpyAsync(client, ct).ConfigureAwait(false);
            await client.FeatureAsync(0x8100, 1, [oldMode], ct).ConfigureAwait(false);
            if (oldMode == 1 && oldActive.Length >= 2 && (oldActive[0] != 0 || oldActive[1] != 0)) await client.FeatureAsync(0x8100, 3, oldActive[..2], ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
