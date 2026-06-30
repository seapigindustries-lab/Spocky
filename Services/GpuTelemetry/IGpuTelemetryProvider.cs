namespace Spocky.Services.GpuTelemetry;

internal interface IGpuTelemetryProvider : IDisposable
{
    bool IsAvailable { get; }

    double? ReadCoreTemperatureC();
}
