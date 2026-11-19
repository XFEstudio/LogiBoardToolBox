using System.Text.Json.Serialization;

namespace LogiBoardToolBox.ViewModels;

public partial class MainPageViewModel
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(List<SavedMacro>), TypeInfoPropertyName = "Macros")]
    [JsonSerializable(typeof(LuaProfile))]
    [JsonSerializable(typeof(OnboardMacroLinkFile))]
    [JsonSerializable(typeof(SoftwareSettings))]
    private sealed partial class SettingsJsonContext : JsonSerializerContext;
}
