using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI.Services.OpticalModules;

namespace ShockUI.ViewModels.Modules;

/// <summary>
/// Partial class extension — simulation fault injection only.
/// Add this file to ViewModels/Modules/ alongside the original
/// OpticalModuleControllerViewModel.cs. Do NOT modify the original.
/// </summary>
public partial class OpticalModuleControllerViewModel
{
    // ── Status ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool isSimFaultMode;

    // ── Controller PBIT ──────────────────────────────────────────────────
    [ObservableProperty] private bool faultControllerVoltage;
    [ObservableProperty] private bool faultControllerPsu;
    [ObservableProperty] private bool faultControllerTemp;
    [ObservableProperty] private bool faultControllerFlash;

    // ── ZG1 PBIT ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool faultZg1Adc;
    [ObservableProperty] private bool faultZg1MotorConnection;
    [ObservableProperty] private bool faultZg1EncoderConnection;
    [ObservableProperty] private bool faultZg1EncoderPolarity;
    [ObservableProperty] private bool faultZg1Stall;
    [ObservableProperty] private bool faultZg1NotCalibrated;

    // ── ZG2 PBIT ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool faultZg2MotorStart;
    [ObservableProperty] private bool faultZg2MotorConnection;
    [ObservableProperty] private bool faultZg2MotorPolarity;
    [ObservableProperty] private bool faultZg2MinEndstop;
    [ObservableProperty] private bool faultZg2MaxEndstop;
    [ObservableProperty] private bool faultZg2Stall;
    [ObservableProperty] private bool faultZg2EncoderFail;

    // ── Temp sensors ─────────────────────────────────────────────────────
    [ObservableProperty] private bool faultTemp1Connection;
    [ObservableProperty] private bool faultTemp1OutOfRange;
    [ObservableProperty] private bool faultTemp2Connection;
    [ObservableProperty] private bool faultTemp2OutOfRange;

    // ── System ───────────────────────────────────────────────────────────
    [ObservableProperty] private bool faultSystemStateError;
    [ObservableProperty] private bool faultExternalAlarm;
    [ObservableProperty] private bool faultProcessorAlarm;

    // ── Runtime ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool faultZg1SimStall;
    [ObservableProperty] private bool faultZg2SimStall;

    public bool HasAnyFaultActive =>
        FaultControllerVoltage || FaultControllerPsu || FaultControllerTemp || FaultControllerFlash ||
        FaultZg1Adc || FaultZg1MotorConnection || FaultZg1EncoderConnection || FaultZg1EncoderPolarity ||
        FaultZg1Stall || FaultZg1NotCalibrated ||
        FaultZg2MotorStart || FaultZg2MotorConnection || FaultZg2MotorPolarity ||
        FaultZg2MinEndstop || FaultZg2MaxEndstop || FaultZg2Stall || FaultZg2EncoderFail ||
        FaultTemp1Connection || FaultTemp1OutOfRange || FaultTemp2Connection || FaultTemp2OutOfRange ||
        FaultSystemStateError || FaultExternalAlarm || FaultProcessorAlarm ||
        FaultZg1SimStall || FaultZg2SimStall;

    [RelayCommand]
    private void ApplyFaults()
    {
        _serialService.SetSimFaults(new OpticalModuleSimFaultConfig
        {
            ControllerVoltageFail = FaultControllerVoltage,
            ControllerPsuFail = FaultControllerPsu,
            ControllerTempFail = FaultControllerTemp,
            ControllerFlashFail = FaultControllerFlash,
            Zg1AdcFail = FaultZg1Adc,
            Zg1MotorConnectionFail = FaultZg1MotorConnection,
            Zg1EncoderConnectionFail = FaultZg1EncoderConnection,
            Zg1EncoderPolarityFail = FaultZg1EncoderPolarity,
            Zg1MotorStall = FaultZg1Stall,
            Zg1NotCalibrated = FaultZg1NotCalibrated,
            Zg2MotorStartFail = FaultZg2MotorStart,
            Zg2MotorConnectionFail = FaultZg2MotorConnection,
            Zg2MotorPolarityFail = FaultZg2MotorPolarity,
            Zg2MinEndstopFail = FaultZg2MinEndstop,
            Zg2MaxEndstopFail = FaultZg2MaxEndstop,
            Zg2MotorStall = FaultZg2Stall,
            Zg2EncoderFail = FaultZg2EncoderFail,
            Temp1ConnectionFail = FaultTemp1Connection,
            Temp1OutOfRange = FaultTemp1OutOfRange,
            Temp2ConnectionFail = FaultTemp2Connection,
            Temp2OutOfRange = FaultTemp2OutOfRange,
            SystemStateError = FaultSystemStateError,
            ExternalDeviceAlarm = FaultExternalAlarm,
            ProcessorAlarm = FaultProcessorAlarm,
            Zg1Stall = FaultZg1SimStall,
            Zg2Stall = FaultZg2SimStall,
        });
        IsSimFaultMode = HasAnyFaultActive;
    }

    [RelayCommand]
    private void ClearAllFaults()
    {
        FaultControllerVoltage = FaultControllerPsu = FaultControllerTemp = FaultControllerFlash =
        FaultZg1Adc = FaultZg1MotorConnection = FaultZg1EncoderConnection = FaultZg1EncoderPolarity =
        FaultZg1Stall = FaultZg1NotCalibrated =
        FaultZg2MotorStart = FaultZg2MotorConnection = FaultZg2MotorPolarity =
        FaultZg2MinEndstop = FaultZg2MaxEndstop = FaultZg2Stall = FaultZg2EncoderFail =
        FaultTemp1Connection = FaultTemp1OutOfRange = FaultTemp2Connection = FaultTemp2OutOfRange =
        FaultSystemStateError = FaultExternalAlarm = FaultProcessorAlarm =
        FaultZg1SimStall = FaultZg2SimStall = false;
        ApplyFaults();
    }
}