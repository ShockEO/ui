using System;

namespace ShockUI.Services.App;

/// <summary>
/// Abstracts the byte-level transport used by a module's serial service.
/// Both UART (RS-232/422 via SerialPort) and LAN (UDP) implement this so
/// the same protocol framers and services work over either medium without
/// caring about the physical channel.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>True while the underlying transport is open and ready.</summary>
    bool IsOpen { get; }

    /// <summary>Human-readable description, e.g. "COM5 @ 115200 8-N-1" or "192.168.1.10:5000 ↔ :5000".</summary>
    string Description { get; }

    /// <summary>
    /// Open the transport. Any prior connection is closed first.
    /// </summary>
    /// <returns><c>true</c> on success, <c>false</c> otherwise (no exception thrown).</returns>
    bool Open();

    /// <summary>Close cleanly. Idempotent.</summary>
    void Close();

    /// <summary>
    /// Transmit bytes. For UART this writes to the COM port; for UDP this
    /// sends a single datagram to the configured remote endpoint.
    /// </summary>
    void Write(byte[] data, int offset, int count);

    /// <summary>
    /// Fires for every chunk of bytes received. UART fires when serial data
    /// arrives; UDP fires once per datagram. Either way, the listener feeds
    /// the bytes to the module's framer just like before.
    /// </summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Fires when the underlying transport reports a transmission error.</summary>
    event Action<string>? ErrorReceived;
}