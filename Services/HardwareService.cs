using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Spocky.Services;

/// <summary>
/// Hardware polling service that relies on ETW/WMI/managed APIs so no kernel driver is required.
/// </summary>
public sealed class HardwareService : IDisposable
{
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1.5);
    private readonly object _driveLock = new();
    private Timer? _timer;
    private bool _disposed;
    private string _driveRoot;

    public event EventHandler<HardwareSnapshot>? MetricsUpdated;

    public HardwareService()
    {
        var preferred = NormalizeDriveRoot(Path.GetPathRoot(Environment.SystemDirectory)) ?? "C:\\";
        var drives = EnumerateFixedDriveRoots()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        _driveRoot = drives.FirstOrDefault(root => string.Equals(root, preferred, StringComparison.OrdinalIgnoreCase))
            ?? drives.FirstOrDefault()
            ?? preferred;
    }

    public string CurrentDrive
    {
        get
        {
            lock (_driveLock)
            {
                return _driveRoot;
            }
        }
    }

    public IReadOnlyList<string> GetFixedDriveRoots()
    {
        return EnumerateFixedDriveRoots()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(root => root)
            .ToList();
    }

    public bool SetDrive(string? driveRoot)
    {
        var normalized = NormalizeDriveRoot(driveRoot);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        var available = GetFixedDriveRoots();
        if (!available.Any(root => string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        lock (_driveLock)
        {
            _driveRoot = normalized;
        }

        return true;
    }

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

    private HardwareSnapshot CollectSnapshot()
    {
        string driveRoot;
        lock (_driveLock)
        {
            driveRoot = _driveRoot;
        }

        var (memoryUsedGb, memoryTotalGb, memoryPercent) = ReadMemory();
        var (storageUsedGb, storageTotalGb, storagePercent) = ReadPrimaryStorage(driveRoot);

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

    private static (double? usedGb, double? totalGb, double? percent) ReadPrimaryStorage(string? preferredDrive)
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToList();

            if (drives.Count == 0)
            {
                return (null, null, null);
            }

            var normalizedPreferred = NormalizeDriveRoot(preferredDrive);
            var drive = drives
                .FirstOrDefault(d => string.Equals(NormalizeDriveRoot(d.Name), normalizedPreferred, StringComparison.OrdinalIgnoreCase))
                ?? drives.First();

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

    private static IEnumerable<string> EnumerateFixedDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
            {
                continue;
            }

            var root = NormalizeDriveRoot(drive.Name);
            if (!string.IsNullOrEmpty(root))
            {
                yield return root;
            }
        }
    }

    private static string? NormalizeDriveRoot(string? driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot))
        {
            return null;
        }

        var root = Path.GetPathRoot(driveRoot);
        return string.IsNullOrEmpty(root) ? null : root.ToUpperInvariant();
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
