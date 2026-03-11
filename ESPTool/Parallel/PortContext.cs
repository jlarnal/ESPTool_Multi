using EspDotNet.Communication;
using EspDotNet.Loaders;
using EspDotNet.Loaders.SoftLoader;

namespace EspDotNet.Parallel;

public class PortContext : IDisposable
{
    public string PortName { get; }
    public Communicator Communicator { get; }
    public ILoader? Loader { get; set; }
    public SoftLoader? SoftLoader { get; set; }
    public ChipTypes ChipType { get; set; }
    public bool Failed { get; set; }
    public string? FailureReason { get; set; }

    public PortContext(string portName, Communicator communicator)
    {
        PortName = portName;
        Communicator = communicator;
    }

    public void Dispose()
    {
        Communicator.Dispose();
    }
}
