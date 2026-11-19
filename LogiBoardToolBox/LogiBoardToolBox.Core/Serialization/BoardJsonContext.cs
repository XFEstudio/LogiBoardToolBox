using System.Text.Json.Serialization;
using LogiBoardToolBox.Core.Devices;
using LogiBoardToolBox.Core.Onboard;

namespace LogiBoardToolBox.Core.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MouseModel[]))]
[JsonSerializable(typeof(MemorySnapshot))]
internal sealed partial class BoardJsonContext : JsonSerializerContext;
