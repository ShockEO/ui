using ShockUI.Models.App;

namespace ShockUI.Services.App;

/// <summary>
/// Builds the appropriate <see cref="ITransport"/> for a given
/// <see cref="SerialPortSettings"/>. Centralises transport selection so every
/// module's serial service uses identical UART-vs-UDP logic.
/// </summary>
public static class TransportFactory
{
    /// <summary>
    /// Create a transport for the given settings.
    /// </summary>
    /// <param name="portNameOrEndpoint">
    /// For <see cref="TransportKind.Uart"/>: the COM port name ("COM5"). Ignored for UDP.
    /// </param>
    /// <param name="settings">Comms settings (UART parameters or UDP endpoint).</param>
    public static ITransport Create(string portNameOrEndpoint, SerialPortSettings settings) =>
        settings.Transport switch
        {
            TransportKind.Udp => new UdpTransport(settings.RemoteHost,
                                                  settings.RemotePort,
                                                  settings.LocalPort),
            _ => new UartTransport(portNameOrEndpoint, settings)
        };
}