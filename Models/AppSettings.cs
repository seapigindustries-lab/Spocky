namespace Spocky.Models;

public class AppSettings
{
    public TemperatureUnit TemperatureUnit { get; set; } = TemperatureUnit.Celsius;

    public string? DriveRoot { get; set; }

    public ProcessSortOption ProcessSort { get; set; } = ProcessSortOption.Cpu;

    public int ProcessRefreshSeconds { get; set; } = 2;
}

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit
}

public enum ProcessSortOption
{
    Cpu,
    Memory
}
