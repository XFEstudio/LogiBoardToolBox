using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using MoonSharp.Interpreter;

namespace LogiBoardToolBox.Core.Macros;

/// <summary>G HUB-style Lua event execution in an interpreter without CLR, files, OS or network modules.</summary>
public sealed class LuaMacroRuntime(IMacroInputSink sink, Func<string, bool>? keyState = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, bool> pressed = new();
    private readonly HashSet<(MacroInputKind Kind, int Code)> held = [];
    private readonly object sync = new();
    private readonly Stopwatch clock = new();
    private Script? script;
    private Channel<(int Button, bool Down)>? events;
    private CancellationTokenSource? lifetime;
    private Task? startup;
    private Task? processing;
    private int? sleep;
    private volatile bool primaryEnabled = true;
    private bool disposed;
    private bool stopping;
    public const int MaximumSourceLength = LuaSourceFile.MaximumSourceCharacters;
    public bool IsRunning => processing is { IsCompleted: false };
    public event EventHandler<Exception>? Faulted;
    public event EventHandler<string>? Logged;

    public Task StartAsync(string source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new FormatException("Lua 脚本不能为空。");
        LuaSourceFile.ValidateSource(source);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (lifetime is not null) throw new InvalidOperationException("Lua 软件宏已启动，请先停止。");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var eventToken = cancellation.Token;
            lifetime = cancellation;
            clock.Restart(); primaryEnabled = true;
            events = Channel.CreateBounded<(int, bool)>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = false });
            // Publish the task under the same lock as its token, before it can be stopped.
            startup = Task.Run(() => InitializeAsync(source, cancellation, eventToken));
            return startup;
        }
    }
    private async Task InitializeAsync(string source, CancellationTokenSource cancellation, CancellationToken eventToken)
    {
        try
        {
            eventToken.ThrowIfCancellationRequested();
            script = CreateScript();
            var initial = script.LoadString(source, codeFriendlyName: "imported.lua");
            await RunFunctionAsync(initial, [], eventToken).ConfigureAwait(false);
            var handler = script.Globals.Get("OnEvent");
            if (handler.Type != DataType.Function) throw new FormatException("Lua 脚本需要定义 function OnEvent(event, arg)。");
            await RunFunctionAsync(handler, [DynValue.NewString("PROFILE_ACTIVATED"), DynValue.NewNumber(0), DynValue.NewString("mouse")], eventToken).ConfigureAwait(false);
            lock (sync)
            {
                eventToken.ThrowIfCancellationRequested();
                processing = Task.Run(() => ProcessAsync(handler, eventToken));
            }
        }
        catch
        {
            ReleaseAll();
            var disposeCancellation = false;
            lock (sync)
            {
                if (ReferenceEquals(lifetime, cancellation) && !stopping)
                { lifetime = null; events = null; script = null; disposeCancellation = true; }
            }
            if (disposeCancellation) cancellation.Dispose();
            throw;
        }
    }

    /// <param name="logitechButton">G HUB numbering: 1 left, 2 middle, 3 right, 4/5 side.</param>
    public void MouseButton(int logitechButton, bool down, bool dispatchEvent = true)
    {
        if (logitechButton is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(logitechButton));
        pressed[logitechButton] = down;
        var overflow = false;
        lock (sync)
        {
            if (!dispatchEvent || (logitechButton == 1 && !primaryEnabled) || lifetime is null || lifetime.IsCancellationRequested) return;
            if (events is not null && !events.Writer.TryWrite((logitechButton, down)))
            { lifetime.Cancel(); overflow = true; }
        }
        if (overflow) Faulted?.Invoke(this, new InvalidOperationException("Lua 事件积压超过 256 个，已停止并释放宏按键。"));
    }

    private async Task ProcessAsync(DynValue handler, CancellationToken ct)
    {
        try
        {
            await foreach (var e in events!.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await RunFunctionAsync(handler, [DynValue.NewString(e.Down ? "MOUSE_BUTTON_PRESSED" : "MOUSE_BUTTON_RELEASED"), DynValue.NewNumber(e.Button), DynValue.NewString("mouse")], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { lifetime?.Cancel(); Faulted?.Invoke(this, FormatError(error)); }
        finally { ReleaseAll(); }
    }

    private async Task RunFunctionAsync(DynValue function, DynValue[] arguments, CancellationToken ct)
    {
        var coroutine = script!.CreateCoroutine(function).Coroutine;
        coroutine.AutoYieldCounter = 1000;
        var slices = 0;
        var elapsed = Stopwatch.StartNew();
        while (coroutine.State != CoroutineState.Dead)
        {
            ct.ThrowIfCancellationRequested();
            sleep = null;
            coroutine.Resume(arguments); arguments = [];
            if (sleep is > 0)
            {
                slices = 0;
                await Task.Delay(sleep.Value, ct).ConfigureAwait(false);
            }
            else if (coroutine.State != CoroutineState.Dead)
            {
                if (++slices > 2000) throw new InvalidOperationException("Lua 连续执行超过 2,000,000 条指令而没有 Sleep，已中止。");
                await Task.Yield();
            }
            if (elapsed.Elapsed > TimeSpan.FromMinutes(10)) throw new InvalidOperationException("单次 Lua 事件执行超过 10 分钟，已停止。");
        }
    }

    private Script CreateScript()
    {
        var engine = new Script(CoreModules.Preset_HardSandbox);
        foreach (var name in new[] { "io", "os", "package", "debug", "clr", "luanet", "require", "dofile", "loadfile", "load", "loadstring", "collectgarbage" }) engine.Globals[name] = DynValue.Nil;
        void Api(string name, Func<CallbackArguments, DynValue> callback) => engine.Globals[name] = DynValue.NewCallback((_, args) => callback(args));
        Api("Sleep", args => { sleep = Integer(args, 0, 0, 600000); return DynValue.NewYieldReq([]); });
        Api("EnablePrimaryMouseButtonEvents", args => { primaryEnabled = args[0].CastToBool(); return DynValue.Nil; });
        Api("IsMouseButtonPressed", args => DynValue.NewBoolean(pressed.TryGetValue(Integer(args, 0, 1, 5), out var down) && down));
        Api("IsKeyLockOn", args => DynValue.NewBoolean(keyState?.Invoke(Text(args, 0).ToLowerInvariant()) ?? false));
        Api("IsModifierPressed", args => DynValue.NewBoolean(keyState?.Invoke(Text(args, 0).ToLowerInvariant()) ?? false));
        Api("GetRunningTime", _ => DynValue.NewNumber(clock.ElapsedMilliseconds));
        Api("PressMouseButton", args => { foreach (var arg in args.GetArray()) Set(MacroInputKind.MouseButton, LuaKeyNames.MouseButton(Number(arg, 1, 5)), true); return DynValue.Nil; });
        Api("ReleaseMouseButton", args => { foreach (var arg in args.GetArray()) Set(MacroInputKind.MouseButton, LuaKeyNames.MouseButton(Number(arg, 1, 5)), false); return DynValue.Nil; });
        Api("PressAndReleaseMouseButton", args => { foreach (var arg in args.GetArray()) Tap(MacroInputKind.MouseButton, LuaKeyNames.MouseButton(Number(arg, 1, 5))); return DynValue.Nil; });
        Api("PressKey", args => { foreach (var arg in args.GetArray()) foreach (var code in LuaKeyNames.KeyCodes(arg)) Set(MacroInputKind.Key, code, true); return DynValue.Nil; });
        Api("ReleaseKey", args => { foreach (var arg in args.GetArray().Reverse()) foreach (var code in LuaKeyNames.KeyCodes(arg).Reverse()) Set(MacroInputKind.Key, code, false); return DynValue.Nil; });
        Api("PressAndReleaseKey", args => { var added = args.GetArray().SelectMany(LuaKeyNames.KeyCodes).Distinct().Where(code => !held.Contains((MacroInputKind.Key, code))).ToArray(); foreach (var code in added) Set(MacroInputKind.Key, code, true); foreach (var code in added.Reverse()) Set(MacroInputKind.Key, code, false); return DynValue.Nil; });
        Api("MoveMouseRelative", args => { sink.Send(new(MacroInputKind.Move, Integer(args, 0, short.MinValue, short.MaxValue), Y: Integer(args, 1, short.MinValue, short.MaxValue))); return DynValue.Nil; });
        Api("MoveMouseWheel", args => { sink.Send(new(MacroInputKind.Wheel, Integer(args, 0, -127, 127))); return DynValue.Nil; });
        Api("OutputLogMessage", args =>
        {
            var text = Text(args, 0);
            if (args.Count > 1) text = engine.Call(engine.Globals.Get("string").Table.Get("format"), args.GetArray()).String;
            if (text.Length > 4096) text = text[..4096];
            Logged?.Invoke(this, text); return DynValue.Nil;
        });
        // Native string.rep would bypass the VM instruction quota while allocating an unbounded string.
        engine.Globals.Get("string").Table["rep"] = DynValue.NewCallback((_, args) =>
        {
            var value = Text(args, 0); var count = Integer(args, 1, 0, 65536);
            if ((long)value.Length * count > 1048576) throw new ScriptRuntimeException("string.rep 的结果不能超过 1 MiB。");
            return DynValue.NewString(string.Concat(Enumerable.Repeat(value, count)));
        });
        return engine;
    }

    private void Tap(MacroInputKind kind, int code)
    { if (held.Contains((kind, code))) return; Set(kind, code, true); Set(kind, code, false); }
    private void Set(MacroInputKind kind, int code, bool down)
    {
        lock (sync)
        {
            if (down ? !held.Add((kind, code)) : !held.Remove((kind, code))) return;
            try { sink.Send(new(kind, code, down)); }
            catch { if (down) held.Remove((kind, code)); throw; }
        }
    }
    private void ReleaseAll()
    {
        lock (sync) foreach (var input in held.ToArray())
        { try { Set(input.Kind, input.Code, false); } catch (Exception error) { Faulted?.Invoke(this, error); } }
    }
    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        Task[] pending;
        lock (sync)
        {
            cancellation = lifetime;
            if (cancellation is null) return;
            stopping = true;
            cancellation.Cancel(); events?.Writer.TryComplete();
            pending = new[] { startup, processing }.OfType<Task>().ToArray();
        }
        // Initialization may be in PROFILE_ACTIVATED/Sleep. Await it as well as the event worker.
        foreach (var task in pending)
        { try { await task.ConfigureAwait(false); } catch (Exception) { /* StartAsync observes initialization errors. */ } }
        lock (sync)
        {
            if (ReferenceEquals(lifetime, cancellation))
            {
                ReleaseAll();
                lifetime = null; processing = null; startup = null; script = null; events = null; pressed.Clear(); stopping = false;
            }
            cancellation.Dispose();
        }
    }
    public async ValueTask DisposeAsync() { lock (sync) { if (disposed) return; disposed = true; } await StopAsync().ConfigureAwait(false); }
    private static Exception FormatError(Exception error) => error is InterpreterException lua ? new InvalidOperationException("Lua：" + lua.DecoratedMessage, lua) : error;
    private static int Integer(CallbackArguments args, int index, int minimum, int maximum) => Number(args[index], minimum, maximum);
    private static int Number(DynValue value, int minimum, int maximum)
    { if (value.Type != DataType.Number || !double.IsFinite(value.Number) || value.Number != Math.Truncate(value.Number) || value.Number < minimum || value.Number > maximum) throw new ScriptRuntimeException($"参数需为 {minimum}–{maximum} 之间的整数。"); return (int)value.Number; }
    private static string Text(CallbackArguments args, int index) => args[index].Type == DataType.String ? args[index].String : throw new ScriptRuntimeException("参数需为字符串。");
}

internal static class LuaKeyNames
{
    public static int MouseButton(int logitech) => logitech switch { 2 => 3, 3 => 2, _ => logitech };
    public static string KeyName(string name) => name.ToLowerInvariant() switch
    {
        "lctrl" => "Ctrl", "rctrl" => "RightCtrl", "lshift" => "Shift", "rshift" => "RightShift",
        "lalt" => "Alt", "ralt" => "RightAlt", "lgui" or "lwin" => "Win", "rgui" or "rwin" => "RightWin",
        "return" => "Enter", "escape" => "Esc", "lbracket" => "LeftBracket", "rbracket" => "RightBracket",
        "apostrophe" => "Quote", "tilde" => "Grave", "pgup" => "PageUp", "pgdn" => "PageDown",
        _ => name,
    };
    public static int[] KeyCodes(DynValue value)
    {
        if (value.Type == DataType.Number)
        {
            try { return [LuaKeyboardCodes.ToHidUsage(value.Number)]; }
            catch (FormatException error) { throw new ScriptRuntimeException(error.Message); }
        }
        if (value.Type != DataType.String) throw new ScriptRuntimeException("Lua 键盘参数需要键名或 G HUB 数字扫描码。");
        var key = HidKeys.ParseChord(KeyName(value.String));
        var codes = Enumerable.Range(0, 8).Where(bit => (key.Modifier & (1 << bit)) != 0).Select(bit => 0xE0 + bit).ToList();
        if (key.Usage != 0) codes.Add(key.Usage);
        return codes.ToArray();
    }
}
