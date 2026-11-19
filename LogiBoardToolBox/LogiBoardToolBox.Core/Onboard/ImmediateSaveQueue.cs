namespace LogiBoardToolBox.Core.Onboard;

public sealed class ImmediateSaveException(Exception cause) : IOException(cause.Message, cause);

/// <summary>Coalesces an editing gesture, serializes commits, and never cancels a flash write midway.</summary>
public sealed class ImmediateSaveQueue(Func<Task> save, TimeSpan? quietPeriod = null) : IAsyncDisposable
{
    private readonly TimeSpan delay = quietPeriod ?? TimeSpan.FromMilliseconds(200);
    private long requested, completed;
    private Task? worker;
    private bool disposed;
    private readonly object sync = new();
    private Exception? failure;
    public event EventHandler<Exception>? Failed;
    public bool Pending { get { lock (sync) return requested != completed; } }
    public void Request()
    {
        lock (sync)
        {
            if (disposed) return;
            requested++;
            if (worker is null || worker.IsCompleted) worker = RunAsync();
        }
    }
    private async Task RunAsync()
    {
        await Task.Yield();
        while (true)
        {
            long version;
            lock (sync) { if (disposed || completed == requested) { worker = null; return; } version = requested; }
            await Task.Delay(delay);
            lock (sync) if (version != requested) continue;
            try { await save(); lock (sync) { completed = version; failure = null; } }
            catch (Exception error)
            {
                bool retry;
                lock (sync) { completed = version; failure = error; retry = requested != version; if (!retry) worker = null; }
                Failed?.Invoke(this, error);
                if (!retry) return;
            }
        }
    }
    public async Task FlushAsync()
    {
        while (true)
        {
            Task? current; Exception? error;
            lock (sync) { current = worker; error = failure; }
            if (current is null) { if (error is not null) throw new ImmediateSaveException(error); return; }
            await current;
        }
    }
    public async ValueTask DisposeAsync() { await FlushAsync(); disposed = true; }
}
