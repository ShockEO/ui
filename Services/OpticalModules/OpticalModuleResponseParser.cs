using ShockUI.Models.OpticalModules;
using System;
using ShockUI.Services;   // Crc16Ccitt

namespace ShockUI.Services.OpticalModules;

/// <summary>
/// Parses EOS-protocol responses for the Optical Module.
///
/// Supported responses (post-refactor — zoom feedback removed):
///   0x0080  State Selection Response
///   0x0081  General Status Response
///   0x0084  FOV Response    → <see cref="OpticalModuleFovFeedback"/>
///   0x0085  Focus Response  → <see cref="OpticalModuleFocusFeedback"/>
/// </summary>
public static class OpticalModuleResponseParser
{
    public static bool TryParseMessage(byte[] frame, out OpticalModuleMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (frame.Length < 13)
        {
            error = "Frame too short.";
            return false;
        }

        if (frame[0] != OpticalModuleMessage.Sync1 || frame[1] != OpticalModuleMessage.Sync2)
        {
            error = "Invalid sync bytes.";
            return false;
        }

        byte length = frame[10];
        int expectedLength = 11 + length + 2;

        if (frame.Length != expectedLength)
        {
            error = $"Invalid frame length. Expected {expectedLength}, got {frame.Length}.";
            return false;
        }

        ushort receivedCrc = (ushort)(frame[^2] | (frame[^1] << 8));
        ushort computedCrc = Crc16Ccitt.Compute(frame.AsSpan(0, frame.Length - 2));

        if (receivedCrc != computedCrc)
        {
            error = "CRC mismatch.";
            return false;
        }

        ushort command = (ushort)((frame[8] << 8) | frame[9]);
        byte[] payload = new byte[length];
        Array.Copy(frame, 11, payload, 0, length);

        message = new OpticalModuleMessage
        {
            ErrorByte1 = frame[3],
            ErrorByte2 = frame[4],
            DestinationId = frame[5],
            SourceId = frame[6],
            SequenceId = frame[7],
            Command = command,
            Length = length,
            Payload = payload
        };

        return true;
    }

    public static OpticalModuleState ParseStateSelectionResponse(OpticalModuleMessage message, out bool targetReached)
    {
        targetReached = false;

        if (message.Command != OpticalModuleCommandBuilder.StateSelectionCommand || message.Length < 2)
            return OpticalModuleState.Unknown;

        byte stateByte = message.Payload[0];
        byte statusByte = message.Payload[1];

        targetReached = (statusByte & 0x01) != 0;

        return (stateByte & 0x07) switch
        {
            0x00 => OpticalModuleState.Operational,
            0x01 => OpticalModuleState.Maintenance,
            0x02 => OpticalModuleState.BuiltInTest,
            0x03 => OpticalModuleState.Error,
            0x04 => OpticalModuleState.Initialization,
            _ => OpticalModuleState.Unknown
        };
    }

    public static OpticalModuleGeneralStatus ParseGeneralStatusResponse(OpticalModuleMessage message)
    {
        var status = new OpticalModuleGeneralStatus();

        if (message.Command != OpticalModuleCommandBuilder.GeneralStatusCommand || message.Length < 13)
            return status;

        byte resetByte = message.Payload[0];

        status.MessageCounterReset = (resetByte & 0x01) != 0;
        status.CrcErrorCounterReset = (resetByte & 0x02) != 0;
        status.MessageFormatErrorCounterReset = (resetByte & 0x04) != 0;

        status.MessageCounter =
            message.Payload[1]
            | (message.Payload[2] << 8)
            | (message.Payload[3] << 16);

        status.CrcErrorCount =
            (ushort)(message.Payload[4] | (message.Payload[5] << 8));

        status.MessageFormatErrorCount =
            (ushort)(message.Payload[6] | (message.Payload[7] << 8));

        status.ControllerPbitStatus = message.Payload[8];
        status.ZoomGroup1PbitStatus = message.Payload[9];
        status.ZoomGroup2PbitStatus = message.Payload[10];
        status.TemperatureSensorsPbitStatus = message.Payload[11];
        status.EtiAndStateByte = message.Payload[12];

        status.ExternalDeviceAlarmActive = (status.EtiAndStateByte & 0x01) != 0;
        status.ProcessorAlarmActive = (status.EtiAndStateByte & 0x02) != 0;

        status.CurrentState = ((status.EtiAndStateByte >> 2) & 0x03) switch
        {
            0x00 => OpticalModuleState.Operational,
            0x01 => OpticalModuleState.Maintenance,
            0x02 => OpticalModuleState.BuiltInTest,
            0x03 => OpticalModuleState.Error,
            _ => OpticalModuleState.Unknown
        };

        return status;
    }

    /// <summary>
    /// Parses a FOV Response (Command 0x0084).
    /// Expected Length = 0x02:
    ///   [0] FOV Control Byte
    ///   [1] FOV Status Byte
    /// </summary>
    public static OpticalModuleFovFeedback ParseFovFeedback(OpticalModuleMessage message)
    {
        if (message.Command != OpticalModuleCommandBuilder.FovCommand || message.Length < 2)
            return new OpticalModuleFovFeedback();

        return new OpticalModuleFovFeedback
        {
            ControlByte = message.Payload[0],
            StatusByte = message.Payload[1]
        };
    }

    /// <summary>
    /// Parses a Focus Response (Command 0x0085).
    /// Expected Length = 0x0A (10 bytes):
    ///   [0]     Focus Control Byte
    ///   [1]     Focus Status Byte
    ///   [2..5]  Focus Position (int32, little-endian)
    ///   [6..9]  Focus Speed    (int32, little-endian)
    /// </summary>
    public static OpticalModuleFocusFeedback ParseFocusFeedback(OpticalModuleMessage message)
    {
        if (message.Command != OpticalModuleCommandBuilder.FocusCommand || message.Length < 10)
            return new OpticalModuleFocusFeedback();

        return new OpticalModuleFocusFeedback
        {
            ControlByte = message.Payload[0],
            StatusByte = message.Payload[1],
            CurrentPosition = ReadInt32Le(message.Payload, 2),
            CurrentSpeed = ReadInt32Le(message.Payload, 6),
        };
    }

    private static int ReadInt32Le(byte[] buffer, int offset)
    {
        return buffer[offset]
             | (buffer[offset + 1] << 8)
             | (buffer[offset + 2] << 16)
             | (buffer[offset + 3] << 24);
    }
}