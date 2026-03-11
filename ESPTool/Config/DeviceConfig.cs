namespace EspDotNet.Config
{
    public class DeviceConfig
    {
        public ChipTypes ChipType { get; set; }
        public int RamBlockSize { get; set; }
        public int FlashBlockSize { get; set; }
        public uint MagicRegisterValue { get; set; }
        public int? ImageChipId { get; set; }
        public Dictionary<EFlagKey, EFuseMapping> EFlags { get; set; } = new Dictionary<EFlagKey, EFuseMapping>();

    }

}
