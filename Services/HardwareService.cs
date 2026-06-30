using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace Spocky.Services;

public sealed class HardwareService : IDisposable
{
    private readonly Computer _computer;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1.5);
    private CancellationTokenSource? _cts;

    public event EventHandler<HardwareSnapshot>? MetricsUpdated;

    public HardwareService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true
        };
    }

    public void Start()
    {
        if (_cts != null)
        {
            return;
        }

        _computer.Open();
        _cts = new CancellationTokenSource();
        Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var snapshot = CollectSnapshot();
            MetricsUpdated?.Invoke(this, snapshot);

            try
            {
                await Task.Delay(_pollInterval, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private HardwareSnapshot CollectSnapshot()
    {
        double? cpuPackageTemp = null;
        double? gpuCoreTemp = null;
        double? memoryUsedGb = null;
        double? memoryTotalGb = null;
        double? memoryLoadPercent = null;
        double? storageUsedGb = null;
        double? storageTotalGb = null;
        double? storageLoadPercent = null;

        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();
            foreach (var subHardware in hardware.SubHardware)
            {
                subHardware.Update();
            }

            switch (hardware.HardwareType)
            {
                case HardwareType.Cpu:
                    cpuPackageTemp ??= GetPreferredTemperature(hardware, "Package");
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpuCoreTemp ??= GetPreferredTemperature(hardware, "Core");
                    break;
                case HardwareType.Memory:
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Memory", StringComparison.OrdinalIgnoreCase))
                        {
                            memoryLoadPercent ??= sensor.Value;
                        }

                        if (sensor.SensorType == SensorType.Data)
                        {
                            if (sensor.Name.Contains("Used", StringComparison.OrdinalIgnoreCase))
                            {
                                memoryUsedGb ??= sensor.Value;
                            }
                            else if (sensor.Name.Contains("Available", StringComparison.OrdinalIgnoreCase))
                            {
                                var available = sensor.Value;
                                if (memoryTotalGb == null && memoryUsedGb.HasValue && available.HasValue)
                                {
                                    memoryTotalGb = memoryUsedGb + available;
                                }
                            }
                            else if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                            {
                                memoryTotalGb ??= sensor.Value;
                            }
                        }
                    }
                    break;
                case HardwareType.Storage:
                    foreach (var sensor in hardware.Sensors)
                    {
                        if (sensor.SensorType == SensorType.Data)
                        {
                            if (sensor.Name.Contains("Used Space", StringComparison.OrdinalIgnoreCase))
                            {
                                storageUsedGb ??= sensor.Value;
                            }
                            else if (sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase) || sensor.Name.Contains("Size", StringComparison.OrdinalIgnoreCase))
                            {
                                storageTotalGb ??= sensor.Value;
                            }
                        }

                        if (sensor.SensorType == SensorType.Load && sensor.Name.Contains("Used Space", StringComparison.OrdinalIgnoreCase))
                        {
                            storageLoadPercent ??= sensor.Value;
                        }
                    }
                    break;
            }
        }

        if (memoryLoadPercent == null && memoryTotalGb.HasValue && memoryUsedGb.HasValue && memoryTotalGb.Value > 0)
        {
            memoryLoadPercent = (memoryUsedGb.Value / memoryTotalGb.Value) * 100.0;
        }

        if (storageLoadPercent == null && storageTotalGb.HasValue && storageUsedGb.HasValue && storageTotalGb.Value > 0)
        {
            storageLoadPercent = (storageUsedGb.Value / storageTotalGb.Value) * 100.0;
        }

        return new HardwareSnapshot(
            cpuPackageTemp,
            gpuCoreTemp,
            memoryUsedGb,
            memoryTotalGb,
            memoryLoadPercent,
            storageUsedGb,
            storageTotalGb,
            storageLoadPercent);
    }

    private static double? GetPreferredTemperature(IHardware hardware, string keyword)
    {
        double? fallback = null;

        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType != SensorType.Temperature)
            {
                continue;
            }

            if (sensor.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return sensor.Value;
            }

            fallback ??= sensor.Value;
        }

        return fallback;
    }

    public void Dispose()
    {
        Stop();
        _computer.Close();
    }
}

public readonly record struct HardwareSnapshot(
    double? CpuPackageTempC,
    double? GpuCoreTempC,
    double? MemoryUsedGb,
    double? MemoryTotalGb,
    double? MemoryUsagePercent,
    double? StorageUsedGb,
    double? StorageTotalGb,
    double? StorageUsagePercent);
