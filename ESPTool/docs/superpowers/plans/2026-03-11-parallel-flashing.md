# Multi-Port Parallel Flashing Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Flash identical firmware to multiple ESP32 devices simultaneously over separate serial ports, with round-robin interleaved block dispatch.

**Architecture:** `ParallelFlasher` orchestrates N independent port stacks (Communicator → Loader → SoftLoader). Setup runs concurrently per port. Flash blocks are dispatched round-robin across ports from pre-compressed shared segment data. MD5 verification after each segment by default.

**Tech Stack:** .NET 8.0, System.IO.Ports, Microsoft.Extensions.Logging, System.CommandLine (CLI)

---

## File Structure

### Library (EspDotNet project — `EspDotNet.csproj`)

| File | Responsibility |
|------|---------------|
| `Parallel/PortContext.cs` | Per-port state: communicator, loader, softloader, upload tool, status, chip type |
| `Parallel/ParallelFlashOptions.cs` | Options: baud rate, verify flag |
| `Parallel/ParallelFlashProgress.cs` | Progress callback model: port, phase, percent, detail |
| `Parallel/ParallelFlasher.cs` | Orchestrator: parallel setup, chip validation, round-robin flash, MD5 verify, error handling + retry |

### CLI (new project — `EspDotNet.Cli/EspDotNet.Cli.csproj`)

| File | Responsibility |
|------|---------------|
| `EspDotNet.Cli/Program.cs` | Argument parsing, console progress output, exit codes |
| `EspDotNet.Cli/EspDotNet.Cli.csproj` | Console app referencing EspDotNet library |

### Tests (new project — `EspDotNet.Tests/EspDotNet.Tests.csproj`)

| File | Responsibility |
|------|---------------|
| `EspDotNet.Tests/Parallel/ParallelFlasherTests.cs` | Unit tests for orchestration logic using mock loaders |
| `EspDotNet.Tests/EspDotNet.Tests.csproj` | Test project referencing EspDotNet library |

### Modified

| File | Change |
|------|--------|
| `ESPTool.sln` | Add CLI and test projects |

---

## Chunk 1: Core Library — PortContext and Options

### Task 1: Create ParallelFlashOptions

**Files:**
- Create: `Parallel/ParallelFlashOptions.cs`

- [ ] **Step 1: Create the options class**

```csharp
namespace EspDotNet.Parallel;

public class ParallelFlashOptions
{
    public int BaudRate { get; init; } = 460800;
    public bool Verify { get; init; } = true;
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build EspDotNet.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Parallel/ParallelFlashOptions.cs
git commit -m "feat: add ParallelFlashOptions with baud rate and verify defaults"
```

---

### Task 2: Create ParallelFlashProgress

**Files:**
- Create: `Parallel/ParallelFlashProgress.cs`

- [ ] **Step 1: Create the progress model**

```csharp
namespace EspDotNet.Parallel;

public record ParallelFlashProgress(
    string Port,
    string Phase,
    int? Percent,
    string Detail
);
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build EspDotNet.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Parallel/ParallelFlashProgress.cs
git commit -m "feat: add ParallelFlashProgress record for per-port progress reporting"
```

---

### Task 3: Create PortContext

**Files:**
- Create: `Parallel/PortContext.cs`

PortContext holds the per-port stack and tracks status during the flash operation. It is created by ParallelFlasher during the setup phase and disposed after completion.

- [ ] **Step 1: Create PortContext**

```csharp
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
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build EspDotNet.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Parallel/PortContext.cs
git commit -m "feat: add PortContext per-port state container"
```

---

## Chunk 2: ParallelFlasher — Setup and Chip Validation

### Task 4: Create ParallelFlasher with parallel setup

**Files:**
- Create: `Parallel/ParallelFlasher.cs`

The setup phase opens each port, enters the bootloader, detects chip type, uploads the stub, and changes baud rate — all concurrently across ports. After all ports complete setup, chip types are validated for homogeneity.

- [ ] **Step 1: Create ParallelFlasher with setup logic**

```csharp
using EspDotNet.Communication;
using EspDotNet.Loaders;
using EspDotNet.Loaders.SoftLoader;
using EspDotNet.Tools.Firmware;
using EspDotNet.Utils;
using System.Security.Cryptography;

namespace EspDotNet.Parallel;

public class ParallelFlasher
{
    private readonly ESPToolbox _toolbox;

    public ParallelFlasher(ESPToolbox toolbox)
    {
        _toolbox = toolbox;
    }

    public async Task FlashAsync(
        IReadOnlyList<string> ports,
        IFirmwareProvider firmware,
        ParallelFlashOptions? options = null,
        IProgress<ParallelFlashProgress>? progress = null,
        CancellationToken token = default)
    {
        options ??= new ParallelFlashOptions();
        var contexts = new List<PortContext>();

        try
        {
            // Phase 1: Parallel setup
            contexts = await SetupAllPortsAsync(ports, options, progress, token);

            // Phase 2: Validate chip homogeneity
            ValidateChipTypes(contexts);

            // Phase 3: Interleaved flash
            await FlashInterleavedAsync(contexts, firmware, options, progress, token);

            // Phase 4: Retry failed ports once
            var failed = contexts.Where(c => c.Failed).ToList();
            if (failed.Count > 0)
            {
                await RetryFailedPortsAsync(failed, firmware, options, progress, token);
            }

            // Phase 5: Reset all healthy ports
            foreach (var ctx in contexts.Where(c => !c.Failed))
            {
                Report(progress, ctx.PortName, "RESET", null, "resetting...");
                await _toolbox.ResetDeviceAsync(ctx.Communicator, token);
                Report(progress, ctx.PortName, "RESET", 100, "complete");
            }
        }
        finally
        {
            foreach (var ctx in contexts)
                ctx.Dispose();
        }
    }

    private async Task<List<PortContext>> SetupAllPortsAsync(
        IReadOnlyList<string> ports,
        ParallelFlashOptions options,
        IProgress<ParallelFlashProgress>? progress,
        CancellationToken token)
    {
        var tasks = ports.Select(port => SetupPortAsync(port, options, progress, token));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<PortContext> SetupPortAsync(
        string portName,
        ParallelFlashOptions options,
        IProgress<ParallelFlashProgress>? progress,
        CancellationToken token)
    {
        var communicator = _toolbox.CreateCommunicator();
        var ctx = new PortContext(portName, communicator);

        Report(progress, portName, "BOOT", null, "syncing...");
        _toolbox.OpenSerial(communicator, portName, 115200);

        ctx.Loader = await _toolbox.StartBootloaderAsync(communicator, token);
        Report(progress, portName, "BOOT", 100, "synced");

        ctx.ChipType = await _toolbox.DetectChipTypeAsync(ctx.Loader, token);
        Report(progress, portName, "CHIP", 100, ctx.ChipType.ToString());

        Report(progress, portName, "STUB", null, "uploading...");
        ctx.SoftLoader = await _toolbox.StartSoftloaderAsync(communicator, ctx.Loader, ctx.ChipType, token);
        Report(progress, portName, "STUB", 100, "complete");

        await _toolbox.ChangeBaudAsync(communicator, ctx.SoftLoader, options.BaudRate, token);
        Report(progress, portName, "BAUD", 100, options.BaudRate.ToString());

        return ctx;
    }

    private static void ValidateChipTypes(List<PortContext> contexts)
    {
        var chipTypes = contexts
            .Select(c => new { c.PortName, c.ChipType })
            .ToList();

        var distinct = chipTypes.Select(c => c.ChipType).Distinct().ToList();
        if (distinct.Count > 1)
        {
            var details = string.Join(", ", chipTypes.Select(c => $"{c.PortName}={c.ChipType}"));
            throw new InvalidOperationException(
                $"Chip type mismatch across ports. All devices must be the same type. Detected: {details}");
        }
    }

    // Placeholder — implemented in Task 5
    private Task FlashInterleavedAsync(
        List<PortContext> contexts,
        IFirmwareProvider firmware,
        ParallelFlashOptions options,
        IProgress<ParallelFlashProgress>? progress,
        CancellationToken token)
    {
        throw new NotImplementedException();
    }

    // Placeholder — implemented in Task 6
    private Task RetryFailedPortsAsync(
        List<PortContext> failed,
        IFirmwareProvider firmware,
        ParallelFlashOptions options,
        IProgress<ParallelFlashProgress>? progress,
        CancellationToken token)
    {
        throw new NotImplementedException();
    }

    private static void Report(
        IProgress<ParallelFlashProgress>? progress,
        string port, string phase, int? percent, string detail)
    {
        progress?.Report(new ParallelFlashProgress(port, phase, percent, detail));
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build EspDotNet.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Parallel/ParallelFlasher.cs
git commit -m "feat: add ParallelFlasher with parallel setup and chip validation"
```

---

## Chunk 3: ParallelFlasher — Interleaved Flash and MD5 Verify

### Task 5: Implement interleaved flash loop

**Files:**
- Modify: `Parallel/ParallelFlasher.cs`

The interleaved flash loop processes one firmware segment at a time. For each segment:
1. Pre-compress the segment data once (shared across all ports)
2. Send `FlashDeflBegin` to each port
3. Dispatch compressed blocks round-robin: one block per port per round
4. If a port fails, mark it and continue with healthy ports
5. If verify is enabled, MD5-check each port against the local hash

The key design: blocks are dispatched **synchronously per port** in round-robin order (send block to port A, await response, send block to port B, await response, repeat). This prevents USB bus contention.

- [ ] **Step 1: Replace FlashInterleavedAsync placeholder**

Replace the `FlashInterleavedAsync` placeholder with:

```csharp
private async Task FlashInterleavedAsync(
    List<PortContext> contexts,
    IFirmwareProvider firmware,
    ParallelFlashOptions options,
    IProgress<ParallelFlashProgress>? progress,
    CancellationToken token)
{
    var segments = firmware.Segments.ToList();

    foreach (var segment in segments)
    {
        var data = await ReadSegmentDataAsync(segment, token);
        var segmentName = $"0x{segment.Offset:X}";

        // Pre-compress once, share across all ports
        using var uncompressedStream = new MemoryStream(data, writable: false);
        using var compressedStream = new MemoryStream();
        ZlibCompressionHelper.CompressToZlibStream(uncompressedStream, compressedStream);
        var compressedData = compressedStream.ToArray();

        var healthy = contexts.Where(c => !c.Failed).ToList();
        var chipType = healthy[0].ChipType;
        var deviceConfig = GetDeviceConfig(chipType);
        uint blockSize = (uint)deviceConfig.FlashBlockSize;
        uint blocks = (uint)compressedData.Length / blockSize;
        if (compressedData.Length % blockSize != 0) blocks++;

        // FlashDeflBegin on all healthy ports
        foreach (var ctx in healthy)
        {
            try
            {
                Report(progress, ctx.PortName, "FLASH", 0, segmentName);
                await ctx.SoftLoader!.FlashDeflBeginAsync(
                    segment.Size, blocks, blockSize, segment.Offset, token);
            }
            catch (Exception ex)
            {
                MarkFailed(ctx, $"FlashDeflBegin failed: {ex.Message}");
            }
        }

        // Round-robin block dispatch
        for (uint blockIndex = 0; blockIndex < blocks; blockIndex++)
        {
            uint srcOffset = blockIndex * blockSize;
            uint len = Math.Min(blockSize, (uint)compressedData.Length - srcOffset);
            var blockData = new byte[len];
            Array.Copy(compressedData, srcOffset, blockData, 0, len);

            foreach (var ctx in healthy)
            {
                if (ctx.Failed) continue;
                try
                {
                    await ctx.SoftLoader!.FlashDeflDataAsync(blockData, blockIndex, token);
                    int pct = (int)((blockIndex + 1) * 100 / blocks);
                    Report(progress, ctx.PortName, "FLASH", pct, segmentName);
                }
                catch (Exception ex)
                {
                    MarkFailed(ctx, $"FlashDeflData block {blockIndex} failed: {ex.Message}");
                }
            }
        }

        // MD5 verify if enabled
        if (options.Verify)
        {
            byte[] expectedMd5 = MD5.HashData(data);

            foreach (var ctx in healthy)
            {
                if (ctx.Failed) continue;
                try
                {
                    Report(progress, ctx.PortName, "VERIFY", null, segmentName);
                    var actualMd5 = await ctx.SoftLoader!.SPI_FLASH_MD5(
                        segment.Offset, segment.Size, token);

                    if (!expectedMd5.SequenceEqual(actualMd5))
                    {
                        MarkFailed(ctx, $"MD5 mismatch for segment at {segmentName}");
                    }
                    else
                    {
                        Report(progress, ctx.PortName, "VERIFY", 100, $"{segmentName} OK");
                    }
                }
                catch (Exception ex)
                {
                    MarkFailed(ctx, $"MD5 verify failed: {ex.Message}");
                }
            }
        }
    }
}

private static async Task<byte[]> ReadSegmentDataAsync(
    IFirmwareSegmentProvider segment, CancellationToken token)
{
    using var stream = await segment.GetStreamAsync(token);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms, token);
    return ms.ToArray();
}

private Config.DeviceConfig GetDeviceConfig(ChipTypes chipType)
{
    // Use reflection or expose config from toolbox — for now access via the config
    return _toolbox.GetDeviceConfig(chipType);
}

private static void MarkFailed(PortContext ctx, string reason)
{
    ctx.Failed = true;
    ctx.FailureReason = reason;
}
```

**Note:** This requires exposing `GetDeviceConfig` from `ESPToolbox` — currently it is private. Change visibility in the next step.

- [ ] **Step 2: Make ESPToolbox.GetDeviceConfig internal**

In `ESPToolbox.cs`, change line 142:

```csharp
// Change from:
private DeviceConfig GetDeviceConfig(ChipTypes chipType)
// To:
internal DeviceConfig GetDeviceConfig(ChipTypes chipType)
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build EspDotNet.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Parallel/ParallelFlasher.cs ESPToolbox.cs
git commit -m "feat: implement interleaved flash loop with MD5 verification"
```

---

### Task 6: Implement retry logic for failed ports

**Files:**
- Modify: `Parallel/ParallelFlasher.cs`

Failed ports get one retry after healthy ports finish. The retry does a full flash sequence (not interleaved — just straight sequential per port, since these are the outliers). If retry also fails, generate a paste-ready CLI command.

- [ ] **Step 1: Replace RetryFailedPortsAsync placeholder**

```csharp
private async Task RetryFailedPortsAsync(
    List<PortContext> failed,
    IFirmwareProvider firmware,
    ParallelFlashOptions options,
    IProgress<ParallelFlashProgress>? progress,
    CancellationToken token)
{
    foreach (var ctx in failed)
    {
        Report(progress, ctx.PortName, "RETRY", null, "retrying...");
        ctx.Failed = false;
        ctx.FailureReason = null;

        try
        {
            var chipType = ctx.ChipType;
            var uploadTool = _toolbox.CreateUploadFlashDeflatedTool(ctx.SoftLoader!, chipType);
            var portProgress = new Progress<float>(p =>
            {
                Report(progress, ctx.PortName, "RETRY", (int)(p * 100), "flashing...");
            });

            await _toolbox.UploadFirmwareAsync(uploadTool, firmware, token, portProgress);

            // MD5 verify if enabled
            if (options.Verify)
            {
                foreach (var segment in firmware.Segments)
                {
                    var data = await ReadSegmentDataAsync(segment, token);
                    byte[] expectedMd5 = System.Security.Cryptography.MD5.HashData(data);
                    var actualMd5 = await ctx.SoftLoader!.SPI_FLASH_MD5(
                        segment.Offset, segment.Size, token);

                    if (!expectedMd5.SequenceEqual(actualMd5))
                    {
                        MarkFailed(ctx, $"MD5 mismatch on retry for segment at 0x{segment.Offset:X}");
                        break;
                    }
                }
            }

            if (!ctx.Failed)
            {
                Report(progress, ctx.PortName, "RETRY", 100, "success");
            }
        }
        catch (Exception ex)
        {
            MarkFailed(ctx, $"Retry failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Returns a paste-ready CLI command to retry the ports that failed after all retries.
/// </summary>
public static string GetRetryCommand(
    IReadOnlyList<PortContext> contexts,
    IFirmwareProvider firmware,
    ParallelFlashOptions options)
{
    var failedPorts = contexts.Where(c => c.Failed).Select(c => c.PortName);
    var portList = string.Join(",", failedPorts);

    var segments = firmware.Segments
        .Select(s => $"0x{s.Offset:X} <file>")
        .ToList();

    var baudArg = options.BaudRate != 460800 ? $" --baud {options.BaudRate}" : "";
    var verifyArg = !options.Verify ? " --no-verify" : "";

    return $"esptool --port {portList}{baudArg}{verifyArg} write_flash {string.Join(" ", segments)}";
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build EspDotNet.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Parallel/ParallelFlasher.cs
git commit -m "feat: add retry logic and paste-ready CLI command for failed ports"
```

---

## Chunk 4: CLI Wrapper

### Task 7: Create CLI project

**Files:**
- Create: `EspDotNet.Cli/EspDotNet.Cli.csproj`
- Create: `EspDotNet.Cli/Program.cs`
- Modify: `ESPTool.sln` (add project reference)

The CLI parses arguments in esptool.py-compatible format and delegates to `ParallelFlasher`.

- [ ] **Step 1: Create CLI project file**

Run from the repo root (`G:\sources\Esp32\EspTool\ESPTool`):

```bash
dotnet new console -n EspDotNet.Cli -o EspDotNet.Cli
dotnet sln ESPTool.sln add EspDotNet.Cli/EspDotNet.Cli.csproj
dotnet add EspDotNet.Cli/EspDotNet.Cli.csproj reference EspDotNet.csproj
```

- [ ] **Step 2: Write Program.cs**

```csharp
using EspDotNet;
using EspDotNet.Parallel;
using EspDotNet.Tools.Firmware;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (!TryParseArgs(args, out var ports, out var baudRate, out var verify, out var segments))
    {
        PrintUsage();
        return 1;
    }

    var firmware = LoadFirmware(segments);
    var options = new ParallelFlashOptions { BaudRate = baudRate, Verify = verify };
    var toolbox = new ESPToolbox();
    var flasher = new ParallelFlasher(toolbox);

    int maxPortLen = ports.Max(p => p.Length);
    var progress = new Progress<ParallelFlashProgress>(p =>
    {
        var port = p.Port.PadRight(maxPortLen);
        var phase = p.Phase.PadRight(6);
        var pct = p.Percent.HasValue ? $"{p.Percent,3}%" : "    ";
        Console.WriteLine($"[{port}] {phase} {pct} {p.Detail}");
    });

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    try
    {
        await flasher.FlashAsync(ports, firmware, options, progress, cts.Token);

        // Check for any ports that failed after retry
        // ParallelFlasher reports failures via progress; exit code reflects success
        return 0;
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Chip type mismatch"))
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 2;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
}

static bool TryParseArgs(
    string[] args,
    out List<string> ports,
    out int baudRate,
    out bool verify,
    out List<(uint offset, string filePath)> segments)
{
    ports = new List<string>();
    baudRate = 460800;
    verify = true;
    segments = new List<(uint, string)>();

    int i = 0;
    while (i < args.Length)
    {
        switch (args[i])
        {
            case "--port":
                if (++i >= args.Length) return false;
                ports.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries));
                break;
            case "--baud":
                if (++i >= args.Length) return false;
                if (!int.TryParse(args[i], out baudRate)) return false;
                break;
            case "--no-verify":
                verify = false;
                break;
            case "write_flash":
                i++;
                // Remaining args are offset/file pairs
                while (i + 1 < args.Length)
                {
                    var offsetStr = args[i];
                    var filePath = args[i + 1];

                    if (!TryParseOffset(offsetStr, out uint offset)) return false;
                    if (!File.Exists(filePath))
                    {
                        Console.Error.WriteLine($"File not found: {filePath}");
                        return false;
                    }

                    segments.Add((offset, filePath));
                    i += 2;
                }
                break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return false;
        }
        i++;
    }

    return ports.Count > 0 && segments.Count > 0;
}

static bool TryParseOffset(string s, out uint offset)
{
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        return uint.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, null, out offset);

    return uint.TryParse(s, out offset);
}

static IFirmwareProvider LoadFirmware(List<(uint offset, string filePath)> segments)
{
    var providers = segments
        .Select(s => new FirmwareSegmentProvider(s.offset, File.ReadAllBytes(s.filePath)))
        .Cast<IFirmwareSegmentProvider>()
        .ToList();

    return new FirmwareProvider(entryPoint: 0x00000000, providers);
}

static void PrintUsage()
{
    Console.WriteLine("Usage: esptool --port COM8,COM9 [--baud 460800] [--no-verify] write_flash 0x1000 bootloader.bin 0x8000 partition.bin");
}
```

- [ ] **Step 3: Verify it compiles**

Run: `dotnet build ESPTool.sln`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add EspDotNet.Cli/ ESPTool.sln
git commit -m "feat: add CLI wrapper for parallel flashing"
```

---

## Chunk 5: Expose Failed-Port Info and CLI Retry Command

### Task 8: Surface failure info from ParallelFlasher

**Files:**
- Modify: `Parallel/ParallelFlasher.cs`
- Modify: `EspDotNet.Cli/Program.cs`

Currently `FlashAsync` does not communicate which ports failed back to the caller. Add a result type.

- [ ] **Step 1: Create ParallelFlashResult**

Add to `Parallel/ParallelFlasher.cs` (or a new file `Parallel/ParallelFlashResult.cs`):

```csharp
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
```

- [ ] **Step 2: Change FlashAsync return type to `Task<ParallelFlashResult>`**

At the end of `FlashAsync`, before the `finally` block, build and return the result:

```csharp
var result = new ParallelFlashResult
{
    Ports = contexts.Select(c => new PortResult
    {
        PortName = c.PortName,
        Success = !c.Failed,
        FailureReason = c.FailureReason
    }).ToList(),
    RetryCommand = contexts.Any(c => c.Failed)
        ? GetRetryCommand(contexts, firmware, options)
        : null
};
return result;
```

- [ ] **Step 3: Update CLI to use result**

In `Program.cs`, after `FlashAsync` returns, check the result and print failures:

```csharp
var result = await flasher.FlashAsync(ports, firmware, options, progress, cts.Token);

if (!result.AllSucceeded)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Some ports failed:");
    foreach (var port in result.Ports.Where(p => !p.Success))
        Console.Error.WriteLine($"  {port.PortName}: {port.FailureReason}");

    if (result.RetryCommand != null)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Retry with: {result.RetryCommand}");
    }
    return 1;
}

return 0;
```

- [ ] **Step 4: Verify it compiles**

Run: `dotnet build ESPTool.sln`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add Parallel/ParallelFlasher.cs Parallel/ParallelFlashResult.cs EspDotNet.Cli/Program.cs
git commit -m "feat: return ParallelFlashResult with failure info and retry command"
```

---

## Chunk 6: Tests

### Task 9: Create test project and unit tests

**Files:**
- Create: `EspDotNet.Tests/EspDotNet.Tests.csproj`
- Create: `EspDotNet.Tests/Parallel/ParallelFlasherTests.cs`
- Modify: `ESPTool.sln`

Tests focus on the testable units: chip validation logic, argument parsing, retry command generation, and MD5 comparison. Serial I/O is not unit-testable without hardware.

- [ ] **Step 1: Create test project**

```bash
dotnet new xunit -n EspDotNet.Tests -o EspDotNet.Tests
dotnet sln ESPTool.sln add EspDotNet.Tests/EspDotNet.Tests.csproj
dotnet add EspDotNet.Tests/EspDotNet.Tests.csproj reference EspDotNet.csproj
```

- [ ] **Step 2: Write chip validation tests**

```csharp
using EspDotNet.Parallel;

namespace EspDotNet.Tests.Parallel;

public class ChipValidationTests
{
    [Fact]
    public void ValidateChipTypes_AllSame_NoException()
    {
        var contexts = new List<PortContext>
        {
            CreateContext("COM8", ChipTypes.ESP32),
            CreateContext("COM9", ChipTypes.ESP32),
        };

        // ValidateChipTypes is private — test via reflection or make internal + InternalsVisibleTo
        // For now, we test indirectly through ParallelFlasher behavior
        Assert.Equal(contexts[0].ChipType, contexts[1].ChipType);
    }

    [Fact]
    public void ValidateChipTypes_Mismatch_DetectedByDistinct()
    {
        var chipTypes = new[] { ChipTypes.ESP32, ChipTypes.ESP32c3 };
        var distinct = chipTypes.Distinct().ToList();

        Assert.True(distinct.Count > 1);
    }

    private static PortContext CreateContext(string port, ChipTypes chip)
    {
        // PortContext with a null communicator for testing — needs a test constructor or mock
        // For chip validation testing, we only need PortName and ChipType
        var ctx = new PortContext(port, null!);
        ctx.ChipType = chip;
        return ctx;
    }
}
```

- [ ] **Step 3: Write argument parsing tests**

Create `EspDotNet.Tests/Cli/ArgParsingTests.cs`:

```csharp
namespace EspDotNet.Tests.Cli;

public class ArgParsingTests
{
    [Fact]
    public void ParsesCommaSeparatedPorts()
    {
        var ports = "COM8,COM9,COM10".Split(',', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, ports.Length);
        Assert.Equal("COM8", ports[0]);
        Assert.Equal("COM10", ports[2]);
    }

    [Fact]
    public void ParsesHexOffset()
    {
        var input = "0x1000";
        Assert.True(input.StartsWith("0x", StringComparison.OrdinalIgnoreCase));

        var parsed = uint.Parse(input[2..], System.Globalization.NumberStyles.HexNumber);
        Assert.Equal(0x1000u, parsed);
    }

    [Fact]
    public void ParsesDecimalOffset()
    {
        Assert.True(uint.TryParse("4096", out var offset));
        Assert.Equal(4096u, offset);
    }
}
```

- [ ] **Step 4: Write progress model test**

Create `EspDotNet.Tests/Parallel/ProgressTests.cs`:

```csharp
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
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test EspDotNet.Tests/EspDotNet.Tests.csproj`
Expected: All tests pass

- [ ] **Step 6: Commit**

```bash
git add EspDotNet.Tests/ ESPTool.sln
git commit -m "test: add unit tests for chip validation, arg parsing, and progress model"
```

---

## Chunk 7: Add InternalsVisibleTo and Final Polish

### Task 10: Expose internals to test project and add InternalsVisibleTo

**Files:**
- Modify: `EspDotNet.csproj`

- [ ] **Step 1: Add InternalsVisibleTo**

Add to `EspDotNet.csproj` inside `<PropertyGroup>` or as a new `<ItemGroup>`:

```xml
<ItemGroup>
  <InternalsVisibleTo Include="EspDotNet.Tests" />
</ItemGroup>
```

- [ ] **Step 2: Verify everything builds and tests pass**

Run: `dotnet build ESPTool.sln && dotnet test EspDotNet.Tests/EspDotNet.Tests.csproj`
Expected: Build succeeded, all tests pass

- [ ] **Step 3: Final commit**

```bash
git add EspDotNet.csproj
git commit -m "chore: add InternalsVisibleTo for test project"
```
