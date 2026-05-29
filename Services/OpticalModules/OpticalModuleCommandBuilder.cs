using ShockUI.Models.OpticalModules;
using ShockUI.Services.Eos;

namespace ShockUI.Services.OpticalModules;

/// <summary>
/// Builds EOS-protocol commands for the Optical Module
/// (VisNIR = 0x54, SWIR = 0x53).
///
/// Command set (post-refactor — zoom-group commands removed):
///   0x0080  State Selection
///   0x0081  General Status
///   0x0084  FOV        (Get / GoTo WFOV / MWFOV / MNFOV / NFOV / Stop)
///   0x0085  Focus      (Get / MoveToInfinity / Stop)
///
/// All frames are constructed via the shared <see cref="EosFramer"/>.
/// </summary>
public static class OpticalModuleCommandBuilder
{
    public const ushort StateSelectionCommand = 0x0080;
    public const ushort GeneralStatusCommand = 0x0081;
    public const ushort FovCommand = 0x0084;
    public const ushort FocusCommand = 0x0085;

    private const byte HostSourceId = 0x00;   // Per Host GUI changes: host TX src = 0x00 (was 0x01)

    // ── State / Status ───────────────────────────────────────────────────

    public static byte[] BuildStateSelection(
        OpticalModuleDefinition module,
        byte sequenceId,
        OpticalModuleRequestedState requestedState)
    {
        byte[] payload = [(byte)requestedState];
        return BuildFrame(module.DeviceId, HostSourceId, sequenceId, StateSelectionCommand, payload);
    }

    public static byte[] BuildGeneralStatusRequest(
        OpticalModuleDefinition module,
        byte sequenceId)
    {
        byte[] payload = [0x55];
        return BuildFrame(module.DeviceId, HostSourceId, sequenceId, GeneralStatusCommand, payload);
    }

    // ── FOV (0x0084) ─────────────────────────────────────────────────────

    /// <summary>Requests current FOV feedback without changing state.</summary>
    public static byte[] BuildFovGetFeedback(OpticalModuleDefinition module, byte sequenceId)
        => BuildFovCommand(module, sequenceId, FovOperation.GetFeedback);

    /// <summary>Commands the module to stop any in-progress FOV movement.</summary>
    public static byte[] BuildFovStop(OpticalModuleDefinition module, byte sequenceId)
        => BuildFovCommand(module, sequenceId, FovOperation.Stop);

    /// <summary>
    /// Builds an arbitrary FOV command with the given operation op-code.
    /// Length = 0x01 (one control byte). Bits 0:2 carry the op, bits 3:7 reserved.
    /// </summary>
    public static byte[] BuildFovCommand(
        OpticalModuleDefinition module,
        byte sequenceId,
        FovOperation operation)
    {
        byte[] payload = [(byte)((byte)operation & 0x07)];
        return BuildFrame(module.DeviceId, HostSourceId, sequenceId, FovCommand, payload);
    }

    /// <summary>Map a user-facing preset name to its FOV op-code.</summary>
    public static FovOperation ToFovOperation(string fovText) => fovText switch
    {
        "WFOV" => FovOperation.GoToWfov,
        "MWFOV" => FovOperation.GoToMwfov,
        "MNFOV" => FovOperation.GoToMnfov,
        "NFOV" => FovOperation.GoToNfov,
        _ => FovOperation.GetFeedback
    };

    // ── Focus (0x0085) ───────────────────────────────────────────────────
    //
    // Payload layout (Length = 0x09 — 9 bytes):
    //   [0]      Focus Control Byte (op-code in bits 0:2)
    //   [1..4]   Focus Position (int32, little-endian)
    //   [5..8]   Focus Speed    (int32, little-endian)
    //
    // For ops that don't use Position and/or Speed (Get / MoveToInfinity /
    // Stop), the unused fields are sent as 0.

    /// <summary>Requests current focus feedback without changing state.</summary>
    public static byte[] BuildFocusGetFeedback(OpticalModuleDefinition module, byte sequenceId)
        => BuildFocusCommand(module, sequenceId, FocusOperation.GetFeedback, 0, 0);

    /// <summary>Commands the module to drive the focus to the given target position.</summary>
    public static byte[] BuildFocusSetPosition(
        OpticalModuleDefinition module, byte sequenceId, int position)
        => BuildFocusCommand(module, sequenceId, FocusOperation.SetPosition, position, 0);

    /// <summary>Commands the module to drive the focus at the given speed.</summary>
    public static byte[] BuildFocusSetSpeed(
        OpticalModuleDefinition module, byte sequenceId, int speed)
        => BuildFocusCommand(module, sequenceId, FocusOperation.SetSpeed, 0, speed);

    /// <summary>Commands the module to drive the focus to its infinity (far) position.</summary>
    public static byte[] BuildFocusMoveToInfinity(OpticalModuleDefinition module, byte sequenceId)
        => BuildFocusCommand(module, sequenceId, FocusOperation.MoveToInfinity, 0, 0);

    /// <summary>Commands the module to stop any in-progress focus movement.</summary>
    public static byte[] BuildFocusStop(OpticalModuleDefinition module, byte sequenceId)
        => BuildFocusCommand(module, sequenceId, FocusOperation.Stop, 0, 0);

    /// <summary>
    /// Builds an arbitrary Focus command. Payload is always 9 bytes:
    /// control byte + position (int32 LE) + speed (int32 LE).
    /// </summary>
    public static byte[] BuildFocusCommand(
        OpticalModuleDefinition module,
        byte sequenceId,
        FocusOperation operation,
        int position,
        int speed)
    {
        byte[] payload = new byte[9];
        payload[0] = (byte)((byte)operation & 0x07);
        WriteInt32Le(payload, 1, position);
        WriteInt32Le(payload, 5, speed);
        return BuildFrame(module.DeviceId, HostSourceId, sequenceId, FocusCommand, payload);
    }

    private static void WriteInt32Le(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    // ── Framing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Shared helper used by both this class and the simulation path in
    /// <c>OpticalModuleSerialService</c> when it assembles response frames.
    /// Delegates entirely to <see cref="EosFramer.BuildCommand"/>.
    /// </summary>
    internal static byte[] BuildFrame(
        byte destinationId,
        byte sourceId,
        byte sequenceId,
        ushort command,
        byte[] payload)
    {
        return EosFramer.BuildCommand(
            dstId: destinationId,
            srcId: sourceId,
            seqId: sequenceId,
            command: command,
            payload: payload);
    }
}