using EspDotNet;
using EspDotNet.Parallel;
using EspDotNet.Tools.Firmware;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || HasFlag(args, "-h", "--help"))
    {
        PrintHelp();
        return 0;
    }

    if (HasFlag(args, "-V", "--version"))
    {
        Console.WriteLine("EspTool_Multi 1.0.0");
        return 0;
    }

    if (!TryParseArgs(args, out var command, out var ports, out var baudRate, out var verify, out var segments, out var error))
    {
        Console.Error.WriteLine($"Error: {error}");
        Console.Error.WriteLine("Run with --help for usage information.");
        return 1;
    }

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
        ParallelFlashResult result;

        if (command == "erase_flash")
        {
            result = await flasher.EraseAsync(ports, options, progress, cts.Token);
        }
        else
        {
            var firmware = LoadFirmware(segments);
            result = await flasher.FlashAsync(ports, firmware, options, progress, cts.Token);
        }

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

        Console.WriteLine();
        var verb = command == "erase_flash" ? "erased" : "flashed";
        Console.WriteLine($"All ports {verb} successfully.");
        return 0;
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Chip type mismatch"))
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 2;
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static bool HasFlag(string[] args, string shortForm, string longForm)
{
    return args.Any(a => a == shortForm || a == longForm);
}

static bool TryParseArgs(
    string[] args,
    out string command,
    out List<string> ports,
    out int baudRate,
    out bool verify,
    out List<(uint offset, string filePath)> segments,
    out string? error)
{
    command = "";
    ports = new List<string>();
    baudRate = 460800;
    verify = true;
    segments = new List<(uint, string)>();
    error = null;

    int i = 0;
    while (i < args.Length)
    {
        switch (args[i])
        {
            case "-p":
            case "--port":
                if (++i >= args.Length) { error = "Missing value for --port."; return false; }
                ports.AddRange(args[i].Split(',', StringSplitOptions.RemoveEmptyEntries));
                break;

            case "-b":
            case "--baud":
                if (++i >= args.Length) { error = "Missing value for --baud."; return false; }
                if (!int.TryParse(args[i], out baudRate))
                {
                    error = $"Invalid baud rate: '{args[i]}'.";
                    return false;
                }
                break;

            case "-n":
            case "--no-verify":
                verify = false;
                break;

            case "-h":
            case "--help":
            case "-V":
            case "--version":
                // Handled before TryParseArgs is called
                break;

            case "erase_flash":
                command = "erase_flash";
                break;

            case "write_flash":
                command = "write_flash";
                i++;
                while (i + 1 < args.Length)
                {
                    if (!TryParseOffset(args[i], out uint offset))
                    {
                        error = $"Invalid flash offset: '{args[i]}'. Use hex (0x1000) or decimal.";
                        return false;
                    }
                    var filePath = args[i + 1];
                    if (!File.Exists(filePath))
                    {
                        error = $"File not found: {filePath}";
                        return false;
                    }
                    segments.Add((offset, filePath));
                    i += 2;
                }
                if (segments.Count == 0)
                {
                    error = "write_flash requires at least one <offset> <file> pair.";
                    return false;
                }
                break;

            default:
                error = $"Unknown argument: {args[i]}";
                return false;
        }
        i++;
    }

    if (ports.Count == 0)
    {
        error = "At least one port is required (-p/--port).";
        return false;
    }

    if (string.IsNullOrEmpty(command))
    {
        error = "No command specified. Use 'write_flash' or 'erase_flash'.";
        return false;
    }

    if (command == "write_flash" && segments.Count == 0)
    {
        error = "No flash segments specified. Use: write_flash <offset> <file> [<offset> <file> ...]";
        return false;
    }

    return true;
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

static void PrintHelp()
{
    Console.WriteLine("""
    EspTool_Multi — Parallel ESP32 flash tool

    USAGE:
        EspTool_Multi [OPTIONS] <COMMAND>

    COMMANDS:
        write_flash <offset> <file> [<offset> <file> ...]
            Flash one or more firmware segments to all target devices.
            Offsets can be hex (0x1000) or decimal (4096).

        erase_flash
            Erase the entire flash memory on all target devices.

    OPTIONS:
        -p, --port <PORTS>  Serial port(s), comma-separated.
                            Single port:    -p COM8
                            Multiple ports: -p COM8,COM9,COM10
                            On Linux:       -p /dev/ttyUSB0,/dev/ttyUSB1

        -b, --baud <RATE>   Baud rate for flashing (default: 460800).
                            Common values: 115200, 230400, 460800, 921600

        -n, --no-verify     Skip MD5 verification after flashing.
                            By default, each segment is verified.

        -h, --help          Show this help message and exit.
        -V, --version       Show version and exit.

    EXAMPLES:
        Erase all boards:
            EspTool_Multi -p COM8,COM9,COM10 erase_flash

        Flash a single board:
            EspTool_Multi -p COM8 write_flash 0x0 bootloader.bin 0x8000 partitions.bin 0x10000 app.bin

        Flash three boards in parallel:
            EspTool_Multi -p COM8,COM9,COM10 write_flash 0x0 bootloader.bin 0x8000 partitions.bin 0x10000 app.bin

        Flash at a lower baud rate without verification:
            EspTool_Multi -p COM8,COM9 -b 115200 -n write_flash 0x10000 firmware.bin

    BEHAVIOR:
        - All ports receive the same firmware segments.
        - Ports are set up in parallel (bootloader sync, stub upload).
        - Flash writes are interleaved round-robin at the block level.
        - On failure, healthy ports continue; failed ports retry once.
        - A paste-ready retry command is printed for any ports that fail.

    EXIT CODES:
        0    All ports completed successfully.
        1    One or more ports failed, or invalid arguments.
        2    Chip type mismatch across ports (strict homogeneous check).
        130  Cancelled by user (Ctrl+C).
    """);
}
