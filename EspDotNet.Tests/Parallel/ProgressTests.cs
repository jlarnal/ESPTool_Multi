using EspDotNet.Parallel;

namespace EspDotNet.Tests.Parallel;

public class ProgressTests
{
    [Fact]
    public void ParallelFlashProgress_RecordEquality()
    {
        var a = new ParallelFlashProgress("COM8", "FLASH", 50, "bootloader.bin");
        var b = new ParallelFlashProgress("COM8", "FLASH", 50, "bootloader.bin");

        Assert.Equal(a, b);
    }

    [Fact]
    public void ParallelFlashProgress_NullPercent()
    {
        var p = new ParallelFlashProgress("COM8", "BOOT", null, "syncing...");

        Assert.Null(p.Percent);
        Assert.Equal("COM8", p.Port);
        Assert.Equal("BOOT", p.Phase);
    }

    [Fact]
    public void ParallelFlashProgress_Deconstruction()
    {
        var p = new ParallelFlashProgress("COM9", "VERIFY", 100, "OK");
        var (port, phase, pct, detail) = p;

        Assert.Equal("COM9", port);
        Assert.Equal("VERIFY", phase);
        Assert.Equal(100, pct);
        Assert.Equal("OK", detail);
    }
}
