using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI.Services.Camera;
using ShockUI.ViewModels;
using ShockUI.Views;

namespace ShockUI.ViewModels.Modules;

/// <summary>
/// Partial class extension — simulation fault injection only.
/// Add this file to ViewModels/Modules/ alongside the original
/// CameraControllerViewModel.cs. Do NOT modify the original.
/// </summary>
public sealed partial class CameraControllerViewModel
{
    // ── Status ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool isSimFaultMode;

    // ── Handshake ────────────────────────────────────────────────────────
    /// <summary>Simulate ZG3 not supported — camera reports 2-axis only.</summary>
    [ObservableProperty] private bool faultZg3NotSupported;

    // ── Position ─────────────────────────────────────────────────────────
    /// <summary>ZG1 position reached flag = 0 (motor still moving).</summary>
    [ObservableProperty] private bool faultZg1NotReached;

    /// <summary>ZG2 position reached flag = 0.</summary>
    [ObservableProperty] private bool faultZg2NotReached;

    /// <summary>ZG3 position reached flag = 0.</summary>
    [ObservableProperty] private bool faultZg3NotReached;

    // ── Thermal ──────────────────────────────────────────────────────────
    /// <summary>Return out-of-range temperatures (~85°C).</summary>
    [ObservableProperty] private bool faultTemperatureOutOfRange;

    // ── System ───────────────────────────────────────────────────────────
    /// <summary>Calibration response returns success = 0.</summary>
    [ObservableProperty] private bool faultCalibrationFail;

    /// <summary>Logger reports 0 stored points.</summary>
    [ObservableProperty] private bool faultLoggerEmpty;

    public bool HasAnyFaultActive =>
        FaultZg3NotSupported || FaultZg1NotReached || FaultZg2NotReached || FaultZg3NotReached ||
        FaultTemperatureOutOfRange || FaultCalibrationFail || FaultLoggerEmpty;

    [RelayCommand]
    private void ApplyFaults()
    {
        _cameraSerialService.SetSimFaults(new CameraSimFaultConfig
        {
            Zg3NotSupported = FaultZg3NotSupported,
            Zg1NotReached = FaultZg1NotReached,
            Zg2NotReached = FaultZg2NotReached,
            Zg3NotReached = FaultZg3NotReached,
            TemperatureOutOfRange = FaultTemperatureOutOfRange,
            CalibrationFail = FaultCalibrationFail,
            LoggerEmpty = FaultLoggerEmpty,
        });
        IsSimFaultMode = HasAnyFaultActive;
        if (IsSimFaultMode)
            ShowWarning("Fault injection ACTIVE — sim will return injected faults.");
        else
            ShowInfo("All faults cleared — sim returning healthy state.");
    }

    [RelayCommand]
    private void ClearAllFaults()
    {
        FaultZg3NotSupported = FaultZg1NotReached = FaultZg2NotReached = FaultZg3NotReached =
        FaultTemperatureOutOfRange = FaultCalibrationFail = FaultLoggerEmpty = false;
        ApplyFaults();
    }

    /// <summary>
    /// Opens the Port Settings popup so the user can tweak baud rate,
    /// parity, timeouts, and auto-reconnect at runtime.
    /// </summary>
    [RelayCommand]
    private void OpenPortSettings()
    {
        var window = new Window
        {
            Title = "Camera Port Settings",
            Width = 380,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var vm = new PortSettingsViewModel(
            current: _cameraSerialService.GetPortSettings(),
            autoReconnectEnabled: _cameraSerialService.AutoReconnectEnabled,
            applySettings: s => { _cameraSerialService.ApplyPortSettings(s); RefreshTransportState(); },
            applyAutoReconnect: v => _cameraSerialService.AutoReconnectEnabled = v,
            onClose: () => window.Close());

        window.Content = new PortSettingsView { DataContext = vm };
        window.Show();
    }
}