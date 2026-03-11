using EspDotNet.Parallel;
using EspDotNet.Tools.Firmware;

namespace EspDotNet.Tests.Parallel;

public class RetryCommandTests
{
    [Fact]
    public void GetRetryCommand_DefaultOptions_OmitsBaudAndVerify()
    {
        var contexts = new List<PortContext>
        {
            new("COM8", null!) { Failed = false },
            new("COM9", null!) { Failed = true, FailureReason = "timeout" },
        };

        var firmware = new FirmwareProvider(0, new IFirmwareSegmentProvider[]
        {
            new FirmwareSegmentProvider(0x1000, new byte[100]),
            new FirmwareSegmentProvider(0x10000, new byte[200]),
        });

        var options = new ParallelFlashOptions();

        var cmd = ParallelFlasher.GetRetryCommand(contexts, firmware, options);

        Assert.Equal("esptool --port COM9 write_flash 0x1000 <file> 0x10000 <file>", cmd);
    }

    [Fact]
    public void GetRetryCommand_CustomBaud_IncludesBaudArg()
    {
        var contexts = new List<PortContext>
        {
            new("COM8", null!) { Failed = true },
        };

        var firmware = new FirmwareProvider(0, new IFirmwareSegmentProvider[]
        {
            new FirmwareSegmentProvider(0x1000, new byte[100]),
        });

        var options = new ParallelFlashOptions { BaudRate = 921600 };

        var cmd = ParallelFlasher.GetRetryCommand(contexts, firmware, options);

        Assert.Contains("--baud 921600", cmd);
    }

    [Fact]
    public void GetRetryCommand_NoVerify_IncludesFlag()
    {
        var contexts = new List<PortContext>
        {
            new("COM8", null!) { Failed = true },
        };

        var firmware = new FirmwareProvider(0, new IFirmwareSegmentProvider[]
        {
            new FirmwareSegmentProvider(0x1000, new byte[100]),
        });

        var options = new ParallelFlashOptions { Verify = false };

        var cmd = ParallelFlasher.GetRetryCommand(contexts, firmware, options);

        Assert.Contains("--no-verify", cmd);
    }

    [Fact]
    public void GetRetryCommand_MultipleFailedPorts_CommaSeparated()
    {
        var contexts = new List<PortContext>
        {
            new("COM8", null!) { Failed = true },
            new("COM9", null!) { Failed = true },
            new("COM10", null!) { Failed = false },
        };

        var firmware = new FirmwareProvider(0, new IFirmwareSegmentProvider[]
        {
            new FirmwareSegmentProvider(0x1000, new byte[100]),
        });

        var cmd = ParallelFlasher.GetRetryCommand(contexts, firmware, new ParallelFlashOptions());

        Assert.StartsWith("esptool --port COM8,COM9", cmd);
    }
}
