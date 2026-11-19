using System.Text.Json.Serialization;
using LogiBoardToolBox.Core.Lighting;
using LogiBoardToolBox.Core.Onboard;
using LogiBoardToolBox.ViewModels;

namespace LogiBoardToolBox.Profiles;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ElementTheme))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Core.HidPlusPlus.SensorPerformance))]
[JsonSerializable(typeof(AssignmentItem))]
[JsonSerializable(typeof(SensitivityPreset[]), TypeInfoPropertyName = "SensitivityPresets")]
[JsonSerializable(typeof(LightingSettings))]
[JsonSerializable(typeof(Dictionary<string, LightingSettings>), TypeInfoPropertyName = "Animations")]
[JsonSerializable(typeof(Dictionary<string, ProfilePairRoute>), TypeInfoPropertyName = "ProfileRoutes")]
[JsonSerializable(typeof(MemorySnapshot))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
