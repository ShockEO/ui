using System;
using System.Collections.Generic;
using ShockUI.Models.Camera;
using ShockUI.Services.Device;     // DecodedFrameRow

namespace ShockUI.Services.Camera;

/// <summary>
/// Decodes raw bytes of the MWIR Camera Controller's 0xAA 0x55 protocol
/// into a list of <see cref="DecodedFrameRow"/> for display in the
/// "Decoded Frame" panel.
///
/// Frame layout (variable length):
///   [0]      Sync 1     = 0xAA
///   [1]      Sync 2     = 0x55
///   [2]      Command ID (see <see cref="CameraCommand"/>)
///   [3]      Data length (payload bytes)
///   [4..n]   Payload
///   [n+1..n+2] CRC16/CCITT-FALSE
///
/// CRC placement and inputs are command-specific in this protocol — this
/// decoder shows the structure but doesn't independently validate CRC.
/// </summary>
public static class CameraFrameDecoder
{
    public static IReadOnlyList<DecodedFrameRow> Decode(byte[] frame)
    {
        var rows = new List<DecodedFrameRow>();

        if (frame is null || frame.Length < 4)
        {
            rows.Add(new DecodedFrameRow
            {
                Position = "—",
                Label = "Invalid",
                HexValue = "—",
                Meaning = $"Frame too short ({frame?.Length ?? 0} bytes). Minimum is 4 bytes (sync + cmd + length)."
            });
            return rows;
        }

        rows.Add(new DecodedFrameRow
        {
            Position = "[0]",
            Label = "Sync 1",
            HexValue = $"0x{frame[0]:X2}",
            Meaning = frame[0] == 0xAA ? "Camera sync byte 1 ✓" : "INVALID — expected 0xAA"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[1]",
            Label = "Sync 2",
            HexValue = $"0x{frame[1]:X2}",
            Meaning = frame[1] == 0x55 ? "Camera sync byte 2 ✓" : "INVALID — expected 0x55"
        });

        byte cmdId = frame[2];
        rows.Add(new DecodedFrameRow
        {
            Position = "[2]",
            Label = "Command",
            HexValue = $"0x{cmdId:X2}",
            Meaning = DescribeCommand(cmdId)
        });

        byte length = frame[3];
        rows.Add(new DecodedFrameRow
        {
            Position = "[3]",
            Label = "Length",
            HexValue = $"0x{length:X2}",
            Meaning = $"{length} byte(s) of data follow"
        });

        // Payload — annotate per-command where we can
        for (int i = 0; i < length && (4 + i) < frame.Length; i++)
        {
            int pos = 4 + i;
            rows.Add(new DecodedFrameRow
            {
                Position = $"[{pos}]",
                Label = $"Payload[{i}]",
                HexValue = $"0x{frame[pos]:X2}",
                Meaning = DescribePayloadByte(cmdId, i, frame[pos])
            });
        }

        // Trailing 2 bytes (if any) — assumed to be CRC for commands that include one
        int payloadEnd = 4 + length;
        int trailing = frame.Length - payloadEnd;

        if (trailing >= 2)
        {
            rows.Add(new DecodedFrameRow
            {
                Position = $"[{payloadEnd}]",
                Label = "CRC (1)",
                HexValue = $"0x{frame[payloadEnd]:X2}",
                Meaning = "CRC byte"
            });
            rows.Add(new DecodedFrameRow
            {
                Position = $"[{payloadEnd + 1}]",
                Label = "CRC (2)",
                HexValue = $"0x{frame[payloadEnd + 1]:X2}",
                Meaning = "CRC byte"
            });
        }

        return rows;
    }

    private static string DescribeCommand(byte cmdId) => cmdId switch
    {
        (byte)CameraCommand.Handshake => "Handshake (detect camera type)",
        (byte)CameraCommand.Calibrate => "Calibrate",
        (byte)CameraCommand.PosControl => "Position Control (set ZG1/ZG2/ZG3)",
        (byte)CameraCommand.SpeedControl => "Speed Control",
        (byte)CameraCommand.TempFeedback => "Temperature Feedback",
        (byte)CameraCommand.StepControl => "Step / Pulse Control",
        (byte)CameraCommand.DataLogger => "Data Logger",
        _ => $"Unknown command (0x{cmdId:X2})"
    };

    private static string DescribePayloadByte(byte cmdId, int idx, byte value)
    {
        // PosControl payload: int32-LE ZG1 (bytes 0-3), int32-LE ZG2 (4-7),
        // optional int32-LE ZG3 (8-11)
        if (cmdId == (byte)CameraCommand.PosControl)
        {
            return idx switch
            {
                >= 0 and <= 3 => $"ZG1 byte {idx}",
                >= 4 and <= 7 => $"ZG2 byte {idx - 4}",
                >= 8 and <= 11 => $"ZG3 byte {idx - 8}",
                _ => $"(payload byte {idx})"
            };
        }

        // SpeedControl payload: axis (1 byte), speed (2 bytes LE)
        if (cmdId == (byte)CameraCommand.SpeedControl)
        {
            return idx switch
            {
                0 => $"Axis ID = {value}",
                1 => "Speed LSB",
                2 => "Speed MSB",
                _ => $"(payload byte {idx})"
            };
        }

        return $"(payload byte {idx})";
    }

    /// <summary>
    /// Parse a space-separated hex string like "AA 55 02 0C ..." back into
    /// raw bytes. Tolerant of dashes ("AA-55-...") and mixed whitespace.
    /// Returns an empty array if parsing fails.
    /// </summary>
    public static byte[] ParseHexString(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return [];

        var tokens = hex.Replace("-", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>(tokens.Length);
        foreach (var t in tokens)
        {
            if (byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out var b))
                bytes.Add(b);
            else
                return [];   // Bail on any malformed token
        }
        return bytes.ToArray();
    }
}