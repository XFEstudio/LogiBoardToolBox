using System.Collections.Concurrent;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Services;

namespace LogiBoardToolBox.Utilities;

internal static partial class UiVerification
{
    private static async Task VerifyNativeRuntimeAsync(Action<string, bool, string> check)
    {
        var inputs = new ConcurrentQueue<MacroInput>();
        await using (var lua = new LuaMacroRuntime(new RecordingSink(inputs)))
        {
            await lua.StartAsync("""
                function OnEvent(event, arg)
                  if event ~= "MOUSE_BUTTON_PRESSED" or arg ~= 4 then return end
                  Sleep(20)
                  if not IsMouseButtonPressed(4) then return end
                  PressMouseButton(1)
                  Sleep(math.random(15, 25))
                  ReleaseMouseButton(1)
                end
                """);
            lua.MouseButton(4, true);
            for (var i = 0; i < 100 && inputs.Count < 2; i++) await Task.Delay(20);
            check("native-lua-interpreter-executes", inputs.SequenceEqual(new[]
            {
                new MacroInput(MacroInputKind.MouseButton, 1, true),
                new MacroInput(MacroInputKind.MouseButton, 1, false)
            }), "Interpreter executed the delayed state check and randomized delay using an in-memory input sink.");
        }
        await VerifyAudioLoopbackAsync(check);
    }

    private static async Task VerifyAudioLoopbackAsync(Action<string, bool, string> check)
    {
        using var meter = new SystemAudioMeter();
        using var silentOutput = new NAudio.Wave.WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, false, 50);
        silentOutput.Init(new NAudio.Wave.SilenceProvider(meter.Format));
        silentOutput.Play();
        for (var i = 0; i < 30 && meter.SamplesReceived == 0; i++) await Task.Delay(50);
        check("native-audio-loopback", meter.SamplesReceived > 0 && double.IsFinite(meter.Level),
            $"Shared output endpoint returned {meter.SamplesReceived} samples during silent playback.");
    }

    private sealed class RecordingSink(ConcurrentQueue<MacroInput> inputs) : IMacroInputSink
    {
        public void Send(MacroInput input) => inputs.Enqueue(input);
    }
}
