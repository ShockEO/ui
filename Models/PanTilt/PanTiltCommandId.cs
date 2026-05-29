namespace ShockUI.Models.PanTilt;

/// <summary>
/// EOS Pan Tilt Stab Controller (PTSC) command identifiers.
///
/// Commands are 2-byte pairs sent in bytes [8] and [9] of the EOS frame.
/// Stored as ushort: low byte = frame[8], high byte = frame[9].
/// All Pan/Tilt commands have frame[8] = 0x00.
///
/// Subsystem IDs:
///   PTSC (target)  = 0x20
///   SCP  (host)    = 0x10
///
/// Source: EΩS Pan/Tilt SRS §3.3
/// </summary>
public static class PanTiltCommandId
{
    // frame[8]=0x00, frame[9]=0x01
    public const ushort GeneralStatus      = 0x0100;

    // frame[8]=0x00, frame[9]=0x08  – Read current motor state
    public const ushort MotorControlGet   = 0x0800;

    // frame[8]=0x00, frame[9]=0x09  – Set motor mode / position / speed
    public const ushort MotorControlSet   = 0x0900;

    // frame[8]=0x00, frame[9]=0x0A  – Read stabilisation state + IMU data
    public const ushort StabControlGet    = 0x0A00;

    // frame[8]=0x00, frame[9]=0x0B  – Set stabilisation mode
    public const ushort StabControlSet    = 0x0B00;

    // frame[8]=0x00, frame[9]=0x23  – Run IBIT / read IBIT results
    public const ushort Ibit              = 0x2300;
}
