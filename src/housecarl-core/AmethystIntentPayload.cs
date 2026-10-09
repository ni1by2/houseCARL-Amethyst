using System.Buffers;
using System.Text.Json;
using MessagePack;

namespace HousecarlCore;

/// <summary>Decode the producer's generic MessagePack intent, preserving binary values and embedded NUL archive keys.</summary>
internal static class AmethystIntentPayload
{
    /// <summary>Convert values through a JSON writer: MessagePack's diagnostic ConvertToJson does not escape every control character.</summary>
    internal static JsonDocument Read(byte[] payload)
    {
        var reader = new MessagePackReader(payload);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(ref reader, writer, 0);
            if (!reader.End) throw new InvalidOperationException("Amethyst intent has trailing MessagePack values");
        }
        return JsonDocument.Parse(stream.ToArray());
    }

    /// <summary>Bound recursion and accept only ordinary saved-intent values; unknown extension encodings refuse.</summary>
    static void Write(ref MessagePackReader reader, Utf8JsonWriter writer, int depth)
    {
        if (depth > 64) throw new InvalidOperationException("Amethyst intent exceeds the supported nesting depth");
        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Map:
                var members = reader.ReadMapHeader(); writer.WriteStartObject();
                for (var i = 0; i < members; i++)
                {
                    if (reader.NextMessagePackType != MessagePackType.String) throw new InvalidOperationException("Amethyst intent has a non-string map key");
                    writer.WritePropertyName(reader.ReadString()!); Write(ref reader, writer, depth + 1);
                }
                writer.WriteEndObject(); break;
            case MessagePackType.Array:
                var count = reader.ReadArrayHeader(); writer.WriteStartArray();
                for (var i = 0; i < count; i++) Write(ref reader, writer, depth + 1);
                writer.WriteEndArray(); break;
            case MessagePackType.String: writer.WriteStringValue(reader.ReadString()); break;
            case MessagePackType.Binary: writer.WriteBase64StringValue(reader.ReadBytes()!.Value.ToArray()); break;
            case MessagePackType.Integer: writer.WriteNumberValue(reader.ReadInt64()); break;
            case MessagePackType.Float: writer.WriteNumberValue(reader.ReadDouble()); break;
            case MessagePackType.Boolean: writer.WriteBooleanValue(reader.ReadBoolean()); break;
            case MessagePackType.Nil: reader.ReadNil(); writer.WriteNullValue(); break;
            default: throw new InvalidOperationException("Amethyst intent has an unsupported MessagePack value");
        }
    }
}
