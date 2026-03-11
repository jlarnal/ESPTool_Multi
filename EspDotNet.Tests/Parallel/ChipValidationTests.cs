using EspDotNet.Parallel;

namespace EspDotNet.Tests.Parallel;

public class ChipValidationTests
{
    [Fact]
    public void ValidateChipTypes_AllSame_NoException()
    {
        var contexts = new List<PortContext>
        {
            new("COM8", null!) { ChipType = ChipTypes.ESP32 },
            new("COM9", null!) { ChipType = ChipTypes.ESP32 },
            new("COM10", null!) { ChipType = ChipTypes.ESP32 },
        };

        ParallelFlasher.ValidateChipTypes(contexts);
    }

    [Fact]
    public void ValidateChipTypes_Mismatch_Throws()
    {
        var contexts = new List<PortContext>
        {
            new("COM8", null!) { ChipType = ChipTypes.ESP32 },
            new("COM9", null!) { ChipType = ChipTypes.ESP32c3 },
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => ParallelFlasher.ValidateChipTypes(contexts));

        Assert.Contains("Chip type mismatch", ex.Message);
        Assert.Contains("COM8=ESP32", ex.Message);
        Assert.Contains("COM9=ESP32c3", ex.Message);
    }

    [Fact]
    public void ValidateChipTypes_SinglePort_NoException()
    {
        var contexts = new List<PortContext>
        {
            new("COM8", null!) { ChipType = ChipTypes.ESP32s3 },
        };

        ParallelFlasher.ValidateChipTypes(contexts);
    }
}
