using System.Drawing;
using System.Runtime.InteropServices;
using LogiBoardToolBox.Core.Lighting;
using NAudio.Wave;

namespace LogiBoardToolBox.Services;

/// <summary>Explicitly enabled output-loopback meter. Samples are never saved to disk.</summary>
public sealed class SystemAudioMeter : IDisposable
{
    private readonly WasapiLoopbackCapture capture = new();
    private double level;
    private long lastSample;
    private long samples;
    public long SamplesReceived => Interlocked.Read(ref samples);
    public WaveFormat Format => capture.WaveFormat;
    public double Level => Environment.TickCount64 - Interlocked.Read(ref lastSample) > 250 ? 0 : Volatile.Read(ref level);
    public event EventHandler<Exception>? Faulted;
    public SystemAudioMeter()
    {
        capture.DataAvailable += (_, e) =>
        {
            var format = capture.WaveFormat;
            var bytes = format.BitsPerSample / 8;
            var floatFormat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
            if (bytes is not (2 or 3 or 4)) return;
            double sum = 0; var count = e.BytesRecorded / bytes;
            for (var i = 0; i < count; i++)
            {
                var offset = i * bytes;
                double value = floatFormat && bytes == 4 ? BitConverter.ToSingle(e.Buffer, offset) : bytes == 2 ? BitConverter.ToInt16(e.Buffer, offset) / 32768.0 : bytes == 3 ? ((e.Buffer[offset] | e.Buffer[offset + 1] << 8 | e.Buffer[offset + 2] << 16) << 8 >> 8) / 8388608.0 : BitConverter.ToInt32(e.Buffer, offset) / 2147483648.0;
                if (double.IsFinite(value)) sum += value * value;
            }
            Volatile.Write(ref level, count > 0 ? Math.Sqrt(sum / count) : 0); Interlocked.Exchange(ref lastSample, Environment.TickCount64);
            Interlocked.Add(ref samples, count);
        };
        capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Faulted?.Invoke(this, e.Exception); };
        capture.StartRecording();
    }
    public void Dispose() { capture.StopRecording(); capture.Dispose(); }
}

public sealed class ScreenSampler
{
    private readonly Rectangle bounds;
    public ScreenSampler(int monitorIndex)
    {
        var monitors = GetMonitors();
        if (monitorIndex < 0 || monitorIndex >= monitors.Count) throw new ArgumentOutOfRangeException(nameof(monitorIndex), "所选显示器已断开，请重新选择。");
        bounds = monitors[monitorIndex];
    }
    public Rgb[] Sample(IReadOnlyList<SampleRegion> regions)
    {
        // Sample each region on a fixed 8×8 grid, without retaining full-screen captures.
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) throw new InvalidOperationException("无法读取所选屏幕。");
        try
        {
            return regions.Select(r =>
            {
                r.Validate(); long red = 0, green = 0, blue = 0; var count = 0;
                for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
                {
                    var px = bounds.X + Math.Clamp((int)((r.X + r.Width * (x + .5) / 8) * bounds.Width), 0, bounds.Width - 1);
                    var py = bounds.Y + Math.Clamp((int)((r.Y + r.Height * (y + .5) / 8) * bounds.Height), 0, bounds.Height - 1);
                    var color = GetPixel(dc, px, py); if (color == uint.MaxValue) continue;
                    red += color & 255; green += color >> 8 & 255; blue += color >> 16 & 255; count++;
                }
                return count == 0 ? default : new Rgb((byte)(red / count), (byte)(green / count), (byte)(blue / count));
            }).ToArray();
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    public static IReadOnlyList<Rectangle> GetMonitors()
    {
        var monitors = new List<Rectangle>();
        MonitorProc proc = (IntPtr h, IntPtr dc, ref Rect rect, IntPtr data) => { monitors.Add(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)); return true; };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero); return monitors;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool MonitorProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
}
