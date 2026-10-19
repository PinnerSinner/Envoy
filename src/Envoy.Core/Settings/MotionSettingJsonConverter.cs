using System.Text.Json;
using System.Text.Json.Serialization;
using Envoy.Core.Ux;

namespace Envoy.Core.Settings;

/// <summary>
/// Reads <see cref="MotionSetting"/> by name (any case) and writes its name. A name this build does not know, such as
/// one a newer Envoy saved, or any other value reads as <see cref="MotionSetting.Auto"/>, so the rest of the settings file
/// still loads (spec 09b, REQ-MOT-1).
/// </summary>
public sealed class MotionSettingJsonConverter : JsonConverter<MotionSetting>
{
    public override MotionSetting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var name = reader.GetString();
                return name is not null && !int.TryParse(name, out _)
                    && Enum.TryParse<MotionSetting>(name, ignoreCase: true, out var named) && Enum.IsDefined(named)
                    ? named
                    : MotionSetting.Auto;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) && Enum.IsDefined((MotionSetting)number) ? (MotionSetting)number : MotionSetting.Auto;
            default:
                reader.Skip();
                return MotionSetting.Auto;
        }
    }

    public override void Write(Utf8JsonWriter writer, MotionSetting value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : nameof(MotionSetting.Auto));
    }
}
