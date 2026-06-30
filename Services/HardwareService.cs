using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spocky.Services;

/// <summary>
/// Hardware polling service that relies exclusively on ETW/WMI/managed APIs so no kernel driver is required.
/// </summary>
public sealed class HardwareService : IDisposable
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1.5);
    private Timer? _timer;
    private bool _disposed;

    public event EventHandler<HardwareSnapshot>? MetricsUpdated;

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }

        _timer = new Timer(OnTick, null, TimeSpan.Zero, _pollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void OnTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = CollectSnapshot();
        MetricsUpdated?.Invoke(this, snapshot);
    }

    private static HardwareSnapshot CollectSnapshot()
    {
        var (memoryUsedGb, memoryTotalGb, memoryPercent) = ReadMemory();
        var (storageUsedGb, storageTotalGb, storagePercent) = ReadPrimaryStorage();

        return new HardwareSnapshot(
            CpuPackageTempC: ReadCpuTemperature(),
            GpuCoreTempC: ReadGpuTemperature(),
            MemoryUsedGb: memoryUsedGb,
            MemoryTotalGb: memoryTotalGb,
            MemoryUsagePercent: memoryPercent,
            StorageUsedGb: storageUsedGb,
            StorageTotalGb: storageTotalGb,
            StorageUsagePercent: storagePercent);
    }

    private static (double? usedGb, double? totalGb, double? percent) ReadMemory()
    {
        if (!TryGetMemoryStatus(out var totalBytes, out var availBytes))
        {
            return (null, null, null);
        }

        var total = BytesToGigabytes(totalBytes);
        var available = BytesToGigabytes(availBytes);
        var used = total - available;
        var percent = total > 0 ? (used / total) * 100.0 : (double?)null;
        return (used, total, percent);
    }

    private static (double? usedGb, double? totalGb, double? percent) ReadPrimaryStorage()
    {
        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .OrderByDescending(d => d.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase))
                .ThenBy(d => d.Name)
                .FirstOrDefault();

            if (drive == null)
            {
                return (null, null, null);
            }

            var total = BytesToGigabytes(drive.TotalSize);
            var free = BytesToGigabytes(drive.TotalFreeSpace);
            var used = total - free;
            var percent = total > 0 ? (used / total) * 100.0 : (double?)null;
            return (used, total, percent);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static double? ReadCpuTemperature()
    {
        try
        {
            const string categoryName = "Thermal Zone Information";
            if (!PerformanceCounterCategory.Exists(categoryName))
            {
                return null;
            }

            var category = new PerformanceCounterCategory(categoryName);
            foreach (var instance in category.GetInstanceNames())
            {
                using var counter = new PerformanceCounter(categoryName, "Temperature", instance, readOnly: true);
                var raw = counter.NextValue();
                if (double.IsNaN(raw) || raw <= 0)
                {
                    continue;
                }

                // Thermal zone counters report tenths of Kelvin.
                var celsius = raw / 10.0 - 273.15;
                if (celsius > -40 && celsius < 150)
                {
                    return Math.Round(celsius, 1);
                }
            }
        }
        catch
        {
            // Ignore and fall through.
        }

        return null;
    }

    private static double? ReadGpuTemperature()
    {
        // GPU temperature is not exposed through built-in ETW/Perf counters without vendor APIs.
        // We return null so the UI keeps "--" but the service stays driverless.
        return null;
    }

    private static bool TryGetMemoryStatus(out ulong total, out ulong available)
    {
        var buffer = new MemoryStatusEx();
        buffer.Init();
        if (GlobalMemoryStatusEx(ref buffer))
        {
            total = buffer.ullTotalPhys;
            available = buffer.ullAvailPhys;
            return true;
        }

        total = 0;
        available = 0;
        return false;
    }

    private static double BytesToGigabytes(ulong value)
    {
        return value / 1024d / 1024d / 1024d;
    }

    private static double BytesToGigabytes(long value)
    {
        return value / 1024d / 1024d / 1024d;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public void Init()
        {
            dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
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
