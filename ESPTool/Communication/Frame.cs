namespace EspDotNet.Communication
{
    public class Frame
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();

        public Frame()
        {

        }
        public Frame(byte[] data)
        {
            Data = data;
        }
    }
}
