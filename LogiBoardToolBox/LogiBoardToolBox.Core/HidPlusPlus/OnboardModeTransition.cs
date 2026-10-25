namespace LogiBoardToolBox.Core.HidPlusPlus;

internal static class OnboardModeTransition
{
    public static async Task StopButtonSpyAsync(HidPlusPlusClient client, CancellationToken ct)
    {
        // G HUB stops device-side button reporting before entering onboard mode.
        // Its state can outlive the application that started it, so disposing only
        // our own HostMacroSession does not complete the handoff.
        var spy = await client.GetFeatureAsync(0x8110, ct).ConfigureAwait(false);
        if (spy != 0) await client.CallAsync(spy, 2, cancellationToken: ct).ConfigureAwait(false);
    }
}
