using System;
using System.Collections.Generic;

namespace ShockUI.Services.Device;

/// <summary>
/// Builds and parses raw byte frames for the SIRS interface.
///
/// Frame layout (unified with the EOS subsystem protocol — see
/// EosFramer for the byte-by-byte source-of-truth):
///   [0]      Sync1          = 0x0A
///   [1]      Sync2          = 0x88
///   [2]      ProtocolVer    = 0x01
///   [3]      ErrorByte1     = 0x00 in commands; error source in responses
///   [4]      ErrorByte2     = 0x00 in commands; error detail in responses
///   [5]      Destination ID = target subsystem (0x10 SC, 0x54 VisNIR EOA, 0x20 PTSC, ...)
///   [6]      Source ID      = originating subsystem (0x00 host)
///   [7]      Sequence ID    = 0x00-0xFF wrapping
///   [8]      Cmd MSB        = big-endian, always 0x00 for current SIRS cmds
///   [9]      Cmd LSB        = SIRS command identifier (see DeviceCommandId)
///   [10]     Length         = number of payload bytes (minimum 0x01)
///   [11..n]  Payload
///   [n+1]    CRC16 LSB      ) little-endian per struct.pack('&lt;H', crc)
///   [n+2]    CRC16 MSB      )
///
/// CRC-16/CCITT (poly 0x1021, init 0xFFFF) computed over bytes [2]..[n]
/// (Protocol Version through last payload byte).
/// </summary>
public static class DeviceFramer
{
    public const byte Sync1 = 0x0A;
    public const byte Sync2 = 0x88;
    public const byte ProtocolVer = 0x01;

    /// <summary>System Controller / Interface Assembly ID per ID document.</summary>
    public const byte SystemControllerDstId = 0x10;

    /// <summary>Pan/Tilt Stab Controller ID per ID document — motor &amp; stab commands route directly here.</summary>
    public const byte PtscDstId = 0x20;

    /// <summary>
    /// Source ID our host PC presents on the wire. Per the Host GUI
    /// changes spec the host always identifies as 0x00 — it is not one
    /// of the assigned subsystem IDs in the ID document; it just means
    /// "engineering laptop / host PC". The SC firmware identifies us
    /// by this value when routing responses back.
    /// </summary>
    public const byte HostSrcId = 0x00;

    /// <summary>VN EOA Zoom Controller per ID document. EOA-direct commands
    /// (NIR FOV change, NIR focus change) use this as their destination so
    /// the SC just forwards the frame to the VisNIR EOA controller.</summary>
    public const byte VisNirDstId = 0x54;

    /// <summary>SWIR EOA Controller per ID document — reserved for the
    /// SWIR module's EOA-direct commands when those are added.</summary>
    public const byte SwirDstId = 0x53;

    private const int HeaderSize = 11;   // bytes 0-10 inclusive
    private const int MinFrame = 14;   // header(11) + min payload(1) + CRC(2)

    // -----------------------------------------------------------------------
    // TX
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a complete SIRS command frame ready for transmission.
    /// Convenience overload — uses default dst (System Controller) and
    /// src (Host) IDs.
    /// </summary>
    public static byte[] BuildCommand(byte commandId, byte seqId, byte[] payload)
        => BuildCommand(commandId, seqId, payload, SystemControllerDstId, HostSrcId);

    /// <summary>
    /// Builds a complete SIRS command frame with explicit dst/src IDs.
    /// </summary>
    /// <param name="commandId">SIRS command (see <see cref="ShockUI.Models.Device.DeviceCommandId"/>).</param>
    /// <param name="seqId">Sequence counter; caller manages wrap-around.</param>
    /// <param name="payload">Data bytes; length must match the SRS-specified Length field.</param>
    /// <param name="dstId">Destination subsystem ID.</param>
    /// <param name="srcId">Source (originator) subsystem ID.</param>
    public static byte[] BuildCommand(byte commandId, byte seqId, byte[] payload,
                                       byte dstId, byte srcId)
    {
        int len = payload.Length;
        var frame = new byte[HeaderSize + len + 2];

        frame[0] = Sync1;
        frame[1] = Sync2;
        frame[2] = ProtocolVer;
        frame[3] = 0x00;        // ErrorByte1 – always 0 in commands
        frame[4] = 0x00;        // ErrorByte2 – always 0 in commands
        frame[5] = dstId;
        frame[6] = srcId;
        frame[7] = seqId;
        frame[8] = 0x00;        // Cmd MSB – always 0 for current SIRS commands
        frame[9] = commandId;   // Cmd LSB – actual command identifier
        frame[10] = (byte)len;

        Array.Copy(payload, 0, frame, HeaderSize, len);

        ushort crc = ComputeCrc(frame, 2, HeaderSize + len - 2);
        // Little-endian per Python struct.pack('<H', crc_val).
        frame[HeaderSize + len] = (byte)(crc & 0xFF);   // CRC LSB
        frame[HeaderSize + len + 1] = (byte)(crc >> 8);     // CRC MSB

        return frame;
    }

    // -----------------------------------------------------------------------
    // RX  (incremental, stateless – caller owns the buffer)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Feeds raw received bytes into <paramref name="buffer"/> and extracts
    /// any complete, CRC-valid frames. Safe to call with partial data.
    /// </summary>
    public static IEnumerable<DeviceParsedFrame> FeedBytes(byte[] incoming, List<byte> buffer)
    {
        buffer.AddRange(incoming);
        var results = new List<DeviceParsedFrame>();

        while (buffer.Count >= MinFrame)
        {
            int syncIdx = FindSync(buffer);

            if (syncIdx < 0)
            {
                // No sync anywhere – discard all but the last byte
                if (buffer.Count > 1)
                    buffer.RemoveRange(0, buffer.Count - 1);
                break;
            }

            if (syncIdx > 0)
                buffer.RemoveRange(0, syncIdx);

            if (buffer.Count < MinFrame)
                break;

            int payloadLen = buffer[10];
            int totalLen = HeaderSize + payloadLen + 2;

            if (buffer.Count < totalLen)
                break;  // Wait for more bytes

            // Validate CRC (little-endian on the wire)
            byte[] candidate = buffer.GetRange(0, totalLen).ToArray();
            ushort rxCrc = (ushort)(candidate[totalLen - 2] | (candidate[totalLen - 1] << 8));
            ushort calcCrc = ComputeCrc(candidate, 2, HeaderSize + payloadLen - 2);

            if (rxCrc != calcCrc)
            {
                // Bad CRC – skip past the sync bytes and re-hunt
                buffer.RemoveRange(0, 2);
                continue;
            }

            results.Add(new DeviceParsedFrame
            {
                ProtocolVersion = candidate[2],
                ErrorByte1 = candidate[3],
                ErrorByte2 = candidate[4],
                DestinationId = candidate[5],
                SourceId = candidate[6],
                SequenceId = candidate[7],
                // 16-bit big-endian command; SIRS only uses the LSB.
                CommandId = candidate[9],
                Payload = candidate[HeaderSize..(HeaderSize + payloadLen)]
            });

            buffer.RemoveRange(0, totalLen);
        }

        return results;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static int FindSync(List<byte> buf)
    {
        for (int i = 0; i < buf.Count - 1; i++)
            if (buf[i] == Sync1 && buf[i + 1] == Sync2)
                return i;
        return -1;
    }

    /// <summary>CRC-16/CCITT  poly=0x1021  init=0xFFFF (matches existing Crc16Ccitt.cs).</summary>
    public static ushort ComputeCrc(byte[] data, int offset, int count)
    {
        ushort crc = 0xFFFF;
        for (int i = offset; i < offset + count; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int j = 0; j < 8; j++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}

/// <summary>
/// A CRC-validated inbound SIRS frame extracted by <see cref="DeviceFramer"/>.
/// </summary>
public sealed class DeviceParsedFrame
{
    public byte ProtocolVersion { get; init; }
    public byte ErrorByte1 { get; init; }
    public byte ErrorByte2 { get; init; }
    public byte DestinationId { get; init; }
    public byte SourceId { get; init; }
    public byte SequenceId { get; init; }
    public byte CommandId { get; init; }
    public byte[] Payload { get; init; } = [];

    public ushort ErrorCode => (ushort)((ErrorByte1 << 8) | ErrorByte2);
    public bool HasError => ErrorCode != 0x0000;
}