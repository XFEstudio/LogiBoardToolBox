using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LogiBoardToolBox.Core.Diagnostics;
using LogiBoardToolBox.Core.Macros;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    private sealed record OnboardMacroLink(ushort Profile, int Button, bool GShift, string Name, string Bytes);
    private sealed record OnboardMacroLinkFile(string DeviceIdentity, OnboardMacroLink[] Bindings);
    private OnboardMacroLink[] onboardMacroLinks = [];
    private string OnboardMacroLinksPath() => Path.Combine(dataDirectory, "onboard-macros-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot!.DeviceIdentity)))[..16] + ".json");

    private async Task LoadOnboardMacroLinksAsync()
    {
        onboardMacroLinks = [];
        if (snapshot is null) return;
        var path = OnboardMacroLinksPath();
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("板载宏关联文件过大。");
            var saved = JsonSerializer.Deserialize<OnboardMacroLinkFile>(await File.ReadAllTextAsync(path));
            if (saved?.DeviceIdentity != snapshot.DeviceIdentity || saved.Bindings is null)
                throw new InvalidDataException("板载宏关联不属于当前设备。");
            onboardMacroLinks = saved.Bindings.Where(b => b.Name is not null && b.Bytes is not null).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { DiagnosticLog.Record("macro-links", "load failed; native bindings preserved", error: error); }
    }

    private async Task SaveOnboardMacroLinksAsync()
    {
        if (snapshot is null || SelectedProfile is null) return;
        var links = onboardMacroLinks.Where(l => l.Profile != SelectedProfile.Sector).ToList();
        foreach (var row in AllBindings)
        {
            if (row.SelectedAction.Id != "macro" || (row.Original[0] >> 4) is not (0 or 3)) continue;
            var bytes = MacroAllocator.ReadMacroBytes(snapshot, new(ButtonBinding.MacroSector(row.Original), ButtonBinding.MacroOffset(row.Original)));
            links.Add(new(SelectedProfile.Sector, row.Index, row.GShift, row.Value, Convert.ToHexString(bytes)));
        }
        onboardMacroLinks = links.ToArray();
        var path = OnboardMacroLinksPath();
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new OnboardMacroLinkFile(snapshot.DeviceIdentity, onboardMacroLinks), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
            DiagnosticLog.Record("macro-links", "saved", new { SelectedProfile.Sector, Count = links.Count });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { DiagnosticLog.Record("macro-links", "metadata save failed; onboard readback succeeded", error: error); Log("板载写入成功，但宏名称关联保存失败：" + error.Message); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { DiagnosticLog.Record("macro-links", "temporary metadata cleanup failed", error: error); }
        }
    }

    private void MatchStoredMacros(bool forSoftware = false)
    {
        if (snapshot is null || SelectedProfile is null) return;
        var known = new List<(string Name, byte[] Bytes, bool LegacyToggle)>();
        foreach (var macro in Macros)
        {
            try
            {
                var program = macro.CompileProgram();
                // Migration recognition only. Never send this old, uncancellable
                // representation to the allocator or treat it as a firmware toggle.
                var legacy = program.Repeat == MacroRepeat.Toggle;
                var bytes = legacy ? program.Press.SelectMany(b => b).Concat(new byte[] { 3, 0xFF }).ToArray() : program.ToOnboard().Bytes;
                known.Add((macro.Name, bytes, legacy));
            }
            catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException or OverflowException) { }
        }
        foreach (var row in AllBindings.Where(r => (r.Original[0] >> 4) is 0 or 3))
        {
            try
            {
                var bytes = MacroAllocator.ReadMacroBytes(snapshot, new(ButtonBinding.MacroSector(row.Original), ButtonBinding.MacroOffset(row.Original)));
                var hex = Convert.ToHexString(bytes);
                var saved = onboardMacroLinks.Where(l => l.Profile == SelectedProfile.Sector && l.Button == row.Index && l.GShift == row.GShift && l.Bytes == hex).ToArray();
                var matches = saved.Length == 1 && Macros.Count(m => m.Name == saved[0].Name) == 1
                    ? known.Where(m => m.Name == saved[0].Name).ToArray() : known.Where(m => m.Bytes.SequenceEqual(bytes)).ToArray();
                if (matches.Length != 1 || Macros.Count(m => m.Name == matches[0].Name) != 1) continue;
                var match = matches[0];
                if (match.LegacyToggle && !forSoftware && (!snapshot.Capabilities.SupportsNativeToggle || !ButtonBinding.IsNativeToggle(row.Original)))
                {
                    row.FriendlyName = match.Name + (match.Bytes.SequenceEqual(bytes) ? "（旧板载循环）" : "（板载版本）");
                    DiagnosticLog.Record("macro-links", "legacy continuous loop is not a same-button toggle", new { row.Index, row.GShift, match.Name });
                    continue;
                }
                row.SelectedAction = ButtonBindingEditor.ActionChoices.First(c => c.Id == "macro");
                row.Value = match.Name; row.FriendlyName = match.Name;
                DiagnosticLog.Record("macro-links", "restored", new { SelectedProfile.Sector, row.Index, row.GShift, match.Name, UsedSavedLink = saved.Length == 1 });
            }
            catch (Exception error) when (error is InvalidDataException or MacroStorageException)
            { DiagnosticLog.Record("macro-links", "preserved original binding", new { row.Index, row.GShift, Binding = Convert.ToHexString(row.Original) }, error); }
        }
    }
}
