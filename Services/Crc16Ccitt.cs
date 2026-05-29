using System;

namespace ShockUI.Services;

/// <summary>
/// CRC-16/CCITT-FALSE implementation, used by every protocol layer in the app:
/// <list type="bullet">
///   <item>EOS framing (Pan/Tilt, VisNIR, SWIR)</item>
///   <item>Phylax SIRS framing (System Controller)</item>
///   <item>0xAA 0x55 framing (Camera Controller)</item>
/// </list>
///
/// <para>
/// <b>Algorithm parameters:</b>
/// </para>
/// <list type="bullet">
///   <item>Polynomial: <c>0x1021</c></item>
///   <item>Initial value: <c>0xFFFF</c></item>
///   <item>Input reflection: none</item>
///   <item>Output reflection: none</item>
///   <item>Final XOR: none</item>
/// </list>
///
/// This is also known as CRC-16/IBM-3740 / CRC-16/AUTOSAR / CRC-CCITT-FALSE.
///
/// Two overloads are provided so existing callers don't need to change their
/// argument shapes; both produce the same result.
/// </summary>
public static class Crc16Ccitt
{
    /// <summary>
    /// Compute the CRC over the given span. Preferred overload — avoids any
    /// allocation when called with <c>frame.AsSpan(...)</c>.
    /// </summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;

        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);

            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    /// <summary>
    /// Compute the CRC over the first <paramref name="length"/> bytes of
    /// <paramref name="data"/>. Provided for legacy callers; internally
    /// delegates to the span overload.
    /// </summary>
    public static ushort Compute(byte[] data, int length)
        => Compute(data.AsSpan(0, length));
}