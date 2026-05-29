namespace ShockUI.Services.PanTilt;

/// <summary>
/// Holds the fault flags that the simulation will inject into its responses.
/// All false = healthy system (default).
/// Each flag maps directly to a specific bit in a specific SRS response byte.
/// </summary>
public sealed class PanTiltSimFaultConfig
{
    // ── §3.3.1.2  General Status ──────────────────────────────────────────

    /// <summary>p[0] bit7 – Power failure (28V bus not detected).</summary>
    public bool PowerFailure { get; set; }

    /// <summary>p[0] bit4 – IMU comms error.</summary>
    public bool ImuError { get; set; }

    /// <summary>p[0] bit5 – Pan encoder invalid.</summary>
    public bool PanEncoderError { get; set; }

    /// <summary>p[0] bit6 – Tilt encoder invalid.</summary>
    public bool TiltEncoderError { get; set; }

    /// <summary>p[1] bit0 – Pan ADC error.</summary>
    public bool PanAdcError { get; set; }

    /// <summary>p[1] bit1 – Tilt ADC error.</summary>
    public bool TiltAdcError { get; set; }

    /// <summary>p[1] bit2 – Motor stator short-brake active.</summary>
    public bool MotorBrakeOn { get; set; }

    /// <summary>p[0] bits0:2 – Set system state to Error (State F) instead of Operational.</summary>
    public bool SystemStateError { get; set; }

    /// <summary>p[2] bit0 – Pan motor disengaged / PWM OFF.</summary>
    public bool PanDisengaged { get; set; }

    /// <summary>p[2] bit2 – Pan motor initialisation fault.</summary>
    public bool PanMotorFault { get; set; }

    /// <summary>p[3] bit0 – Tilt motor disengaged / PWM OFF.</summary>
    public bool TiltDisengaged { get; set; }

    /// <summary>p[3] bit2 – Tilt motor initialisation fault.</summary>
    public bool TiltMotorFault { get; set; }

    // ── §3.3.2.2  Motor Control GET ───────────────────────────────────────

    /// <summary>SSI bit30 – Pan encoder Position Valid flag = 0 (signal lost).</summary>
    public bool PanPositionInvalid { get; set; }

    /// <summary>SSI bit30 – Tilt encoder Position Valid flag = 0 (signal lost).</summary>
    public bool TiltPositionInvalid { get; set; }

    // ── §3.3.4.2  IBIT ───────────────────────────────────────────────────

    /// <summary>p[2] bits – Pan motor start fail (bit0).</summary>
    public bool PanIbitStartFail { get; set; }

    /// <summary>p[2] bits – Pan motor connection fail (bit1).</summary>
    public bool PanIbitConnectionFail { get; set; }

    /// <summary>p[2] bits – Pan motor polarity fail (bit2).</summary>
    public bool PanIbitPolarityFail { get; set; }

    /// <summary>p[2] bits – Pan motor stall detected (bit3).</summary>
    public bool PanIbitStall { get; set; }

    /// <summary>p[2] bits – Pan encoder fail (bit4).</summary>
    public bool PanIbitEncoderFail { get; set; }

    /// <summary>p[2] bits – Pan motion range fail / 360° not achieved (bit5).</summary>
    public bool PanIbitMotionFail { get; set; }

    /// <summary>p[3] bits – Tilt motor start fail (bit0).</summary>
    public bool TiltIbitStartFail { get; set; }

    /// <summary>p[3] bits – Tilt motor connection fail (bit1).</summary>
    public bool TiltIbitConnectionFail { get; set; }

    /// <summary>p[3] bits – Tilt motor polarity fail (bit2).</summary>
    public bool TiltIbitPolarityFail { get; set; }

    /// <summary>p[3] bits – Tilt motor stall (bit3).</summary>
    public bool TiltIbitStall { get; set; }

    /// <summary>p[3] bits – Tilt encoder fail (bit4).</summary>
    public bool TiltIbitEncoderFail { get; set; }

    /// <summary>p[3] bits – Tilt min end stop not reached −30° (bit5).</summary>
    public bool TiltIbitMinStopFail { get; set; }

    /// <summary>p[3] bits – Tilt max end stop not reached +75° (bit6).</summary>
    public bool TiltIbitMaxStopFail { get; set; }

    /// <summary>p[4] bit0 – Power fail (central electronics).</summary>
    public bool SensorIbitPowerFail { get; set; }

    /// <summary>p[4] bit1 – Motor brake still on during IBIT.</summary>
    public bool SensorIbitBrakeOn { get; set; }

    /// <summary>p[4] bit2 – IMU self-test fail.</summary>
    public bool SensorIbitImuFail { get; set; }

    /// <summary>p[4] bit3 – IMU comms fail.</summary>
    public bool SensorIbitImuComms { get; set; }

    /// <summary>p[4] bit4 – Pan ADC fail.</summary>
    public bool SensorIbitPanAdc { get; set; }

    /// <summary>p[4] bit5 – Tilt ADC fail.</summary>
    public bool SensorIbitTiltAdc { get; set; }

    // ── §3.3.4.2  IBIT additions (May 2026 SRS update) ────────────────────

    /// <summary>p[2] bit6 – Pan Encoder Zero Offset Test fail (needs recal).</summary>
    public bool PanIbitZeroOffsetFail { get; set; }

    /// <summary>p[3] bit7 – Tilt Encoder Zero Offset Test fail (needs recal).</summary>
    public bool TiltIbitZeroOffsetFail { get; set; }

    /// <summary>p[5] – Pan Extended Faults (CBIT). Any bit set = failed.
    /// bit0=ISR Overcurrent, bit1=ISR Control Saturation (PI Flatline),
    /// bit2=ADC KCL Error, bit3=ADC Rail Short, bit4=ADC Phantom Voltage.</summary>
    public byte PanCbitBits { get; set; }

    /// <summary>p[6] – Tilt Extended Faults (CBIT). Same layout as PanCbitBits.</summary>
    public byte TiltCbitBits { get; set; }

    /// <summary>p[7] – IMU PBIT/CBIT failures. Setting any bit reports that subsystem failed.
    /// bit0=Comms, bit1=BIT, bit2=Acc X, bit3=Acc Y, bit4=Acc Z,
    /// bit5=Gyro X, bit6=Gyro Y, bit7=Gyro Z.
    /// Note: bits are FAIL flags here for ease of injection; the wire format
    /// stores them as PASS flags (1=pass) and the sim builder inverts them.</summary>
    public byte ImuIbitFailBits { get; set; }

    /// <summary>p[8] bit0 – Accelerometer operational range failed.</summary>
    public bool ImuAccelRangeFail { get; set; }

    /// <summary>p[8] bit1 – Gyroscope operational range failed.</summary>
    public bool ImuGyroRangeFail { get; set; }

    /// <summary>p[8] bit2 – Gyroscope operating in high-range mode (above 1833°/s).
    /// Not strictly a fault but an informational flag.</summary>
    public bool ImuGyroHighRangeActive { get; set; }

    // ── Helpers ──────────────────────────────────────────────────────────

    public bool HasAnyIbitFault =>
        PanIbitStartFail || PanIbitConnectionFail || PanIbitPolarityFail ||
        PanIbitStall || PanIbitEncoderFail || PanIbitMotionFail ||
        TiltIbitStartFail || TiltIbitConnectionFail || TiltIbitPolarityFail ||
        TiltIbitStall || TiltIbitEncoderFail || TiltIbitMinStopFail ||
        TiltIbitMaxStopFail || SensorIbitPowerFail || SensorIbitBrakeOn ||
        SensorIbitImuFail || SensorIbitImuComms || SensorIbitPanAdc ||
        SensorIbitTiltAdc ||
        // May 2026 additions
        PanIbitZeroOffsetFail || TiltIbitZeroOffsetFail ||
        PanCbitBits != 0 || TiltCbitBits != 0 ||
        ImuIbitFailBits != 0 || ImuAccelRangeFail || ImuGyroRangeFail;
}