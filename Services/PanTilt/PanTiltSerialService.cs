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
using ShockUI.Models.PanTilt;
using ShockUI.Services.Eos;

namespace ShockUI.Services.PanTilt;

/// <summary>
/// Serial service for the EΩS Pan Tilt Stab Controller (PTSC, ID = 0x20).
/// Uses <see cref="EosFramer"/> for all frame building and parsing.
///
/// Physical interface: RS-422, 115200 baud, 8N1.
/// (No parity – matches working OpticalModuleSerialService convention.
///  The EOS SRS says "8 data bits, 1 even parity" but production firmware
///  may differ; adjust Parity here if required.)
/// </summary>
public sealed class PanTiltSerialService : IDisposable
{
    // EOS subsystem IDs (§3.3 tables)
    public const byte PtscId = 0x20;   // Pan Tilt Stab Controller
    public const byte ScpId = 0x00;   // Host PC / engineering laptop (per Host GUI changes spec, src=0x00)
                                      // Was 0x10 historically; that ID is now the Interface Assembly / System Controller.

    private ITransport? _transport;
    private readonly List<byte> _rxBuffer = [];
    private byte _sequenceId;
    private bool _isSimulationMode;
    private bool _isSimConnected;
    private PanTiltSimFaultConfig _faults = new();

    // Sim state shared between commands so feedback values agree across cards.
    private int _simPanSpeed;
    private int _simTiltSpeed;
    private int _simEkfPitch;
    private int _simEkfYaw;

    private SerialPortSettings _settings = SerialPortSettings.Default115200N81();
    private readonly SemaphoreSlim _txLock = new(1, 1);
    private string? _lastPortName;
    private readonly SerialAutoReconnect _reconnect;

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------
    public event Action<string>? StatusChanged;
    public event Action<string>? LogMessage;
    public event Action<byte[]>? FrameTransmitted;
    public event Action<byte[]>? FrameReceived;
    public event Action<EosParsedFrame>? ResponseReceived;

    public bool IsConnected => _isSimConnected || (_transport?.IsOpen ?? false);

    // -----------------------------------------------------------------------
    // Ctor
    // -----------------------------------------------------------------------
    public PanTiltSerialService()
    {
        // _transport is created lazily in TryOpenPort() — this lets us switch
        // freely between UART and UDP at runtime via the Port Settings popup.

        _reconnect = new SerialAutoReconnect(
            isConnected: () => _transport?.IsOpen ?? false,
            tryReconnect: _ => Task.Run(() =>
            {
                if (string.IsNullOrEmpty(_lastPortName)) return;
                try { TryOpenPort(_lastPortName); }
                catch (Exception ex) { Log("RC", $"Reconnect failed: {ex.Message}"); }
            }),
            onStatus: msg => Log("RC", msg),
            intervalMs: 2000);
    }

    public SerialPortSettings GetPortSettings() => _settings.Clone();

    public void ApplyPortSettings(SerialPortSettings settings)
    {
        _settings = settings.Clone();
        bool wasOpen = _transport?.IsOpen ?? false;
        string? port = _lastPortName;
        if (wasOpen) try { _transport!.Close(); } catch { }
        Log("SER", $"Port settings updated: {_settings}");
        if (wasOpen && !string.IsNullOrEmpty(port)) TryOpenPort(port);
    }

    public bool AutoReconnectEnabled
    {
        get => _reconnect.Enabled;
        set => _reconnect.Enabled = value;
    }

    private void OnErrorReceived(string message)
    {
        Log("ERR", $"Transport error: {message}");
        _reconnect.Trigger();
    }

    private bool TryOpenPort(string portName)
    {
        try
        {
            // Tear down any prior transport before creating a new one.
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
                StatusChanged?.Invoke("Connect failed");
                return false;
            }

            _rxBuffer.Clear();
            StatusChanged?.Invoke($"Connected to {_transport.Description}");
            Log("SER", $"Opened {_transport.Description}");
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Connect failed: {ex.Message}");
            Log("ERR", ex.Message);
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Connection
    // -----------------------------------------------------------------------

    public void SetSimulationMode(bool enabled)
    {
        _isSimulationMode = enabled;
        Log("SYS", enabled ? "Simulation mode ON" : "Hardware mode ON");
    }

    /// <summary>
    /// Pushes an updated fault configuration into the simulation.
    /// All BuildSim* methods read from this on every call.
    /// </summary>
    public void SetSimFaults(PanTiltSimFaultConfig config)
    {
        _faults = config;
        Log("SIM", "Fault config updated.");
    }

    public ObservableCollection<string> GetAvailablePorts()
    {
        var ports = new List<string>();
        if (_isSimulationMode) ports.Add("SIMULATED");
        try { ports.AddRange(SerialPortDiscovery.Enumerate().Select(p => p.DisplayName).ToArray()); } catch { }
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
            _isSimConnected = true;
            StatusChanged?.Invoke("Connected to SIMULATED");
            Log("SIM", "Simulation connection opened.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            StatusChanged?.Invoke("No COM port selected.");
            return false;
        }

        _lastPortName = portName;
        bool ok = TryOpenPort(portName);
        if (ok) _reconnect.Arm();
        return ok;
    }

    public void Disconnect()
    {
        _reconnect.Disarm();
        _reconnect.Enabled = false;

        _isSimConnected = false;
        if (_transport is not null)
        {
            _transport.DataReceived -= OnDataReceived;
            _transport.ErrorReceived -= OnErrorReceived;
            try { _transport.Close(); } catch { }
            _transport.Dispose();
            _transport = null;
        }
        _rxBuffer.Clear();
        StatusChanged?.Invoke("Disconnected");
        Log("SYS", "Disconnected.");
    }

    // -----------------------------------------------------------------------
    // Commands  (§3.3)
    // -----------------------------------------------------------------------

    /// <summary>§3.3.1  General Status – poll system/motor/IMU health.</summary>
    public void SendGeneralStatusRequest()
        => Send(PanTiltCommandId.GeneralStatus, [0x00, 0x00]);

    /// <summary>§3.3.2.1  Motor Control GET – read current mode, position, speed.</summary>
    public void SendMotorControlGet()
        => Send(PanTiltCommandId.MotorControlGet, [0x00, 0x00]);

    /// <summary>
    /// §3.3.2.3  Motor Control SET – set control mode, target position and speed for
    /// both Pan and Tilt axes.
    ///
    /// <paramref name="panMode"/>  / <paramref name="tiltMode"/>:
    ///   0x00 = Safety/Damping, 0x01 = Rate/Velocity, 0x02 = Position, 0x03 = Stabilised.
    /// <paramref name="panDisengage"/> / <paramref name="tiltDisengage"/>:
    ///   true = PWM OFF (motor disengaged).
    /// Position in encoder ticks (uint32, 21-bit Zettlex SSI, 0.000172°/tick).
    /// Speed in encoder ticks/second (int32, signed for direction).
    /// </summary>
    public void SendMotorControlSet(
        byte panMode, bool panDisengage,
        byte tiltMode, bool tiltDisengage,
        uint panPosition, int panSpeed,
        uint tiltPosition, int tiltSpeed)
    {
        byte panCtrl = (byte)((panMode & 0x03) << 1 | (panDisengage ? 0x01 : 0x00));
        byte tiltCtrl = (byte)((tiltMode & 0x03) << 1 | (tiltDisengage ? 0x01 : 0x00));

        var p = new byte[20];
        p[0] = panCtrl;
        p[1] = tiltCtrl;
        WriteUInt32Le(p, 2, panPosition);
        WriteUInt32Le(p, 6, tiltPosition);
        WriteInt32Le(p, 10, panSpeed);
        WriteInt32Le(p, 14, tiltSpeed);
        // p[18..19] = reserve
        Send(PanTiltCommandId.MotorControlSet, p);

        // Mirror to sim state so Stab Control GET feedback shows the
        // requested speed values consistently across cards.
        if (_isSimConnected)
        {
            _simPanSpeed = panSpeed;
            _simTiltSpeed = tiltSpeed;
        }
    }

    /// <summary>§3.3.3.1  Stab Control GET – read IMU + stab state.</summary>
    public void SendStabControlGet()
        => Send(PanTiltCommandId.StabControlGet, [0x00, 0x00]);

    /// <summary>
    /// §3.3.3.3  Stab Control SET – set stabilisation mode, EKF world-angle
    /// targets and pan/tilt nudge rates.
    ///
    /// Verified wire layout (20-byte payload, length 0x14):
    ///   [0]      panCtrl   = (panMode &lt;&lt; 1) | disengage
    ///   [1]      tiltCtrl  = (tiltMode &lt;&lt; 1) | disengage
    ///   [2..5]   EKF Pitch target — float32 LE, DEGREES
    ///   [6..9]   EKF Yaw   target — float32 LE, DEGREES
    ///   [10..13] Pan nudge rate   — int32 LE, encoder ticks (deg/s × 2^21/360)
    ///   [14..17] Tilt nudge rate  — int32 LE, encoder ticks (deg/s × 2^21/360)
    ///   [18..19] reserve (0x00 0x00)
    ///
    /// EKF targets are world angles in degrees (IEEE-754 float), used when the
    /// axis mode = Stabilised. Nudge rates feed the Xbox-controller stab nudge.
    /// Pass 0 for any field not in use.
    /// </summary>
    public void SendStabControlSet(
        byte panMode, bool panDisengage,
        byte tiltMode, bool tiltDisengage,
        float ekfPitchTargetDeg = 0f,
        float ekfYawTargetDeg = 0f,
        int panNudgeTicks = 0,
        int tiltNudgeTicks = 0)
    {
        byte panCtrl = (byte)((panMode & 0x03) << 1 | (panDisengage ? 0x01 : 0x00));
        byte tiltCtrl = (byte)((tiltMode & 0x03) << 1 | (tiltDisengage ? 0x01 : 0x00));

        // Length = 0x14 (20 bytes) per the verified C2000 frame breakdown.
        var p = new byte[20];
        p[0] = panCtrl;
        p[1] = tiltCtrl;
        WriteFloatLe(p, 2, ekfPitchTargetDeg);   // EKF Pitch (deg, float32)
        WriteFloatLe(p, 6, ekfYawTargetDeg);     // EKF Yaw   (deg, float32)
        WriteInt32Le(p, 10, panNudgeTicks);      // Pan nudge rate (ticks)
        WriteInt32Le(p, 14, tiltNudgeTicks);     // Tilt nudge rate (ticks)
        // p[18..19] = reserve

        Send(PanTiltCommandId.StabControlSet, p);

        // Mirror EKF targets to sim state so the Stab GET response shows
        // the requested values consistently. Sim state stores encoder ticks,
        // so convert the degree targets using the verified scale.
        if (_isSimConnected)
        {
            _simEkfPitch = (int)Math.Round(ekfPitchTargetDeg * TicksPerDegree);
            _simEkfYaw = (int)Math.Round(ekfYawTargetDeg * TicksPerDegree);
        }
    }

    /// <summary>
    /// §3.3.4.1  IBIT Command.
    ///   mode 0x00 = Read previous results (no movement).
    ///   mode 0x01 = Start full IBIT (motors + sensors, involves movement).
    ///   mode 0x02 = Start silent IBIT (sensors only, no movement).
    /// </summary>
    public void SendIbit(byte mode = 0x01)
        => Send(PanTiltCommandId.Ibit, [(byte)(mode & 0x03), 0x00]);

    // -----------------------------------------------------------------------
    // Internal send
    // -----------------------------------------------------------------------

    private void Send(ushort command, byte[] payload)
    {
        byte seq = _sequenceId++;
        byte[] frame = EosFramer.BuildCommand(PtscId, ScpId, seq, command, payload);
        FrameTransmitted?.Invoke(frame);

        Log("TX", $"CMD=0x{command:X4} seq={seq}");

        if (_isSimConnected)
        {
            _ = SimulateResponseAsync(command, seq);
            return;
        }

        if (!(_transport?.IsOpen ?? false)) return;

        _txLock.Wait();
        try
        {
            _transport!.Write(frame, 0, frame.Length);
        }
        catch (Exception ex)
        {
            Log("ERR", $"TX failed: {ex.Message}");
            _reconnect.Trigger();
        }
        finally
        {
            _txLock.Release();
        }
    }

    // -----------------------------------------------------------------------
    // RX
    // -----------------------------------------------------------------------

    private void OnDataReceived(byte[] buf)
    {
        try
        {
            // Bytes arrive raw; pass them straight to the framer, which
            // handles fragmented or batched arrivals identically.
            foreach (var frame in EosFramer.FeedBytes(buf, _rxBuffer))
                DispatchFrame(frame);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Log("ERR", $"RX error: {ex.Message}");
            });
        }
    }

    private void DispatchFrame(EosParsedFrame frame)
    {
        byte[] raw = EosFramer.BuildCommand(frame.DestinationId, frame.SourceId,
                                            frame.SequenceId, frame.Command, frame.Payload);
        Dispatcher.UIThread.Post(() =>
        {
            FrameReceived?.Invoke(raw);
            Log("RX", $"CMD=0x{frame.Command:X4} seq={frame.SequenceId}" +
                      (frame.HasError ? $" ERR=0x{frame.ErrorCode:X4}" : ""));
            ResponseReceived?.Invoke(frame);
        });
    }

    // -----------------------------------------------------------------------
    // Simulation
    // -----------------------------------------------------------------------

    private async Task SimulateResponseAsync(ushort command, byte seq)
    {
        await Task.Delay(150);

        // All BuildSim* methods read _faults — injecting individual flags
        // gives granular control over exactly which UI paths are exercised.
        byte[] payload = command switch
        {
            PanTiltCommandId.GeneralStatus => BuildSimGeneralStatus(),
            PanTiltCommandId.MotorControlGet => BuildSimMotorGet(),
            PanTiltCommandId.MotorControlSet => BuildSimMotorGet(),
            PanTiltCommandId.StabControlGet => BuildSimStabGet(),
            PanTiltCommandId.StabControlSet => BuildSimStabSetResponse(),
            PanTiltCommandId.Ibit => BuildSimIbit(),
            _ => [0x00]
        };

        var simFrame = new EosParsedFrame
        {
            ErrorByte1 = 0x00,
            ErrorByte2 = 0x00,
            DestinationId = ScpId,
            SourceId = PtscId,
            SequenceId = seq,
            Command = command,
            Payload = payload
        };

        Dispatcher.UIThread.Post(() =>
        {
            Log("SIM", $"RSP CMD=0x{command:X4}");
            ResponseReceived?.Invoke(simFrame);
        });
    }

    // ── §3.3.1.2  General Status sim (10 bytes) ─────────────────────────────
    private byte[] BuildSimGeneralStatus()
    {
        var f = _faults;

        // p[0] General Status Byte 1
        byte sys = 0x08;  // bit3=system ready, bits0:2=0 (State C operational)
        if (f.SystemStateError) sys = (byte)((sys & 0xF8) | 0x03);  // State F: Error
        if (f.ImuError) sys |= (1 << 4);
        if (f.PanEncoderError) sys |= (1 << 5);
        if (f.TiltEncoderError) sys |= (1 << 6);
        if (f.PowerFailure) sys |= (1 << 7);

        // p[1] General Status Byte 2
        byte sys2 = 0x00;
        if (f.PanAdcError) sys2 |= (1 << 0);
        if (f.TiltAdcError) sys2 |= (1 << 1);
        if (f.MotorBrakeOn) sys2 |= (1 << 2);

        // p[2] Pan motor: bit0=disengaged, bit2=fault
        byte pan = 0x00;
        if (f.PanDisengaged) pan |= (1 << 0);
        if (f.PanMotorFault) pan |= (1 << 2);

        // p[3] Tilt motor
        byte tilt = 0x00;
        if (f.TiltDisengaged) tilt |= (1 << 0);
        if (f.TiltMotorFault) tilt |= (1 << 2);

        // p[4] Control Mode: Pan=Stab(bits0:1=3), Tilt=Stab(bits2:3=3) → 0x0F
        return [sys, sys2, pan, tilt, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x00];
    }

    // ── §3.3.2.2  Motor Control GET sim (20 bytes) ──────────────────────────
    private byte[] BuildSimMotorGet()
    {
        var f = _faults;

        const uint PAN_TICKS = 1_048_576u;  // ≈ 180°
        const uint TILT_TICKS = 0u;          // ≈ 0°

        // SSI word: bit30=PV, bit29=ZPD, bits27:7=position
        // PV=0 (bit30 clear) → encoder signal lost
        uint panPv = f.PanPositionInvalid ? 0x20000000u : 0x60000000u;
        uint tiltPv = f.TiltPositionInvalid ? 0x20000000u : 0x60000000u;
        uint panSsi = panPv | (PAN_TICKS << 7);
        uint tiltSsi = tiltPv | (TILT_TICKS << 7);

        byte panCtrl = (byte)(f.PanDisengaged ? 0x01 : 0x06);  // disengaged|Safety or active|Stab
        byte tiltCtrl = (byte)(f.TiltDisengaged ? 0x01 : 0x06);

        var p = new byte[20];
        p[0] = panCtrl;
        p[1] = tiltCtrl;
        WriteUInt32Le(p, 2, panSsi);
        WriteUInt32Le(p, 6, tiltSsi);
        WriteInt32Le(p, 10, 0);   // Pan  speed = 0
        WriteInt32Le(p, 14, 0);   // Tilt speed = 0
        return p;
    }

    // ── §3.3.3.2  Stab Control GET sim (44 bytes) ───────────────────────────
    private byte[] BuildSimStabGet()
    {
        var f = _faults;

        // May 2026 SRS: Stab GET payload is now 52 bytes (length 0x34).
        // Layout (payload-relative, frame bytes 11-62):
        //   [0]     Pan Control Status Byte
        //   [1]     Tilt Control Status Byte
        //   [2..5]  IMU Timestamp (uint32 LE)
        //   [6]     IMU Status Byte 1 (comms, BIT, axis PBIT/CBIT)
        //   [7]     IMU Status Byte 2 (range flags)
        //   [8..11] IMU Accel X (int32 LE)
        //   [12..15] IMU Accel Y
        //   [16..19] IMU Accel Z
        //   [20..23] IMU Gyro X
        //   [24..27] IMU Gyro Y
        //   [28..31] IMU Gyro Z
        //   [32..33] IMU Temp (int16 LE, 0.01 °C)
        //   [34..37] EKF Pitch (int32 LE)
        //   [38..41] EKF Yaw   (int32 LE)
        //   [42..45] Current Pan Speed (int32 LE, encoder ticks/s) - NEW
        //   [46..49] Current Tilt Speed (int32 LE)                  - NEW
        //   [50..51] Reserve bytes
        var p = new byte[52];
        p[0] = (byte)(f.PanDisengaged ? 0x01 : 0x06);
        p[1] = (byte)(f.TiltDisengaged ? 0x01 : 0x06);

        byte imu1 = 0xFF;
        if (f.ImuError) imu1 = 0x00;
        byte imu2 = 0x03;
        p[6] = imu1;
        p[7] = imu2;

        // EKF Pitch/Yaw and Pan/Tilt Speed reuse the sim motor state stored
        // alongside Motor Control so feedback values agree across cards.
        WriteInt32Le(p, 34, _simEkfPitch);
        WriteInt32Le(p, 38, _simEkfYaw);
        WriteInt32Le(p, 42, _simPanSpeed);
        WriteInt32Le(p, 46, _simTiltSpeed);
        return p;
    }

    // ── §3.3.3.4  Stab Control SET sim (12 bytes) ───────────────────────────
    private byte[] BuildSimStabSetResponse()
    {
        var f = _faults;
        return [
            (byte)(f.PanDisengaged  ? 0x01 : 0x06),  // Pan status
            (byte)(f.TiltDisengaged ? 0x01 : 0x06),  // Tilt status
            0x00, 0x00, 0x00, 0x00,                  // EKF Pitch = 0
            0x00, 0x00, 0x00, 0x00,                  // EKF Yaw = 0
            0x00, 0x00                               // Reserve
        ];
    }

    // ── §3.3.4.2  IBIT sim (May 2026 SRS — 9 bytes payload + filler) ───────
    private byte[] BuildSimIbit()
    {
        var f = _faults;

        byte panBits = 0x00;
        if (f.PanIbitStartFail) panBits |= (1 << 0);
        if (f.PanIbitConnectionFail) panBits |= (1 << 1);
        if (f.PanIbitPolarityFail) panBits |= (1 << 2);
        if (f.PanIbitStall) panBits |= (1 << 3);
        if (f.PanIbitEncoderFail) panBits |= (1 << 4);
        if (f.PanIbitMotionFail) panBits |= (1 << 5);
        // bit 6 NEW: Pan Encoder Zero Offset Test (1 = needs recalibration)
        if (f.PanIbitZeroOffsetFail) panBits |= (1 << 6);

        byte tiltBits = 0x00;
        if (f.TiltIbitStartFail) tiltBits |= (1 << 0);
        if (f.TiltIbitConnectionFail) tiltBits |= (1 << 1);
        if (f.TiltIbitPolarityFail) tiltBits |= (1 << 2);
        if (f.TiltIbitStall) tiltBits |= (1 << 3);
        if (f.TiltIbitEncoderFail) tiltBits |= (1 << 4);
        if (f.TiltIbitMinStopFail) tiltBits |= (1 << 5);
        if (f.TiltIbitMaxStopFail) tiltBits |= (1 << 6);
        // bit 7 NEW: Tilt Encoder Zero Offset Test
        if (f.TiltIbitZeroOffsetFail) tiltBits |= (1 << 7);

        byte sensorBits = 0x00;
        if (f.SensorIbitPowerFail) sensorBits |= (1 << 0);
        if (f.SensorIbitBrakeOn) sensorBits |= (1 << 1);
        if (f.SensorIbitImuFail) sensorBits |= (1 << 2);
        if (f.SensorIbitImuComms) sensorBits |= (1 << 3);
        if (f.SensorIbitPanAdc) sensorBits |= (1 << 4);
        if (f.SensorIbitTiltAdc) sensorBits |= (1 << 5);

        // p[5] NEW: Pan Extended Faults (CBIT) — bits set = failed
        byte panCbit = f.PanCbitBits;
        // p[6] NEW: Tilt Extended Faults (CBIT)
        byte tiltCbit = f.TiltCbitBits;

        // p[7] NEW: IMU Status bits — wire format is PASS-flags (1 = pass),
        // sim's ImuIbitFailBits stores FAIL flags for easier injection.
        byte imuStatus = (byte)(~f.ImuIbitFailBits);

        // p[8] NEW: IMU Status bits 2 — bit0=accel range pass, bit1=gyro range pass,
        //                                bit2=gyro high range active (informational).
        byte imuStatus2 = 0x03;  // default: both ranges OK
        if (f.ImuAccelRangeFail) imuStatus2 = (byte)(imuStatus2 & ~0x01);
        if (f.ImuGyroRangeFail) imuStatus2 = (byte)(imuStatus2 & ~0x02);
        if (f.ImuGyroHighRangeActive) imuStatus2 = (byte)(imuStatus2 | 0x04);

        // Status: bit1=PASSED, bit2=FAILED
        byte status = f.HasAnyIbitFault ? (byte)0x04 : (byte)0x02;

        return [
            status, 100,
            panBits, tiltBits, sensorBits,
            panCbit, tiltCbit, imuStatus, imuStatus2,
            0x00, 0x00,
        ];
    }

    private void Log(string src, string msg)
        => LogMessage?.Invoke($"{DateTime.Now:HH:mm:ss.fff}  {src,-4}  {msg}");

    private static void WriteUInt32Le(byte[] buf, int off, uint v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteInt32Le(byte[] buf, int off, int v)
        => WriteUInt32Le(buf, off, (uint)v);

    /// <summary>Write an IEEE-754 float32 in little-endian byte order.</summary>
    private static void WriteFloatLe(byte[] buf, int off, float v)
        => WriteUInt32Le(buf, off, BitConverter.SingleToUInt32Bits(v));

    /// <summary>
    /// Encoder ticks per degree for the 21-bit Zettlex SSI scale used by the
    /// PTSC: a full revolution (360°) maps to 2^21 ticks. Verified against the
    /// C2000 reference frames (e.g. +20°/s → 116508 ticks, −10° → −58254).
    /// </summary>
    public const double TicksPerDegree = 2097152.0 / 360.0;   // 2^21 / 360 ≈ 5825.4222

    public void Dispose()
    {
        _reconnect.Dispose();
        _txLock.Dispose();
        _isSimConnected = false;
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