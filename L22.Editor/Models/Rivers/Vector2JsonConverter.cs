using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace L22.Editor.Models.Rivers;

// System.Numerics.Vector2 exposes X / Y as public fields, which the
// default STJ serializer silently skips — the resulting JSON is "{}" and
// the reload resets the vector to (0, 0). The CompareRivers test caught
// nothing because both sides serialise the same empty object.
//
// This converter writes {"x": …, "y": …} explicitly and parses either
// case on read, so rivers.json stops leaking bezier handles.
public sealed class Vector2JsonConverter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, System.Type typeToConvert, JsonSerializerOptions options)
    {
        float x = 0f, y = 0f;
        if (reader.TokenType != JsonTokenType.StartObject) return default;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString();
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number) continue;
            if (string.Equals(name, "x", System.StringComparison.OrdinalIgnoreCase)) x = reader.GetSingle();
            else if (string.Equals(name, "y", System.StringComparison.OrdinalIgnoreCase)) y = reader.GetSingle();
        }
        return new Vector2(x, y);
    }

    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteEndObject();
    }
}
