using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recon.Core.Converters;

public class JsonIntStringConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt32();
        }
        
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            
            if (string.IsNullOrWhiteSpace(value))
            {
                return 25; 
            }
            
            if (int.TryParse(value, out int result))
            {
                return result;
            }
        }
        
        return 25;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value);
    }
}