using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using WinForms = System.Windows.Forms;

namespace Spocky.Services;

/// <summary>
/// Driverless hardware polling service that leans on ETW/WMI/managed APIs only.
/// </summary>
public sealed class HardwareService : IDisposable
{
    private const string NetworkCategory = "Network Interface";
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(1.5);
    private readonly object _driveLock = new();

    private System.Threading.Timer? _timer;
    private bool _disposed;
    private string _driveRoot;

    private PerformanceCounter? _cpuUsageCounter;
    private PerformanceCounter? _cpuFrequencyCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private readonly List<PerformanceCounter> _networkSendCounters = new();
    private readonly List<PerformanceCounter> _networkReceiveCounters = new();

    public event EventHandler<HardwareSnapshot>? MetricsUpdated;

    public HardwareService()
    {
        _driveRoot = NormalizeDriveRoot(Path.GetPathRoot(Environment.SystemDirectory)) ?? "C:\\";
        InitializePerformanceCounters();
        SelectBestDriveDefault();
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

        _timer = new System.Threading.Timer(OnTick, null, TimeSpan.Zero, _pollInterval);
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
        var (diskRead, diskWrite) = ReadDiskThroughput();
        var (netSend, netReceive) = ReadNetworkThroughput();
        var (batteryPercent, batteryOnAc, batteryCharging) = ReadBatteryStatus();

        return new HardwareSnapshot(
            CpuPackageTempC: ReadCpuTemperature(),
            GpuCoreTempC: ReadGpuTemperature(),
            MemoryUsedGb: memoryUsedGb,
            MemoryTotalGb: memoryTotalGb,
            MemoryUsagePercent: memoryPercent,
            StorageUsedGb: storageUsedGb,
            StorageTotalGb: storageTotalGb,
            StorageUsagePercent: storagePercent,
            CpuUsagePercent: ReadCpuUsagePercent(),
            CpuFrequencyMHz: ReadCpuFrequencyMHz(),
            DiskReadMbPerSec: diskRead,
            DiskWriteMbPerSec: diskWrite,
            NetworkSendMbps: netSend,
            NetworkReceiveMbps: netReceive,
            SystemUptimeSeconds: ReadSystemUptimeSeconds(),
            BatteryPercent: batteryPercent,
            IsOnAcPower: batteryOnAc,
            IsBatteryCharging: batteryCharging);
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

    private double? ReadCpuUsagePercent()
    {
        return SafeNextValue(_cpuUsageCounter);
    }

    private double? ReadCpuFrequencyMHz()
    {
        return SafeNextValue(_cpuFrequencyCounter);
    }

    private (double? read, double? write) ReadDiskThroughput()
    {
        var read = SafeNextValue(_diskReadCounter);
        var write = SafeNextValue(_diskWriteCounter);

        return (ToMegabytesPerSecond(read), ToMegabytesPerSecond(write));
    }

    private (double? send, double? receive) ReadNetworkThroughput()
    {
        if (_networkSendCounters.Count == 0 || _networkReceiveCounters.Count == 0)
        {
            return (null, null);
        }

        double totalSend = 0;
        double totalReceive = 0;
        foreach (var counter in _networkSendCounters)
        {
            var value = SafeNextValue(counter);
            if (value.HasValue)
            {
                totalSend += value.Value;
            }
        }

        foreach (var counter in _networkReceiveCounters)
        {
            var value = SafeNextValue(counter);
            if (value.HasValue)
            {
                totalReceive += value.Value;
            }
        }

        return (ToMegabitsPerSecond(totalSend), ToMegabitsPerSecond(totalReceive));
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
        return null;
    }

    private (double? chargePercent, bool? isOnAc, bool? isCharging) ReadBatteryStatus()
    {
        try
        {
            var status = WinForms.SystemInformation.PowerStatus;
            var flags = status.BatteryChargeStatus;
            var hasBattery = !flags.HasFlag(WinForms.BatteryChargeStatus.NoSystemBattery);

            double? percent = status.BatteryLifePercent < 0 || !hasBattery
                ? null
                : status.BatteryLifePercent * 100.0;

            bool? onAc = status.PowerLineStatus switch
            {
                WinForms.PowerLineStatus.Online => true,
                WinForms.PowerLineStatus.Offline => false,
                _ => null
            };

            bool? charging = hasBattery
                ? flags.HasFlag(WinForms.BatteryChargeStatus.Charging)
                : null;

            return (percent, onAc, charging);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static double ReadSystemUptimeSeconds()
    {
        return TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds;
    }

    private void InitializePerformanceCounters()
    {
        _cpuUsageCounter = CreateCounter("Processor", "% Processor Time", "_Total");
        _cpuFrequencyCounter = CreateCounter("Processor Information", "Processor Frequency", "_Total");
        _diskReadCounter = CreateCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        _diskWriteCounter = CreateCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        InitializeNetworkCounters();
    }

    private void InitializeNetworkCounters()
    {
        _networkSendCounters.Clear();
        _networkReceiveCounters.Clear();

        if (!PerformanceCounterCategory.Exists(NetworkCategory))
        {
            return;
        }

        var category = new PerformanceCounterCategory(NetworkCategory);
        foreach (var instance in category.GetInstanceNames())
        {
            if (IsLoopback(instance))
            {
                continue;
            }

            var send = CreateCounter(NetworkCategory, "Bytes Sent/sec", instance);
            var receive = CreateCounter(NetworkCategory, "Bytes Received/sec", instance);

            if (send != null && receive != null)
            {
                _networkSendCounters.Add(send);
                _networkReceiveCounters.Add(receive);
            }
        }
    }

    private static bool IsLoopback(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return true;
        }

        var name = interfaceName.ToLowerInvariant();
        return name.Contains("loopback") || name.Contains("isatap") || name.Contains("tunnel");
    }

    private static PerformanceCounter? CreateCounter(string category, string counter, string? instance, bool readOnly = true)
    {
        try
        {
            if (instance == null)
            {
                return new PerformanceCounter(category, counter, readOnly: readOnly);
            }

            return new PerformanceCounter(category, counter, instance, readOnly: readOnly);
        }
        catch
        {
            return null;
        }
    }

    private static double? SafeNextValue(PerformanceCounter? counter)
    {
        if (counter == null)
        {
            return null;
        }

        try
        {
            var value = counter.NextValue();
            return double.IsNaN(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static double? ToMegabytesPerSecond(double? bytesPerSecond)
    {
        return bytesPerSecond.HasValue ? bytesPerSecond / 1024d / 1024d : null;
    }

    private static double? ToMegabitsPerSecond(double bytesPerSecond)
    {
        return bytesPerSecond <= 0 ? null : (bytesPerSecond * 8d) / 1024d / 1024d;
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

    private void SelectBestDriveDefault()
    {
        var drives = GetFixedDriveRoots();
        if (drives.Count == 0)
        {
            return;
        }

        if (!drives.Any(root => string.Equals(root, _driveRoot, StringComparison.OrdinalIgnoreCase)))
        {
            _driveRoot = drives.First();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        DisposeCounters();
    }

    private void DisposeCounters()
    {
        _cpuUsageCounter?.Dispose();
        _cpuFrequencyCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();

        foreach (var counter in _networkSendCounters)
        {
            counter.Dispose();
        }

        foreach (var counter in _networkReceiveCounters)
        {
            counter.Dispose();
        }
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
    double? StorageUsagePercent,
    double? CpuUsagePercent,
    double? CpuFrequencyMHz,
    double? DiskReadMbPerSec,
    double? DiskWriteMbPerSec,
    double? NetworkSendMbps,
    double? NetworkReceiveMbps,
    double? SystemUptimeSeconds,
    double? BatteryPercent,
    bool? IsOnAcPower,
    bool? IsBatteryCharging);
