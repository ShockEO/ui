using System;
using System.Collections.Generic;
using System.IO.Ports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI.Models.App;

namespace ShockUI.ViewModels;

/// <summary>
/// ViewModel for the "Port Settings" popup. Supports both UART and UDP
/// transports. The UI shows the relevant fields for the selected transport;
/// the inactive fields stay in the model so switching back doesn't lose
/// previous values.
/// </summary>
public sealed partial class PortSettingsViewModel : ObservableObject
{
    private readonly Action<SerialPortSettings> _applySettings;
    private readonly Action<bool> _applyAutoReconnect;
    private readonly Action? _onClose;

    public PortSettingsViewModel(
        SerialPortSettings current,
        bool autoReconnectEnabled,
        Action<SerialPortSettings> applySettings,
        Action<bool> applyAutoReconnect,
        Action? onClose = null)
    {
        _applySettings = applySettings;
        _applyAutoReconnect = applyAutoReconnect;
        _onClose = onClose;

        Transport = current.Transport;
        BaudRate = current.BaudRate;
        DataBits = current.DataBits;
        Parity = current.Parity;
        StopBits = current.StopBits;
        Handshake = current.Handshake;
        ReadTimeout = current.ReadTimeout;
        WriteTimeout = current.WriteTimeout;
        RemoteHost = current.RemoteHost;
        RemotePort = current.RemotePort;
        LocalPort = current.LocalPort;

        AutoReconnect = autoReconnectEnabled;
    }

    public IReadOnlyList<int> BaudRateOptions { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];
    public IReadOnlyList<int> DataBitsOptions { get; } = [5, 6, 7, 8];
    public IReadOnlyList<Parity> ParityOptions { get; } = [Parity.None, Parity.Odd, Parity.Even, Parity.Mark, Parity.Space];
    public IReadOnlyList<StopBits> StopBitsOptions { get; } = [StopBits.One, StopBits.OnePointFive, StopBits.Two];
    public IReadOnlyList<Handshake> HandshakeOptions { get; } = [Handshake.None, Handshake.XOnXOff, Handshake.RequestToSend, Handshake.RequestToSendXOnXOff];

    [ObservableProperty] private TransportKind transport;
    public bool IsUart => Transport == TransportKind.Uart;
    public bool IsUdp => Transport == TransportKind.Udp;

    partial void OnTransportChanged(TransportKind value)
    {
        OnPropertyChanged(nameof(IsUart));
        OnPropertyChanged(nameof(IsUdp));
    }

    [RelayCommand] private void SelectUart() => Transport = TransportKind.Uart;
    [RelayCommand] private void SelectUdp() => Transport = TransportKind.Udp;

    [ObservableProperty] private int baudRate;
    [ObservableProperty] private int dataBits;
    [ObservableProperty] private Parity parity;
    [ObservableProperty] private StopBits stopBits;
    [ObservableProperty] private Handshake handshake;
    [ObservableProperty] private int readTimeout;
    [ObservableProperty] private int writeTimeout;

    [ObservableProperty] private string remoteHost = "192.168.1.10";
    [ObservableProperty] private int remotePort = 5000;
    [ObservableProperty] private int localPort = 5001;

    [ObservableProperty] private bool autoReconnect;

    [RelayCommand]
    private void Apply()
    {
        var settings = new SerialPortSettings
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
        _applySettings(settings);
        _applyAutoReconnect(AutoReconnect);
        _onClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => _onClose?.Invoke();
}