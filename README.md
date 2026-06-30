# Spocky

Spocky is a native Windows 11 hardware intelligence console that channels the retro-futuristic LCARS interface language while recreating the feature set of legacy Speccy. Built on WPF for .NET 8, it wraps LibreHardwareMonitorLib to surface accurate CPU, GPU, memory, and storage telemetry with a minimal 1.5-second polling cadence.

## Stack

- .NET 8 (Windows target)
- WPF with a custom chrome-free LCARS layout
- LibreHardwareMonitorLib for sensor access (requires administrator privileges)

## Development

1. Ensure the .NET 8 SDK is installed (`net8.0-windows`).
2. Restore dependencies: `dotnet restore`
3. Build & run: `dotnet run`

Because LibreHardwareMonitorLib needs ring-0 sensor access, Spocky requests elevated rights via `app.manifest`. Always launch from an elevated shell when debugging to ensure hardware sensors populate.
