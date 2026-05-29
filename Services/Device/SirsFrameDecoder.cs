using System;
using System.Collections.Generic;
using ShockUI.Models.Device;

namespace ShockUI.Services.Device;

/// <summary>
/// One decoded field within a SIRS frame. Rendered as a row in the
/// "Decoded Frame" UI panel.
/// </summary>
public sealed class DecodedFrameRow
{
    public string Position { get; init; } = "";
    public string Label { get; init; } = "";
    public string HexValue { get; init; } = "";
    public string Meaning { get; init; } = "";
}

/// <summary>
/// Decodes a SIRS frame into a sequence of labelled rows for display.
/// Pure function — no state, no events.
///
/// Frame layout (14 bytes minimum, aligned with EOS):
///   [0]      Sync1          = 0x0A
///   [1]      Sync2          = 0x88
///   [2]      ProtocolVer    = 0x01
///   [3]      ErrorByte1
///   [4]      ErrorByte2
///   [5]      Destination ID = 0x10 for System Controller / Interface Assembly
///   [6]      Source ID      = 0x10 for host
///   [7]      Sequence ID
///   [8]      Cmd MSB        (always 0 for current SIRS commands)
///   [9]      Cmd LSB        (command identifier)
///   [10]     Length
///   [11..n]  Payload
///   [n+1]    CRC16 LSB      (little-endian)
///   [n+2]    CRC16 MSB
/// </summary>
public static class SirsFrameDecoder
{
    private const int HeaderSize = 11;
    private const int MinFrame = 14;

    public static IReadOnlyList<DecodedFrameRow> Decode(byte[] frame)
    {
        var rows = new List<DecodedFrameRow>();

        // Empty / null frame
        if (frame is null || frame.Length == 0)
        {
            rows.Add(new DecodedFrameRow
            {
                Position = "—",
                Label = "Empty",
                HexValue = "—",
                Meaning = "No frame data."
            });
            return rows;
        }

        // Detect non-SIRS frames by the missing sync header (0x0A 0x88).
        // These are typically raw Noptel LRX packets emitted via the
        // SendLrfNoptelAsync passthrough — show them with a friendly
        // decode instead of rejecting them as malformed SIRS.
        bool hasSirsSync = frame.Length >= 2 && frame[0] == 0x0A && frame[1] == 0x88;
        if (!hasSirsSync)
            return DecodeRawPassthrough(frame);

        // SIRS sync present but frame is truncated
        if (frame.Length < MinFrame)
        {
            rows.Add(new DecodedFrameRow
            {
                Position = "—",
                Label = "Truncated",
                HexValue = "—",
                Meaning = $"SIRS frame too short ({frame.Length} bytes). Minimum is {MinFrame} bytes."
            });
            return rows;
        }

        // [0] Sync1
        rows.Add(new DecodedFrameRow
        {
            Position = "[0]",
            Label = "Sync 1",
            HexValue = $"0x{frame[0]:X2}",
            Meaning = frame[0] == 0x0A ? "SIRS sync byte 1 ✓" : "INVALID — expected 0x0A"
        });

        // [1] Sync2
        rows.Add(new DecodedFrameRow
        {
            Position = "[1]",
            Label = "Sync 2",
            HexValue = $"0x{frame[1]:X2}",
            Meaning = frame[1] == 0x88 ? "SIRS sync byte 2 ✓" : "INVALID — expected 0x88"
        });

        // [2] Protocol version
        rows.Add(new DecodedFrameRow
        {
            Position = "[2]",
            Label = "Protocol Ver",
            HexValue = $"0x{frame[2]:X2}",
            Meaning = frame[2] == 0x01 ? "Protocol v1 ✓" : $"Unknown protocol version (expected 0x01)"
        });

        // [3] Error byte 1
        rows.Add(new DecodedFrameRow
        {
            Position = "[3]",
            Label = "Error 1",
            HexValue = $"0x{frame[3]:X2}",
            Meaning = frame[3] == 0x00 ? "OK (command frame, no error)" : "Error source"
        });

        // [4] Error byte 2
        rows.Add(new DecodedFrameRow
        {
            Position = "[4]",
            Label = "Error 2",
            HexValue = $"0x{frame[4]:X2}",
            Meaning = frame[4] == 0x00 ? "OK" : "Error detail"
        });

        // Determine TX/RX direction
        // Byte order: [5]=Dst, [6]=Src
        //   TX: host -> device -> [5]=anyDest,  [6]=HostSrc
        //   RX: device -> host -> [5]=HostSrc,  [6]=anyDevice
        bool isLikelyTx = frame[6] == DeviceFramer.HostSrcId;
        bool isLikelyRx = frame[5] == DeviceFramer.HostSrcId;

        // [5] Source ID
        rows.Add(new DecodedFrameRow
        {
            Position = "[5]",
            Label = "Dest ID",
            HexValue = $"0x{frame[5]:X2}",
            Meaning = isLikelyTx ? DescribeEndpoint(frame[5]) + " — outgoing"
                    : isLikelyRx ? $"Host (0x{DeviceFramer.HostSrcId:X2}) — incoming"
                                 : $"Destination endpoint 0x{frame[5]:X2}"
        });

        // [6] Destination ID
        rows.Add(new DecodedFrameRow
        {
            Position = "[6]",
            Label = "Src ID",
            HexValue = $"0x{frame[6]:X2}",
            Meaning = isLikelyTx ? $"Host (0x{DeviceFramer.HostSrcId:X2})"
                    : isLikelyRx ? DescribeEndpoint(frame[6])
                                 : $"Source endpoint 0x{frame[6]:X2}"
        });

        // [7] Sequence ID
        rows.Add(new DecodedFrameRow
        {
            Position = "[7]",
            Label = "Sequence ID",
            HexValue = $"0x{frame[7]:X2}",
            Meaning = $"Frame sequence #{frame[7]}"
        });

        // [8] Cmd MSB
        rows.Add(new DecodedFrameRow
        {
            Position = "[8]",
            Label = "Cmd MSB",
            HexValue = $"0x{frame[8]:X2}",
            Meaning = frame[8] == 0x00 ? "Reserved (always 0 for SIRS) ✓" : "Non-zero — extended command space?"
        });

        // [9] Cmd LSB — actual command ID
        rows.Add(new DecodedFrameRow
        {
            Position = "[9]",
            Label = "Command",
            HexValue = $"0x{frame[9]:X2}",
            Meaning = DecodeCommandId(frame[9])
        });

        // [10] Length
        byte length = frame[10];
        int expectedTotal = HeaderSize + length + 2;
        rows.Add(new DecodedFrameRow
        {
            Position = "[10]",
            Label = "Length",
            HexValue = $"0x{length:X2}",
            Meaning = frame.Length == expectedTotal
                ? $"{length} payload byte(s), total frame = {expectedTotal} ✓"
                : $"{length} payload byte(s) — but total frame is {frame.Length}, expected {expectedTotal} ✗"
        });

        // [11..] Payload bytes
        if (length > 0 && frame.Length >= HeaderSize + length)
        {
            for (int i = 0; i < length; i++)
            {
                int pos = HeaderSize + i;
                rows.Add(new DecodedFrameRow
                {
                    Position = $"[{pos}]",
                    Label = $"Payload[{i}]",
                    HexValue = $"0x{frame[pos]:X2}",
                    Meaning = DecodePayloadByte(frame[9], i, frame[pos], length)
                });
            }
        }

        // CRC — last two bytes (little-endian: LSB first, then MSB)
        if (frame.Length >= MinFrame)
        {
            int crcLoIdx = frame.Length - 2;
            int crcHiIdx = frame.Length - 1;
            ushort txCrc = (ushort)(frame[crcLoIdx] | (frame[crcHiIdx] << 8));
            ushort calc = DeviceFramer.ComputeCrc(frame, 2, frame.Length - 4);
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

    // -------------------------------------------------------------------
    // Field-specific decoders
    // -------------------------------------------------------------------

    private static string DecodeCommandId(byte id) => id switch
    {
        DeviceCommandId.GeneralStatus => "GeneralStatus (§3.3.1)",
        DeviceCommandId.Boresight => "Boresight (§3.3.2)",
        DeviceCommandId.NirSensorSettings => "NIR Sensor Settings (§3.3.3)",
        DeviceCommandId.MwirSensorSettings => "MWIR Sensor Settings (§3.3.4)",
        DeviceCommandId.PanTiltMotorControl => "Pan/Tilt Motor Control (§3.3.5)",
        DeviceCommandId.StabControl => "Stab Control (§3.3.6)",
        DeviceCommandId.VideoSourceSelection => "Video Source Selection (§3.3.7)",
        DeviceCommandId.NirFovChange => "NIR FOV Change (§3.3.8)",
        DeviceCommandId.MwirFovChange => "MWIR FOV Change (§3.3.8)",
        DeviceCommandId.NirFocusChange => "NIR Focus Change (§3.3.9)",
        DeviceCommandId.MwirFocusChange => "MWIR Focus Change (§3.3.9)",
        DeviceCommandId.MwirImageEnhancement => "MWIR Image Enhancement (§3.3.10)",
        DeviceCommandId.NirImageEnhancement => "NIR Image Enhancement (§3.3.10)",
        DeviceCommandId.RgbImageEnhancement => "RGB Image Enhancement (§3.3.10)",
        DeviceCommandId.LrfRangeMeasurement => "LRF Range Measurement (§3.3.11)",
        DeviceCommandId.LrfStopCmm => "LRF Stop CMM (§3.3.12)",
        DeviceCommandId.LrfMeasurementRange => "LRF Range Window (§3.3.13)",
        DeviceCommandId.NirBrightnessContrast => "NIR Brightness/Contrast (§3.3.14)",
        DeviceCommandId.MwirBrightnessContrast => "MWIR Brightness/Contrast (§3.3.14)",
        DeviceCommandId.Stream1Symbology => "Stream 1 Symbology (§3.3.15)",
        DeviceCommandId.Stream2Symbology => "Stream 2 Symbology (§3.3.15)",
        DeviceCommandId.NirExposure => "NIR Exposure (auto/manual + gain + value)",
        DeviceCommandId.VisExposure => "VIS Exposure (auto/manual + gain + value)",
        DeviceCommandId.Ibit => "IBIT (§3.3.16)",
        DeviceCommandId.LrfStatusQuery => "LRF Status Query (LRX 0xC7)",
        DeviceCommandId.LrfOpticalCrosstalk => "LRF Optical Crosstalk (LRX 0xDE)",
        DeviceCommandId.LrfAlignmentPointer => "LRF Alignment Pointer (LRX 0xC5)",
        DeviceCommandId.LrfBaudRate => "LRF Baud Rate (LRX 0xC8)",
        DeviceCommandId.LrfIdentification => "LRF Identification (LRX 0xC0)",
        DeviceCommandId.LrfDiagnostics => "LRF Diagnostics (LRX 0xC2)",
        DeviceCommandId.LrfResetErrorCounter => "LRF Reset Err Counter (LRX 0xCB)",
        _ => $"Unknown command ID (0x{id:X2})"
    };

    private static string DecodePayloadByte(byte cmdId, int byteIndex, byte value, int totalLength)
    {
        if (cmdId == DeviceCommandId.GeneralStatus && byteIndex == 0 && totalLength == 1)
            return value == 0x55 ? "Request marker (0x55 = 'please respond')" : $"Unexpected (expected 0x55)";

        return $"(payload byte {byteIndex})";
    }

    // -------------------------------------------------------------------
    // Raw passthrough decoding (Noptel LRX or other non-SIRS bytes)
    // -------------------------------------------------------------------

    private static IReadOnlyList<DecodedFrameRow> DecodeRawPassthrough(byte[] frame)
    {
        var rows = new List<DecodedFrameRow>();

        // Noptel TX command frame (per ICD O50090DE):
        //   [0]      CmdID
        //   [1..n-3] payload bytes
        //   [n-2]    0x00 ("Not used")
        //   [n-1]    0x00 ("Not used")
        //   [n]      checkByte = (sum of [0..n-1]) XOR 0x50
        //
        // RX response frames start with 0x59 SYNC + echo'd cmd byte —
        // detected separately below.
        bool isRxResponse = frame.Length >= 2 && frame[0] == 0x59;
        int cmdIndex = isRxResponse ? 1 : 0;
        byte cmdByte = cmdIndex < frame.Length ? frame[cmdIndex] : (byte)0x00;
        string cmdMeaning = DecodeNoptelCommand(cmdByte);
        bool isNoptel = !cmdMeaning.StartsWith("Unknown");

        rows.Add(new DecodedFrameRow
        {
            Position = "—",
            Label = isRxResponse ? "Noptel RX"
                     : isNoptel ? "Noptel TX"
                                    : "Raw bytes",
            HexValue = "—",
            Meaning = isRxResponse
                ? $"Response from LRX — echo of {cmdMeaning}"
                : isNoptel
                    ? $"Command to LRX — {cmdMeaning}"
                    : $"Non-SIRS bytes ({frame.Length} byte(s)); command 0x{cmdByte:X2} unknown."
        });

        // Expected check byte = sum of everything except last byte, XOR 0x50
        byte? expectedCheck = null;
        if (frame.Length >= 2)
        {
            int sum = 0;
            for (int i = 0; i < frame.Length - 1; i++) sum += frame[i];
            expectedCheck = (byte)(sum ^ 0x50);
        }

        // TX layout has 2 "Not used" placeholder bytes immediately
        // before the check byte. Identify their indices so the loop
        // can label them as "Not used" rather than "Payload".
        int placeholderHi = frame.Length - 3;  // [n-2]
        int placeholderLo = frame.Length - 2;  // [n-1]

        for (int i = 0; i < frame.Length; i++)
        {
            string label, meaning;
            bool isLast = i == frame.Length - 1;

            if (isRxResponse && i == 0)
            {
                label = "Sync";
                meaning = "Noptel response SYNC (0x59)";
            }
            else if (i == cmdIndex)
            {
                label = isRxResponse ? "Cmd echo" : "Command";
                meaning = cmdMeaning;
            }
            else if (!isRxResponse && (i == placeholderHi || i == placeholderLo) && i > cmdIndex)
            {
                label = "Not used";
                meaning = "ICD placeholder (0x00)";
            }
            else if (isLast && expectedCheck.HasValue && frame.Length >= 2)
            {
                bool ok = frame[i] == expectedCheck.Value;
                label = "Check";
                meaning = ok
                    ? $"check byte ✓ (sum XOR 0x50 = 0x{expectedCheck:X2})"
                    : $"check byte MISMATCH — got 0x{frame[i]:X2}, expected 0x{expectedCheck:X2}";
            }
            else
            {
                int payloadStart = cmdIndex + 1;
                label = $"Payload[{i - payloadStart}]";
                meaning = "(parameter byte)";
            }

            rows.Add(new DecodedFrameRow
            {
                Position = $"[{i}]",
                Label = label,
                HexValue = $"0x{frame[i]:X2}",
                Meaning = meaning
            });
        }

        return rows;
    }

    private static string DecodeNoptelCommand(byte cmd) => cmd switch
    {
        0xC0 => "Request Identification (Noptel §3.10)",
        0xC2 => "Request Diagnostic Data (Noptel §3.11)",
        0xC5 => "Set Alignment Pointer (Noptel §3.5)",
        0xC6 => "Break / Stop Range Measurement (Noptel)",
        0xC7 => "Ask Status (Noptel §3.4)",
        0xC8 => "Set Baud Rate / Save Settings (Noptel §3.9)",
        0xCA => "Set Measurement Range (Noptel — verify cmd byte vs ICD)",
        0xCB => "Reset Serial Error Counter (Noptel §3.12)",
        0xCC => "Execute Range Measurement (Noptel — verify cmd byte vs ICD)",
        0xCD => "Get Measurement Range (Noptel — verify cmd byte vs ICD)",
        0xDE => "Check Optical Crosstalk (Noptel §3.3)",
        _ => $"Unknown Noptel command (0x{cmd:X2})"
    };

    /// <summary>
    /// Friendly-name lookup for subsystem IDs from the EOS ID document.
    /// Used by the Decoded Frame view to label [5] / [6] endpoints.
    /// </summary>
    private static string DescribeEndpoint(byte id) => id switch
    {
        0x00 => "Host (0x00)",
        0x10 => "System Controller / Interface Assembly (0x10)",
        0x20 => "Pan/Tilt Stab Controller (0x20)",
        0x50 => "Payload Assembly (0x50)",
        0x52 => "MWIR EOA (0x52)",
        0x53 => "SWIR EOA (0x53)",
        0x54 => "VisNIR EOA (0x54)",
        0x55 => "LPI (0x55)",
        0x56 => "LRF / Noptel (0x56)",
        _ => $"Unknown endpoint (0x{id:X2})"
    };
}