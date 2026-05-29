namespace ShockUI.Services.OpticalModules;

/// <summary>
/// Fault injection config for OpticalModuleSerialService simulation.
/// Maps to specific bits in the General Status and Zoom Feedback response payloads.
/// All false = healthy system.
/// </summary>
public sealed class OpticalModuleSimFaultConfig
{
    // ── General Status response (payload[8..12]) ──────────────────────────

    /// <summary>payload[8] bit0 – Controller input voltage fail.</summary>
    public bool ControllerVoltageFail    { get; set; }

    /// <summary>payload[8] bit1 – Controller PSU fail.</summary>
    public bool ControllerPsuFail        { get; set; }

    /// <summary>payload[8] bit2 – Controller temperature out of range.</summary>
    public bool ControllerTempFail       { get; set; }

    /// <summary>payload[8] bit3 – Flash fail.</summary>
    public bool ControllerFlashFail      { get; set; }

    /// <summary>payload[9] bit0 – ZG1 ADC fail.</summary>
    public bool Zg1AdcFail               { get; set; }

    /// <summary>payload[9] bit1 – ZG1 motor connection fail.</summary>
    public bool Zg1MotorConnectionFail   { get; set; }

    /// <summary>payload[9] bit2 – ZG1 encoder connection fail.</summary>
    public bool Zg1EncoderConnectionFail { get; set; }

    /// <summary>payload[9] bit3 – ZG1 encoder polarity fail.</summary>
    public bool Zg1EncoderPolarityFail   { get; set; }

    /// <summary>payload[9] bit4 – ZG1 motor stall.</summary>
    public bool Zg1MotorStall            { get; set; }

    /// <summary>payload[9] bit5 – ZG1 motor not calibrated.</summary>
    public bool Zg1NotCalibrated         { get; set; }

    /// <summary>payload[10] bit0 – ZG2 motor start fail.</summary>
    public bool Zg2MotorStartFail        { get; set; }

    /// <summary>payload[10] bit1 – ZG2 motor connection fail.</summary>
    public bool Zg2MotorConnectionFail   { get; set; }

    /// <summary>payload[10] bit2 – ZG2 motor polarity fail.</summary>
    public bool Zg2MotorPolarityFail     { get; set; }

    /// <summary>payload[10] bit3 – ZG2 min endstop not reached.</summary>
    public bool Zg2MinEndstopFail        { get; set; }

    /// <summary>payload[10] bit4 – ZG2 max endstop not reached.</summary>
    public bool Zg2MaxEndstopFail        { get; set; }

    /// <summary>payload[10] bit5 – ZG2 motor stall.</summary>
    public bool Zg2MotorStall            { get; set; }

    /// <summary>payload[10] bit6 – ZG2 quadrature encoder fail.</summary>
    public bool Zg2EncoderFail           { get; set; }

    /// <summary>payload[11] bit0 – Temp sensor 1 connection fail.</summary>
    public bool Temp1ConnectionFail      { get; set; }

    /// <summary>payload[11] bit1 – Temp sensor 1 out of range.</summary>
    public bool Temp1OutOfRange          { get; set; }

    /// <summary>payload[11] bit2 – Temp sensor 2 connection fail.</summary>
    public bool Temp2ConnectionFail      { get; set; }

    /// <summary>payload[11] bit3 – Temp sensor 2 out of range.</summary>
    public bool Temp2OutOfRange          { get; set; }

    /// <summary>payload[12] bits0:1 state – force system into Error state.</summary>
    public bool SystemStateError         { get; set; }

    /// <summary>payload[12] bit2 – External device alarm active.</summary>
    public bool ExternalDeviceAlarm      { get; set; }

    /// <summary>payload[12] bit3 – Processor alarm active.</summary>
    public bool ProcessorAlarm           { get; set; }

    // ── Zoom Feedback ─────────────────────────────────────────────────────

    /// <summary>ZG1 feedback – simulate motor stall termination.</summary>
    public bool Zg1Stall                 { get; set; }

    /// <summary>ZG2 feedback – simulate motor stall termination.</summary>
    public bool Zg2Stall                 { get; set; }

    public bool HasAnyFault =>
        ControllerVoltageFail || ControllerPsuFail || ControllerTempFail || ControllerFlashFail ||
        Zg1AdcFail || Zg1MotorConnectionFail || Zg1EncoderConnectionFail || Zg1EncoderPolarityFail ||
        Zg1MotorStall || Zg1NotCalibrated ||
        Zg2MotorStartFail || Zg2MotorConnectionFail || Zg2MotorPolarityFail ||
        Zg2MinEndstopFail || Zg2MaxEndstopFail || Zg2MotorStall || Zg2EncoderFail ||
        Temp1ConnectionFail || Temp1OutOfRange || Temp2ConnectionFail || Temp2OutOfRange ||
        SystemStateError || ExternalDeviceAlarm || ProcessorAlarm ||
        Zg1Stall || Zg2Stall;
}
