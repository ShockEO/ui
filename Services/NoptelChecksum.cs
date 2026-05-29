// ============================================================
//  Services/NoptelChecksum.cs
//
//  Noptel LRX TX packet framing + check-byte algorithm.
//
//  ── Command frame layout (per Noptel ICD O50090DE) ─────────
//
//      [0]      CmdID
//      [1..n-3] payload bytes
//      [n-2]    0x00 (Not used)
//      [n-1]    0x00 (Not used)
//      [n]      Check byte = (sum of [0..n-1]) XOR 0x50
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
    /// Builds a complete Noptel TX command packet:
    ///   <c>[cmdId][payload...][0x00][0x00][checkByte]</c>
    /// </summary>
    /// <param name="cmdId">Noptel command byte (e.g. 0xCC = range measurement).</param>
    /// <param name="payload">Command parameters; pass empty/default for none.</param>
    public static byte[] BuildPacket(byte cmdId, ReadOnlySpan<byte> payload = default)
    {
        // 1 cmd + payload + 2 "Not used" placeholders + 1 check
        int totalLen = 1 + payload.Length + 2 + 1;
        byte[] pkt = new byte[totalLen];

        pkt[0] = cmdId;
        if (!payload.IsEmpty)
            payload.CopyTo(pkt.AsSpan(1));
        // pkt[1 + payload.Length]   = 0x00 (default-init, "Not used")
        // pkt[2 + payload.Length]   = 0x00 (default-init, "Not used")

        // Check covers cmd + payload + the two placeholders.
        pkt[totalLen - 1] = Compute(pkt.AsSpan(0, totalLen - 1));
        return pkt;
    }
}