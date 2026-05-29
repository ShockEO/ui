namespace ShockUI.Services.Device;

/// <summary>
/// Fault injection config for DeviceSerialService simulation.
/// Maps to specific bit positions in the SRS §3.3 response payloads.
/// All false = healthy. 
/// </summary>
public sealed class DeviceSimFaultConfig
{
    // ── §3.3.1 General Status ─────────────────────────────────────────────

    /// <summary>Byte 9 bit0 – Power failure.</summary>
    public bool PowerFailure { get; set; }

    /// <summary>Byte 9 bit1 – Fibre error.</summary>
    public bool FibreError { get; set; }

    /// <summary>Byte 9 bit2 – IMU error.</summary>
    public bool ImuError { get; set; }

    /// <summary>Byte 10 bit0 – LRF not safe.</summary>
    public bool LrfNotSafe { get; set; }

    /// <summary>Byte 10 bit1 – LPI not safe.</summary>
    public bool LpiNotSafe { get; set; }

    /// <summary>Force system state to State F: Error instead of State C: Operational.</summary>
    public bool SystemStateError { get; set; }

    /// <summary>Byte 16 bit0 – Humidity sensor triggered.</summary>
    public bool HumidityTriggered { get; set; }

    /// <summary>Byte 16 bit1 – Pressure sensor triggered.</summary>
    public bool PressureTriggered { get; set; }

    /// <summary>Byte 16 bit2 – Temperature out of range.</summary>
    public bool TemperatureOutOfRange { get; set; }

    /// <summary>Byte 26 bit0 – MWIR cooler busy with cooldown.</summary>
    public bool MwirCoolerBusy { get; set; }

    /// <summary>Byte 26 bit4 – MWIR motor initialisation fault.</summary>
    public bool MwirMotorFault { get; set; }

    /// <summary>Byte 27 bit3 – NIR motor initialisation fault.</summary>
    public bool NirMotorFault { get; set; }

    /// <summary>Byte 28 bit1 – Pan motor fault.</summary>
    public bool PanMotorFault { get; set; }

    /// <summary>Byte 29 bit1 – Tilt motor fault.</summary>
    public bool TiltMotorFault { get; set; }

    /// <summary>Byte 32 bit0 – LRF hardware fault.</summary>
    public bool LrfFault { get; set; }

    /// <summary>Byte 33 bit0 – LPI hardware fault.</summary>
    public bool LpiFault { get; set; }

    // ── §3.3.8  FOV ───────────────────────────────────────────────────────

    /// <summary>FOV responses return FovReached=0 (still moving).</summary>
    public bool FovNotReached { get; set; }

    // ── §3.3.11 LRF ──────────────────────────────────────────────────────

    /// <summary>LRF returns no return (all ranges = 0xFFFF = out of range).</summary>
    public bool LrfNoReturn { get; set; }

    // ── §3.3.16 IBIT ─────────────────────────────────────────────────────

    /// <summary>IBIT returns FAILED with multiple subsystem faults active.</summary>
    public bool IbitFailed { get; set; }

    public bool HasAnyFault =>
        PowerFailure || FibreError || ImuError || LrfNotSafe || LpiNotSafe ||
        SystemStateError || HumidityTriggered || PressureTriggered || TemperatureOutOfRange ||
        MwirCoolerBusy || MwirMotorFault || NirMotorFault || PanMotorFault || TiltMotorFault ||
        LrfFault || LpiFault || FovNotReached || LrfNoReturn || IbitFailed;
}