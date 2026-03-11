namespace EspDotNet.Parallel;

public record ParallelFlashProgress(
    string Port,
    string Phase,
    int? Percent,
    string Detail
);
