namespace ShockUI.Models.OpticalModules;

/// <summary>
/// Op-code encoded in bits 0:2 of the Focus Control Byte (command 0x0085).
/// Values match the wire protocol exactly.
/// </summary>
public enum FocusOperation : byte
{
    /// <summary>0x00 – Request current focus feedback only.</summary>
    GetFeedback = 0x00,

    /// <summary>0x01 – Drive the focus to a specific target position.</summary>
    SetPosition = 0x01,

    /// <summary>0x02 – Drive the focus at a specific speed.</summary>
    SetSpeed = 0x02,

    /// <summary>0x03 – Move focus to its infinity (far) position.</summary>
    MoveToInfinity = 0x03,

    /// <summary>0x04 – Stop the ongoing focus control action.</summary>
    Stop = 0x04,
}