# EspTool — C# ESP32 Serial Flasher

## Project Overview
Native C# implementation of Espressif's esptool for ESP32 devices. Serial SLIP protocol, ROM bootloader handshake, stub flasher upload, compressed flash writes. Supports ESP32, ESP32-C3, ESP32-C6, ESP32-S3, ESP32-H2, and others.

## Build
```bash
dotnet build ESPTool.sln
```

## Architecture (single-port, existing)
- **ESPToolbox** — stateless facade/factory, all public API
- **Communicator** — wraps `System.IO.Ports.SerialPort`, owns one port
- **SlipFraming** — SLIP encode/decode (0xC0 delimiters, 0xDB escapes)
- **ILoader / ESP32Loader** — ROM bootloader commands (sync, flash, mem, read reg)
- **SoftLoader** — stub flasher commands (compressed flash, MD5 verify, erase)
- **IUploadTool** — strategy pattern: UploadFlashTool, UploadRamTool, UploadFlashDeflatedTool
- **IFirmwareProvider / FirmwareSegmentProvider** — firmware segments (offset + data)
- **ConfigProvider** — embedded JSON resources (device configs, stub binaries, pin sequences)

## Key Design Patterns
- Factory pattern in ESPToolbox (explicit state: loader, chipType, communicator)
- Fluent builder for RequestCommand
- Strategy pattern for upload tools
- Async/await throughout, CancellationToken everywhere
- No internal parallelism — each Communicator is single-threaded

## In-Progress: Multi-Port Parallel Flashing
Design session started 2026-03-11 in the Squeek project context (G:\sources\Esp32\Squeek).

### Locked-In Design Decisions
1. **Deliverables**: Library class (`ParallelFlasher`) + thin CLI wrapper (Option C)
2. **Chip validation**: Strict homogeneous — detect all chips first, abort entirely on mismatch, report all detected types
3. **Transfer strategy**: Round-robin interleaved at the block level — single dispatch loop sends one flash block per port per round, no port starves the USB bus. NOT full-parallel bulk streams.
4. **Progress reporting**: Per-port with phase labels:
   ```
   [COM8  ] FLASH 45% firmware.bin
   [COM9  ] FLASH 43% firmware.bin
   [COM10 ] ERASE complete
   ```
5. **Error handling**: Continue healthy ports on failure + retry failed ports once after others finish + print paste-ready CLI command for the user to retry culprit ports later
6. **Architecture**: Each port gets its own Communicator/Loader/SoftLoader — zero shared state. The interleaving loop is the only coordination point.

### Motivation
Python esptool.py has a ~30s serialization delay on the last port to finish when flashing multiple ESP32s in parallel (suspected pyserial/driver mutex). This C# implementation bypasses that entirely by owning the serial I/O stack via System.IO.Ports.

### Resolved Design Questions
- **Baud rate**: Default 460800 (matches esptool.py), overridable via `--baud` / constructor param
- **Read-flash (0x0E)**: Out of scope — MD5 verify is sufficient for write verification
- **MD5 verify**: On by default after each segment, opt-out via `--no-verify` / `verify: false`
- **CLI arg format**: `--port COM8,COM9,COM10 write_flash 0x1000 file.bin 0x8000 file2.bin`
- **Setup phase**: Parallel (concurrent async tasks per port), then validate chip homogeneity, then interleaved flash loop
- **Firmware model**: Single IFirmwareProvider shared across all ports — identical binaries to all devices

## Embedded Resources
- `Resources/Config/ESPToolConfig.json` — boot/reset pin sequences
- `Resources/Config/Devices/*.json` — per-chip configs (magic register, block sizes)
- `Resources/stub/stub_flasher_*.json` — stub binaries (base64)

## Target
.NET 8.0, dependencies: System.IO.Ports (5.0.1), Microsoft.Extensions.Logging (8.0.0)
