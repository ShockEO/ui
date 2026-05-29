using System.IO.Ports;

namespace ShockUI.Models.App;

/// <summary>
/// Which physical transport a module uses to reach its hardware. The same
/// protocol bytes go over either — only the channel differs.
/// </summary>
public enum TransportKind
{
    /// <summary>RS-232 / RS-422 serial via SerialPort. Default.</summary>
    Uart = 0,

    /// <summary>LAN UDP. Sends datagrams to RemoteHost:RemotePort and listens on LocalPort.</summary>
    Udp = 1,
}

/// <summary>
/// Runtime-configurable comms settings shared by every module's service.
/// Carries both UART parameters (baud, parity, etc.) and UDP parameters
/// (remote host/port, local listen port). The active fields depend on
/// <see cref="Transport"/>.
/// </summary>
public sealed class SerialPortSettings
{
    // ── Transport choice ──────────────────────────────────────────────────
    public TransportKind Transport { get; set; } = TransportKind.Uart;

    // ── UART fields ───────────────────────────────────────────────────────
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;

    /// <summary>Receive timeout in ms. 0 = disabled.</summary>
    public int ReadTimeout { get; set; } = 500;

    /// <summary>Transmit timeout in ms. 0 = disabled.</summary>
    public int WriteTimeout { get; set; } = 500;

    // ── UDP fields ────────────────────────────────────────────────────────
    /// <summary>Remote hardware IP or hostname (UDP only).</summary>
    public string RemoteHost { get; set; } = "192.168.1.10";

    /// <summary>Remote UDP port to send to (UDP only).</summary>
    public int RemotePort { get; set; } = 5000;

    /// <summary>Local UDP port to bind for receive (UDP only). Set independently of RemotePort.</summary>
    public int LocalPort { get; set; } = 5001;

    // ── Factory defaults ──────────────────────────────────────────────────

    /// <summary>Default used by VisNIR / SWIR / PanTilt / Camera (EOS &amp; camera framing).</summary>
    public static SerialPortSettings Default115200N81() => new()
    {
        Transport = TransportKind.Uart,
        BaudRate = 115200,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
        Handshake = Handshake.None,
    };

    /// <summary>
    /// System Controller (SIRS protocol §3.1) - 115200 8-N-1.
    /// Note: SIRS spec nominally calls for Even parity, but real hardware
    /// in this deployment uses None. Change here if the board's UART
    /// configuration differs.
    /// </summary>
    public static SerialPortSettings Sirs115200E81() => new()
    {
        Transport = TransportKind.Uart,
        BaudRate = 115200,
        DataBits = 8,
        Parity = Parity.None,
        StopBits = StopBits.One,
        Handshake = Handshake.None,
    };

    public SerialPortSettings Clone() => new()
    {
        Transport = Transport,
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
        Handshake = Handshake,
        ReadTimeout = ReadTimeout,
        WriteTimeout = WriteTimeout,
        RemoteHost = RemoteHost,
        RemotePort = RemotePort,
        LocalPort = LocalPort,
    };

    public override string ToString() => Transport switch
    {
        TransportKind.Udp => $"UDP {RemoteHost}:{RemotePort} ← :{LocalPort}",
        _ => $"{BaudRate} {DataBits}-{ParityChar()}-{StopBitsNum()}"
    };

    private char ParityChar() => Parity switch
    {
        Parity.None => 'N',
        Parity.Odd => 'O',
        Parity.Even => 'E',
        Parity.Mark => 'M',
        Parity.Space => 'S',
        _ => '?'
    };

    private string StopBitsNum() => StopBits switch
    {
        StopBits.One => "1",
        StopBits.OnePointFive => "1.5",
        StopBits.Two => "2",
        _ => "?"
    };
}