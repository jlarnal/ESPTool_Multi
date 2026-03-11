using System.Text.Json.Serialization;
using EspDotNet.Config;

namespace EspDotNet.Config;

[JsonSerializable(typeof(ESPToolConfig))]
[JsonSerializable(typeof(DeviceConfig))]
[JsonSerializable(typeof(List<DeviceConfig>))]
[JsonSerializable(typeof(List<List<PinSequenceStep>>))]
[JsonSerializable(typeof(List<PinSequenceStep>))]
[JsonSerializable(typeof(PinSequenceStep))]
[JsonSerializable(typeof(EFuseMapping))]
[JsonSerializable(typeof(Dictionary<EFlagKey, EFuseMapping>))]
[JsonSourceGenerationOptions(
    Converters = [typeof(JsonStringEnumConverter<ChipTypes>), typeof(JsonStringEnumConverter<EFlagKey>)],
    PropertyNameCaseInsensitive = true)]
internal partial class EspJsonContext : JsonSerializerContext;
