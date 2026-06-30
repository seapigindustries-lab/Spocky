# Spocky

Spocky is a native Windows 11 hardware intelligence console that channels the retro-futuristic LCARS interface language while recreating the feature set of legacy Speccy. Built on WPF for .NET 8, it now uses only inbox ETW/WMI/perf-counter sources so no kernel drivers are required or flagged by Defender, while still exposing CPU thermal zones (when available), memory load, and primary storage metrics on a 1.5-second cadence.

## Stack

- .NET 8 (Windows target)
- WPF with a custom chrome-free LCARS layout
- ETW/WMI/Performance Counters for driverless telemetry (no UAC prompt required)

## Controls

- Temperature chips in the header instantly toggle between Celsius and Fahrenheit without restarting the polling loop.
- The drive dropdown limits the storage card to whichever fixed drive you care about; selections are validated against the currently mounted volumes.

## Metrics (driverless)

- CPU package temperature (ACPI thermal zone) plus GPU placeholder for future vendor hooks.
- CPU load and current frequency (GHz/MHz) pulled from Windows performance counters.
- Memory usage (percent plus used/total GB) via `GlobalMemoryStatusEx`.
- Storage utilization for the selected fixed drive, along with live disk read/write throughput in MB/s.
- Aggregate network up/down throughput in Mb/s across active adapters.
- System uptime since last boot and battery percentage/status (if a battery exists).

## Development

1. Ensure the .NET 8 SDK is installed (`net8.0-windows`).
2. Restore dependencies: `dotnet restore`
3. Build & run: `dotnet run`

Because the telemetry stack is driverless, Spocky launches fine without elevation. Some machines do not surface the "Thermal Zone Information" counters—when that happens CPU/GPU tiles will fallback to `--` until the OS exposes data.
