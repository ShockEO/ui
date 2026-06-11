namespace ShockUI.Models.PanTilt;

/// <summary>
/// EOS Pan Tilt Stab Controller (PTSC) command identifiers.
///
/// Commands are 2-byte pairs sent in bytes [8] and [9] of the EOS frame.
/// Wire order is big-endian: frame[8] = MSB, frame[9] = LSB. All current
/// Pan/Tilt commands have frame[8] = 0x00 and the identifier in frame[9],
/// so each constant is 0x00XX (e.g. GeneralStatus → wire 00 01).
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
    public const ushort GeneralStatus = 0x0001;

    // frame[8]=0x00, frame[9]=0x08  – Read current motor state
    public const ushort MotorControlGet = 0x0008;

    // frame[8]=0x00, frame[9]=0x09  – Set motor mode / position / speed
    public const ushort MotorControlSet = 0x0009;

    // frame[8]=0x00, frame[9]=0x0A  – Read stabilisation state + IMU data
    public const ushort StabControlGet = 0x000A;

    // frame[8]=0x00, frame[9]=0x0B  – Set stabilisation mode
    public const ushort StabControlSet = 0x000B;

    // frame[8]=0x00, frame[9]=0x23  – Run IBIT / read IBIT results
    public const ushort Ibit = 0x0023;
}