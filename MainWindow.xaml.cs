using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spocky.Models;
using Spocky.Services;

namespace Spocky;

public partial class MainWindow : Window
{
    private readonly HardwareService _hardwareService = new();
    private readonly SettingsService _settingsService = new();
    private readonly ProcessMonitorService _processMonitorService = new();
    private readonly List<DriveOption> _driveOptions = new();
    private IReadOnlyList<ProcessSnapshot> _latestProcessSnapshots = Array.Empty<ProcessSnapshot>();
    private AppSettings _settings = new();
    private HardwareSnapshot? _latestSnapshot;
    private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        _temperatureUnit = _settings.TemperatureUnit;
        if (!string.IsNullOrWhiteSpace(_settings.DriveRoot))
        {
            _hardwareService.SetDrive(_settings.DriveRoot);
        }

        InitializeDriveSelector();
        InitializeProcessControls();
        UpdateTemperatureButtons();
        _hardwareService.MetricsUpdated += OnHardwareMetricsUpdated;
        _hardwareService.Start();

        _processMonitorService.ProcessesUpdated += OnProcessesUpdated;
        _processMonitorService.SetRefreshInterval(TimeSpan.FromSeconds(_settings.ProcessRefreshSeconds));
        _processMonitorService.Start();
    }

    private void OnHardwareMetricsUpdated(object? sender, HardwareSnapshot snapshot)
    {
        Dispatcher.Invoke(() =>
        {
            _latestSnapshot = snapshot;
            UpdateMetrics(snapshot);
        });
    }

    private void UpdateMetrics(HardwareSnapshot snapshot)
    {
        CpuTempText.Text = snapshot.CpuPackageTempC.HasValue
            ? FormatTemperature(snapshot.CpuPackageTempC.Value)
            : TemperaturePlaceholder();

        GpuTempText.Text = snapshot.GpuCoreTempC.HasValue
            ? FormatTemperature(snapshot.GpuCoreTempC.Value)
            : TemperaturePlaceholder();

        CpuLoadText.Text = snapshot.CpuUsagePercent.HasValue
            ? FormatPercent(snapshot.CpuUsagePercent.Value)
            : "-- %";

        CpuClockText.Text = snapshot.CpuFrequencyMHz.HasValue
            ? FormatFrequency(snapshot.CpuFrequencyMHz.Value)
            : "-- MHz";

        if (snapshot.MemoryUsagePercent.HasValue)
        {
            MemoryUsageText.Text = FormatPercent(snapshot.MemoryUsagePercent.Value);
        }
        else
        {
            MemoryUsageText.Text = "-- %";
        }

        if (snapshot.MemoryUsedGb.HasValue && snapshot.MemoryTotalGb.HasValue)
        {
            MemoryDetailText.Text = $"{snapshot.MemoryUsedGb.Value:F1} / {snapshot.MemoryTotalGb.Value:F1} GB";
        }
        else
        {
            MemoryDetailText.Text = "-- / -- GB";
        }

        if (snapshot.StorageUsagePercent.HasValue)
        {
            StorageUsageText.Text = FormatPercent(snapshot.StorageUsagePercent.Value);
        }
        else
        {
            StorageUsageText.Text = "-- %";
        }

        if (snapshot.StorageUsedGb.HasValue && snapshot.StorageTotalGb.HasValue)
        {
            StorageDetailText.Text = $"{snapshot.StorageUsedGb.Value:F1} / {snapshot.StorageTotalGb.Value:F1} GB";
        }
        else
        {
            StorageDetailText.Text = "-- / -- GB";
        }

        DiskReadText.Text = FormatDataRate("Read", snapshot.DiskReadMbPerSec);
        DiskWriteText.Text = FormatDataRate("Write", snapshot.DiskWriteMbPerSec);
        NetworkSendText.Text = FormatNetworkRate("Up", snapshot.NetworkSendMbps);
        NetworkReceiveText.Text = FormatNetworkRate("Down", snapshot.NetworkReceiveMbps);
        UptimeText.Text = snapshot.SystemUptimeSeconds.HasValue
            ? FormatUptime(snapshot.SystemUptimeSeconds.Value)
            : "--";

        BatteryPercentText.Text = snapshot.BatteryPercent.HasValue
            ? $"{snapshot.BatteryPercent.Value:F0} %"
            : "-- %";
        BatteryStatusText.Text = FormatBatteryStatus(snapshot);
    }

    private void OnProcessesUpdated(object? sender, IReadOnlyList<ProcessSnapshot> snapshots)
    {
        Dispatcher.Invoke(() => UpdateProcessList(snapshots));
    }

    private void UpdateProcessList(IReadOnlyList<ProcessSnapshot> snapshots)
    {
        _latestProcessSnapshots = snapshots;

        IEnumerable<ProcessSnapshot> ordered = _settings.ProcessSort switch
        {
            ProcessSortOption.Memory => snapshots.OrderByDescending(p => p.MemoryMb),
            _ => snapshots.OrderByDescending(p => p.CpuPercent)
        };

        var displays = ordered
            .Take(8)
            .Select(p => new ProcessDisplay(
                p.Name,
                $"CPU {p.CpuPercent:F1} %",
                $"MEM {p.MemoryMb:F1} MB"))
            .ToList();

        ProcessListControl.ItemsSource = displays;
    }

    private string FormatTemperature(double celsius)
    {
        var value = _temperatureUnit == TemperatureUnit.Celsius
            ? celsius
            : (celsius * 9.0 / 5.0) + 32.0;

        var suffix = _temperatureUnit == TemperatureUnit.Celsius ? "°C" : "°F";
        return $"{value.ToString("F1", CultureInfo.InvariantCulture)} {suffix}";
    }

    private static string FormatPercent(double value)
    {
        return $"{value.ToString("F0", CultureInfo.InvariantCulture)} %";
    }

    private static string FormatFrequency(double mhz)
    {
        return mhz >= 1000
            ? $"{(mhz / 1000d).ToString("F2", CultureInfo.InvariantCulture)} GHz"
            : $"{mhz.ToString("F0", CultureInfo.InvariantCulture)} MHz";
    }

    private string TemperaturePlaceholder()
    {
        return _temperatureUnit == TemperatureUnit.Celsius ? "-- °C" : "-- °F";
    }

    private static string FormatDataRate(string label, double? value)
    {
        return value.HasValue
            ? $"{label} {value.Value.ToString("F1", CultureInfo.InvariantCulture)} MB/s"
            : $"{label} -- MB/s";
    }

    private static string FormatNetworkRate(string label, double? value)
    {
        return value.HasValue
            ? $"{label} {value.Value.ToString("F1", CultureInfo.InvariantCulture)} Mb/s"
            : $"{label} -- Mb/s";
    }

    private static string FormatUptime(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours:D2}h {span.Minutes:D2}m";
        }

        return $"{span.Hours:D2}h {span.Minutes:D2}m";
    }

    private static string FormatBatteryStatus(HardwareSnapshot snapshot)
    {
        if (!snapshot.BatteryPercent.HasValue && snapshot.IsOnAcPower == null && snapshot.IsBatteryCharging == null)
        {
            return "No battery";
        }

        if (snapshot.IsOnAcPower == true)
        {
            return snapshot.IsBatteryCharging == true ? "Charging (AC)" : "On AC power";
        }

        if (snapshot.IsOnAcPower == false)
        {
            return "On battery";
        }

        return "Battery status unknown";
    }

    private void InitializeDriveSelector()
    {
        _driveOptions.Clear();
        foreach (var root in _hardwareService.GetFixedDriveRoots())
        {
            _driveOptions.Add(new DriveOption(root, FormatDriveLabel(root)));
        }

        DriveSelector.ItemsSource = _driveOptions.ToList();
        var preferred = _settings.DriveRoot ?? _hardwareService.CurrentDrive;
        DriveSelector.SelectedValue = _driveOptions
            .FirstOrDefault(opt => string.Equals(opt.Root, preferred, StringComparison.OrdinalIgnoreCase))?
            .Root;

        if (DriveSelector.SelectedValue == null && DriveSelector.Items.Count > 0)
        {
            DriveSelector.SelectedIndex = 0;
            if (DriveSelector.SelectedValue is string fallback)
            {
                _hardwareService.SetDrive(fallback);
            }
        }

        UpdateStorageTitle();
    }

    private void InitializeProcessControls()
    {
        var sortOptions = new[]
        {
            new ComboOption<ProcessSortOption>(ProcessSortOption.Cpu, "CPU %"),
            new ComboOption<ProcessSortOption>(ProcessSortOption.Memory, "Memory")
        };
        ProcessSortSelector.ItemsSource = sortOptions;
        ProcessSortSelector.SelectedValue = _settings.ProcessSort;

        var refreshOptions = new[]
        {
            new ComboOption<int>(1, "1s"),
            new ComboOption<int>(2, "2s"),
            new ComboOption<int>(5, "5s"),
            new ComboOption<int>(10, "10s")
        };
        ProcessRefreshSelector.ItemsSource = refreshOptions;
        var refresh = Math.Clamp(_settings.ProcessRefreshSeconds, 1, 10);
        ProcessRefreshSelector.SelectedValue = refresh;
        _settings.ProcessRefreshSeconds = refresh;
    }

    private static string FormatDriveLabel(string root)
    {
        return root.TrimEnd('\\');
    }

    private void UpdateStorageTitle()
    {
        if (DriveSelector.SelectedItem is DriveOption option)
        {
            StorageTitleText.Text = $"STORAGE {option.Label}";
        }
        else
        {
            StorageTitleText.Text = "PRIMARY STORAGE";
        }
    }

    private void OnDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveSelector.SelectedValue is not string root)
        {
            return;
        }

        if (_hardwareService.SetDrive(root))
        {
            UpdateStorageTitle();
            _settings.DriveRoot = root;
            PersistSettings();
        }
    }

    private void OnProcessSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessSortSelector.SelectedValue is not ProcessSortOption sort || sort == _settings.ProcessSort)
        {
            return;
        }

        _settings.ProcessSort = sort;
        PersistSettings();
        UpdateProcessList(_latestProcessSnapshots);
    }

    private void OnProcessRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessRefreshSelector.SelectedValue is not int seconds)
        {
            return;
        }

        if (_settings.ProcessRefreshSeconds == seconds)
        {
            return;
        }

        _settings.ProcessRefreshSeconds = seconds;
        _processMonitorService.SetRefreshInterval(TimeSpan.FromSeconds(seconds));
        PersistSettings();
    }

    private void SelectCelsius(object sender, RoutedEventArgs e)
    {
        SetTemperatureUnit(TemperatureUnit.Celsius);
    }

    private void SelectFahrenheit(object sender, RoutedEventArgs e)
    {
        SetTemperatureUnit(TemperatureUnit.Fahrenheit);
    }

    private void SetTemperatureUnit(TemperatureUnit unit)
    {
        if (_temperatureUnit == unit)
        {
            return;
        }

        _temperatureUnit = unit;
        UpdateTemperatureButtons();
        if (_latestSnapshot.HasValue)
        {
            UpdateMetrics(_latestSnapshot.Value);
        }

        _settings.TemperatureUnit = unit;
        PersistSettings();
    }

    private void UpdateTemperatureButtons()
    {
        var active = (System.Windows.Media.Brush)FindResource("LcNeonBlue");
        var inactive = (System.Windows.Media.Brush)FindResource("LcMutedOrange");

        SetChipVisuals(CelsiusButton, _temperatureUnit == TemperatureUnit.Celsius ? active : inactive);
        SetChipVisuals(FahrenheitButton, _temperatureUnit == TemperatureUnit.Fahrenheit ? active : inactive);
    }

    private static void SetChipVisuals(System.Windows.Controls.Button button, System.Windows.Media.Brush brush)
    {
        button.Background = brush;
        button.BorderBrush = brush;
    }

    private void HandleWindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindow(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _hardwareService.MetricsUpdated -= OnHardwareMetricsUpdated;
        _hardwareService.Dispose();
        _processMonitorService.ProcessesUpdated -= OnProcessesUpdated;
        _processMonitorService.Dispose();
        PersistSettings();
    }

    private sealed record DriveOption(string Root, string Label);

    private sealed record ProcessDisplay(string Name, string CpuText, string MemoryText);

    private sealed record ComboOption<T>(T Value, string Label);

    private void PersistSettings()
    {
        _settingsService.Save(_settings);
    }
}
