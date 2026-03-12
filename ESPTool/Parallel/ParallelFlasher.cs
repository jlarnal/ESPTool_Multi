using EspDotNet.Communication;
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

    public async Task<ParallelFlashResult> EraseAsync(
        IReadOnlyList<string> ports,
        ParallelFlashOptions? options = null,
        IProgress<ParallelFlashProgress>? progress = null,
        CancellationToken token = default)
    {
        options ??= new ParallelFlashOptions();
        var contexts = new List<PortContext>();

        try
        {
            contexts = await SetupAllPortsAsync(ports, options, progress, token);

            // Erase all ports in parallel
            var healthy = contexts.Where(c => !c.Failed).ToList();
            var eraseTasks = healthy.Select(async ctx =>
            {
                try
                {
                    Report(progress, ctx.PortName, "ERASE", null, "erasing...");
                    await ctx.SoftLoader!.EraseFlashAsync(token);
                    Report(progress, ctx.PortName, "ERASE", 100, "complete");
                }
                catch (Exception ex)
                {
                    MarkFailed(ctx, $"Erase failed: {ex.Message}");
                }
            });
            await Task.WhenAll(eraseTasks);

            // Reset all healthy ports
            foreach (var ctx in contexts.Where(c => !c.Failed))
            {
                Report(progress, ctx.PortName, "RESET", null, "resetting...");
                await _toolbox.ResetDeviceAsync(ctx.Communicator, token);
                Report(progress, ctx.PortName, "RESET", 100, "complete");
            }

            return new ParallelFlashResult
            {
                Ports = contexts.Select(c => new PortResult
                {
                    PortName = c.PortName,
                    Success = !c.Failed,
                    FailureReason = c.FailureReason
                }).ToList(),
                RetryCommand = null
            };
        }
        finally
        {
            foreach (var ctx in contexts)
                ctx.Dispose();
        }
    }

    public async Task<ParallelFlashResult> EraseRegionAsync(
        IReadOnlyList<string> ports,
        uint offset,
        uint size,
        ParallelFlashOptions? options = null,
        IProgress<ParallelFlashProgress>? progress = null,
        CancellationToken token = default)
    {
        options ??= new ParallelFlashOptions();
        var contexts = new List<PortContext>();

        try
        {
            contexts = await SetupAllPortsAsync(ports, options, progress, token);

            var regionName = $"0x{offset:X}+0x{size:X}";
            var healthy = contexts.Where(c => !c.Failed).ToList();
            var eraseTasks = healthy.Select(async ctx =>
            {
                try
                {
                    Report(progress, ctx.PortName, "ERASE", null, regionName);
                    await ctx.SoftLoader!.EraseRegionAsync(offset, size, token);
                    Report(progress, ctx.PortName, "ERASE", 100, regionName);
                }
                catch (Exception ex)
                {
                    MarkFailed(ctx, $"Erase region failed: {ex.Message}");
                }
            });
            await Task.WhenAll(eraseTasks);

            foreach (var ctx in contexts.Where(c => !c.Failed))
            {
                Report(progress, ctx.PortName, "RESET", null, "resetting...");
                await _toolbox.ResetDeviceAsync(ctx.Communicator, token);
                Report(progress, ctx.PortName, "RESET", 100, "complete");
            }

            return new ParallelFlashResult
            {
                Ports = contexts.Select(c => new PortResult
                {
                    PortName = c.PortName,
                    Success = !c.Failed,
                    FailureReason = c.FailureReason
                }).ToList(),
                RetryCommand = null
            };
        }
        finally
        {
            foreach (var ctx in contexts)
                ctx.Dispose();
        }
    }

    public async Task<ParallelFlashResult> FlashAsync(
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
                await RetryFailedPortsAsync(failed, firmware, options, progress, token);

            // Phase 5: Reset all healthy ports
            foreach (var ctx in contexts.Where(c => !c.Failed))
            {
                Report(progress, ctx.PortName, "RESET", null, "resetting...");
                await _toolbox.ResetDeviceAsync(ctx.Communicator, token);
                Report(progress, ctx.PortName, "RESET", 100, "complete");
            }

            // Build result
            return new ParallelFlashResult
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

    internal static void ValidateChipTypes(List<PortContext> contexts)
    {
        var chipTypes = contexts.Select(c => new { c.PortName, c.ChipType }).ToList();
        var distinct = chipTypes.Select(c => c.ChipType).Distinct().ToList();

        if (distinct.Count > 1)
        {
            var details = string.Join(", ", chipTypes.Select(c => $"{c.PortName}={c.ChipType}"));
            throw new InvalidOperationException(
                $"Chip type mismatch across ports. All devices must be the same type. Detected: {details}");
        }
    }

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
            if (healthy.Count == 0) break;

            var chipType = healthy[0].ChipType;
            var deviceConfig = _toolbox.GetDeviceConfig(chipType);
            uint blockSize = (uint)deviceConfig.FlashBlockSize;
            uint blocks = (uint)compressedData.Length / blockSize;
            if (compressedData.Length % blockSize != 0) blocks++;

            // Progress step: 2% for segments < 4MB, 1% for >= 4MB
            int progressStep = data.Length < 4 * 1024 * 1024 ? 2 : 1;
            var lastReportedPct = new Dictionary<string, int>();

            // FlashDeflBegin on all healthy ports
            foreach (var ctx in healthy)
            {
                try
                {
                    Report(progress, ctx.PortName, "FLASH", 0, segmentName);
                    lastReportedPct[ctx.PortName] = 0;
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
                        int lastPct = lastReportedPct.GetValueOrDefault(ctx.PortName, 0);
                        if (pct >= 100 || pct >= lastPct + progressStep)
                        {
                            Report(progress, ctx.PortName, "FLASH", pct, segmentName);
                            lastReportedPct[ctx.PortName] = pct;
                        }
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
                        byte[] expectedMd5 = MD5.HashData(data);
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
                    Report(progress, ctx.PortName, "RETRY", 100, "success");
            }
            catch (Exception ex)
            {
                MarkFailed(ctx, $"Retry failed: {ex.Message}");
            }
        }
    }

    internal static string GetRetryCommand(
        IReadOnlyList<PortContext> contexts,
        IFirmwareProvider firmware,
        ParallelFlashOptions options)
    {
        var failedPorts = contexts.Where(c => c.Failed).Select(c => c.PortName);
        var portList = string.Join(",", failedPorts);
        var segments = firmware.Segments.Select(s => $"0x{s.Offset:X} <file>").ToList();
        var baudArg = options.BaudRate != 460800 ? $" --baud {options.BaudRate}" : "";
        var verifyArg = !options.Verify ? " --no-verify" : "";
        return $"esptool --port {portList}{baudArg}{verifyArg} write_flash {string.Join(" ", segments)}";
    }

    private static async Task<byte[]> ReadSegmentDataAsync(
        IFirmwareSegmentProvider segment, CancellationToken token)
    {
        using var stream = await segment.GetStreamAsync(token);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, token);
        return ms.ToArray();
    }

    private static void MarkFailed(PortContext ctx, string reason)
    {
        ctx.Failed = true;
        ctx.FailureReason = reason;
    }

    private static void Report(
        IProgress<ParallelFlashProgress>? progress,
        string port, string phase, int? percent, string detail)
    {
        progress?.Report(new ParallelFlashProgress(port, phase, percent, detail));
    }
}
