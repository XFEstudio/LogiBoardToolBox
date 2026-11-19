using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogiBoardToolBox.Core.Diagnostics;

/// <summary>Writes the heterogeneous diagnostic payloads without creating converters at runtime.</summary>
public static class DiagnosticJson
{
    public static string Serialize(object? value, bool indented = false) => Encoding.UTF8.GetString(Encode(value, indented).WrittenSpan);

    public static JsonElement ToElement(object? value)
    {
        using var document = JsonDocument.Parse(Encode(value, false).WrittenMemory);
        return document.RootElement.Clone();
    }

    public static async Task WriteAsync(Stream stream, object? value, CancellationToken ct) =>
        await stream.WriteAsync(Encode(value, true).WrittenMemory, ct).ConfigureAwait(false);

    private static ArrayBufferWriter<byte> Encode(object? value, bool indented)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, MaxDepth = 64 });
        WriteValue(writer, value);
        writer.Flush();
        return buffer;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case JsonElement json: json.WriteTo(writer); break;
            case string text: writer.WriteStringValue(text); break;
            case char character: writer.WriteStringValue(character.ToString()); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            case byte n: writer.WriteNumberValue(n); break;
            case sbyte n: writer.WriteNumberValue(n); break;
            case short n: writer.WriteNumberValue(n); break;
            case ushort n: writer.WriteNumberValue(n); break;
            case int n: writer.WriteNumberValue(n); break;
            case uint n: writer.WriteNumberValue(n); break;
            case long n: writer.WriteNumberValue(n); break;
            case ulong n: writer.WriteNumberValue(n); break;
            case float n: writer.WriteNumberValue(n); break;
            case double n: writer.WriteNumberValue(n); break;
            case decimal n: writer.WriteNumberValue(n); break;
            case Guid id: writer.WriteStringValue(id); break;
            case DateTime time: writer.WriteStringValue(time); break;
            case DateTimeOffset time: writer.WriteStringValue(time); break;
            case TimeSpan duration: writer.WriteStringValue(duration.ToString("c", CultureInfo.InvariantCulture)); break;
            case Enum item:
                if (Enum.GetUnderlyingType(item.GetType()) == typeof(ulong)) writer.WriteNumberValue(Convert.ToUInt64(item, CultureInfo.InvariantCulture));
                else writer.WriteNumberValue(Convert.ToInt64(item, CultureInfo.InvariantCulture));
                break;
            case IDictionary dictionary:
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                    WriteValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                break;
            case IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default: WriteProperties(writer, value); break;
        }
    }

    private static void WriteProperties(Utf8JsonWriter writer, object value)
    {
        // The Native AOT publish roots the app and Core, which define these payloads.
        writer.WriteStartObject();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetMethod is null || property.GetIndexParameters().Length != 0 || property.IsDefined(typeof(JsonIgnoreAttribute))) continue;
            writer.WritePropertyName(property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name);
            WriteValue(writer, property.GetValue(value));
        }
        writer.WriteEndObject();
    }
}
