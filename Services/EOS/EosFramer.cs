using System;
using System.Collections.Generic;
// Crc16Ccitt now lives in ShockUI.Services (shared utility)

namespace ShockUI.Services.Eos;

/// <summary>
/// Shared EOS binary protocol framer.
/// Extracted from OpticalModuleCommandBuilder / OpticalModuleResponseParser so that
/// any EOS subsystem service (VisNIR, SWIR, Pan/Tilt, MWIR…) can reuse one implementation.
///
/// EOS frame layout (identical for EΩS and Phylax hardware):
///   [0]      Sync1          = 0x0A
///   [1]      Sync2          = 0x88
///   [2]      Protocol Ver   = 0x01
///   [3]      Error Byte 1   = 0x00 (commands) | system error (responses)
///   [4]      Error Byte 2   = 0x00 (commands) | system error (responses)
///   [5]      Destination ID = target subsystem ID (e.g. 0x20=PTSC)
///   [6]      Source ID      = originating subsystem ID (0x00 host)
///   [7]      Sequence ID    = 0x00-0xFF wrapping counter
///   [8]      Cmd Byte 1     = command MSB (big-endian, matches struct.pack('>H',cmd))
///   [9]      Cmd Byte 2     = command LSB
///   [10]     Length         = payload byte count (≥ 1, excludes CRC)
///   [11..n]  Payload        = data bytes
///   [n+1]    CRC16 LSB      )  CRC-16/CCITT over bytes[0]..[n]
///   [n+2]    CRC16 MSB      )  (full frame except CRC bytes themselves)
///
/// Min frame size = 11 (header) + 1 (min payload) + 2 (CRC) = 14 bytes.
/// </summary>
public static class EosFramer
{
    public const byte Sync1 = 0x0A;
    public const byte Sync2 = 0x88;
    public const byte ProtocolVer = 0x01;

    private const int HeaderSize = 11;   // bytes 0-10 inclusive
    private const int MinFrame = 14;   // header + 1 payload + 2 CRC

    // -----------------------------------------------------------------------
    // TX
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a complete EOS command frame ready for transmission.
    /// </summary>
    /// <param name="dstId">Destination subsystem ID (e.g. 0x20 for PTSC).</param>
    /// <param name="srcId">Source subsystem ID (e.g. 0x10 for SCP/Host).</param>
    /// <param name="seqId">Current sequence counter (caller manages wrap-around at 0xFF).</param>
    /// <param name="command">Two-byte command as ushort. Byte[8]=LSB, Byte[9]=MSB.</param>
    /// <param name="payload">Data payload. Must contain at least 1 byte.</param>
    public static byte[] BuildCommand(byte dstId, byte srcId, byte seqId,
                                      ushort command, byte[] payload)
    {
        int len = payload.Length;
        var frame = new byte[HeaderSize + len + 2];

        frame[0] = Sync1;
        frame[1] = Sync2;
        frame[2] = ProtocolVer;
        frame[3] = 0x00;                   // Error Byte 1 – always 0 in commands
        frame[4] = 0x00;                   // Error Byte 2
        frame[5] = dstId;
        frame[6] = srcId;
        frame[7] = seqId;
        frame[8] = (byte)(command >> 8);   // Cmd Byte 1 = MSB (big-endian)
        frame[9] = (byte)(command & 0xFF); // Cmd Byte 2 = LSB
        frame[10] = (byte)len;

        Array.Copy(payload, 0, frame, HeaderSize, len);

        ushort crc = Crc16Ccitt.Compute(frame.AsSpan(0, HeaderSize + len));
        frame[HeaderSize + len] = (byte)(crc & 0xFF);  // CRC LSB
        frame[HeaderSize + len + 1] = (byte)(crc >> 8);    // CRC MSB

        return frame;
    }

    // -----------------------------------------------------------------------
    // RX – single frame validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validates and parses a raw byte array as an EOS frame.
    /// </summary>
    public static bool TryParse(byte[] frame, out EosParsedFrame? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (frame.Length < MinFrame)
        {
            error = $"Frame too short ({frame.Length} < {MinFrame}).";
            return false;
        }

        if (frame[0] != Sync1 || frame[1] != Sync2)
        {
            error = "Invalid sync bytes.";
            return false;
        }

        int payloadLen = frame[10];
        int expectedTotal = HeaderSize + payloadLen + 2;

        if (frame.Length != expectedTotal)
        {
            error = $"Length mismatch: expected {expectedTotal}, got {frame.Length}.";
            return false;
        }

        ushort rxCrc = (ushort)(frame[^2] | (frame[^1] << 8));
        ushort calcCrc = Crc16Ccitt.Compute(frame.AsSpan(0, frame.Length - 2));

        if (rxCrc != calcCrc)
        {
            error = $"CRC mismatch: received 0x{rxCrc:X4}, computed 0x{calcCrc:X4}.";
            return false;
        }

        byte[] payload = new byte[payloadLen];
        Array.Copy(frame, HeaderSize, payload, 0, payloadLen);

        parsed = new EosParsedFrame
        {
            ErrorByte1 = frame[3],
            ErrorByte2 = frame[4],
            DestinationId = frame[5],
            SourceId = frame[6],
            SequenceId = frame[7],
            Command = (ushort)((frame[8] << 8) | frame[9]),  // big-endian
            Payload = payload
        };

        return true;
    }

    // -----------------------------------------------------------------------
    // RX – incremental parser (caller owns buffer)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Feeds raw received bytes into <paramref name="buffer"/> and extracts
    /// any complete, CRC-valid frames. Safe to call with partial data.
    /// </summary>
    public static IEnumerable<EosParsedFrame> FeedBytes(byte[] incoming, List<byte> buffer)
    {
        buffer.AddRange(incoming);
        var results = new List<EosParsedFrame>();

        while (buffer.Count >= MinFrame)
        {
            int syncIdx = FindSync(buffer);
            if (syncIdx < 0)
            {
                if (buffer.Count > 1) buffer.RemoveRange(0, buffer.Count - 1);
                break;
            }

            if (syncIdx > 0) buffer.RemoveRange(0, syncIdx);
            if (buffer.Count < MinFrame) break;

            int payloadLen = buffer[10];
            int totalLen = HeaderSize + payloadLen + 2;

            if (buffer.Count < totalLen) break;

            byte[] candidate = buffer.GetRange(0, totalLen).ToArray();
            if (TryParse(candidate, out var parsed, out _) && parsed is not null)
                results.Add(parsed);

            buffer.RemoveRange(0, totalLen);
        }

        return results;
    }

    // -----------------------------------------------------------------------

    private static int FindSync(List<byte> buf)
    {
        for (int i = 0; i < buf.Count - 1; i++)
            if (buf[i] == Sync1 && buf[i + 1] == Sync2)
                return i;
        return -1;
    }
}

/// <summary>
/// A CRC-validated inbound EOS frame produced by <see cref="EosFramer"/>.
/// </summary>
public sealed class EosParsedFrame
{
    public byte ErrorByte1 { get; init; }
    public byte ErrorByte2 { get; init; }
    public byte DestinationId { get; init; }
    public byte SourceId { get; init; }
    public byte SequenceId { get; init; }
    public ushort Command { get; init; }
    public byte[] Payload { get; init; } = [];

    public bool HasError => ErrorByte1 != 0 || ErrorByte2 != 0;
    public ushort ErrorCode => (ushort)((ErrorByte1 << 8) | ErrorByte2);
}