namespace Spocky.Models;

public readonly record struct ProcessSnapshot(
    int Id,
    string Name,
    double CpuPercent,
    double MemoryMb);
