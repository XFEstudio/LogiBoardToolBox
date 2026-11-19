using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Windows;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    public const string ExampleLua = "EnablePrimaryMouseButtonEvents(true)\n\nfunction OnEvent(event, arg)\n    if event == \"MOUSE_BUTTON_PRESSED\" and arg == 1 then\n        PressMouseButton(1)\n        Sleep(50)\n        ReleaseMouseButton(1)\n        Sleep(100)\n    end\nend";
    private WindowsLuaSession? luaSession;
    private LuaEditorDocument luaDocument = new(ExampleLua);
    private bool loadingLuaPage, committingLuaPage, luaPageDirty;
    private int luaPage, luaRevision;
    [ObservableProperty] public partial string LuaSource { get; set; } = ExampleLua;
    [ObservableProperty] public partial string LuaEditorText { get; set; } = ExampleLua;
    [ObservableProperty] public partial string LuaPageStatus { get; set; } = "源码第 1 / 1 段";
    [ObservableProperty] public partial bool LuaConverting { get; set; }
    public bool HasPreviousLuaPage => luaPage > 0;
    public bool HasNextLuaPage => luaPage + 1 < luaDocument.PageCount;
    [ObservableProperty] public partial string LuaName { get; set; } = "Lua 宏";
    [ObservableProperty] public partial bool LuaExecuteLeft { get; set; } = true;
    [ObservableProperty] public partial bool LuaExecuteRight { get; set; } = true;
    [ObservableProperty] public partial bool LuaRunning { get; set; }
    [ObservableProperty] public partial string LuaStatus { get; set; } = "脚本未启用。导入或粘贴 Lua，然后明确启用。";
    [ObservableProperty] public partial string LuaLog { get; set; } = "";
    [ObservableProperty] public partial EditorChoice LuaBoardTrigger { get; set; } = new("1", "左键事件（arg = 1）");
    [ObservableProperty] public partial EditorChoice LuaBoardRepeat { get; set; } = MacroEditor.RepeatChoices[0];
    public List<EditorChoice> LuaBoardTriggers { get; } = [new("1", "左键事件（arg = 1）"), new("3", "右键事件（arg = 3）"), new("2", "中键事件（arg = 2）"), new("4", "后退侧键（arg = 4）"), new("5", "前进侧键（arg = 5）")];
    public List<EditorChoice> LuaBoardRepeats { get; } = MacroEditor.RepeatChoices;
    public bool CanEditLua => !LuaRunning && !LuaConverting;
    public event EventHandler? LuaMacroCreated;
    partial void OnLuaRunningChanged(bool value) { OnPropertyChanged(nameof(CanEditLua)); UpdateCapabilities(); }
    partial void OnLuaConvertingChanged(bool value) => OnPropertyChanged(nameof(CanEditLua));
    partial void OnLuaSourceChanging(string value) => LuaSourceFile.ValidateSource(value);
    partial void OnLuaSourceChanged(string value)
    {
        luaRevision++;
        if (!committingLuaPage) { luaDocument = new(value); luaPage = 0; LoadLuaPage(); }
        InvalidateLuaConversion();
    }
    partial void OnLuaEditorTextChanged(string value)
    {
        if (loadingLuaPage) return;
        luaPageDirty = true; luaRevision++; InvalidateLuaConversion();
    }
    private void LoadLuaPage()
    {
        loadingLuaPage = true;
        try
        {
            luaPage = Math.Clamp(luaPage, 0, luaDocument.PageCount - 1);
            LuaEditorText = luaDocument.ReadPage(luaPage); luaPageDirty = false;
            LuaPageStatus = $"源码第 {luaPage + 1} / {luaDocument.PageCount} 段 · 全文 {luaDocument.Source.Length:N0} 字符";
            OnPropertyChanged(nameof(HasPreviousLuaPage)); OnPropertyChanged(nameof(HasNextLuaPage));
        }
        finally { loadingLuaPage = false; }
    }
    public void CommitLuaEditor()
    {
        if (!luaPageDirty) return;
        luaDocument.ReplacePage(luaPage, LuaEditorText);
        committingLuaPage = true;
        try { LuaSource = luaDocument.Source; }
        finally { committingLuaPage = false; }
        LoadLuaPage();
    }
    [RelayCommand] private void PreviousLuaPage() => ChangeLuaPage(-1);
    [RelayCommand] private void NextLuaPage() => ChangeLuaPage(1);
    private void ChangeLuaPage(int delta)
    {
        if (!CanEditLua) return;
        try { CommitLuaEditor(); luaPage += delta; LoadLuaPage(); }
        catch (Exception error) { LuaStatus = error.Message; }
    }
    partial void OnLuaBoardTriggerChanged(EditorChoice value) { luaRevision++; InvalidateLuaConversion(); }
    partial void OnLuaBoardRepeatChanged(EditorChoice value) { luaRevision++; InvalidateLuaConversion(); }
    private void InvalidateLuaConversion()
    {
        if (!LuaRunning) LuaStatus = "脚本或转换选项已修改，请重新检查板载转换。";
    }
    private sealed record LuaProfile(string Name, string Source, bool ExecuteLeft, bool ExecuteRight);
    private void ReadLuaSettings()
    {
        try
        {
            var path = Path.Combine(dataDirectory, "lua-profile.json");
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJsonContext.Default.LuaProfile);
            if (saved is null) return;
            LuaSourceFile.ValidateSource(saved.Source);
            LuaName = saved.Name; LuaSource = saved.Source; LuaExecuteLeft = saved.ExecuteLeft; LuaExecuteRight = saved.ExecuteRight;
        }
        catch (Exception error) { Log("Lua 配置读取失败：" + error.Message); }
        LuaBoardTrigger = LuaBoardTriggers[0];
    }
    [RelayCommand] private async Task SaveLuaAsync() => await RunAsync(SaveLuaCoreAsync);
    private async Task SaveLuaCoreAsync()
    {
        CommitLuaEditor();
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "lua-profile.json"), JsonSerializer.Serialize(new LuaProfile(LuaName, LuaSource, LuaExecuteLeft, LuaExecuteRight), SettingsJsonContext.Default.LuaProfile));
        LuaStatus = "Lua 源码及左右键设置已保存；软件启动时保持关闭。";
    }
    [RelayCommand] private async Task StartLuaAsync() => await RunAsync(async () =>
    {
        CommitLuaEditor();
        if (Utilities.UiVerification.IsRequested) throw new InvalidOperationException("后台验收只使用模拟输入，不启动全局 Lua。");
        await StopHostMacrosCoreAsync();
        await StopLuaCoreAsync();
        var session = new WindowsLuaSession(LuaExecuteLeft, LuaExecuteRight);
        session.Faulted += SessionFaulted;
        session.Logged += (_, line) =>
        {
            Core.Diagnostics.DiagnosticLog.Record("lua-output", line);
            ui?.TryEnqueue(() => LuaLog = (LuaLog + line)[Math.Max(0, LuaLog.Length + line.Length - 8000)..]);
        };
        luaSession = session; LuaRunning = true;
        try { await session.StartAsync(LuaSource); if (!ReferenceEquals(luaSession, session)) return; await SaveLuaCoreAsync(); }
        catch (OperationCanceledException) { if (ReferenceEquals(luaSession, session)) await StopLuaCoreAsync(); return; }
        catch { if (ReferenceEquals(luaSession, session)) await StopLuaCoreAsync(); else await session.DisposeAsync(); throw; }
        LuaStatus = "软件 Lua 已启用 · 左键 " + (LuaExecuteLeft ? "执行" : "不执行") + " · 右键 " + (LuaExecuteRight ? "执行" : "不执行");
    });
    [RelayCommand] private async Task StopLuaAsync()
    {
        try { await StopLuaCoreAsync(); }
        catch (Exception error) { ShowError(error); }
    }
    private async Task StopLuaCoreAsync()
    {
        var session = luaSession; luaSession = null;
        try { if (session is not null) await session.DisposeAsync(); }
        finally { LuaRunning = false; if (session is not null) LuaStatus = "Lua 已关闭，所持按键已释放。"; }
    }
    [RelayCommand(IncludeCancelCommand = true)] private async Task ConvertLuaToBoardAsync(CancellationToken cancellationToken)
    {
        if (LuaRunning) { LuaStatus = "请先关闭正在执行的 Lua 脚本。"; return; }
        try
        {
            CommitLuaEditor();
            RecordDiagnosticContext("Lua conversion requested");
            var trigger = int.Parse(LuaBoardTrigger.Id);
            var repeat = (MacroRepeat)int.Parse(LuaBoardRepeat.Id);
            var source = LuaSource; var revision = luaRevision; var captured = snapshot;
            var identity = snapshotDeviceIdentity; var profileSector = SelectedProfile?.Sector;
            var target = trigger switch
            {
                1 => StandardBindings.FirstOrDefault(b => b.Index == 0),
                3 => StandardBindings.FirstOrDefault(b => b.Index == 1),
                2 => StandardBindings.FirstOrDefault(b => b.Index == 2),
                _ => StandardBindings.FirstOrDefault(b => b.SelectedAction.Id == "mouse" && b.Value == trigger.ToString())
            };
            var targetIndex = target?.Index;
            LuaConverting = true; LuaStatus = "正在后台转换 Lua 并检查板载占用…可取消。";
            var result = await Task.Run(() =>
            {
                var converted = LuaMacroImport.Convert(source, trigger, repeat, cancellationToken);
                var bytecode = converted.CompileProgram().ToOnboard(captured?.Capabilities);
                cancellationToken.ThrowIfCancellationRequested();
                MacroAllocation? allocation = null;
                if (captured?.Capabilities.CanWriteMacros == true && profileSector is not null && targetIndex is int at)
                    allocation = MacroAllocator.Allocate(captured, [new(at, false, bytecode)], profileSector);
                cancellationToken.ThrowIfCancellationRequested();
                return (Conversion: converted, Compiled: bytecode, Allocation: allocation);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != luaRevision || identity != snapshotDeviceIdentity || profileSector != SelectedProfile?.Sector || !ReferenceEquals(captured, snapshot))
            { LuaStatus = "脚本、设备或配置已变化，本次转换结果未应用；请重新转换。"; return; }
            var conversion = result.Conversion; var compiled = result.Compiled;
            var script = conversion.Script;
            var name = LuaName; var i = 2; while (Macros.Any(m => m.Name == name)) name = LuaName + " " + i++;
            var macro = new MacroEditor { Name = name, Script = script, HoldScript = conversion.HoldScript, ReleaseScript = conversion.ReleaseScript, UseStandardDelay = false, SelectedRepeat = MacroEditor.RepeatChoices[(int)conversion.Repeat] };
            Macros.Add(macro); SelectedMacro = macro;
            // Lua event numbers are not G-key numbers. Never guess a side-key record.
            MacroTarget = target;
            var storage = result.Allocation is { } allocationResult ? $"本次预计更新 {allocationResult.Sectors.Count} 个宏区扇区；最终绑定时会重新检查空间。\n" : "";
            MacroPreview = $"Lua 已转换为 {compiled.Length:N0} 字节板载指令，Sleep 延迟已保留。\n{storage}{conversion.Notice}\n请选择目标按键并绑定；板载配置将自动备份、写入和读回。";
            LuaStatus = MacroPreview; RefreshAssignmentPalette(); LuaMacroCreated?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { LuaStatus = "Lua 转换已取消，原文保留，未生成或写入新宏。"; }
        catch (Exception error) { LuaStatus = "无法转换为板载宏：" + error.Message; RecordDiagnosticContext("Lua conversion failed", error); }
        finally { LuaConverting = false; }
    }
}
