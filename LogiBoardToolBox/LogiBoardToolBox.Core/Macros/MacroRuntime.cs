using System.Buffers.Binary;

namespace LogiBoardToolBox.Core.Macros;

public sealed record MacroProgram(MacroRepeat Repeat, IReadOnlyList<byte[]> Press, IReadOnlyList<byte[]> Hold, IReadOnlyList<byte[]> Release)
{
    public int InstructionCount => Press.Count + Hold.Count + Release.Count;
    public CompiledMacro ToOnboard()
    {
        if (Repeat == MacroRepeat.Sequence && Hold.Count != 0)
            throw new NotSupportedException("该序列包含独立的按住循环。格式 1 没有片段跳转指令，无法同时保留点击一次和按住循环；请使用软件模式执行，或清空按住片段后写入点击/松开序列。");
        var output = Press.Select(b => b.ToArray()).ToList();
        if (Repeat == MacroRepeat.Sequence) { output.Add([1]); output.AddRange(Release); }
        else if (Repeat != MacroRepeat.Once) output.Add([(byte)(Repeat == MacroRepeat.WhileHeld ? 2 : 3)]);
        output.Add([0xFF]);
        return new(output, Repeat);
    }
}

public enum MacroInputKind { Key, MouseButton, Consumer, Wheel, Pan, Move }
public sealed record MacroInput(MacroInputKind Kind, int Code, bool Down = false, int Y = 0);
public interface IMacroInputSink { void Send(MacroInput input); }

/// <summary>Per-trigger state machines. Input ownership is reference counted across simultaneous macros.</summary>
public sealed class MacroRuntime(IMacroInputSink sink) : IAsyncDisposable
{
    private sealed class Run(MacroProgram program)
    {
        public MacroProgram Program { get; } = program;
        public CancellationTokenSource Cancel { get; } = new();
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource HoldCancel { get; } = new();
        public HashSet<(MacroInputKind Kind, int Code)> Held { get; } = [];
        public Task Task { get; set; } = Task.CompletedTask;
    }
    private readonly object sync = new();
    private readonly Dictionary<string, Run> runs = [];
    private readonly Dictionary<(MacroInputKind Kind, int Code), int> owners = [];
    private bool stopped;
    public event EventHandler<Exception>? Faulted;
    public Task Press(string trigger, MacroProgram program)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            if (runs.TryGetValue(trigger, out var existing))
            {
                if (program.Repeat == MacroRepeat.Toggle) existing.Cancel.Cancel();
                return existing.Task;
            }
            var run = new Run(program); runs.Add(trigger, run);
            run.Task = Task.Run(() => ExecuteAsync(trigger, run));
            return run.Task;
        }
    }
    public void Release(string trigger)
    {
        lock (sync) if (runs.TryGetValue(trigger, out var run))
        {
            run.Released.TrySetResult(); run.HoldCancel.Cancel();
            if (run.Program.Repeat == MacroRepeat.WhileHeld) run.Cancel.Cancel();
        }
    }
    public async Task StopAllAsync()
    {
        Task[] tasks;
        lock (sync) { foreach (var run in runs.Values) run.Cancel.Cancel(); tasks = runs.Values.Select(r => r.Task).ToArray(); }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    private async Task ExecuteAsync(string trigger, Run run)
    {
        try
        {
            var p = run.Program;
            await PlayAsync(p.Press, run, run.Cancel.Token).ConfigureAwait(false);
            if (p.Repeat == MacroRepeat.Sequence)
            {
                var baseline = run.Held.ToHashSet();
                using var hold = CancellationTokenSource.CreateLinkedTokenSource(run.Cancel.Token, run.HoldCancel.Token);
                try
                {
                    if (p.Hold.Count == 0) await run.Released.Task.WaitAsync(run.Cancel.Token).ConfigureAwait(false);
                    else while (!hold.IsCancellationRequested)
                    {
                        await PlayAsync(p.Hold, run, hold.Token).ConfigureAwait(false);
                        await Task.Delay(1, hold.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!run.Cancel.IsCancellationRequested) { }
                foreach (var input in run.Held.Except(baseline).ToArray()) SetKey(run, input.Kind, input.Code, false);
                // Restore press-held keys if a hold iteration was interrupted between up/down.
                foreach (var input in baseline.Except(run.Held).ToArray()) SetKey(run, input.Kind, input.Code, true);
                await PlayAsync(p.Release, run, run.Cancel.Token).ConfigureAwait(false);
            }
            else if (p.Repeat is MacroRepeat.WhileHeld or MacroRepeat.Toggle)
                while (!run.Cancel.IsCancellationRequested)
                {
                    await Task.Delay(1, run.Cancel.Token).ConfigureAwait(false);
                    await PlayAsync(p.Press, run, run.Cancel.Token).ConfigureAwait(false);
                }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Faulted?.Invoke(this, ex); }
        finally
        {
            foreach (var key in run.Held.ToArray())
            { try { SetKey(run, key.Kind, key.Code, false); } catch (Exception ex) { Faulted?.Invoke(this, ex); } }
            lock (sync) { if (runs.TryGetValue(trigger, out var current) && current == run) runs.Remove(trigger); }
            run.Cancel.Dispose(); run.HoldCancel.Dispose();
        }
    }
    private async Task PlayAsync(IReadOnlyList<byte[]> instructions, Run run, CancellationToken ct)
    {
        foreach (var b in instructions)
        {
            ct.ThrowIfCancellationRequested();
            switch (b[0])
            {
                case 1: await run.Released.Task.WaitAsync(ct).ConfigureAwait(false); break;
                case 0x40: await Task.Delay(BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1)), ct).ConfigureAwait(false); break;
                case 0x43 or 0x44:
                    var down = b[0] == 0x43;
                    if (down) for (var i = 0; i < 8; i++) if ((b[1] & (1 << i)) != 0) SetKey(run, MacroInputKind.Key, 0xE0 + i, true);
                    if (b[2] != 0) SetKey(run, MacroInputKind.Key, b[2], down);
                    if (!down) for (var i = 7; i >= 0; i--) if ((b[1] & (1 << i)) != 0) SetKey(run, MacroInputKind.Key, 0xE0 + i, false);
                    break;
                case 0x41 or 0x42:
                    var mask = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1));
                    for (var i = 0; i < 16; i++) if ((mask & (1 << i)) != 0) SetKey(run, MacroInputKind.MouseButton, i + 1, b[0] == 0x41);
                    break;
                case 0x45 or 0x46: SetKey(run, MacroInputKind.Consumer, BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(1)), b[0] == 0x45); break;
                case 0x20 or 0x21: sink.Send(new(b[0] == 0x20 ? MacroInputKind.Wheel : MacroInputKind.Pan, unchecked((sbyte)b[1]))); break;
                case 0x61: sink.Send(new(MacroInputKind.Move, BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(1)), Y: BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(3)))); break;
                default: throw new InvalidDataException($"软件宏不支持指令 0x{b[0]:X2}。");
            }
        }
    }
    private void SetKey(Run run, MacroInputKind kind, int code, bool down)
    {
        lock (sync)
        {
            var key = (kind, code);
            if (down)
            {
                if (!run.Held.Add(key)) return;
                owners.TryGetValue(key, out var count);
                try { if (count == 0) sink.Send(new(kind, code, true)); owners[key] = count + 1; }
                catch { run.Held.Remove(key); throw; }
            }
            else if (run.Held.Remove(key))
            {
                var count = owners[key] - 1;
                if (count == 0) { owners.Remove(key); sink.Send(new(kind, code, false)); }
                else owners[key] = count;
            }
        }
    }
    public async ValueTask DisposeAsync() { lock (sync) stopped = true; await StopAllAsync().ConfigureAwait(false); }
}
