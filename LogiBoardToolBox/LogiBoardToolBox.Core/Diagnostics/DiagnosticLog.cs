using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LogiBoardToolBox.Core.Diagnostics;

/// <summary>Only application diagnostics are recorded; logging must never break device I/O.</summary>
public static class DiagnosticLog
{
    public static DiagnosticSession? Session { get; set; }
    public static void Record(string category, string message, object? data = null, Exception? error = null) =>
        Session?.Record(category, message, data, error);
}

public sealed class DiagnosticSession
{
    private readonly object sync = new();
    private long sequence;
    private int part;
    private const long PartSize = 8 * 1024 * 1024;
    public string DirectoryPath { get; }
    public string Id { get; } = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    public string? LastError { get; private set; }
    private string CurrentPath => Path.Combine(DirectoryPath, $"events-{part:D4}.jsonl");

    public DiagnosticSession(string root)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(root), Id);
        Record("session", "start", EnvironmentInfo());
    }

    public static object EnvironmentInfo() => new
    {
        At = DateTimeOffset.Now, TimeZone = TimeZoneInfo.Local.Id,
        OS = RuntimeInformation.OSDescription, Framework = RuntimeInformation.FrameworkDescription,
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        OSArchitecture = RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessId,
        Executable = Environment.ProcessPath, DynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
        Assemblies = new[] { Assembly.GetEntryAssembly(), typeof(DiagnosticSession).Assembly }
            .Where(a => a is not null).Distinct().Select(a => AssemblyInfo(a!)).ToArray()
    };

    private static object AssemblyInfo(Assembly assembly)
    {
        var location = RuntimeFeature.IsDynamicCodeSupported ? assembly.Location :
            assembly == Assembly.GetEntryAssembly() ? Environment.ProcessPath ?? "" : "";
        string? hash = null;
        try { if (File.Exists(location)) hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new { Name = assembly.GetName().Name, Version = assembly.GetName().Version?.ToString(),
            Information = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Path = location, Sha256 = hash };
    }

    public void Record(string category, string message, object? data = null, Exception? error = null)
    {
        lock (sync)
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(CurrentPath) && new FileInfo(CurrentPath).Length >= PartSize) part++;
                var line = DiagnosticJson.Serialize(new { Sequence = ++sequence, At = DateTimeOffset.Now,
                    Thread = Environment.CurrentManagedThreadId, Category = category, Message = message,
                    Data = data, Exception = error?.ToString(), ExceptionData = error?.Data.Keys.Cast<object>()
                        .ToDictionary(k => k.ToString() ?? "", k => error.Data[k]?.ToString()) });
                File.AppendAllText(CurrentPath, line + "\n", new UTF8Encoding(false));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException or JsonException
                or ArgumentException or InvalidOperationException or TargetInvocationException)
            { LastError = failure.ToString(); }
        }
    }

    /// <summary>Freezes lengths between complete records, even while HID traces keep arriving.</summary>
    public IReadOnlyList<DiagnosticFile> CaptureFiles()
    {
        lock (sync)
        {
            return Directory.Exists(DirectoryPath)
                ? Directory.GetFiles(DirectoryPath).Order().Select(p => new DiagnosticFile(p,
                    $"sessions/{Id}/{Path.GetFileName(p)}", new FileInfo(p).Length)).ToArray() : [];
        }
    }
}

public sealed record DiagnosticFile(string Path, string EntryName, long? Length = null);
