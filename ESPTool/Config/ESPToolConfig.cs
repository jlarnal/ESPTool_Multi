using EspDotNet.Tools.Firmware;

namespace EspDotNet.Config
{
    public class ESPToolConfig
    {
        public List<DeviceConfig> Devices { get; set; } = [];
        public List<List<PinSequenceStep>> BootloaderSequences { get; set; } = [];
        public List<PinSequenceStep> ResetSequence { get; set; } = [];
    }
}




