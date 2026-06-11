// ============================================================
//  Services/NoptelChecksum.cs
//
//  Noptel LRX TX packet framing + check-byte algorithm.
//
//  ── Command frame layout (per Noptel ICD O50090DE) ─────────
//
//      [0]      CmdID
//      [1..]    payload bytes
//      [..]     0x00 0x00 (Not used)  ── ONLY for Range Measurement
//                                        (0xCC); omitted for all others
//      [n]      Check byte = (sum of [0..n-1]) XOR 0x50
//
//  NOTE: Most LRX commands have NO trailing reserve bytes. Only the
//  Range Measurement (0xCC) frame carries the two 0x00 placeholders.
//  Callers control this via BuildPacket(..., includeReserved:).
//
//  ── Example: 10 Hz CMM range measurement ───────────────────
//
//      Wire bytes: CC 03 00 00 9F
//                  └┘ └┘ └────┘ └┘
//                  cmd mode  Not-used  check
//                                      = (CC+03+00+00) XOR 0x50
//                                      = CF XOR 50 = 9F ✓
//
//  ── Response frame ────────────────────────────────────────
//
//  The 0x59 SYNC byte and command echo only appear in RESPONSES
//  from the LRX (not in commands we send). The RX parser handles
//  those separately; this file is TX-only.
// ============================================================
using System;

namespace ShockUI.Services;

public static class NoptelChecksum
{
    /// <summary>SYNC byte that prefixes every LRX *response* frame.</summary>
    public const byte ResponseSyncByte = 0x59;

    /// <summary>
    /// Computes the 1-byte Noptel check over <paramref name="data"/>:
    /// (sum of all bytes) XOR 0x50, truncated to a byte.
    /// </summary>
    public static byte Compute(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        for (int i = 0; i < data.Length; i++)
            sum += data[i];
        return (byte)(sum ^ 0x50);
    }

    /// <summary>
    /// Builds a complete Noptel TX command packet.
    ///
    /// With <paramref name="includeReserved"/> = true (default):
    ///   <c>[cmdId][payload...][0x00][0x00][checkByte]</c>
    /// With <paramref name="includeReserved"/> = false:
    ///   <c>[cmdId][payload...][checkByte]</c>
    ///
    /// Per the verified Noptel LRX ICD, most commands do NOT carry the
    /// two trailing "Not used" reserve bytes. They are retained only for
    /// the Range Measurement command (0xCC), which the ICD shows with the
    /// filler present (e.g. 10 Hz CMM: CC 03 00 00 9F).
    /// </summary>
    /// <param name="cmdId">Noptel command byte (e.g. 0xCC = range measurement).</param>
    /// <param name="payload">Command parameters; pass empty/default for none.</param>
    /// <param name="includeReserved">
    /// When true, appends two 0x00 "Not used" placeholder bytes before the
    /// check byte. Pass false for commands whose ICD frame has no reserve
    /// bytes (Status, Stop CMM, Alignment Pointer, Optical Crosstalk,
    /// Identification, Diagnostics, Reset Err Counter, Baud, Set/Get Range).
    /// </param>
    public static byte[] BuildPacket(byte cmdId, ReadOnlySpan<byte> payload = default,
                                     bool includeReserved = true)
    {
        int reserved = includeReserved ? 2 : 0;
        // 1 cmd + payload + [0|2] "Not used" placeholders + 1 check
        int totalLen = 1 + payload.Length + reserved + 1;
        byte[] pkt = new byte[totalLen];

        pkt[0] = cmdId;
        if (!payload.IsEmpty)
            payload.CopyTo(pkt.AsSpan(1));
        // Any reserve placeholders are left default-initialised to 0x00.

        // Check covers cmd + payload + the (optional) placeholders.
        pkt[totalLen - 1] = Compute(pkt.AsSpan(0, totalLen - 1));
        return pkt;
    }
}