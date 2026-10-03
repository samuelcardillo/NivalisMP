using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NivalisTogether.Dedicated
{
    // Item rows contain four integers and profit rows two. The default array
    // reader allocates a List and its backing array before the final array for
    // every row. Keep short rows on the stack until their exact size is known.
    // This converter belongs to the protocol's strict-number options; it does
    // not implement JsonNumberHandling overrides. Null handling remains with
    // JsonConverter<T>, and larger arrays retain ordinary growable storage.
    internal sealed class Int32ArrayJsonConverter : JsonConverter<int[]>
    {
        public override int[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an integer array.");
            Span<int> small = stackalloc int[8];
            int count = 0;
            List<int> large = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    return large != null ? large.ToArray() : count == 0 ? Array.Empty<int>() : small.Slice(0, count).ToArray();
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int value))
                    throw new JsonException("Expected a signed 32-bit integer.");
                if (count < small.Length) small[count] = value;
                else
                {
                    if (large == null)
                    {
                        large = new List<int>(16);
                        for (int i = 0; i < small.Length; i++) large.Add(small[i]);
                    }
                    large.Add(value);
                }
                count++;
            }
            throw new JsonException("Incomplete integer array.");
        }

        public override void Write(Utf8JsonWriter writer, int[] value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (int item in value) writer.WriteNumberValue(item);
            writer.WriteEndArray();
        }
    }
}
