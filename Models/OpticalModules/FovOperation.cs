namespace ShockUI.Models.OpticalModules;

/// <summary>
/// Op-code encoded in bits 0:2 of the FOV Control Byte (command 0x0084).
/// Values match the wire protocol exactly.
/// </summary>
public enum FovOperation : byte
{
    /// <summary>0x00 – Request current FOV feedback only.</summary>
    GetFeedback = 0x00,

    /// <summary>0x01 – Go to Wide FOV.</summary>
    GoToWfov = 0x01,

    /// <summary>0x02 – Go to Medium-Wide FOV.</summary>
    GoToMwfov = 0x02,

    /// <summary>0x03 – Go to Medium-Narrow FOV.</summary>
    GoToMnfov = 0x03,

    /// <summary>0x04 – Go to Narrow FOV.</summary>
    GoToNfov = 0x04,

    /// <summary>0x05 – Stop the ongoing FOV control action.</summary>
    Stop = 0x05,
}