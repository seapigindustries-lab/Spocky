using System.Diagnostics;
using System.Linq;
using System.Threading;
using Spocky.Models;

namespace Spocky.Services;

internal sealed class ProcessMonitorService : IDisposable
{
    private readonly Dictionary<int, CpuSample> _cpuSamples = new();
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private TimeSpan _refreshInterval = TimeSpan.FromSeconds(2);
    private bool _disposed;

    public event EventHandler<IReadOnlyList<ProcessSnapshot>>? ProcessesUpdated;

    public void SetRefreshInterval(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMilliseconds(500))
        {
            interval = TimeSpan.FromMilliseconds(500);
        }

        if (interval > TimeSpan.FromSeconds(10))
        {
            interval = TimeSpan.FromSeconds(10);
        }

        _refreshInterval = interval;

        if (_timer != null)
        {
            _timer.Change(TimeSpan.Zero, _refreshInterval);
        }
    }

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }

        _timer = new System.Threading.Timer(Tick, null, TimeSpan.Zero, _refreshInterval);
    }

    private void Tick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var snapshots = CollectSnapshots();
        ProcessesUpdated?.Invoke(this, snapshots);
    }

    private IReadOnlyList<ProcessSnapshot> CollectSnapshots()
    {
        var timestamp = DateTime.UtcNow;
        var processorCount = Environment.ProcessorCount;
        var list = new List<ProcessSnapshot>();
        Process[] processes;

        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return list;
        }

        var seen = new HashSet<int>();

        foreach (var process in processes)
        {
            try
            {
                var id = process.Id;
                seen.Add(id);
                var totalMs = process.TotalProcessorTime.TotalMilliseconds;
                var memoryMb = process.WorkingSet64 / 1024d / 1024d;

                var cpuPercent = 0d;

                lock (_lock)
                {
                    if (_cpuSamples.TryGetValue(id, out var sample))
                    {
                        var elapsedMs = (timestamp - sample.Timestamp).TotalMilliseconds;
                        if (elapsedMs > 0)
                        {
                            cpuPercent = ((totalMs - sample.TotalProcessorTimeMs) / (processorCount * elapsedMs)) * 100.0;
                            if (double.IsNaN(cpuPercent) || double.IsInfinity(cpuPercent) || cpuPercent < 0)
                            {
                                cpuPercent = 0;
                            }
                        }
                        _cpuSamples[id] = new CpuSample(totalMs, timestamp);
                    }
                    else
                    {
                        _cpuSamples[id] = new CpuSample(totalMs, timestamp);
                    }
                }

                list.Add(new ProcessSnapshot(
                    Id: id,
                    Name: process.ProcessName,
                    CpuPercent: Math.Round(cpuPercent, 1),
                    MemoryMb: Math.Round(memoryMb, 1)));
            }
            catch
            {
                // Ignore access issues
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // Ignore
                }
            }
        }

        lock (_lock)
        {
            var stale = _cpuSamples.Keys.Where(id => !seen.Contains(id)).ToList();
            foreach (var pid in stale)
            {
                _cpuSamples.Remove(pid);
            }
        }

        return list;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        lock (_lock)
        {
            _cpuSamples.Clear();
        }
    }

    private sealed record CpuSample(double TotalProcessorTimeMs, DateTime Timestamp);
}
