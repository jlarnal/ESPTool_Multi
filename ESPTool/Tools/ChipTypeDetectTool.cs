using EspDotNet.Commands;
using EspDotNet.Communication;
using EspDotNet.Config;
using EspDotNet.Loaders;
using EspDotNet.Loaders.ESP32BootLoader;

namespace EspDotNet.Tools
{
    public class ChipTypeDetectTool
    {
        private readonly ILoader _loader;
        private readonly ESPToolConfig _config;

        public ChipTypeDetectTool(ILoader loader, ESPToolConfig config)
        {
            _loader = loader;
            _config = config;
        }

        public async Task<ChipTypes> DetectChipTypeAsync(CancellationToken token)
        {
            // Try GET_SECURITY_INFO first (ESP32-S3, C3, C6, H2 and later)
            var chipId = await TryGetChipIdAsync(token);
            if (chipId.HasValue)
            {
                var byChipId = _config.Devices.FirstOrDefault(d => d.ImageChipId == chipId.Value);
                if (byChipId != null)
                    return byChipId.ChipType;
            }

            // Fall back to magic register (ESP32, ESP8266, ESP32-S2)
            uint CHIP_DETECT_MAGIC_REG_ADDR = 0x40001000;
            uint magicValue = await _loader.ReadRegisterAsync(CHIP_DETECT_MAGIC_REG_ADDR, token);
            var byMagic = _config.Devices.FirstOrDefault(d => d.MagicRegisterValue == magicValue);
            return byMagic?.ChipType ?? ChipTypes.Unknown;
        }

        private async Task<int?> TryGetChipIdAsync(CancellationToken token)
        {
            if (_loader is not ESP32BootLoader bootLoader)
                return null;

            try
            {
                return await bootLoader.GetChipIdAsync(token);
            }
            catch
            {
                return null;
            }
        }
    }
}
