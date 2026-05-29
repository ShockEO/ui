using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ShockUI.Models.App;
using ShockUI.Services.App;
using Avalonia.Threading;
using ShockUI.Models.OpticalModules;

namespace ShockUI.Services.OpticalModules;

public sealed class OpticalModuleSerialService : IDisposable
{
    private ITransport? _transport;
    private readonly List<byte> _rxBuffer = [];
    private byte _sequenceId;
    private bool _isSimulationMode;
    private bool _isSimulationConnected;
    private OpticalModuleSimFaultConfig _faults = new();

    // Serial port settings — defaults can be overridden by the user via ApplySettings()
    private SerialPortSettings _settings = SerialPortSettings.Default115200N81();

    // Single-writer lock so concurrent UI commands don't interleave TX bytes
    private readonly SemaphoreSlim _txLock = new(1, 1);

    // Auto-reconnect support — remembers the last-used port so a dropped link can be restored
    private string? _lastPortName;
    private readonly SerialAutoReconnect _reconnect;

    private OpticalModuleState _simState = OpticalModuleState.Operational;
    private int _simMessageCounter;

    // FOV sim state – tracks the current FOV plus the most recent commanded op
    private byte _simFovControl = 0x00;  // default: Giving feedback
    private byte _simCurrentFov = 0x01;  // default: WFOV

    // Focus sim state – Movement status: 0=free, 1=min reached, 2=max reached, 3=blocked
    private byte _simFocusControl = 0x00;
    private byte _simFocusMovement = 0x00;
    private bool _simFocusControlActive;
    private bool _simFocusPositionReached = true;
    private int _simFocusPosition;      // current focus position (ticks)
    private int _simFocusSpeed;         // current focus speed    (raw units)

    public bool IsConnected => _isSimulationConnected || (_transport?.IsOpen ?? false);
    public bool IsSimulationMode => _isSimulationMode;

    public event Action<string>? StatusChanged;
    public event Action<string>? LogMessage;
    public event Action<byte[]>? FrameTransmitted;
    public event Action<byte[]>? FrameReceived;
    public event Action<OpticalModuleState, bool>? StateSelectionReceived;
    public event Action<OpticalModuleGeneralStatus>? GeneralStatusReceived;
    public event Action<OpticalModuleFovFeedback>? FovFeedbackReceived;
    public event Action<OpticalModuleFocusFeedback>? FocusFeedbackReceived;

    public OpticalModuleSerialService()
    {
        // _transport is created lazily in TryOpenPort() — this lets us switch
        // freely between UART and UDP at runtime via the Port Settings popup.

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

    /// <summary>Gets a clone of the current serial port settings.</summary>
    public SerialPortSettings GetPortSettings() => _settings.Clone();

    /// <summary>
    /// Applies new serial port settings. If the port is currently open it will
    /// be closed and reopened with the new values.
    /// </summary>
    public void ApplyPortSettings(SerialPortSettings settings)
    {
        _settings = settings.Clone();

        bool wasOpen = _transport?.IsOpen ?? false;
        string? port = _lastPortName;

        if (wasOpen)
            try { _transport!.Close(); } catch { /* ignored */ }

        PublishLog("SER", $"Port settings updated: {_settings}");

        if (wasOpen && !string.IsNullOrEmpty(port))
            TryOpenPort(port);
    }

    /// <summary>True while the background auto-reconnect loop is armed.</summary>
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

    /// <summary>Opens the port under the current settings. Raises status/log on failure.</summary>
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
            PublishLog("SER", $"Opened {_transport.Description}");
            return true;
        }
        catch (Exception ex)
        {
            PublishStatus($"Connect failed: {ex.Message}");
            PublishLog("ERR", ex.Message);
            return false;
        }
    }

    public void SetSimulationMode(bool enabled)
    {
        _isSimulationMode = enabled;
        PublishStatus(enabled ? "Simulation mode enabled." : "Hardware mode enabled.");
    }

    public void SetSimFaults(OpticalModuleSimFaultConfig config)
    {
        _faults = config;
        PublishLog("SIM", "Fault config updated.");
    }

    public ObservableCollection<string> GetAvailablePorts()
    {
        var ports = new List<string>();

        if (_isSimulationMode)
            ports.Add("SIMULATED");

        try
        {
            ports.AddRange(SerialPortDiscovery.Enumerate().Select(p => p.DisplayName).ToArray());
        }
        catch
        {
        }

        return new ObservableCollection<string>(ports);
    }

    public bool Connect(string? portName)
    {
        // Display names may carry a friendly description appended:
        //   "COM4 — Silicon Labs CP210x USB to UART Bridge"
        // Strip everything after " — " so we hand the OS a bare
        // port name to open.
        if (portName is not null)
            portName = SerialPortDiscovery.ExtractPortName(portName);

        if (_isSimulationMode || string.Equals(portName, "SIMULATED", StringComparison.OrdinalIgnoreCase))
        {
            _isSimulationConnected = true;
            PublishStatus("Connected to SIMULATED");
            PublishLog("SIM", "Simulation connection opened.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            PublishStatus("No COM port selected.");
            return false;
        }

        _lastPortName = portName;
        bool ok = TryOpenPort(portName);
        if (ok) _reconnect.Arm();
        return ok;
    }

    public void Disconnect()
    {
        try
        {
            _reconnect.Disarm();
            _reconnect.Enabled = false;  // user-initiated — don't auto-retry

            _isSimulationConnected = false;

            if (_transport is not null)
            {
                _transport.DataReceived -= OnDataReceived;
                _transport.ErrorReceived -= OnErrorReceived;
                _transport.Close();
                _transport.Dispose();
                _transport = null;
            }

            _rxBuffer.Clear();
            PublishStatus("Disconnected");
        }
        catch (Exception ex)
        {
            PublishStatus($"Disconnect failed: {ex.Message}");
        }
    }

    public void SendStateSelection(OpticalModuleDefinition module, OpticalModuleRequestedState requestedState)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildStateSelection(module, sequence, requestedState);
        SendFrame(tx);

        PublishLog("TX", $"{module.Name} state selection -> {requestedState}");

        if (_isSimulationConnected)
            _ = SimulateStateSelectionAsync(module, sequence, requestedState);
    }

    public void SendGeneralStatusRequest(OpticalModuleDefinition module)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildGeneralStatusRequest(module, sequence);
        SendFrame(tx);

        PublishLog("TX", $"{module.Name} general status request");

        if (_isSimulationConnected)
            _ = SimulateGeneralStatusAsync(module, sequence);
    }

    public void SendFovGetFeedback(OpticalModuleDefinition module)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildFovGetFeedback(module, sequence);
        TransmitOrSimulate(tx, () =>
        {
            _simFovControl = 0x00;
            return SimulateFovAsync(module, sequence);
        });
    }

    public void SendFovGoTo(OpticalModuleDefinition module, string fovText)
    {
        byte sequence = NextSequenceId();
        byte op = fovText switch
        {
            "WFOV" => (byte)FovOperation.GoToWfov,
            "MWFOV" => (byte)FovOperation.GoToMwfov,
            "MNFOV" => (byte)FovOperation.GoToMnfov,
            "NFOV" => (byte)FovOperation.GoToNfov,
            _ => (byte)FovOperation.GetFeedback,
        };
        byte[] tx = OpticalModuleCommandBuilder.BuildFovCommand(module, sequence, (FovOperation)op);
        TransmitOrSimulate(tx, () =>
        {
            _simFovControl = op;
            _simCurrentFov = op;   // Going-to value coincides with the destination FOV code
            return SimulateFovAsync(module, sequence);
        });
    }

    public void SendFovStop(OpticalModuleDefinition module)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildFovStop(module, sequence);
        TransmitOrSimulate(tx, () =>
        {
            _simFovControl = (byte)FovOperation.Stop;
            return SimulateFovAsync(module, sequence);
        });
    }

    public void SendFocusGetFeedback(OpticalModuleDefinition module)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildFocusGetFeedback(module, sequence);
        TransmitOrSimulate(tx, () =>
        {
            _simFocusControl = 0x00;
            return SimulateFocusAsync(module, sequence);
        });
    }

    public void SendFocusMoveToInfinity(OpticalModuleDefinition module)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildFocusMoveToInfinity(module, sequence);
        TransmitOrSimulate(tx, () =>
        {
            _simFocusControl = (byte)FocusOperation.MoveToInfinity;
            _simFocusControlActive = true;
            _simFocusPositionReached = true;
            _simFocusMovement = 0x02;   // max reached (infinity)
            _simFocusPosition = int.MaxValue;
            _simFocusSpeed = 0;
            return SimulateFocusAsync(module, sequence);
        });
    }

    /// <summary>Sends a Focus SetPosition command with the given target position.</summary>
    public void SendFocusSetPosition(OpticalModuleDefinition module, int position)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildFocusSetPosition(module, sequence, position);
        TransmitOrSimulate(tx, () =>
        {
            _simFocusControl = (byte)FocusOperation.SetPosition;
            _simFocusControlActive = true;
            _simFocusPositionReached = true;    // sim snaps instantly to target
            _simFocusMovement = 0x00;    // free to move
            _simFocusPosition = position;
            _simFocusSpeed = 0;
            return SimulateFocusAsync(module, sequence);
        });
    }

    /// <summary>Sends a Focus SetSpeed command with the given target speed.</summary>
    public void SendFocusSetSpeed(OpticalModuleDefinition module, int speed)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildFocusSetSpeed(module, sequence, speed);
        TransmitOrSimulate(tx, () =>
        {
            _simFocusControl = (byte)FocusOperation.SetSpeed;
            _simFocusControlActive = true;
            _simFocusPositionReached = false;   // speed mode never "reaches"
            _simFocusMovement = 0x00;
            _simFocusSpeed = speed;
            return SimulateFocusAsync(module, sequence);
        });
    }

    public void SendFocusStop(OpticalModuleDefinition module)
    {
        byte sequence = NextSequenceId();
        byte[] tx = OpticalModuleCommandBuilder.BuildFocusStop(module, sequence);
        TransmitOrSimulate(tx, () =>
        {
            _simFocusControl = (byte)FocusOperation.Stop;
            _simFocusControlActive = false;
            _simFocusSpeed = 0;
            return SimulateFocusAsync(module, sequence);
        });
    }

    private void TransmitOrSimulate(byte[] tx, Func<Task> simAction)
    {
        SendFrame(tx);
        if (_isSimulationMode)
            _ = simAction();
    }

    private byte NextSequenceId()
    {
        byte current = _sequenceId;
        _sequenceId++;
        return current;
    }

    private void SendFrame(byte[] frame)
    {
        if (_isSimulationConnected)
        {
            FrameTransmitted?.Invoke(frame);
            return;
        }

        if (!(_transport?.IsOpen ?? false))
        {
            PublishStatus("Serial port not connected.");
            return;
        }

        // Serialize writes so concurrent UI commands don't interleave bytes on the wire.
        _txLock.Wait();
        try
        {
            _transport!.Write(frame, 0, frame.Length);
            FrameTransmitted?.Invoke(frame);
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

    private async Task SimulateStateSelectionAsync(
        OpticalModuleDefinition module,
        byte sequence,
        OpticalModuleRequestedState requestedState)
    {
        await Task.Delay(200);

        if (requestedState != OpticalModuleRequestedState.GetCurrentState)
        {
            _simState = requestedState switch
            {
                OpticalModuleRequestedState.Operational => OpticalModuleState.Operational,
                OpticalModuleRequestedState.Maintenance => OpticalModuleState.Maintenance,
                OpticalModuleRequestedState.BuiltInTest => OpticalModuleState.BuiltInTest,
                _ => _simState
            };
        }

        byte stateByte = _simState switch
        {
            OpticalModuleState.Operational => 0x00,
            OpticalModuleState.Maintenance => 0x01,
            OpticalModuleState.BuiltInTest => 0x02,
            OpticalModuleState.Error => 0x03,
            OpticalModuleState.Initialization => 0x04,
            _ => 0x04
        };

        byte[] payload = [stateByte, 0x01];
        byte[] rx = OpticalModuleCommandBuilder.BuildFrame(
            0x01,
            module.DeviceId,
            sequence,
            OpticalModuleCommandBuilder.StateSelectionCommand,
            payload);

        Dispatcher.UIThread.Post(() =>
        {
            FrameReceived?.Invoke(rx);
            StateSelectionReceived?.Invoke(_simState, true);
            PublishLog("SIM", $"{module.Name} state response -> {_simState}");
        });
    }

    private async Task SimulateGeneralStatusAsync(
        OpticalModuleDefinition module,
        byte sequence)
    {
        await Task.Delay(220);
        _simMessageCounter += 1;
        var f = _faults;

        byte stateBits = (byte)(f.SystemStateError ? 0x03 : _simState switch
        {
            OpticalModuleState.Operational => 0x00,
            OpticalModuleState.Maintenance => 0x01,
            OpticalModuleState.BuiltInTest => 0x02,
            OpticalModuleState.Error => 0x03,
            _ => 0x00
        });

        byte etiState = (byte)(stateBits << 2);
        if (f.ExternalDeviceAlarm) etiState |= 0x01;
        if (f.ProcessorAlarm) etiState |= 0x02;

        byte ctrlPbit = 0x00;
        if (f.ControllerVoltageFail) ctrlPbit |= (1 << 0);
        if (f.ControllerPsuFail) ctrlPbit |= (1 << 1);
        if (f.ControllerTempFail) ctrlPbit |= (1 << 2);
        if (f.ControllerFlashFail) ctrlPbit |= (1 << 3);

        byte zg1Pbit = 0x00;
        if (f.Zg1AdcFail) zg1Pbit |= (1 << 0);
        if (f.Zg1MotorConnectionFail) zg1Pbit |= (1 << 1);
        if (f.Zg1EncoderConnectionFail) zg1Pbit |= (1 << 2);
        if (f.Zg1EncoderPolarityFail) zg1Pbit |= (1 << 3);
        if (f.Zg1MotorStall) zg1Pbit |= (1 << 4);
        if (f.Zg1NotCalibrated) zg1Pbit |= (1 << 5);

        byte zg2Pbit = 0x00;
        if (f.Zg2MotorStartFail) zg2Pbit |= (1 << 0);
        if (f.Zg2MotorConnectionFail) zg2Pbit |= (1 << 1);
        if (f.Zg2MotorPolarityFail) zg2Pbit |= (1 << 2);
        if (f.Zg2MinEndstopFail) zg2Pbit |= (1 << 3);
        if (f.Zg2MaxEndstopFail) zg2Pbit |= (1 << 4);
        if (f.Zg2MotorStall) zg2Pbit |= (1 << 5);
        if (f.Zg2EncoderFail) zg2Pbit |= (1 << 6);

        byte tempPbit = 0x00;
        if (f.Temp1ConnectionFail) tempPbit |= (1 << 0);
        if (f.Temp1OutOfRange) tempPbit |= (1 << 1);
        if (f.Temp2ConnectionFail) tempPbit |= (1 << 2);
        if (f.Temp2OutOfRange) tempPbit |= (1 << 3);

        byte[] payload = new byte[13];
        payload[0] = 0x00;
        payload[1] = (byte)(_simMessageCounter & 0xFF);
        payload[2] = (byte)((_simMessageCounter >> 8) & 0xFF);
        payload[3] = (byte)((_simMessageCounter >> 16) & 0xFF);
        payload[4] = 0x00;
        payload[5] = 0x00;
        payload[6] = 0x00;
        payload[7] = 0x00;
        payload[8] = ctrlPbit;
        payload[9] = zg1Pbit;
        payload[10] = zg2Pbit;
        payload[11] = tempPbit;
        payload[12] = etiState;

        byte[] rx = OpticalModuleCommandBuilder.BuildFrame(
            0x01,
            module.DeviceId,
            sequence,
            OpticalModuleCommandBuilder.GeneralStatusCommand,
            payload);

        Dispatcher.UIThread.Post(() =>
        {
            FrameReceived?.Invoke(rx);
            GeneralStatusReceived?.Invoke(
                OpticalModuleResponseParser.ParseGeneralStatusResponse(
                    new OpticalModuleMessage
                    {
                        Command = OpticalModuleCommandBuilder.GeneralStatusCommand,
                        Length = (byte)payload.Length,
                        Payload = payload
                    }));
            PublishLog("SIM", $"{module.Name} general status response");
        });
    }

    private async Task SimulateFovAsync(OpticalModuleDefinition module, byte sequence)
    {
        await Task.Delay(200);

        // Build payload: [ControlByte, StatusByte]
        //
        // Control byte bits 0..2 = current op (we already set _simFovControl)
        // Status byte:
        //   bit 0 = control active (true while we're going-to something other than feedback/stop)
        //   bit 1 = FOV reached     (true once the move completes — in sim we mark it reached immediately)
        //   bits 2..4 = current FOV
        bool active = _simFovControl is >= 0x01 and <= 0x04;  // Going-to-*
        bool reached = _simFovControl is >= 0x01 and <= 0x04;  // sim: arrival is instant

        byte statusByte = 0x00;
        if (active) statusByte |= 0x01;
        if (reached) statusByte |= 0x02;
        statusByte |= (byte)((_simCurrentFov & 0x07) << 2);

        byte[] payload = [_simFovControl, statusByte];
        byte[] rx = OpticalModuleCommandBuilder.BuildFrame(
            0x01, module.DeviceId, sequence, OpticalModuleCommandBuilder.FovCommand, payload);

        Dispatcher.UIThread.Post(() =>
        {
            FrameReceived?.Invoke(rx);
            var msg = new OpticalModuleMessage
            {
                Command = OpticalModuleCommandBuilder.FovCommand,
                Length = (byte)payload.Length,
                Payload = payload
            };
            FovFeedbackReceived?.Invoke(OpticalModuleResponseParser.ParseFovFeedback(msg));
            PublishLog("SIM", $"{module.Name} FOV response");
        });
    }

    private async Task SimulateFocusAsync(OpticalModuleDefinition module, byte sequence)
    {
        await Task.Delay(200);

        byte statusByte = 0x00;
        if (_simFocusControlActive) statusByte |= 0x01;
        if (_simFocusPositionReached) statusByte |= 0x02;
        statusByte |= (byte)((_simFocusMovement & 0x03) << 2);

        // Response Length = 0x0A (10 bytes): control + status + pos (4) + speed (4)
        byte[] payload = new byte[10];
        payload[0] = _simFocusControl;
        payload[1] = statusByte;
        WriteInt32LittleEndian(payload, 2, _simFocusPosition);
        WriteInt32LittleEndian(payload, 6, _simFocusSpeed);

        byte[] rx = OpticalModuleCommandBuilder.BuildFrame(
            0x01, module.DeviceId, sequence, OpticalModuleCommandBuilder.FocusCommand, payload);

        Dispatcher.UIThread.Post(() =>
        {
            FrameReceived?.Invoke(rx);
            var msg = new OpticalModuleMessage
            {
                Command = OpticalModuleCommandBuilder.FocusCommand,
                Length = (byte)payload.Length,
                Payload = payload
            };
            FocusFeedbackReceived?.Invoke(OpticalModuleResponseParser.ParseFocusFeedback(msg));
            PublishLog("SIM", $"{module.Name} Focus response " +
                              $"(Ctrl=0x{_simFocusControl:X2}, Pos={_simFocusPosition}, Speed={_simFocusSpeed})");
        });
    }

    private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private void OnDataReceived(byte[] buf)
    {
        try
        {
            // Bytes arrive as a chunk from whichever transport; the framer
            // accumulates them across calls.
            _rxBuffer.AddRange(buf);
            TryParseFrames();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                PublishStatus($"RX error: {ex.Message}");
                PublishLog("ERR", ex.Message);
            });
        }
    }

    private void TryParseFrames()
    {
        while (_rxBuffer.Count >= 13)
        {
            int syncIndex = FindSync();
            if (syncIndex < 0)
            {
                _rxBuffer.Clear();
                return;
            }

            if (syncIndex > 0)
                _rxBuffer.RemoveRange(0, syncIndex);

            if (_rxBuffer.Count < 13)
                return;

            int payloadLength = _rxBuffer[10];
            int frameLength = 11 + payloadLength + 2;

            if (_rxBuffer.Count < frameLength)
                return;

            byte[] frame = _rxBuffer.GetRange(0, frameLength).ToArray();
            _rxBuffer.RemoveRange(0, frameLength);

            Dispatcher.UIThread.Post(() =>
            {
                FrameReceived?.Invoke(frame);

                if (!OpticalModuleResponseParser.TryParseMessage(frame, out var message, out var error) || message is null)
                {
                    PublishLog("RX", $"Invalid frame: {error}");
                    return;
                }

                HandleMessage(message);
            });
        }
    }

    private int FindSync()
    {
        for (int i = 0; i < _rxBuffer.Count - 1; i++)
        {
            if (_rxBuffer[i] == OpticalModuleMessage.Sync1 &&
                _rxBuffer[i + 1] == OpticalModuleMessage.Sync2)
            {
                return i;
            }
        }

        return -1;
    }

    private void HandleMessage(OpticalModuleMessage message)
    {
        switch (message.Command)
        {
            case OpticalModuleCommandBuilder.StateSelectionCommand:
                {
                    OpticalModuleState state = OpticalModuleResponseParser.ParseStateSelectionResponse(message, out bool reached);
                    PublishLog("RX", $"State response -> {state}, Reached={reached}");
                    StateSelectionReceived?.Invoke(state, reached);
                    break;
                }

            case OpticalModuleCommandBuilder.GeneralStatusCommand:
                {
                    OpticalModuleGeneralStatus status = OpticalModuleResponseParser.ParseGeneralStatusResponse(message);
                    PublishLog("RX", $"General status -> State={status.CurrentState}, MsgCount={status.MessageCounter}");
                    GeneralStatusReceived?.Invoke(status);
                    break;
                }

            case OpticalModuleCommandBuilder.FovCommand:
                {
                    OpticalModuleFovFeedback feedback = OpticalModuleResponseParser.ParseFovFeedback(message);
                    PublishLog("RX", $"FOV -> {feedback.Summary}");
                    FovFeedbackReceived?.Invoke(feedback);
                    break;
                }

            case OpticalModuleCommandBuilder.FocusCommand:
                {
                    OpticalModuleFocusFeedback feedback = OpticalModuleResponseParser.ParseFocusFeedback(message);
                    PublishLog("RX", $"Focus -> {feedback.Summary}");
                    FocusFeedbackReceived?.Invoke(feedback);
                    break;
                }

            default:
                PublishLog("RX", $"Unhandled command 0x{message.Command:X4}");
                break;
        }
    }

    private void PublishStatus(string message) => StatusChanged?.Invoke(message);

    private void PublishLog(string source, string message)
    {
        LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss.fff}  {source,-4}  {message}");
    }

    public void Dispose()
    {
        try
        {
            _isSimulationConnected = false;

            if (_transport is not null)
            {
                _transport.DataReceived -= OnDataReceived;
                _transport.ErrorReceived -= OnErrorReceived;
                _transport.Close();
                _transport.Dispose();
                _transport = null;
            }
        }
        catch
        {
        }
    }
}