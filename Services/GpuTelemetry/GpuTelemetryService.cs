using Spocky.Services.GpuTelemetry.Providers;

namespace Spocky.Services.GpuTelemetry;

internal sealed class GpuTelemetryService : IDisposable
{
    private readonly List<IGpuTelemetryProvider> _providers = new();

    public GpuTelemetryService()
    {
        TryAddProvider(() => new NvidiaGpuTelemetryProvider());
    }

    public double? ReadCoreTemperatureC()
    {
        foreach (var provider in _providers)
        {
            var temp = provider.ReadCoreTemperatureC();
            if (temp.HasValue)
            {
                return temp;
            }
        }

        return null;
    }

    private void TryAddProvider(Func<IGpuTelemetryProvider> factory)
    {
        IGpuTelemetryProvider? provider = null;
        try
        {
            provider = factory();
            if (provider.IsAvailable)
            {
                _providers.Add(provider);
            }
            else
            {
                provider.Dispose();
            }
        }
        catch (DllNotFoundException)
        {
            provider?.Dispose();
        }
        catch (EntryPointNotFoundException)
        {
            provider?.Dispose();
        }
        catch
        {
            provider?.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        _providers.Clear();
    }
}
