using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using LogiBoardToolBox.Core.Diagnostics;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    [ObservableProperty] public partial string DiagnosticsStatus { get; set; } = "详细日志已启用";
    [ObservableProperty] public partial bool ExportingDiagnostics { get; set; }
    public bool CanExportDiagnostics => !ExportingDiagnostics;
    partial void OnExportingDiagnosticsChanged(bool value) => OnPropertyChanged(nameof(CanExportDiagnostics));
    private string DiagnosticsDirectory => Path.Combine(dataDirectory, "Diagnostics");

    private void InitializeDiagnostics()
    {
        DiagnosticLog.Session = new(DiagnosticsDirectory);
        DiagnosticsStatus = DiagnosticLog.Session.LastError is null
            ? "详细日志已启用 · 包含设备、板载数据、宏与 Lua、通信及完整异常"
            : "日志目录无法写入 · 导出时会标明缺失";
    }

    // Capture on the UI thread: no live observable collection is enumerated by
    // the background ZIP writer, and no device is read or changed by exporting.
    internal JsonElement CaptureDiagnosticContext() => DiagnosticJson.ToElement(new
    {
        At = DateTimeOffset.Now, SessionId = DiagnosticLog.Session?.Id,
        DiagnosticLog.Session?.LastError, Utilities.UiVerification.IsRequested,
        Devices = Devices.ToArray(), SelectedDevice, SelectedModel = DeviceModel,
        Configuration = SelectedConfiguration?.Key, ProfileSector = SelectedProfile?.Sector,
        StatusTitle, Status, HasError, PersistenceStatus, IsBusy, AllowExperimental,
        DeviceSummary, ModeSummary, CapabilityNote, LastBackupPath,
        Sensor = sensor, PerformanceSummary, PerformanceNote,
        Snapshot = snapshot, SectorChecks = snapshot?.Sectors.OrderBy(s => s.Key).Select(s => new
        {
            Sector = s.Key, Length = s.Value.Length, RequiresCrc = !snapshot.Capabilities.IsRawMacroSector(s.Key), ValidCrc = SectorChecksum.IsValid(s.Value),
            Erased = SectorChecksum.IsErased(s.Value), StoredCrc = s.Value.Length >= 2 ? Convert.ToHexString(s.Value.AsSpan(s.Value.Length - 2)) : "",
            CalculatedCrc = s.Value.Length >= 2 ? SectorChecksum.Compute(s.Value.AsSpan(0, s.Value.Length - 2)).ToString("X4") : "", Raw = Convert.ToHexString(s.Value)
        }).ToArray(),
        ProfileDecode = DiagnosticProfiles(),
        Draft = new { ProfileName, ProfileEnabled, DefaultDpi, ShiftDpi, SelectedRate = SelectedRate?.Id,
            SelectedWirelessRate = SelectedWirelessRate?.Id, PowerSaveSeconds, PowerOffSeconds,
            DpiStages = DpiStages.Select(s => new { s.Index, s.Value, s.ValueY, s.Enabled, s.LinkAxes }).ToArray(),
            Bindings = AllBindings.Select(b => new { b.Index, b.GShift, b.Label, Original = Convert.ToHexString(b.Original),
                Action = b.SelectedAction.Id, b.Value, b.FriendlyName }).ToArray(),
            Lightsync = DiagnosticLighting(),
            PendingProfiles = drafts.ToDictionary(p => p.Key, p => p.Value),
            PendingLights = stagedLights.ToDictionary(p => p.Key, p => p.Value) },
        Macros = Macros.Select(DiagnosticMacro).ToArray(),
        SelectedMacro = SelectedMacro is null ? null : DiagnosticMacro(SelectedMacro),
        OnboardMacroLinks = onboardMacroLinks,
        ProfileRoutes = profileRoutes,
        Lua = new { LuaName, LuaSource, LuaExecuteLeft, LuaExecuteRight, LuaStatus, LuaLog, LuaRunning,
            LuaEditorText, LuaPageStatus, LuaConverting,
            Trigger = LuaBoardTrigger.Id, Repeat = LuaBoardRepeat.Id },
        Runtimes = new { HostMacrosRunning, LightsyncRunning, IsRecording },
        Timeline = Timeline.Select(s => new { Script = s.ToScript() }).ToArray(), MacroPreview, MacroExecutionTrace,
        Activity = Activity.ToArray()
    });

    private object DiagnosticProfiles()
    {
        try { return snapshot?.GetProfiles().Select(p => new { p.Sector, p.Name, p.Enabled, p.FromFactory, p.ProfileFormat,
            Bindings = Enumerable.Range(0, snapshot.Capabilities.BindingRecordCount).Select(i => new
            { ButtonIndex = i, Normal = Convert.ToHexString(p.GetBinding(i)), GShift = Convert.ToHexString(p.GetBinding(i, true)) }).ToArray() }).ToArray() ?? (object)Array.Empty<object>(); }
        catch (Exception error) { return new { Error = error.ToString() }; }
    }
    private object DiagnosticLighting()
    {
        try { return BuildLightsyncSettings(); }
        catch (Exception error) { return new { Error = error.ToString() }; }
    }
    private object DiagnosticMacro(MacroEditor macro)
    {
        string? bytes = null, error = null;
        try { bytes = Convert.ToHexString(macro.Compile(snapshot?.Capabilities).Bytes); }
        catch (Exception failure) { error = failure.ToString(); }
        return new { macro.Name, macro.Script, macro.HoldScript, macro.ReleaseScript, macro.Repeat,
            macro.UseStandardDelay, macro.StandardDelay, macro.ShowUpDown, macro.Color, FirmwareBytes = bytes, CompileError = error };
    }
    private void RecordDiagnosticContext(string reason, Exception? error = null)
    {
        try { DiagnosticLog.Record("workspace", reason, CaptureDiagnosticContext(), error); }
        catch (Exception failure) { DiagnosticLog.Record("diagnostic-error", "capture context failed", error: failure); }
    }

    public async Task<DiagnosticExportResult> ExportDiagnosticsAsync(string path, IReadOnlyList<string>? captureIssues = null)
    {
        if (ExportingDiagnostics) throw new InvalidOperationException("诊断包正在导出。");
        ExportingDiagnostics = true;
        try
        {
            RecordDiagnosticContext("export requested");
            var context = CaptureDiagnosticContext();
            var files = new List<DiagnosticFile>();
            var collectionIssues = captureIssues?.ToList() ?? [];
            var session = DiagnosticLog.Session;
            if (session is not null) files.AddRange(session.CaptureFiles());
            AddDirectory(DiagnosticsDirectory, "sessions", skipDirectory: session?.DirectoryPath);
            foreach (var file in Directory.Exists(dataDirectory) ? Directory.GetFiles(dataDirectory, "*.json") : [])
                files.Add(new(file, "attachments/settings/" + Path.GetFileName(file)));
            AddDirectory(BackupDirectory, "attachments/backups");
            // Include the framework's historical logs from both product spellings.
            if (!Utilities.UiVerification.IsRequested)
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach (var name in new[] { Core.Storage.RuntimeDataPaths.ProductName, Core.Storage.RuntimeDataPaths.LegacyProductName })
                    AddDirectory(Path.Combine(local, name, "CrossVersion", "Log"), "attachments/history/" + name);
            }
            if (session?.LastError is { } logError) collectionIssues.Add("日志写入失败: " + logError);
            var result = await Task.Run(() => DiagnosticPackage.ExportAsync(path, context, files, collectionIssues: collectionIssues));
            DiagnosticsStatus = result.Issues.Count == 0 && session?.LastError is null
                ? $"诊断包已导出 · {result.Files} 个文件 · {result.Path}"
                : $"诊断包已导出，部分记录缺失，请查看 manifest.json · {result.Path}";
            DiagnosticLog.Record("diagnostics", "export completed", result);
            return result;

            void AddDirectory(string directory, string prefix, string? skipDirectory = null)
            {
                if (directory == skipDirectory || !Directory.Exists(directory)) return;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    { collectionIssues.Add(prefix + ": 不跟随目录链接。"); return; }
                    foreach (var file in Directory.GetFiles(directory))
                        files.Add(new(file, prefix + "/" + Path.GetFileName(file), new FileInfo(file).Length));
                    foreach (var child in Directory.GetDirectories(directory)) AddDirectory(child, prefix + "/" + Path.GetFileName(child), skipDirectory);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    collectionIssues.Add(prefix + ": " + error.Message);
                    DiagnosticLog.Record("diagnostic-error", "attachment enumeration failed", new { directory }, error);
                }
            }
        }
        finally { ExportingDiagnostics = false; }
    }
}
