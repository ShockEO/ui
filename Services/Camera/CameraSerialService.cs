using System;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ShockUI.Models.App;
using ShockUI.Services;             // Crc16Ccitt
using ShockUI.Services.App;
using Avalonia.Threading;
using ShockUI.Models.Camera;

namespace ShockUI.Services.Camera;

public sealed class CameraSerialService : IDisposable
{
    private ITransport? _transport;
    // RTS-enable required for camera UART (per legacy code); UART transport ignores it for UDP
    private CameraRxState _rxState = CameraRxState.Sync0;

    private readonly CameraSerialMessage _workingMessage = new();
    private readonly CameraSerialMessage _receivedMessage = new();

    private int _rxCounter;
    private bool _supportsZg3;

    private int _simPos1;
    private int _simPos2;
    private int _simPos3;
    private readonly Random _random = new();
    private CameraSimFaultConfig _faults = new();

    private SerialPortSettings _settings = SerialPortSettings.Default115200N81();
    private readonly SemaphoreSlim _txLock = new(1, 1);
    private string? _lastPortName;
    private readonly SerialAutoReconnect _reconnect;

    public bool IsConnected => SimulationMode || (_transport?.IsOpen ?? false);
    public bool SimulationMode { get; private set; } = true;

    public int SentCount { get; private set; }
    public int ReceivedCount { get; private set; }

    public event Action<string>? StatusChanged;
    public event Action<string>? LogMessage;
    public event Action<int, int>? CommsChanged;
    public event Action<CameraDeviceInfo>? HandshakeCompleted;
    public event Action<bool>? CalibrationCompleted;
    public event Action<int, int, int, bool, bool, bool>? PositionFeedbackReceived;
    public event Action<double, double>? TemperatureReceived;
    public event Action<ushort>? LoggerCountReceived;
    public event Action<LogDataPoint>? LoggerPointReceived;
    public event Action<string>? LoggerStatusChanged;
    public event Action<PacketTraceEntry>? PacketTraceReceived;
    public event Action? Disconnected;

    public CameraSerialService()
    {
        // _transport is created lazily in TryOpenPort() so we can switch
        // between UART and UDP at runtime via the Port Settings popup.

        _reconnect = new SerialAutoReconnect(
            isConnected: () => _transport?.IsOpen ?? false,
            tryReconnect: _ => Task.Run(() =>
            {
                if (string.IsNullOrEmpty(_lastPortName)) return;
                try { TryOpenPort(_lastPortName); }
                catch (Exception ex) { PublishLog("RC", $"Reconnect failed: {ex.Message}"); }
            }),
            onStatus: msg => PublishLog("RC", msg),
            intervalMs: 2000);
    }

    public SerialPortSettings GetPortSettings() => _settings.Clone();

    public void ApplyPortSettings(SerialPortSettings settings)
    {
        _settings = settings.Clone();
        bool wasOpen = _transport?.IsOpen ?? false;
        string? port = _lastPortName;
        if (wasOpen) try { _transport!.Close(); } catch { }
        PublishLog("SER", $"Port settings updated: {_settings}");
        if (wasOpen && !string.IsNullOrEmpty(port)) TryOpenPort(port);
    }

    public bool AutoReconnectEnabled
    {
        get => _reconnect.Enabled;
        set => _reconnect.Enabled = value;
    }

    private void OnErrorReceived(string message)
    {
        PublishLog("ERR", $"Transport error: {message}");
        _reconnect.Trigger();
    }

    private bool TryOpenPort(string portName)
    {
        try
        {
            if (_transport is not null)
            {
                _transport.DataReceived -= OnDataReceived;
                _transport.ErrorReceived -= OnErrorReceived;
                _transport.Close();
                _transport.Dispose();
                _transport = null;
            }

            _transport = TransportFactory.Create(portName, _settings);
            _transport.DataReceived += OnDataReceived;
            _transport.ErrorReceived += OnErrorReceived;

            if (!_transport.Open())
            {
                _transport.Dispose();
                _transport = null;
                PublishStatus("Connect failed");
                return false;
            }

            PublishStatus($"Connected to {_transport.Description}");
            PublishLog("USB", $"Opened {_transport.Description}");
            return true;
        }
        catch (Exception ex)
        {
            PublishStatus($"Connect failed: {ex.Message}");
            PublishLog("ERR", $"Connect failed: {ex.Message}");
            return false;
        }
    }

    public void SetSimulationMode(bool enabled)
    {
        SimulationMode = enabled;
        PublishLog("GUI", enabled ? "Simulation mode enabled." : "Simulation mode disabled.");
        PublishStatus(enabled ? "Simulation mode active." : "Hardware mode active.");
    }

    public void SetSimFaults(CameraSimFaultConfig config)
    {
        _faults = config;
        PublishLog("SIM", "Fault config updated.");
    }

    public ObservableCollection<string> GetAvailablePorts()
    {
        try
        {
            var ports = new ObservableCollection<string>(SerialPortDiscovery.Enumerate().Select(p => p.DisplayName).ToArray());
            if (ports.Count == 0 && SimulationMode)
            {
                ports.Add("SIMULATED");
            }

            return ports;
        }
        catch
        {
            return SimulationMode ? ["SIMULATED"] : [];
        }
    }

    public bool Connect(string? portName)
    {
        // Display names may carry a friendly description appended:
        //   "COM4 — Silicon Labs CP210x USB to UART Bridge"
        // Strip everything after " — " so we hand the OS a bare
        // port name to open.
        if (portName is not null)
            portName = SerialPortDiscovery.ExtractPortName(portName);

        if (SimulationMode)
        {
            PublishStatus("Connected to simulated device");
            PublishLog("SIM", "Simulated connection opened.");
            _ = SimulateHandshakeAsync();
            return true;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            PublishStatus("No COM port selected.");
            PublishLog("GUI", "No COM port selected.");
            return false;
        }

        _lastPortName = portName;
        bool ok = TryOpenPort(portName);
        if (ok)
        {
            _reconnect.Arm();
            SendHandshake();
        }
        return ok;
    }

    public void Disconnect()
    {
        try
        {
            _reconnect.Disarm();
            _reconnect.Enabled = false;

            if (SimulationMode)
            {
                ResetRx();
                PublishStatus("Disconnected");
                PublishLog("SIM", "Simulated device disconnected.");
                Disconnected?.Invoke();
                return;
            }

            if (_transport is not null)
            {
                if (_transport.IsOpen)
                {
                    byte[] stopData = [0xAA, 0x55, (byte)CameraCommand.Handshake, 0x00];
                    WriteFrame(stopData, "Disconnect stop");
                }
                _transport.DataReceived -= OnDataReceived;
                _transport.ErrorReceived -= OnErrorReceived;
                _transport.Close();
                _transport.Dispose();
                _transport = null;
            }

            ResetRx();
            PublishStatus("Disconnected");
            PublishLog("USB", "Disconnected.");
            Disconnected?.Invoke();
        }
        catch (Exception ex)
        {
            PublishStatus($"Disconnect failed: {ex.Message}");
            PublishLog("ERR", $"Disconnect failed: {ex.Message}");
        }
    }

    public void SendHandshake()
    {
        byte[] data = [0xAA, 0x55, (byte)CameraCommand.Handshake, 0x03, 0xAB, 0xCD, 0xEF];

        if (SimulationMode)
        {
            WriteTrace("TX", "Handshake request", data);
            _ = SimulateHandshakeAsync();
            return;
        }

        WriteFrame(data, "Handshake request");
        PublishStatus("Handshake sent...");
        PublishLog("TX", "Handshake request sent.");
    }

    public void SendCalibrate()
    {
        byte[] data = [0xAA, 0x55, (byte)CameraCommand.Calibrate, 0x00];

        if (SimulationMode)
        {
            WriteTrace("TX", "Calibrate", data);
            _ = SimulateCalibrateAsync();
            return;
        }

        WriteFrame(data, "Calibrate");
        PublishStatus("Calibration command sent...");
        PublishLog("TX", "Calibration command sent.");
    }

    public void SendPositionControl(int zg1, int zg2, int? zg3 = null)
    {
        byte[] data;

        if (_supportsZg3 && zg3.HasValue)
        {
            data = new byte[16];
            data[0] = 0xAA;
            data[1] = 0x55;
            data[2] = (byte)CameraCommand.PosControl;
            data[3] = 0x0C;
            WriteInt32LittleEndian(data, 4, zg1);
            WriteInt32LittleEndian(data, 8, zg2);
            WriteInt32LittleEndian(data, 12, zg3.Value);
        }
        else
        {
            data = new byte[12];
            data[0] = 0xAA;
            data[1] = 0x55;
            data[2] = (byte)CameraCommand.PosControl;
            data[3] = 0x08;
            WriteInt32LittleEndian(data, 4, zg1);
            WriteInt32LittleEndian(data, 8, zg2);
        }

        if (SimulationMode)
        {
            WriteTrace("TX", "PosControl", data);
            _ = SimulatePositionAsync(zg1, zg2, zg3 ?? 0);
            return;
        }

        WriteFrame(data, "PosControl");
        PublishStatus("Position command sent.");
        PublishLog("TX", "Position command sent.");
    }

    public void SendSpeedControl(int axis, int speed)
    {
        byte[] data = new byte[7];
        data[0] = 0xAA;
        data[1] = 0x55;
        data[2] = (byte)CameraCommand.SpeedControl;
        data[3] = 0x03;
        data[4] = (byte)(axis & 0xFF);
        data[5] = (byte)(speed & 0xFF);
        data[6] = (byte)((speed >> 8) & 0xFF);

        if (SimulationMode)
        {
            WriteTrace("TX", $"SpeedControl axis={axis} speed={speed}", data);
            _ = SimulateSpeedAsync(axis, speed);
            return;
        }

        WriteFrame(data, "SpeedControl");
        PublishStatus($"Speed command sent: axis {axis}, speed {speed}");
        PublishLog("TX", $"SpeedControl -> Axis={axis}, Speed={speed}");
    }

    public void StopAxis(int axis)
    {
        SendSpeedControl(axis, 0);
    }

    public void SendStepControl(uint stepType, uint axis, float amplitudeFraction, uint duration)
    {
        byte[] data = new byte[14];
        byte[] crcData = new byte[8];

        int amplitudeBits = BitConverter.ToInt32(BitConverter.GetBytes(amplitudeFraction), 0);

        crcData[0] = (byte)stepType;
        crcData[1] = (byte)axis;
        crcData[2] = (byte)(amplitudeBits & 0xFF);
        crcData[3] = (byte)((amplitudeBits >> 8) & 0xFF);
        crcData[4] = (byte)((amplitudeBits >> 16) & 0xFF);
        crcData[5] = (byte)((amplitudeBits >> 24) & 0xFF);
        crcData[6] = (byte)(duration & 0xFF);
        crcData[7] = (byte)((duration >> 8) & 0xFF);

        ushort crc = Crc16Ccitt.Compute(crcData, 8);

        data[0] = 0xAA;
        data[1] = 0x55;
        data[2] = (byte)CameraCommand.StepControl;
        data[3] = 10;
        data[4] = crcData[0];
        data[5] = crcData[1];
        data[6] = crcData[2];
        data[7] = crcData[3];
        data[8] = crcData[4];
        data[9] = crcData[5];
        data[10] = crcData[6];
        data[11] = crcData[7];
        data[12] = (byte)(crc & 0xFF);
        data[13] = (byte)((crc >> 8) & 0xFF);

        if (SimulationMode)
        {
            WriteTrace("TX", $"StepControl type={stepType} axis={axis}", data);
            _ = SimulateStepAsync();
            return;
        }

        WriteFrame(data, "StepControl");
        PublishStatus("Step control command sent.");
        PublishLog("TX", $"StepControl -> Type={stepType}, Axis={axis}, Amp={amplitudeFraction:F3}, Duration={duration} ms");
    }

    public void RequestLoggerCount()
    {
        byte[] data = [0xAA, 0x55, (byte)CameraCommand.DataLogger, 0x01, 0x00];

        if (SimulationMode)
        {
            WriteTrace("TX", "Logger count request", data);
            _ = SimulateLoggerCountAsync();
            return;
        }

        WriteFrame(data, "Logger count request");
        PublishStatus("Logger count requested.");
        PublishLog("TX", "Logger count request sent.");
        LoggerStatusChanged?.Invoke("Count requested");
    }

    public void RequestLoggerPoint(ushort index)
    {
        byte[] data =
        [
            0xAA, 0x55, (byte)CameraCommand.DataLogger, 0x03, 0x01,
            (byte)(index & 0xFF),
            (byte)((index >> 8) & 0xFF)
        ];

        if (SimulationMode)
        {
            WriteTrace("TX", $"Logger point request {index}", data);
            _ = SimulateLoggerPointAsync(index);
            return;
        }

        WriteFrame(data, $"Logger point request {index}");
        PublishStatus($"Logger point requested: {index}");
        PublishLog("TX", $"Logger point request sent for index {index}.");
        LoggerStatusChanged?.Invoke($"Point {index} requested");
    }

    private async Task SimulateHandshakeAsync()
    {
        await Task.Delay(250);

        // Data[3] = 0x03 must always = MWIR (parsed by ProcessHandshake).
        // ZG3 support is controlled directly via _supportsZg3, not via the response byte.
        byte[] response =
        [
            0xAA, 0x55, (byte)CameraCommand.Handshake, 0x10,
            0xAB, 0xCD, 0xEF, 0x03,
            0x20, 0x4E, 0x00, 0x00,
            0x40, 0x9C, 0x00, 0x00,
            0x80, 0xEA, 0x00, 0x00
        ];

        SimulateIncomingPayload(response, "Handshake response");
        _supportsZg3 = !_faults.Zg3NotSupported;
    }

    private async Task SimulateCalibrateAsync()
    {
        await Task.Delay(300);
        byte successByte = _faults.CalibrationFail ? (byte)0x00 : (byte)0x01;
        byte[] response = [0xAA, 0x55, (byte)CameraCommand.Calibrate, 0x01, successByte];
        SimulateIncomingPayload(response, "Calibrate response");
    }

    private async Task SimulatePositionAsync(int zg1, int zg2, int zg3)
    {
        await Task.Delay(180);
        _simPos1 = zg1;
        _simPos2 = zg2;
        _simPos3 = zg3;

        if (_supportsZg3)
        {
            byte[] payload = new byte[19];
            payload[0] = 0xAA;
            payload[1] = 0x55;
            payload[2] = (byte)CameraCommand.PosControl;
            payload[3] = 0x0F;
            WriteInt32LittleEndian(payload, 4, _simPos1);
            payload[8] = (byte)(_faults.Zg1NotReached ? 0 : 1);
            WriteInt32LittleEndian(payload, 9, _simPos2);
            payload[13] = (byte)(_faults.Zg2NotReached ? 0 : 1);
            WriteInt32LittleEndian(payload, 14, _simPos3);
            payload[18] = (byte)(_faults.Zg3NotReached ? 0 : 1);
            SimulateIncomingPayload(payload, "Position feedback");
        }
        else
        {
            byte[] payload = new byte[14];
            payload[0] = 0xAA;
            payload[1] = 0x55;
            payload[2] = (byte)CameraCommand.PosControl;
            payload[3] = 0x0A;
            WriteInt32LittleEndian(payload, 4, _simPos1);
            payload[8] = (byte)(_faults.Zg1NotReached ? 0 : 1);
            WriteInt32LittleEndian(payload, 9, _simPos2);
            payload[13] = (byte)(_faults.Zg2NotReached ? 0 : 1);
            SimulateIncomingPayload(payload, "Position feedback");
        }

        await SimulateTempPushAsync();
    }

    private async Task SimulateSpeedAsync(int axis, int speed)
    {
        await Task.Delay(120);

        int delta = speed / 10;
        switch (axis)
        {
            case 1: _simPos1 += delta; break;
            case 2: _simPos2 += delta; break;
            case 3: _simPos3 += delta; break;
        }

        byte[] payload;
        if (_supportsZg3)
        {
            payload = new byte[16];
            payload[0] = 0xAA;
            payload[1] = 0x55;
            payload[2] = (byte)CameraCommand.SpeedControl;
            payload[3] = 0x0C;
            WriteInt32LittleEndian(payload, 4, _simPos1);
            WriteInt32LittleEndian(payload, 8, _simPos2);
            WriteInt32LittleEndian(payload, 12, _simPos3);
        }
        else
        {
            payload = new byte[12];
            payload[0] = 0xAA;
            payload[1] = 0x55;
            payload[2] = (byte)CameraCommand.SpeedControl;
            payload[3] = 0x08;
            WriteInt32LittleEndian(payload, 4, _simPos1);
            WriteInt32LittleEndian(payload, 8, _simPos2);
        }

        SimulateIncomingPayload(payload, "Speed feedback");
        await SimulateTempPushAsync();
    }

    private async Task SimulateStepAsync()
    {
        await Task.Delay(250);
        byte[] response = [0xAA, 0x55, (byte)CameraCommand.StepControl, 0x01, 0x01];
        SimulateIncomingPayload(response, "Step response");
    }

    private async Task SimulateLoggerCountAsync()
    {
        await Task.Delay(150);
        byte countByte = _faults.LoggerEmpty ? (byte)0x00 : (byte)0x05;
        byte[] response = [0xAA, 0x55, (byte)CameraCommand.DataLogger, 0x02, countByte, 0x00];
        SimulateIncomingPayload(response, "Logger count response");
    }

    private async Task SimulateLoggerPointAsync(ushort index)
    {
        await Task.Delay(120);

        short t1 = (short)(2450 + index);
        short t2 = (short)(2630 + index);

        byte[] payload = new byte[24];
        payload[0] = 0xAA;
        payload[1] = 0x55;
        payload[2] = (byte)CameraCommand.DataLogger;
        payload[3] = 0x14;

        payload[4] = (byte)(index & 0xFF);
        payload[5] = (byte)((index >> 8) & 0xFF);

        ushort timeMs = (ushort)(index * 100);
        payload[6] = (byte)(timeMs & 0xFF);
        payload[7] = (byte)((timeMs >> 8) & 0xFF);

        WriteInt32LittleEndian(payload, 8, 1000 + index * 10);
        WriteInt32LittleEndian(payload, 12, 2000 + index * 10);
        WriteInt32LittleEndian(payload, 16, 3000 + index * 10);

        payload[20] = (byte)(t1 & 0xFF);
        payload[21] = (byte)((t1 >> 8) & 0xFF);
        payload[22] = (byte)(t2 & 0xFF);
        payload[23] = (byte)((t2 >> 8) & 0xFF);

        SimulateIncomingPayload(payload, $"Logger point response {index}");
    }

    private async Task SimulateTempPushAsync()
    {
        await Task.Delay(60);

        // Healthy: 25-30°C. Fault: ~85°C (very hot = out of range)
        int raw1 = _faults.TemperatureOutOfRange ? (85 << 2) : ((25 + _random.Next(0, 5)) << 2);
        int raw2 = _faults.TemperatureOutOfRange ? (88 << 2) : ((27 + _random.Next(0, 5)) << 2);

        byte[] payload =
        [
            0xAA, 0x55, (byte)CameraCommand.TempFeedback, 0x04,
            (byte)(raw1 & 0xFF), (byte)((raw1 >> 8) & 0xFF),
            (byte)(raw2 & 0xFF), (byte)((raw2 >> 8) & 0xFF)
        ];

        SimulateIncomingPayload(payload, "Temp feedback");
    }

    private void SimulateIncomingPayload(byte[] fullFrame, string summary)
    {
        WriteTrace("RX", summary, fullFrame);

        byte cmd = fullFrame[2];
        byte len = fullFrame[3];

        Array.Clear(_receivedMessage.Data, 0, _receivedMessage.Data.Length);

        int availablePayload = Math.Max(0, fullFrame.Length - 4);
        int copyLength = Math.Min(len, Math.Min(availablePayload, _receivedMessage.Data.Length));

        for (int i = 0; i < copyLength; i++)
        {
            _receivedMessage.Data[i] = fullFrame[4 + i];
        }

        _receivedMessage.Command = cmd;
        _receivedMessage.Length = copyLength;

        ReceivedCount++;
        PublishComms();
        ProcessReceivedMessage(_receivedMessage);
    }

    private void WriteFrame(byte[] data, string summary)
    {
        _txLock.Wait();
        try
        {
            _transport!.Write(data, 0, data.Length);
            SentCount++;
            PublishComms();
            WriteTrace("TX", summary, data);
        }
        catch (Exception ex)
        {
            PublishStatus($"TX failed: {ex.Message}");
            PublishLog("ERR", ex.Message);
            _reconnect.Trigger();
        }
        finally
        {
            _txLock.Release();
        }
    }

    private void WriteTrace(string direction, string summary, byte[] data)
    {
        PacketTraceReceived?.Invoke(new PacketTraceEntry
        {
            Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
            Direction = direction,
            Summary = summary,
            Hex = BitConverter.ToString(data).Replace("-", " ")
        });
    }

    private void OnDataReceived(byte[] buf)
    {
        try
        {
            // Camera RX state machine consumes bytes one at a time, but the
            // transport hands us a chunk per event. Iterate over the chunk.
            for (int idx = 0; idx < buf.Length; idx++)
            {
                byte rxByte = buf[idx];

                switch (_rxState)
                {
                    case CameraRxState.Sync0:
                        if (rxByte == 0xAA)
                            _rxState = CameraRxState.Sync1;
                        break;

                    case CameraRxState.Sync1:
                        if (rxByte == 0x55)
                            _rxState = CameraRxState.Cmd;
                        else
                            _rxState = CameraRxState.Sync0;
                        break;

                    case CameraRxState.Cmd:
                        if (rxByte >= (byte)CameraCommand.InvalidCmdId)
                        {
                            _rxState = CameraRxState.Sync0;
                        }
                        else
                        {
                            _workingMessage.Command = rxByte;
                            _rxState = CameraRxState.Num;
                        }
                        break;

                    case CameraRxState.Num:
                        if (rxByte > 64)
                        {
                            _rxState = CameraRxState.Sync0;
                        }
                        else if (rxByte == 0)
                        {
                            _receivedMessage.Command = _workingMessage.Command;
                            _receivedMessage.Length = 0;
                            Array.Clear(_receivedMessage.Data, 0, _receivedMessage.Data.Length);

                            ReceivedCount++;
                            PublishComms();
                            ProcessReceivedMessage(_receivedMessage);
                            _rxState = CameraRxState.Sync0;
                        }
                        else
                        {
                            _workingMessage.Length = rxByte;
                            _rxCounter = 0;
                            Array.Clear(_workingMessage.Data, 0, _workingMessage.Data.Length);
                            _rxState = CameraRxState.Data;
                        }
                        break;

                    case CameraRxState.Data:
                        if (_rxCounter < _workingMessage.Data.Length)
                        {
                            _workingMessage.Data[_rxCounter] = rxByte;
                        }

                        _rxCounter++;

                        if (_rxCounter == _workingMessage.Length)
                        {
                            _receivedMessage.Command = _workingMessage.Command;
                            _receivedMessage.Length = Math.Min(_workingMessage.Length, _receivedMessage.Data.Length);
                            Array.Copy(
                                _workingMessage.Data,
                                _receivedMessage.Data,
                                Math.Min(_workingMessage.Length, _receivedMessage.Data.Length));

                            ReceivedCount++;
                            PublishComms();
                            ProcessReceivedMessage(_receivedMessage);
                            _rxState = CameraRxState.Sync0;
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                PublishStatus($"RX error: {ex.Message}");
                PublishLog("ERR", $"RX error: {ex.Message}");
            });
        }
    }

    private void ProcessReceivedMessage(CameraSerialMessage message)
    {
        if (message.Command == (byte)CameraCommand.Handshake)
        {
            ProcessHandshake(message);
            return;
        }

        if (message.Command == (byte)CameraCommand.Calibrate)
        {
            ProcessCalibration(message);
            return;
        }

        if (message.Command == (byte)CameraCommand.PosControl)
        {
            ProcessPositionFeedback(message);
            return;
        }

        if (message.Command == (byte)CameraCommand.SpeedControl)
        {
            ProcessSpeedFeedback(message);
            return;
        }

        if (message.Command == (byte)CameraCommand.TempFeedback)
        {
            ProcessTemperatureFeedback(message);
            return;
        }

        if (message.Command == (byte)CameraCommand.StepControl)
        {
            Dispatcher.UIThread.Post(() =>
            {
                PublishStatus("Step control response received.");
                PublishLog("RX", "Step control response received.");
            });
            return;
        }

        if (message.Command == (byte)CameraCommand.DataLogger)
        {
            ProcessLoggerMessage(message);
        }
    }

    private void ProcessLoggerMessage(CameraSerialMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (message.Length == 2)
            {
                ushort count = (ushort)(message.Data[0] | (message.Data[1] << 8));
                PublishLog("RX", $"Logger count response -> {count} points.");
                LoggerStatusChanged?.Invoke($"Count received: {count}");
                LoggerCountReceived?.Invoke(count);
                return;
            }

            if (message.Length >= 20)
            {
                ushort index = (ushort)(message.Data[0] | (message.Data[1] << 8));
                ushort timeMs = (ushort)(message.Data[2] | (message.Data[3] << 8));

                int zg1 = ReadInt32LittleEndian(message.Data, 4);
                int zg2 = ReadInt32LittleEndian(message.Data, 8);
                int zg3 = ReadInt32LittleEndian(message.Data, 12);

                short rawT1 = (short)(message.Data[16] | (message.Data[17] << 8));
                short rawT2 = (short)(message.Data[18] | (message.Data[19] << 8));

                var point = new LogDataPoint
                {
                    Index = index,
                    TimeMs = timeMs,
                    ZG1 = zg1,
                    ZG2 = zg2,
                    ZG3 = zg3,
                    Temp1 = rawT1 / 100.0,
                    Temp2 = rawT2 / 100.0,
                    Notes = SimulationMode ? "Simulated point" : "Parsed point"
                };

                PublishLog("RX",
                    $"Logger point -> Idx={index}, T={timeMs} ms, ZG1={zg1}, ZG2={zg2}, ZG3={zg3}, T1={point.Temp1:F2}, T2={point.Temp2:F2}");
                LoggerStatusChanged?.Invoke($"Point received: {index}");
                LoggerPointReceived?.Invoke(point);
                return;
            }

            PublishLog("RX", $"Logger response received with unsupported length {message.Length}.");
            LoggerStatusChanged?.Invoke("Unsupported logger frame");
        });
    }

    private void ProcessHandshake(CameraSerialMessage message)
    {
        if ((message.Length != 12 && message.Length != 16) ||
            message.Data[0] != 0xAB ||
            message.Data[1] != 0xCD ||
            message.Data[2] != 0xEF)
        {
            Dispatcher.UIThread.Post(() =>
            {
                PublishStatus("Invalid handshake response.");
                PublishLog("RX", "Invalid handshake response.");
            });
            return;
        }

        CameraType cameraType = message.Data[3] switch
        {
            1 => CameraType.VISNIR,
            2 => CameraType.SWIR,
            3 => CameraType.MWIR,
            _ => CameraType.None
        };

        if (cameraType == CameraType.None)
        {
            Dispatcher.UIThread.Post(() =>
            {
                PublishStatus("Unknown camera type.");
                PublishLog("RX", "Unknown camera type in handshake.");
            });
            return;
        }

        uint maxPos1 = ReadUInt32LittleEndian(message.Data, 4);
        uint maxPos2 = ReadUInt32LittleEndian(message.Data, 8);

        uint maxPos3 = 0;
        if (cameraType == CameraType.MWIR && message.Length >= 16)
        {
            maxPos3 = ReadUInt32LittleEndian(message.Data, 12);
        }

        _supportsZg3 = cameraType == CameraType.MWIR;

        var info = new CameraDeviceInfo
        {
            CameraType = cameraType,
            MaxPos1 = maxPos1,
            MaxPos2 = maxPos2,
            MaxPos3 = maxPos3
        };

        Dispatcher.UIThread.Post(() =>
        {
            PublishStatus($"Handshake complete: {cameraType}");
            PublishLog("RX", $"Handshake complete. Camera={cameraType}, Max1={maxPos1}, Max2={maxPos2}, Max3={maxPos3}");
            HandshakeCompleted?.Invoke(info);
        });
    }

    private void ProcessCalibration(CameraSerialMessage message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool ok = message.Length == 1 && message.Data[0] == 1;
            PublishStatus(ok ? "Calibration complete." : "Calibration response received.");
            PublishLog("RX", ok ? "Calibration successful." : "Calibration response received.");
            CalibrationCompleted?.Invoke(ok);
        });
    }

    private void ProcessPositionFeedback(CameraSerialMessage message)
    {
        if (message.Length != 10 && message.Length != 15)
            return;

        int pos1 = ReadInt32LittleEndian(message.Data, 0);
        bool reached1 = message.Data[4] == 1;

        int pos2 = ReadInt32LittleEndian(message.Data, 5);
        bool reached2 = message.Data[9] == 1;

        int pos3 = 0;
        bool reached3 = false;

        if (message.Length == 15)
        {
            pos3 = ReadInt32LittleEndian(message.Data, 10);
            reached3 = message.Data[14] == 1;
        }

        Dispatcher.UIThread.Post(() =>
        {
            PublishStatus("Position feedback received.");
            PublishLog("RX", message.Length == 15
                ? $"Position -> ZG1={pos1} ({reached1}), ZG2={pos2} ({reached2}), ZG3={pos3} ({reached3})"
                : $"Position -> ZG1={pos1} ({reached1}), ZG2={pos2} ({reached2})");
            PositionFeedbackReceived?.Invoke(pos1, pos2, pos3, reached1, reached2, reached3);
        });
    }

    private void ProcessSpeedFeedback(CameraSerialMessage message)
    {
        if (message.Length != 8 && message.Length != 12)
            return;

        int pos1 = ReadInt32LittleEndian(message.Data, 0);
        int pos2 = ReadInt32LittleEndian(message.Data, 4);
        int pos3 = 0;

        if (message.Length == 12)
            pos3 = ReadInt32LittleEndian(message.Data, 8);

        Dispatcher.UIThread.Post(() =>
        {
            PublishStatus("Speed feedback received.");
            PublishLog("RX", message.Length == 12
                ? $"Speed feedback -> ZG1={pos1}, ZG2={pos2}, ZG3={pos3}"
                : $"Speed feedback -> ZG1={pos1}, ZG2={pos2}");
            PositionFeedbackReceived?.Invoke(pos1, pos2, pos3, true, true, true);
        });
    }

    private void ProcessTemperatureFeedback(CameraSerialMessage message)
    {
        if (message.Length != 4)
            return;

        int raw1 = (message.Data[1] << 8) | message.Data[0];
        int raw2 = (message.Data[3] << 8) | message.Data[2];

        double temp1 = Math.Round(0.03125 * (raw1 >> 2), 2);
        double temp2 = Math.Round(0.03125 * (raw2 >> 2), 2);

        Dispatcher.UIThread.Post(() =>
        {
            PublishLog("RX", $"Temperature feedback -> T1={temp1:F2} °C, T2={temp2:F2} °C");
            TemperatureReceived?.Invoke(temp1, temp2);
        });
    }

    private void PublishStatus(string message)
    {
        StatusChanged?.Invoke(message);
    }

    private void PublishLog(string source, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {source,-4}  {message}";
        LogMessage?.Invoke(line);
    }

    private void PublishComms()
    {
        CommsChanged?.Invoke(SentCount, ReceivedCount);
    }

    private void ResetRx()
    {
        _rxState = CameraRxState.Sync0;
        _rxCounter = 0;
        _supportsZg3 = false;
        Array.Clear(_workingMessage.Data, 0, _workingMessage.Data.Length);
        Array.Clear(_receivedMessage.Data, 0, _receivedMessage.Data.Length);
    }

    private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static int ReadInt32LittleEndian(byte[] buffer, int offset)
    {
        return buffer[offset]
             | (buffer[offset + 1] << 8)
             | (buffer[offset + 2] << 16)
             | (buffer[offset + 3] << 24);
    }

    private static uint ReadUInt32LittleEndian(byte[] buffer, int offset)
    {
        return (uint)(
            buffer[offset]
            | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16)
            | (buffer[offset + 3] << 24));
    }

    public void Dispose()
    {
        _reconnect.Dispose();
        _txLock.Dispose();
        if (_transport is not null)
        {
            _transport.DataReceived -= OnDataReceived;
            _transport.ErrorReceived -= OnErrorReceived;
            try { _transport.Close(); } catch { }
            _transport.Dispose();
            _transport = null;
        }
    }
}