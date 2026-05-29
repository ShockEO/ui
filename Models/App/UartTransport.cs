using System;
using System.IO.Ports;
using ShockUI.Models.App;

namespace ShockUI.Services.App;

/// <summary>
/// UART (serial port) transport. Wraps <see cref="SerialPort"/> and exposes
/// it through the common <see cref="ITransport"/> interface so module
/// services can use UART or UDP interchangeably.
/// </summary>
public sealed class UartTransport : ITransport
{
    private readonly string _portName;
    private readonly SerialPortSettings _settings;
    private SerialPort? _port;

    public UartTransport(string portName, SerialPortSettings settings)
    {
        _portName = portName;
        _settings = settings;
    }

    public bool IsOpen => _port?.IsOpen ?? false;

    public string Description => $"{_portName} @ {_settings}";

    public event Action<byte[]>? DataReceived;
    public event Action<string>? ErrorReceived;

    public bool Open()
    {
        try
        {
            Close();

            _port = new SerialPort(_portName,
                                   _settings.BaudRate,
                                   _settings.Parity,
                                   _settings.DataBits,
                                   _settings.StopBits)
            {
                Handshake = _settings.Handshake,
                ReadTimeout = _settings.ReadTimeout == 0 ? 200 : _settings.ReadTimeout,
                WriteTimeout = _settings.WriteTimeout == 0 ? 200 : _settings.WriteTimeout,
            };

            _port.DataReceived += OnDataReceived;
            _port.ErrorReceived += OnErrorReceived;
            _port.Open();
            return true;
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(ex.Message);
            _port?.Dispose();
            _port = null;
            return false;
        }
    }

    public void Close()
    {
        if (_port is null) return;
        try
        {
            _port.DataReceived -= OnDataReceived;
            _port.ErrorReceived -= OnErrorReceived;
            if (_port.IsOpen) _port.Close();
        }
        catch { /* ignore */ }
        finally
        {
            _port.Dispose();
            _port = null;
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        if (_port is null || !_port.IsOpen)
            throw new InvalidOperationException("UART transport is not open.");

        _port.Write(data, offset, count);
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_port is null || !_port.IsOpen) return;
        try
        {
            int n = _port.BytesToRead;
            if (n <= 0) return;
            byte[] buf = new byte[n];
            _port.Read(buf, 0, n);
            DataReceived?.Invoke(buf);
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(ex.Message);
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        => ErrorReceived?.Invoke($"Serial error: {e.EventType}");

    public void Dispose() => Close();
}