namespace EspDotNet.Parallel;

public class ParallelFlashResult
{
    public required IReadOnlyList<PortResult> Ports { get; init; }
    public bool AllSucceeded => Ports.All(p => p.Success);
    public string? RetryCommand { get; init; }
}

public class PortResult
{
    public required string PortName { get; init; }
    public bool Success { get; init; }
    public string? FailureReason { get; init; }
}
