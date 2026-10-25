namespace LogiBoardToolBox.Core.HidPlusPlus;

/// <summary>One vendor HID collection. Mouse/keyboard input collections are never opened.</summary>
public interface IHidTransport : IDisposable
{
    int InputReportLength { get; }
    int OutputReportLength { get; }
    Task WriteAsync(byte[] report, CancellationToken cancellationToken);
    Task<byte[]> ReadAsync(CancellationToken cancellationToken);
}
