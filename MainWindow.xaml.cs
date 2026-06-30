using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Spocky.Services;

namespace Spocky;

public partial class MainWindow : Window
{
    private readonly HardwareService _hardwareService = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hardwareService.MetricsUpdated += OnHardwareMetricsUpdated;
        _hardwareService.Start();
    }

    private void OnHardwareMetricsUpdated(object? sender, HardwareSnapshot snapshot)
    {
        Dispatcher.Invoke(() => UpdateMetrics(snapshot));
    }

    private void UpdateMetrics(HardwareSnapshot snapshot)
    {
        CpuTempText.Text = snapshot.CpuPackageTempC.HasValue
            ? FormatTemperature(snapshot.CpuPackageTempC.Value)
            : "-- °C";

        GpuTempText.Text = snapshot.GpuCoreTempC.HasValue
            ? FormatTemperature(snapshot.GpuCoreTempC.Value)
            : "-- °C";

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

    private static string FormatTemperature(double value)
    {
        return $"{value.ToString("F1", CultureInfo.InvariantCulture)} °C";
    }

    private static string FormatPercent(double value)
    {
        return $"{value.ToString("F0", CultureInfo.InvariantCulture)} %";
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
}
