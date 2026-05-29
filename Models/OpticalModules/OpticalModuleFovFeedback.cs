namespace ShockUI.Models.OpticalModules;

/// <summary>
/// Decoded response from the FOV command (0x0084) on the optical module.
///
/// Wire format (Length = 0x02):
///   Payload[0]  FOV Control Byte   – current command state
///                 Bits 0..2:
///                   0x00 Giving FB / idle
///                   0x01 Going to WFOV
///                   0x02 Going to MWFOV
///                   0x03 Going to MNFOV
///                   0x04 Going to NFOV
///                   0x05 Stopped control
///                 Bits 3..7:  Reserved
///
///   Payload[1]  FOV Status Byte
///                 Bit 0:     Control active (0 = not active, 1 = active)
///                 Bit 1:     FOV reached   (0 = not reached, 1 = reached)
///                 Bits 2..4: Current FOV
///                   0x00 Undefined
///                   0x01 WFOV
///                   0x02 MWFOV
///                   0x03 MNFOV
///                   0x04 NFOV
///                 Bits 5..7: Reserved
/// </summary>
public sealed class OpticalModuleFovFeedback
{
    public byte ControlByte { get; init; }
    public byte StatusByte { get; init; }

    public string CommandState => (ControlByte & 0x07) switch
    {
        0x00 => "Giving feedback",
        0x01 => "Going to WFOV",
        0x02 => "Going to MWFOV",
        0x03 => "Going to MNFOV",
        0x04 => "Going to NFOV",
        0x05 => "Stopped",
        _ => "-"
    };

    public bool ControlActive => (StatusByte & 0x01) != 0;
    public bool FovReached => (StatusByte & 0x02) != 0;

    public string CurrentFov => ((StatusByte >> 2) & 0x07) switch
    {
        0x00 => "Undefined",
        0x01 => "WFOV",
        0x02 => "MWFOV",
        0x03 => "MNFOV",
        0x04 => "NFOV",
        _ => "-"
    };

    public string Summary =>
        $"{CurrentFov} (Cmd: {CommandState}, Active: {ControlActive}, Reached: {FovReached})";
}