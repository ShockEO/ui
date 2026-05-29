namespace ShockUI.Services.Camera;

/// <summary>
/// Fault injection config for CameraSerialService simulation.
/// All false = healthy. Flags map to specific response payload fields.
/// </summary>
public sealed class CameraSimFaultConfig
{
    // ── Handshake ─────────────────────────────────────────────────────────

    /// <summary>Simulate ZG3 not supported (3-axis camera).</summary>
    public bool Zg3NotSupported { get; set; }

    // ── Position feedback ─────────────────────────────────────────────────

    /// <summary>ZG1 position reached flag = 0 (motor still moving / not reached target).</summary>
    public bool Zg1NotReached { get; set; }

    /// <summary>ZG2 position reached flag = 0.</summary>
    public bool Zg2NotReached { get; set; }

    /// <summary>ZG3 position reached flag = 0.</summary>
    public bool Zg3NotReached { get; set; }

    // ── Temperature ───────────────────────────────────────────────────────

    /// <summary>Return out-of-range temperatures (high value = thermal fault).</summary>
    public bool TemperatureOutOfRange { get; set; }

    // ── Calibration ───────────────────────────────────────────────────────

    /// <summary>Calibration fails (returns success=0 instead of 1).</summary>
    public bool CalibrationFail { get; set; }

    // ── Logger ────────────────────────────────────────────────────────────

    /// <summary>Logger reports 0 stored points.</summary>
    public bool LoggerEmpty { get; set; }

    public bool HasAnyFault =>
        Zg3NotSupported || Zg1NotReached || Zg2NotReached || Zg3NotReached ||
        TemperatureOutOfRange || CalibrationFail || LoggerEmpty;
}
