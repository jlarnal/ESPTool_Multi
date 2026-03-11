# Multi-Port Parallel Flashing — Design Spec

## Problem

Python esptool.py has a ~30s serialization delay when flashing multiple ESP32s in parallel (suspected pyserial/driver mutex). This C# implementation bypasses that by owning the serial I/O stack via System.IO.Ports.

## Goal

Flash identical firmware to N ESP32 devices simultaneously over separate serial ports, faster than sequential esptool.py invocations.

## Deliverables

1. **`ParallelFlasher`** — library class in the ESPTool project
2. **Thin CLI wrapper** — console app consuming ParallelFlasher

## Design Decisions

### Firmware Model

Single `IFirmwareProvider` shared across all ports. All devices receive identical binaries. No per-port firmware variation.

### Chip Validation

Strict homogeneous. During setup, detect chip type on every port. If any port reports a different chip type, abort entirely before flashing. Report all detected types in the error message so the user knows which port has the wrong device.

### Setup Phase — Parallel

Each port runs its full setup sequence concurrently (each on its own async task):

1. Open serial at 115200
2. Enter bootloader (pin sequence + sync)
3. Detect chip type
4. Upload stub flasher to RAM
5. Change baud to target rate (default 460800)

All ports must complete setup successfully. Then validate chip homogeneity across all ports. If validation fails, abort with diagnostic info. If any single port fails setup, report it and abort.

### Flash Phase — Round-Robin Interleaved

A single dispatch loop iterates over firmware segments. For each segment:

1. Send `FlashDeflBegin` to all ports (round-robin, one port per iteration)
2. Send compressed blocks round-robin: one block to COM8, one to COM9, one to COM10, repeat
3. Send `FlashDeflEnd` to all ports

This prevents any single port from monopolizing the USB bus. All ports make roughly equal progress.

### MD5 Verification

On by default. After each firmware segment is fully written to a port, compute MD5 via `SPI_FLASH_MD5(address, size)` and compare against the local hash of the source data. Mismatch = port failure.

Opt-out via `--no-verify` CLI flag / `verify: false` parameter on the library API.

### Progress Reporting

Per-port with phase labels, written to console (CLI) or delivered via callback (library):

```
[COM8  ] BOOT   syncing...
[COM9  ] BOOT   syncing...
[COM10 ] BOOT   synced
[COM8  ] STUB   uploading...
[COM9  ] STUB   uploading...
[COM10 ] STUB   complete
[COM8  ] FLASH  45% bootloader.bin
[COM9  ] FLASH  43% bootloader.bin
[COM10 ] FLASH  47% bootloader.bin
[COM8  ] VERIFY bootloader.bin OK
```

Library API: progress callback with `(string port, string phase, int? percent, string detail)`.

### Error Handling

1. If a port fails during flash, mark it failed and continue flashing healthy ports
2. After all healthy ports finish, retry each failed port once (full flash sequence for that port)
3. If retry fails, print a paste-ready CLI command for the user to retry the culprit port(s) manually
4. Exit code: 0 if all ports succeeded, non-zero if any port failed after retry

### Baud Rate

Default: 460800 (matches esptool.py). Overridable via `--baud` CLI flag / constructor parameter.

Initial connection always at 115200 (ROM bootloader fixed rate), then switches to target baud after stub upload.

### Architecture

Each port gets its own independent stack — zero shared mutable state:

```
ParallelFlasher
 ├─ PortContext[COM8]  → Communicator → ESP32Loader → SoftLoader → UploadFlashDeflatedTool
 ├─ PortContext[COM9]  → Communicator → ESP32Loader → SoftLoader → UploadFlashDeflatedTool
 └─ PortContext[COM10] → Communicator → ESP32Loader → SoftLoader → UploadFlashDeflatedTool
```

The interleaving dispatch loop is the only coordination point. It reads from a shared (immutable) firmware source and dispatches blocks to each port's tool in round-robin order.

### CLI Interface

```
esptool --port COM8,COM9,COM10 write_flash 0x1000 bootloader.bin 0x8000 partition.bin 0x10000 app.bin
```

Flags:
- `--port <ports>` — comma-separated list of serial ports (required)
- `--baud <rate>` — baud rate after stub upload (default: 460800)
- `--no-verify` — skip MD5 verification after write
- `write_flash <offset file>...` — pairs of flash offset and binary file path

### Out of Scope

- Read-flash (command 0x0E) — not needed; MD5 verification is sufficient
- Per-port firmware variation — all ports get identical binaries
- Non-ESP32 chip families (future work)

## Key Files (New)

- `ParallelFlasher.cs` — orchestrator class (setup, interleave loop, error handling, retry)
- `PortContext.cs` — per-port state container (communicator, loader, softloader, upload tool, status)
- `ParallelFlashProgress.cs` — progress callback model
- `CLI/Program.cs` — CLI entry point (argument parsing, console progress output)

## Key Files (Modified)

- `ESPToolbox.cs` — expose MD5 verify method (wire `SoftLoader.SPI_FLASH_MD5` to public API)
- `CLAUDE.md` — update with resolved design decisions
