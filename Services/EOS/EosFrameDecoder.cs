using System;
using System.Collections.Generic;
using ShockUI.Services.Device;   // DecodedFrameRow

namespace ShockUI.Services.Eos;

/// <summary>
/// Decodes a raw EOS-protocol frame into a list of <see cref="DecodedFrameRow"/>
/// for display in the "Decoded Frame" panel. Used by every EOS-based module:
/// Pan/Tilt, VisNIR, SWIR.
///
/// Frame layout (minimum 14 bytes — see <see cref="EosFramer"/>):
///   [0]      Sync1          = 0x0A
///   [1]      Sync2          = 0x88
///   [2]      Protocol Ver   = 0x01
///   [3]      Error Byte 1
///   [4]      Error Byte 2
///   [5]      Destination ID
///   [6]      Source ID
///   [7]      Sequence ID
///   [8]      Cmd LSB
///   [9]      Cmd MSB
///   [10]     Length         (payload byte count)
///   [11..n]  Payload
///   [n+1]    CRC LSB
///   [n+2]    CRC MSB
///
/// Each module passes a <c>commandLookup</c> delegate that maps the 16-bit
/// command ID (frame[8] | frame[9] &lt;&lt; 8) to a human-readable label, so the
/// same decoder can label Pan/Tilt commands differently from VisNIR/SWIR ones.
/// </summary>
public static class EosFrameDecoder
{
    /// <param name="frame">Raw bytes as captured from TX/RX.</param>
    /// <param name="commandLookup">
    /// Maps the 16-bit Command ID to a friendly label
    /// (e.g. <c>0x0001 → "GeneralStatus (§3.3.1)"</c>).
    /// Return <c>null</c> for unknown commands; the decoder will fall back
    /// to a generic placeholder.
    /// </param>
    /// <param name="hostSrcId">
    /// Expected SrcID for outgoing frames (e.g. <c>0x10</c> for SCP).
    /// Used only to annotate the row meaning, not for validation.
    /// </param>
    /// <param name="targetDstId">
    /// Expected DstID for outgoing frames (e.g. <c>0x20</c> for PTSC,
    /// <c>0x54</c> for VisNIR, <c>0x53</c> for SWIR).
    /// </param>
    public static IReadOnlyList<DecodedFrameRow> Decode(
        byte[] frame,
        Func<ushort, string?> commandLookup,
        byte hostSrcId,
        byte targetDstId)
    {
        var rows = new List<DecodedFrameRow>();

        if (frame is null || frame.Length < 14)
        {
            rows.Add(new DecodedFrameRow
            {
                Position = "—",
                Label = "Invalid",
                HexValue = "—",
                Meaning = $"Frame too short ({frame?.Length ?? 0} bytes). Minimum EOS frame is 14 bytes."
            });
            return rows;
        }

        rows.Add(new DecodedFrameRow
        {
            Position = "[0]",
            Label = "Sync 1",
            HexValue = $"0x{frame[0]:X2}",
            Meaning = frame[0] == 0x0A ? "EOS sync byte 1 ✓" : "INVALID — expected 0x0A"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[1]",
            Label = "Sync 2",
            HexValue = $"0x{frame[1]:X2}",
            Meaning = frame[1] == 0x88 ? "EOS sync byte 2 ✓" : "INVALID — expected 0x88"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[2]",
            Label = "Protocol Ver",
            HexValue = $"0x{frame[2]:X2}",
            Meaning = frame[2] == 0x01 ? "Protocol v1 ✓" : "Unknown protocol version (expected 0x01)"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[3]",
            Label = "Error 1",
            HexValue = $"0x{frame[3]:X2}",
            Meaning = frame[3] == 0x00 ? "OK (command frame, no error)" : "Error indication"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[4]",
            Label = "Error 2",
            HexValue = $"0x{frame[4]:X2}",
            Meaning = frame[4] == 0x00 ? "OK" : "Error detail"
        });

        // Dest / Src — direction-dependent meaning
        bool isLikelyTx = frame[5] == targetDstId && frame[6] == hostSrcId;
        bool isLikelyRx = frame[5] == hostSrcId && frame[6] == targetDstId;

        rows.Add(new DecodedFrameRow
        {
            Position = "[5]",
            Label = "Dest ID",
            HexValue = $"0x{frame[5]:X2}",
            Meaning = isLikelyTx ? $"Target module (0x{targetDstId:X2}) — outgoing"
                    : isLikelyRx ? $"Host (0x{hostSrcId:X2}) — incoming response"
                                 : "Unrecognised endpoint ID"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[6]",
            Label = "Src ID",
            HexValue = $"0x{frame[6]:X2}",
            Meaning = isLikelyTx ? $"Host (0x{hostSrcId:X2})"
                    : isLikelyRx ? $"Target module (0x{targetDstId:X2})"
                                 : "Unrecognised endpoint ID"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[7]",
            Label = "Sequence ID",
            HexValue = $"0x{frame[7]:X2}",
            Meaning = $"Frame sequence #{frame[7]}"
        });

        // 16-bit command ID, LSB first
        ushort cmdId = (ushort)((frame[8] << 8) | frame[9]);  // big-endian
        string? cmdName = commandLookup?.Invoke(cmdId);

        rows.Add(new DecodedFrameRow
        {
            Position = "[8]",
            Label = "Cmd LSB",
            HexValue = $"0x{frame[8]:X2}",
            Meaning = $"Command low byte"
        });

        rows.Add(new DecodedFrameRow
        {
            Position = "[9]",
            Label = "Cmd MSB",
            HexValue = $"0x{frame[9]:X2}",
            Meaning = cmdName is not null
                ? $"Full command ID = 0x{cmdId:X4} → {cmdName}"
                : $"Full command ID = 0x{cmdId:X4} (unknown)"
        });

        byte length = frame[10];
        int expectedTotal = 11 + length + 2;

        rows.Add(new DecodedFrameRow
        {
            Position = "[10]",
            Label = "Length",
            HexValue = $"0x{length:X2}",
            Meaning = frame.Length == expectedTotal
                ? $"{length} payload byte(s), total frame = {expectedTotal} ✓"
                : $"{length} payload byte(s) — but total frame is {frame.Length}, expected {expectedTotal} ✗"
        });

        // Payload rows
        if (length > 0 && frame.Length >= 11 + length)
        {
            for (int i = 0; i < length; i++)
            {
                int pos = 11 + i;
                rows.Add(new DecodedFrameRow
                {
                    Position = $"[{pos}]",
                    Label = $"Payload[{i}]",
                    HexValue = $"0x{frame[pos]:X2}",
                    Meaning = DescribePayloadByte(cmdId, i, frame[pos], length)
                });
            }
        }

        // CRC (LSB then MSB on wire for EOS framer)
        if (frame.Length >= 14)
        {
            int crcLoIdx = frame.Length - 2;
            int crcHiIdx = frame.Length - 1;
            ushort txCrc = (ushort)(frame[crcLoIdx] | (frame[crcHiIdx] << 8));
            ushort calc = Crc16Ccitt.Compute(frame.AsSpan(0, frame.Length - 2));
            bool match = txCrc == calc;

            rows.Add(new DecodedFrameRow
            {
                Position = $"[{crcLoIdx}]",
                Label = "CRC LSB",
                HexValue = $"0x{frame[crcLoIdx]:X2}",
                Meaning = ""
            });
            rows.Add(new DecodedFrameRow
            {
                Position = $"[{crcHiIdx}]",
                Label = "CRC MSB",
                HexValue = $"0x{frame[crcHiIdx]:X2}",
                Meaning = match
                    ? $"Full CRC = 0x{txCrc:X4} ✓ matches computed"
                    : $"Full CRC = 0x{txCrc:X4} ✗ EXPECTED 0x{calc:X4}"
            });
        }

        return rows;
    }

    private static string DescribePayloadByte(ushort cmdId, int idx, byte value, byte totalLen)
    {
        // Common pattern: GET-commands carry a single 0x55 "request" marker
        if (totalLen == 2 && idx == 0 && value == 0x55)
            return "Request marker (0x55 = 'please respond')";
        if (totalLen == 2 && idx == 1 && value == 0x00)
            return "Reserve byte";

        return $"(payload byte {idx})";
    }
}