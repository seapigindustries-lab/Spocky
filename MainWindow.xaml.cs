using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spocky.Services;

namespace Spocky;

public partial class MainWindow : Window
{
    private readonly HardwareService _hardwareService = new();
    private readonly List<DriveOption> _driveOptions = new();
    private HardwareSnapshot? _latestSnapshot;
    private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeDriveSelector();
        UpdateTemperatureButtons();
        _hardwareService.MetricsUpdated += OnHardwareMetricsUpdated;
        _hardwareService.Start();
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

    private string TemperaturePlaceholder()
    {
        return _temperatureUnit == TemperatureUnit.Celsius ? "-- °C" : "-- °F";
    }

    private void InitializeDriveSelector()
    {
        _driveOptions.Clear();
        foreach (var root in _hardwareService.GetFixedDriveRoots())
        {
            _driveOptions.Add(new DriveOption(root, FormatDriveLabel(root)));
        }

        DriveSelector.ItemsSource = _driveOptions.ToList();
        var current = _hardwareService.CurrentDrive;
        DriveSelector.SelectedValue = _driveOptions
            .FirstOrDefault(opt => string.Equals(opt.Root, current, StringComparison.OrdinalIgnoreCase))?
            .Root;

        if (DriveSelector.SelectedValue == null && DriveSelector.Items.Count > 0)
        {
            DriveSelector.SelectedIndex = 0;
        }

        UpdateStorageTitle();
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
        }
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
    }

    private void UpdateTemperatureButtons()
    {
        var active = (Brush)FindResource("LcNeonBlue");
        var inactive = (Brush)FindResource("LcMutedOrange");

        SetChipVisuals(CelsiusButton, _temperatureUnit == TemperatureUnit.Celsius ? active : inactive);
        SetChipVisuals(FahrenheitButton, _temperatureUnit == TemperatureUnit.Fahrenheit ? active : inactive);
    }

    private static void SetChipVisuals(Button button, Brush brush)
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
    }

    private sealed record DriveOption(string Root, string Label);

    private enum TemperatureUnit
    {
        Celsius,
        Fahrenheit
    }
}
