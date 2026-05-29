namespace ShockUI.Models.OpticalModules;

/// <summary>
/// Decoded response from the Focus command (0x0085) on the optical module.
///
/// Wire format (Length = 0x0A — 10 payload bytes):
///   Payload[0]      Focus Control Byte – current command state
///                     Bits 0..2:
///                       0x00 Giving feedback
///                       0x01 Setting focus position
///                       0x02 Setting focus speed
///                       0x03 Moving to infinite focus
///                       0x04 Stopped control
///                     Bits 3..7: Reserved
///
///   Payload[1]      Focus Status Byte
///                     Bit 0:     Control active
///                     Bit 1:     Position reached
///                     Bits 2..3: Movement limits
///                       0x00 Free to move
///                       0x01 Minimum position reached
///                       0x02 Maximum position reached
///                       0x03 Blocked by other lens
///                     Bits 4..7: Reserved
///
///   Payload[2..5]   Focus Position (int32, little-endian)
///   Payload[6..9]   Focus Speed    (int32, little-endian)
/// </summary>
public sealed class OpticalModuleFocusFeedback
{
    public byte ControlByte { get; init; }
    public byte StatusByte { get; init; }

    /// <summary>Current focus position (raw encoder ticks).</summary>
    public int CurrentPosition { get; init; }

    /// <summary>Current focus speed (raw units).</summary>
    public int CurrentSpeed { get; init; }

    public string CommandState => (ControlByte & 0x07) switch
    {
        0x00 => "Giving feedback",
        0x01 => "Setting position",
        0x02 => "Setting speed",
        0x03 => "Moving to infinity",
        0x04 => "Stopped",
        _ => "-"
    };

    public bool ControlActive => (StatusByte & 0x01) != 0;
    public bool PositionReached => (StatusByte & 0x02) != 0;

    public string MovementLimit => ((StatusByte >> 2) & 0x03) switch
    {
        0x00 => "Free to move",
        0x01 => "Minimum reached",
        0x02 => "Maximum reached",
        0x03 => "Blocked",
        _ => "-"
    };

    public string Summary =>
        $"{CommandState} | Pos={CurrentPosition}, Speed={CurrentSpeed} | " +
        $"{MovementLimit} (Active: {ControlActive}, Reached: {PositionReached})";
}