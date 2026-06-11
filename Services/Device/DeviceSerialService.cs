using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using ShockUI.Models.App;
using ShockUI.Models.Device;
using ShockUI.Services.App;

namespace ShockUI.Services.Device;

/// <summary>
/// RS-422 / Serial transport for the SIRS protocol (SRS §3.1 – §3.2).
///
/// Electrical characteristics (SRS §3.1):
///   Baud : 115200
///   Data : 8 bits, LSB first
///   Parity : Even
///   Stop : 1
///
/// Comms sequence (SRS §3.2.1.2):
///   Controller sends one Command → awaits one Response.
///   No next command until valid response received OR timeout elapsed.
///   Timeout 50-200 ms (SRS §3.4.1) – we default to 200 ms.
/// </summary>
public sealed class DeviceSerialService : IDisposable
{
    // -----------------------------------------------------------------------
    // Configuration
    // -----------------------------------------------------------------------
    private const int TimeoutMs = 200;
    private const int MaxRetries = 3;
    private const int BaudRate = 115200;

    // -----------------------------------------------------------------------
    // Events
    // -----------------------------------------------------------------------
    public event Action<string>? StatusChanged;
    public event Action<string>? LogMessage;
    public event Action<byte[], bool>? RawFrame;        // (bytes, isTx)
    public event Action<DeviceParsedFrame>? ResponseReceived;

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------
    public bool IsConnected => _simulationMode ? _simConnected : (_transport?.IsOpen ?? false);

    private ITransport? _transport;
    private byte _seqId;
    private readonly List<byte> _rxBuf = [];

    // DeviceFramer maintains its own reassembly state across calls; this is
    // that buffer. Kept separate from _rxBuf (the raw accumulation buffer).
    private readonly List<byte> _sirsBuf = [];
    private readonly SemaphoreSlim _txLock = new(1, 1);

    private bool _simulationMode;
    private bool _simConnected;
    private DeviceSimFaultConfig _faults = new();

    // Runtime-configurable serial port settings. Device/SIRS defaults to 115200 8-E-1.
    private SerialPortSettings _settings = SerialPortSettings.Sirs115200E81();
    private string? _lastPortName;
    private readonly SerialAutoReconnect _reconnect;

    // -----------------------------------------------------------------------
    // Ctor
    // -----------------------------------------------------------------------
    public DeviceSerialService()
    {
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

    /// <summary>Gets a clone of the current serial port settings.</summary>
    public SerialPortSettings GetPortSettings() => _settings.Clone();

    /// <summary>
    /// Applies new port settings. If the port is live it will be re-opened
    /// under the new settings. Preserve SIRS Even-parity default on first use.
    /// </summary>
    public void ApplyPortSettings(SerialPortSettings settings)
    {
        _settings = settings.Clone();
        bool wasOpen = _transport?.IsOpen ?? false;
        string? port = _lastPortName;

        if (wasOpen)
        {
            try { _transport!.Close(); } catch { }
        }

        Log("SER", $"Port settings updated: {_settings}");

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
        Log("ERR", $"Transport error: {message}");
        _reconnect.Trigger();
    }

    private bool TryOpenPort(string portName)
    {
        try
        {
            // Tear down any previous transport
            if (_transport is not null)
            {
                _transport.DataReceived -= OnDataReceived;
                _transport.ErrorReceived -= OnErrorReceived;
                _transport.Close();
                _transport.Dispose();
                _transport = null;
            }

            // Build the right transport for the current settings (UART or UDP)
            _transport = TransportFactory.Create(portName, _settings);
            _transport.DataReceived += OnDataReceived;
            _transport.ErrorReceived += OnErrorReceived;

            if (!_transport.Open())
            {
                _transport.Dispose();
                _transport = null;
                StatusChanged?.Invoke("Disconnected");
                Log("ERR", $"Connect failed (transport could not open)");
                return false;
            }

            _rxBuf.Clear(); _sirsBuf.Clear();
            _seqId = 0;
            StatusChanged?.Invoke($"Connected – {_transport.Description}");
            Log("SYS", $"Opened {_transport.Description}");
            return true;
        }
        catch (Exception ex)
        {
            _transport?.Dispose();
            _transport = null;
            StatusChanged?.Invoke("Disconnected");
            Log("ERR", $"Connect failed: {ex.Message}");
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Connection
    // -----------------------------------------------------------------------

    public IEnumerable<string> GetAvailablePorts()
        => _simulationMode
            ? ["SIM-COM1", "SIM-COM2"]
            : SerialPortDiscovery.Enumerate().Select(p => p.DisplayName).ToArray();

    public void SetSimulationMode(bool enabled)
    {
        _simulationMode = enabled;
        Log("SYS", $"Simulation mode {(enabled ? "ON" : "OFF")}");
    }

    public void SetSimFaults(DeviceSimFaultConfig config)
    {
        _faults = config;
        Log("SIM", "Fault config updated.");
    }

    public async Task ConnectAsync(string portName)
    {
        // Display names may carry a friendly description appended:
        //   "COM4 — Silicon Labs CP210x USB to UART Bridge"
        // Strip everything after " — " so we hand the OS a bare
        // port name to open.
        portName = SerialPortDiscovery.ExtractPortName(portName);

        if (IsConnected) return;

        if (_simulationMode)
        {
            _simConnected = true;
            _seqId = 0;
            StatusChanged?.Invoke($"Simulation – {portName}");
            Log("SIM", $"Connected (simulation) to {portName}");
            return;
        }

        _lastPortName = portName;
        bool ok = TryOpenPort(portName);
        if (ok) _reconnect.Arm();
        else throw new InvalidOperationException($"Failed to open {portName}.");

        await Task.CompletedTask;
    }

    public void Disconnect()
    {
        _reconnect.Disarm();
        _reconnect.Enabled = false;

        _simConnected = false;
        if (_transport is not null)
        {
            _transport.DataReceived -= OnDataReceived;
            _transport.ErrorReceived -= OnErrorReceived;
            _transport.Close();
            _transport.Dispose();
            _transport = null;
        }
        _rxBuf.Clear(); _sirsBuf.Clear();
        StatusChanged?.Invoke("Disconnected");
        Log("SYS", "Disconnected.");
    }

    // -----------------------------------------------------------------------
    // Send helpers – one per SRS §3.3 command
    // -----------------------------------------------------------------------

    /// <summary>§3.3.1  General Status (GET — payload 00 00) to PTSC dst 0x20.</summary>
    public Task SendGeneralStatusAsync()
        => SendAsync(DeviceCommandId.Ptsc.GeneralStatusGet, [0x00, 0x00], DeviceFramer.PtscDstId);

    /// <summary>§3.3 Motor Status (GET — payload 00 00) to PTSC dst 0x20.</summary>
    public Task SendMotorStatusAsync()
        => SendAsync(DeviceCommandId.Ptsc.MotorStatusGet, [0x00, 0x00], DeviceFramer.PtscDstId);

    /// <summary>§3.3 Stab Status (GET — payload 00 00) to PTSC dst 0x20.</summary>
    public Task SendStabStatusAsync()
        => SendAsync(DeviceCommandId.Ptsc.StabStatusGet, [0x00, 0x00], DeviceFramer.PtscDstId);

    /// <summary>§3.3.2  Boresight Command</summary>
    public Task SendBoresightAsync(bool nirRight, bool nirDown, bool save,
                                   ushort nirPixels, bool retRight, bool retDown, ushort retPixels)
        => SendAsync(DeviceCommandId.Boresight,
        [
            (byte)((nirRight ? 0x01 : 0) | (nirDown ? 0x02 : 0)),
            save ? (byte)0x01 : (byte)0x00,
            (byte)(nirPixels & 0xFF), (byte)(nirPixels >> 8),
            (byte)((retRight ? 0x01 : 0) | (retDown ? 0x02 : 0)),
            (byte)(retPixels & 0xFF), (byte)(retPixels >> 8),
            0x00, 0x00   // reserve bytes 16-17
        ]);

    /// <summary>§3.3.3  NIR Sensor Settings Command</summary>
    public Task SendNirSensorSettingsAsync(bool setIntegration, bool manualExposure,
                                           byte gainIndex, uint integrationUs)
        => SendAsync(DeviceCommandId.NirSensorSettings,
        [
            setIntegration ? (byte)0x01 : (byte)0x00,
            manualExposure ? (byte)0x01 : (byte)0x00,
            (byte)(gainIndex & 0x0F),
            (byte)(integrationUs & 0xFF),
            (byte)((integrationUs >> 8)  & 0xFF),
            (byte)((integrationUs >> 16) & 0xFF),
            (byte)((integrationUs >> 24) & 0xFF),
            0x00, 0x00   // reserve
        ]);

    /// <summary>§3.3.4  MWIR Sensor Settings Command</summary>
    public Task SendMwirSensorSettingsAsync(bool manualExposure, byte gainCap,
                                            byte nucMode, uint integrationUs)
        => SendAsync(DeviceCommandId.MwirSensorSettings,
        [
            manualExposure ? (byte)0x01 : (byte)0x00,
            (byte)((gainCap & 0x03) | ((nucMode & 0x03) << 2)),
            (byte)(integrationUs & 0xFF),
            (byte)((integrationUs >> 8)  & 0xFF),
            (byte)((integrationUs >> 16) & 0xFF),
            (byte)((integrationUs >> 24) & 0xFF),
            0x00, 0x00   // reserve
        ]);

    /// <summary>§3.3.5  Pan and Tilt Motor Control Command</summary>
    /// <summary>
    /// §3.3.5  Pan/Tilt Motor Control — sent DIRECTLY to PTSC (dst=0x20),
    /// not through the System Controller's local handler. The SC just
    /// forwards the frame over the internal RS422 bus. Payload format
    /// follows the SIRS §3.3.5 spec until/unless PTSC firmware switches
    /// to its native EOS motor-control payload.
    /// </summary>
    /// <summary>
    /// Encoder ticks per degree for the PTSC's 21-bit scale: 360° = 2^21 ticks.
    /// Verified against the C2000 reference frames (+20°/s → 116508 ticks).
    /// Applies to both position (deg) and rate (deg/s) fields.
    /// </summary>
    private const double TicksPerDegree = 2097152.0 / 360.0;   // 2^21 / 360 ≈ 5825.4222

    // Sim-only mirror of the last commanded EKF targets (ticks), so the
    // simulated Stab Status response can echo plausible values.
    private int _simEkfPitch;
    private int _simEkfYaw;

    private static int DegToTicks(double degrees)
        => (int)Math.Round(degrees * TicksPerDegree);

    /// <summary>
    /// §3.3.5  Pan/Tilt Motor Control — sent DIRECTLY to PTSC (dst=0x20).
    /// Verified 16-byte payload:
    ///   [0]  panCtrl  = (panMode &lt;&lt; 1) | panDisengage
    ///   [1]  tiltCtrl = (tiltMode &lt;&lt; 1) | tiltDisengage
    ///   [2..5]   pan position  — int32 LE, ticks (deg × 2^21/360)
    ///   [6..9]   tilt position — int32 LE, ticks
    ///   [10..13] pan speed     — int32 LE, ticks/s (deg/s × 2^21/360)
    ///   [14..17] tilt speed    — int32 LE, ticks/s
    ///   [18..19] reserve (0x00 0x00)
    /// (Total 20-byte payload, matching the verified C2000 MotorSet frame.)
    /// Modes: 0=Safety/Damping, 1=Rate/Velocity, 2=Position, 3=Stabilised.
    /// Angles in degrees, speeds in degrees/second.
    /// </summary>
    public Task SendPanTiltAsync(byte panMode, byte tiltMode,
                                 double panAngleDeg, double tiltAngleDeg,
                                 double panSpeedDps, double tiltSpeedDps,
                                 bool panDisengage = false, bool tiltDisengage = false)
    {
        byte panCtrl = (byte)((panMode & 0x03) << 1 | (panDisengage ? 0x01 : 0x00));
        byte tiltCtrl = (byte)((tiltMode & 0x03) << 1 | (tiltDisengage ? 0x01 : 0x00));

        int panPos = DegToTicks(panAngleDeg);
        int tiltPos = DegToTicks(tiltAngleDeg);
        int panSpd = DegToTicks(panSpeedDps);
        int tiltSpd = DegToTicks(tiltSpeedDps);

        // Mirror the commanded state so a simulated Motor Status (0x08) poll
        // reflects what was last sent (position mode → echo target; rate mode →
        // echo velocity). On real hardware the device reports actual values.
        _simPanCtrl = panCtrl;
        _simTiltCtrl = tiltCtrl;
        _simPanPosTicks = panPos;
        _simTiltPosTicks = tiltPos;
        _simPanSpdTicks = panSpd;
        _simTiltSpdTicks = tiltSpd;

        var p = new byte[20];
        p[0] = panCtrl;
        p[1] = tiltCtrl;
        WriteI32(p, 2, panPos);
        WriteI32(p, 6, tiltPos);
        WriteI32(p, 10, panSpd);
        WriteI32(p, 14, tiltSpd);
        // p[18..19] = reserve (already 0)
        return SendAsync(DeviceCommandId.Ptsc.MotorSet, p, DeviceFramer.PtscDstId);
    }

    /// <summary>
    /// §3.3.6  Stab Control — also sent DIRECTLY to PTSC. Same
    /// rationale as <see cref="SendPanTiltAsync"/> above.
    /// </summary>
    /// <summary>
    /// §3.3.6  Stab Control — sent DIRECTLY to PTSC (dst=0x20).
    /// Verified 20-byte payload:
    ///   [0]      panCtrl  = (panMode &lt;&lt; 1) | panDisengage
    ///   [1]      tiltCtrl = (tiltMode &lt;&lt; 1) | tiltDisengage
    ///   [2..5]   EKF Pitch target — float32 LE, DEGREES
    ///   [6..9]   EKF Yaw   target — float32 LE, DEGREES
    ///   [10..13] Pan nudge rate   — int32 LE, ticks/s (deg/s × 2^21/360)
    ///   [14..17] Tilt nudge rate  — int32 LE, ticks/s
    ///   [18..19] reserve
    /// The simple single-mode overload is kept for existing callers that only
    /// toggle the stab mode (e.g. controller A-button); it sets both axes to
    /// the same mode with zero targets.
    /// </summary>
    public Task SendStabControlAsync(
        byte panMode, bool panDisengage,
        byte tiltMode, bool tiltDisengage,
        float ekfPitchDeg = 0f, float ekfYawDeg = 0f,
        double panNudgeDps = 0, double tiltNudgeDps = 0)
    {
        byte panCtrl = (byte)((panMode & 0x03) << 1 | (panDisengage ? 0x01 : 0x00));
        byte tiltCtrl = (byte)((tiltMode & 0x03) << 1 | (tiltDisengage ? 0x01 : 0x00));

        var p = new byte[20];
        p[0] = panCtrl;
        p[1] = tiltCtrl;
        WriteF32(p, 2, ekfPitchDeg);
        WriteF32(p, 6, ekfYawDeg);
        WriteI32(p, 10, DegToTicks(panNudgeDps));
        WriteI32(p, 14, DegToTicks(tiltNudgeDps));
        // p[18..19] = reserve

        // Mirror EKF targets so the simulated Stab Status (0x0A) can echo them.
        _simEkfPitch = DegToTicks(ekfPitchDeg);
        _simEkfYaw = DegToTicks(ekfYawDeg);

        return SendAsync(DeviceCommandId.Ptsc.StabSet, p, DeviceFramer.PtscDstId);
    }

    /// <summary>
    /// Convenience overload: set both axes to <paramref name="stabMode"/>
    /// with no EKF target or nudge. Used by simple mode-toggle callers.
    /// </summary>
    public Task SendStabControlAsync(byte stabMode)
        => SendStabControlAsync(stabMode, false, stabMode, false);

    /// <summary>§3.3.7  Video Source Selection Command</summary>
    public Task SendVideoSourceAsync(bool sdi1Nir, bool sdi2Nir)
        => SendAsync(DeviceCommandId.VideoSourceSelection,
        [(byte)((sdi1Nir ? 0x01 : 0) | (sdi2Nir ? 0x02 : 0)), 0x00, 0x00]);

    /// <summary>
    /// VIS/NIR FOV Change — sent DIRECTLY to the VisNIR EOA Controller
    /// (dst=0x54) using the EOA-native command ID 0x0084 per the Host
    /// GUI changes spec. The SC reads the dst byte and forwards the
    /// frame to the VisNIR EOA without processing it locally.
    ///
    /// EOA expects a single-byte payload (just the FOV selection) —
    /// NO trailing reserve bytes. Frame length = 1.
    /// Example wire bytes for WFOV (selection = 1):
    ///   0A 88 01 00 00 54 00 SS 00 84 01 01 CRC_LSB CRC_MSB
    /// </summary>
    public Task SendNirFovAsync(byte fovCommand)
        => SendAsync(
            commandId: 0x84,                          // Cmd LSB; MSB=0x00 → 0x0084 big-endian on wire
            payload: new byte[] { (byte)(fovCommand & 0x07) },   // 1 byte: FOV selection only
            dstId: DeviceFramer.VisNirDstId);     // 0x54

    /// <summary>§3.3.8.3  MWIR FOV Change Command</summary>
    public Task SendMwirFovAsync(byte fovCommand)
        => SendAsync(DeviceCommandId.MwirFovChange,
        [(byte)(fovCommand & 0x07), 0x00, 0x00]);

    /// <summary>
    /// VIS/NIR Focus Change — sent DIRECTLY to the VisNIR EOA Controller
    /// (dst=0x54) using the EOA-native command ID 0x0085 per the Host
    /// GUI changes spec. SendFocusAsync's existing payload format is
    /// preserved since the EOA understands the same mode/position/speed
    /// layout that SIRS used.
    /// </summary>
    public Task SendNirFocusAsync(byte focusMode, int position, int speed)
    {
        // Capture for the sim BEFORE the wire send so the response
        // can echo the requested motor state.
        if (_simulationMode)
            CaptureFocusRequest(focusMode, position, speed);

        return SendFocusAsync(cmdId: 0x85, mode: focusMode, pos: position, spd: speed,
                              dstId: DeviceFramer.VisNirDstId);
    }

    /// <summary>§3.3.9.3  MWIR Focus Change Command</summary>
    public Task SendMwirFocusAsync(byte focusMode, int position, int speed)
    {
        if (_simulationMode)
            CaptureFocusRequest(focusMode, position, speed);
        return SendFocusAsync(DeviceCommandId.MwirFocusChange, focusMode, position, speed);
    }

    /// <summary>§3.3.10.1  MWIR Image Enhancement Command</summary>
    public Task SendMwirImageEnhancementAsync(byte edgeMode, byte contrastMode,
                                              byte nucMode, bool deadPixelEnable,
                                              bool noiseSuppressEnable, bool upscaleEnable,
                                              byte polarityMode)
        => SendAsync(DeviceCommandId.MwirImageEnhancement,
        [
            (byte)(edgeMode     & 0x03),
            (byte)(contrastMode & 0x03),
            (byte)(nucMode      & 0x03),
            (byte)((deadPixelEnable ? 0 : 0x01) | (noiseSuppressEnable ? 0 : 0x02)),
            (byte)((upscaleEnable ? 0 : 0x01)   | ((polarityMode & 0x03) << 1)),
            0x00, 0x00   // reserve
        ]);

    /// <summary>
    /// NIR Exposure — Auto/Manual selector + analogue gain +
    /// manual-exposure value (IEEE-754 float, milliseconds).
    /// Payload (8 bytes):
    ///   [0]    1=Manual, 0=Auto
    ///   [1]    Analogue gain (default 4)
    ///   [2..5] Manual exposure as IEEE-754 single-precision, little-endian
    ///   [6][7] 0x00 0x00 (reserve)
    /// </summary>
    public Task SendNirExposureAsync(bool manual, byte gain, float exposureMs)
        => SendAsync(DeviceCommandId.NirExposure, BuildExposurePayload(manual, gain, exposureMs));

    /// <summary>
    /// VIS Exposure — same payload shape as NIR exposure; addresses
    /// the visible-spectrum (RGB) sensor's auto-exposure controller.
    /// </summary>
    public Task SendVisExposureAsync(bool manual, byte gain, float exposureMs)
        => SendAsync(DeviceCommandId.VisExposure, BuildExposurePayload(manual, gain, exposureMs));

    private static byte[] BuildExposurePayload(bool manual, byte gain, float exposureMs)
    {
        // IEEE-754 single-precision, little-endian on x64 Windows + Linux.
        // (BitConverter.IsLittleEndian == true on every supported host.)
        byte[] expBytes = BitConverter.GetBytes(exposureMs);
        return new byte[]
        {
            manual ? (byte)0x01 : (byte)0x00,
            gain,
            expBytes[0], expBytes[1], expBytes[2], expBytes[3],
            0x00, 0x00   // reserve
        };
    }

    /// <summary>§3.3.10.3  NIR Image Enhancement Command</summary>
    public Task SendNirImageEnhancementAsync(byte edgeMode, byte contrastMode,
                                             byte colourMatrix, bool deadPixelEnable,
                                             bool noiseSuppressEnable)
        => SendAsync(DeviceCommandId.NirImageEnhancement,
        [
            (byte)(edgeMode     & 0x03),
            (byte)(contrastMode & 0x03),
            (byte)(colourMatrix & 0x03),
            (byte)((deadPixelEnable ? 0 : 0x01) | (noiseSuppressEnable ? 0 : 0x02)),
            0x00, 0x00   // reserve
        ]);

    /// <summary>
    /// §3.3.10.x  RGB Image Enhancement Command — visible-spectrum
    /// sensor. Payload mirrors the NIR enhancement command above so
    /// the firmware can reuse the same decoder pattern.
    /// </summary>
    public Task SendRgbImageEnhancementAsync(byte edgeMode, byte contrastMode,
                                             bool deadPixelEnable,
                                             bool noiseSuppressEnable)
        => SendAsync(DeviceCommandId.RgbImageEnhancement,
        [
            (byte)(edgeMode     & 0x03),
            (byte)(contrastMode & 0x03),
            (byte)((deadPixelEnable ? 0 : 0x01) | (noiseSuppressEnable ? 0 : 0x02)),
            0x00, 0x00   // reserve
        ]);

    /// <summary>
    /// LRF Range Measurement — raw Noptel passthrough (no SIRS wrapping).
    /// Noptel command 0xCC + mode byte. mode: 0=SMM (single), 1..3=CMM rates.
    /// Verify packet contents against Noptel LRX ICD O50090DE.
    /// </summary>
    /// <summary>
    /// Noptel "Execute range measurement" (0xCC). The mode byte maps:
    ///   0x00 = SMM            0x01 = CMM   1 Hz     0x05 = CMM 100 Hz
    ///   0x10 = QSMM 1         0x02 = CMM   4 Hz     0x06 = CMM 200 Hz
    ///   0x20 = QSMM 2         0x03 = CMM  10 Hz     0x07 = CMM 500 Hz
    ///                          0x04 = CMM  20 Hz
    /// The caller passes the exact byte (no masking); the VM's
    /// LrfModeBytes lookup converts the dropdown index to it.
    /// </summary>
    public Task SendLrfMeasurementAsync(byte measurementMode)
        => SendLrfNoptelAsync(
            cmdId: 0xCC,
            payload: new byte[] { measurementMode },
            simSirsCmdId: DeviceCommandId.LrfRangeMeasurement,
            includeReserved: true);   // 0xCC frame carries 2 "Not used" bytes

    /// <summary>
    /// LRF Stop CMM — raw Noptel passthrough. Noptel "break" command 0xC6
    /// ends an active continuous measurement. Verify against ICD.
    /// </summary>
    public Task SendLrfStopCmmAsync()
        => SendLrfNoptelAsync(0xC6, EmptyPayload, DeviceCommandId.LrfStopCmm);

    /// <summary>
    /// LRF Get Measurement Range — raw Noptel passthrough.
    /// Noptel GET range command (0x30, no params). No reserve bytes.
    /// </summary>
    public Task SendLrfGetRangeAsync()
        => SendLrfNoptelAsync(0x30, EmptyPayload, DeviceCommandId.LrfMeasurementRange);

    /// <summary>
    /// LRF Set Minimum Range — raw Noptel passthrough.
    /// Noptel SET MIN range command (0x31 + value as 2-byte LE). No reserve bytes.
    /// </summary>
    public Task SendLrfSetMinRangeAsync(ushort minMeters)
    {
        byte[] p = { (byte)(minMeters & 0xFF), (byte)(minMeters >> 8) };
        return SendLrfNoptelAsync(0x31, p, DeviceCommandId.LrfMeasurementRange);
    }

    /// <summary>
    /// LRF Set Maximum Range — raw Noptel passthrough.
    /// Noptel SET MAX range command (0x32 + value as 2-byte LE). No reserve bytes.
    /// </summary>
    public Task SendLrfSetMaxRangeAsync(ushort maxMeters)
    {
        byte[] p = { (byte)(maxMeters & 0xFF), (byte)(maxMeters >> 8) };
        return SendLrfNoptelAsync(0x32, p, DeviceCommandId.LrfMeasurementRange);
    }

    // ── LRF send methods — RAW NOPTEL PASSTHROUGH ─────────────────────
    //
    // Per the Host GUI changes spec: "LRF commands should not have our
    // protocol structure. No sync -> length + No our CRC. Only send
    // their complete packet as the whole packet."
    //
    // So every LRF method below builds the native Noptel LRX packet
    // (per Noptel ICD O50090DE) and ships it down the wire UNWRAPPED —
    // no 0A 88 sync, no SIRS length/CRC. The System Controller passes
    // these bytes through to the LRX module untouched.
    //
    // The packet contents below follow the simplest "command-byte only"
    // form from the Noptel ICD; verify each against the spec and update
    // if any of them require parameter bytes or trailing checksums.

    /// <summary>Noptel §3.4  Ask Status (0xC7)</summary>
    public Task SendLrfStatusQueryAsync()
        => SendLrfNoptelAsync(0xC7, EmptyPayload, DeviceCommandId.LrfStatusQuery);

    /// <summary>Noptel §3.3  Check Optical Crosstalk (0xDE)</summary>
    public Task SendLrfOpticalCrosstalkAsync()
        => SendLrfNoptelAsync(0xDE, EmptyPayload, DeviceCommandId.LrfOpticalCrosstalk);

    /// <summary>Noptel §3.5  Set Alignment Pointer (0xC5)
    /// Parameter: 0x02 = visible ON, 0x00 = OFF.</summary>
    public Task SendLrfAlignmentPointerAsync(bool on)
        => SendLrfNoptelAsync(
            cmdId: 0xC5,
            payload: new byte[] { on ? (byte)0x02 : (byte)0x00 },
            simSirsCmdId: DeviceCommandId.LrfAlignmentPointer);

    /// <summary>Noptel §3.9  Set Baud Rate and Save Settings (0xC8)
    /// Parameter: 0=save current, 1..7 = baud rate slot.</summary>
    public Task SendLrfBaudRateAsync(byte selection)
        => SendLrfNoptelAsync(
            cmdId: 0xC8,
            payload: new byte[] { (byte)(selection & 0x07) },
            simSirsCmdId: DeviceCommandId.LrfBaudRate);

    /// <summary>Noptel §3.10  Request Identification (0xC0)</summary>
    public Task SendLrfIdentificationAsync()
        => SendLrfNoptelAsync(0xC0, EmptyPayload, DeviceCommandId.LrfIdentification);

    /// <summary>Noptel §3.11  Request Diagnostic Data (0xC2)</summary>
    public Task SendLrfDiagnosticsAsync()
        => SendLrfNoptelAsync(0xC2, EmptyPayload, DeviceCommandId.LrfDiagnostics);

    /// <summary>Noptel §3.12  Reset Serial Error Counter (0xCB)</summary>
    public Task SendLrfResetErrorCounterAsync()
        => SendLrfNoptelAsync(0xCB, EmptyPayload, DeviceCommandId.LrfResetErrorCounter);

    /// <summary>
    /// Low-level raw-Noptel writer for LRF commands. Bypasses
    /// DeviceFramer entirely so no SIRS sync/length/CRC is added.
    ///
    /// In simulation mode the call is routed back through the existing
    /// SimulateResponse pipeline using a SIRS-equivalent command ID so
    /// the VM still gets the parsed response it expects. In real mode
    /// the bytes go straight to the transport.
    /// </summary>
    /// <summary>
    /// Send a Noptel LRX command. The helper handles framing:
    ///   wire bytes = [SYNC=0x59] [cmdId] [payload...] [checkByte]
    /// Caller passes only cmdId and any payload params.
    /// </summary>
    /// <param name="cmdId">Noptel command byte (e.g. 0xC7 = ask status).</param>
    /// <param name="payload">Optional command parameters; empty for none.</param>
    /// <param name="simSirsCmdId">
    /// SIRS-equivalent command ID used in simulation mode so the
    /// existing SimulateResponse pipeline can synthesise a structured
    /// response for the VM.
    /// </param>
    private async Task SendLrfNoptelAsync(byte cmdId, byte[] payload, byte simSirsCmdId,
                                          bool includeReserved = false)
    {
        await _txLock.WaitAsync();
        try
        {
            byte seq = _seqId++;
            byte[] packet = NoptelChecksum.BuildPacket(cmdId, payload, includeReserved);

            if (_simulationMode)
            {
                Log("TX", $"SIM NOPTEL LRF [{BitConverter.ToString(packet)}]");
                RawFrame?.Invoke(packet, true);
                SimulateResponse(simSirsCmdId, seq, DeviceFramer.SystemControllerDstId);
                return;
            }

            if (_transport is null || !_transport.IsOpen) return;

            Log("TX", $"NOPTEL LRF [{BitConverter.ToString(packet)}]");
            RawFrame?.Invoke(packet, true);
            _transport!.Write(packet, 0, packet.Length);
            await Task.CompletedTask;
        }
        finally { _txLock.Release(); }
    }

    private static readonly byte[] EmptyPayload = Array.Empty<byte>();

    /// <summary>§3.3.14.1  NIR Brightness and Contrast Command</summary>
    public Task SendNirBrightnessContrastAsync(bool setValues, ushort brightness, ushort contrast)
        => SendBrightnessContrastAsync(DeviceCommandId.NirBrightnessContrast, setValues, brightness, contrast);

    /// <summary>§3.3.14.3  MWIR Brightness and Contrast Command</summary>
    public Task SendMwirBrightnessContrastAsync(bool setValues, ushort brightness, ushort contrast)
        => SendBrightnessContrastAsync(DeviceCommandId.MwirBrightnessContrast, setValues, brightness, contrast);

    /// <summary>
    /// §3.3.15.x  Stream-1 (primary) symbology on/off. Uses its own
    /// command ID per the Host GUI changes spec; payload is the single
    /// on/off byte (1 = on, 0 = off) — no stream selector needed since
    /// the command ID identifies the stream.
    /// </summary>
    public Task SendStream1SymbologyAsync(bool on)
        => SendAsync(DeviceCommandId.Stream1Symbology,
                     [on ? (byte)0x01 : (byte)0x00]);

    /// <summary>
    /// §3.3.15.x  Stream-2 (secondary) symbology on/off. Mirrors the
    /// stream-1 method but with its own unique command ID. Confirm the
    /// 0x16 allocation against the SRS (it's a locally-assigned ID for
    /// now — see DeviceCommandId.Stream2Symbology remark).
    /// </summary>
    public Task SendStream2SymbologyAsync(bool on)
        => SendAsync(DeviceCommandId.Stream2Symbology,
                     [on ? (byte)0x01 : (byte)0x00]);

    /// <summary>
    /// §3.3.16.1  IBIT Command. Payload [mode, 0x00] where
    ///   mode 0x00 = Read previous results, 0x01 = Full IBIT,
    ///   0x02 = Silent IBIT (sensors only).
    /// </summary>
    /// <summary>
    /// §3.3.16 IBIT Control (0x23). RX payload = [Action, Reserve].
    ///   Action 0x00 = Read progress, 0x01 = Full IBIT, 0x02 = Silent IBIT.
    /// </summary>
    public Task SendIbitAsync(byte mode = 0x01)
    {
        // A Full (0x01) or Silent (0x02) action begins a new test; reset the
        // simulated progress ramp so it animates from 0 again.
        if ((mode & 0x03) != 0x00) ResetSimIbit();
        return SendAsync(DeviceCommandId.Ptsc.Ibit, [(byte)(mode & 0x03), 0x00], DeviceFramer.PtscDstId);
    }

    /// <summary>Poll IBIT progress/results (Action 0x00 = Read).</summary>
    public Task SendIbitReadAsync()
        => SendAsync(DeviceCommandId.Ptsc.Ibit, [0x00, 0x00], DeviceFramer.PtscDstId);

    // ── Maintenance: Start NUC on each sensor ─────────────────────────
    // Payload format (per the May 2026 NUC spec):
    //   [0] NUC type — 1 = 1-Point NUC, 2 = 2-Point NUC
    //   [1] reserve  (0x00)
    //   [2] reserve  (0x00)
    // Length byte on the wire = 0x03. Routed to the System Controller
    // (default dst 0x10), which forwards to the appropriate sensor.

    // Remember the most recent NUC type per sensor so the simulator can
    // echo back the value that was actually requested (rather than a
    // hard-coded constant). Defaults to 2 (2-Point).
    private byte _lastRgbNucType = 2;
    private byte _lastNirNucType = 2;

    /// <summary>Start NUC on the RGB sensor. Maintenance only.</summary>
    public Task SendRgbStartNucAsync(byte nucType)
    {
        _lastRgbNucType = nucType;
        return SendAsync(DeviceCommandId.RgbStartNuc, [nucType, 0x00, 0x00]);
    }

    /// <summary>Start NUC on the NIR sensor. Maintenance only.</summary>
    public Task SendNirStartNucAsync(byte nucType)
    {
        _lastNirNucType = nucType;
        return SendAsync(DeviceCommandId.NirStartNuc, [nucType, 0x00, 0x00]);
    }

    // -----------------------------------------------------------------------
    // Shared send helpers
    // -----------------------------------------------------------------------

    private Task SendFocusAsync(byte cmdId, byte mode, int pos, int spd,
                                byte dstId = DeviceFramer.SystemControllerDstId)
        // Focus_Commands_Updated.txt — payload is 9 bytes, length 0x09:
        //   [0]    Focus Control Byte (bits 0:2 = mode)
        //   [1..4] Focus Position (int32 LE, encoder ticks)
        //   [5..8] Focus Speed    (int32 LE, signed — negative = reverse)
        // Modes (bits 0:2):
        //   0 = Get feedback           1 = Set focus position
        //   2 = Set focus speed        3 = Move to infinite focus
        //   4 = Stop control
        => SendAsync(cmdId,
        [
            (byte)(mode & 0x07),
            (byte)( pos        & 0xFF),
            (byte)((pos >> 8)  & 0xFF),
            (byte)((pos >> 16) & 0xFF),
            (byte)((pos >> 24) & 0xFF),
            (byte)( spd        & 0xFF),
            (byte)((spd >> 8)  & 0xFF),
            (byte)((spd >> 16) & 0xFF),
            (byte)((spd >> 24) & 0xFF),
        ], dstId);

    private Task SendBrightnessContrastAsync(byte cmdId, bool set, ushort brightness, ushort contrast)
        => SendAsync(cmdId,
        [
            set ? (byte)0x01 : (byte)0x00,
            (byte)(brightness & 0xFF), (byte)(brightness >> 8),
            (byte)(contrast   & 0xFF), (byte)(contrast   >> 8),
            0x00, 0x00   // reserve
        ]);

    // -----------------------------------------------------------------------
    // Core send / receive
    // -----------------------------------------------------------------------

    private Task SendAsync(byte commandId, byte[] payload)
        => SendAsync(commandId, payload, DeviceFramer.SystemControllerDstId);

    /// <summary>
    /// Sends a SIRS-formatted frame to a specific destination ID. Used
    /// for commands that bypass the System Controller's local handling
    /// and need to land directly on a downstream subsystem (e.g. PTSC
    /// motor and stab commands, which the SC just forwards over the
    /// internal RS422 bus).
    /// </summary>
    private async Task SendAsync(byte commandId, byte[] payload, byte dstId)
    {
        await _txLock.WaitAsync();
        try
        {
            byte seq = _seqId++;
            byte[] frame = DeviceFramer.BuildCommand(
                commandId, seq, payload,
                dstId,
                DeviceFramer.HostSrcId);

            // High-frequency controller motion frames (MotorSet / StabSet to
            // the PTSC) would otherwise spam the Log and Raw Trace panels at the
            // controller rate, churning UI bindings and spiking the CPU. Throttle
            // their logging to ~4 Hz; all other commands always log.
            bool isMotion = dstId == DeviceFramer.PtscDstId &&
                            (commandId == DeviceCommandId.Ptsc.MotorSet ||
                             commandId == DeviceCommandId.Ptsc.StabSet);
            bool logThis = !isMotion ||
                           (DateTime.UtcNow - _lastMotionLog).TotalMilliseconds >= 250;
            if (isMotion && logThis) _lastMotionLog = DateTime.UtcNow;

            if (_simulationMode)
            {
                if (logThis)
                {
                    Log("TX", $"SIM CMD 0x{commandId:X2} seq={seq} [{BitConverter.ToString(payload)}]");
                    RawFrame?.Invoke(frame, true);
                }
                SimulateResponse(commandId, seq, dstId);
                return;
            }

            if (!IsConnected)
            {
                Log("ERR", "Cannot send – not connected.");
                return;
            }

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    _transport!.Write(frame, 0, frame.Length);
                    if (logThis)
                    {
                        RawFrame?.Invoke(frame, true);
                        Log("TX", $"CMD 0x{commandId:X2} seq={seq} attempt={attempt}");
                    }
                    // Fire-and-forget motion frames (MotorSet / StabSet) expect no
                    // reply, so we must NOT hold the TX lock for the response
                    // timeout — doing so backs up the controller's frames and was
                    // the root cause of commands queueing and the gimbal lagging.
                    if (!isMotion)
                        await Task.Delay(TimeoutMs);
                    break;
                }
                catch (TimeoutException)
                {
                    Log("WARN", $"TX timeout attempt {attempt}/{MaxRetries}");
                    if (attempt == MaxRetries)
                        Log("ERR", $"CMD 0x{commandId:X2} failed after {MaxRetries} retries.");
                }
            }
        }
        finally
        {
            _txLock.Release();
        }
    }

    private void OnDataReceived(byte[] buf)
    {
        try
        {
            // Bytes arrive raw from whichever transport (UART chunk or UDP datagram).
            RawFrame?.Invoke(buf, false);

            // The LRF (Noptel LRX) replies in its own framing, which is NOT a SIRS
            // frame, so SIRS and Noptel replies can interleave on the same wire.
            // Accumulate everything, then peel complete messages off the front,
            // dispatching by the leading sync byte: 0x0A(+0x88) = SIRS, 0x59 = Noptel.
            _rxBuf.AddRange(buf);

            bool progress = true;
            while (progress && _rxBuf.Count > 0)
            {
                progress = false;
                byte lead = _rxBuf[0];

                if (lead == NoptelChecksum.ResponseSyncByte)
                {
                    int consumed = TryExtractNoptelReply(_rxBuf);
                    if (consumed > 0) { _rxBuf.RemoveRange(0, consumed); progress = true; }
                    else break;   // need more bytes to complete the Noptel reply
                }
                else if (lead == 0x0A)
                {
                    // Feed the SIRS framer only the bytes up to the next Noptel
                    // sync (0x59), so it never discards interleaved LRF replies
                    // while hunting for its own sync.
                    int nextNoptel = _rxBuf.IndexOf(NoptelChecksum.ResponseSyncByte);
                    int take = nextNoptel < 0 ? _rxBuf.Count : nextNoptel;
                    var sirsChunk = _rxBuf.GetRange(0, take).ToArray();

                    bool any = false;
                    foreach (var frame in DeviceFramer.FeedBytes(sirsChunk, _sirsBuf))
                    {
                        Log("RX", $"RSP 0x{frame.CommandId:X2} seq={frame.SequenceId}" +
                                   (frame.HasError ? $" ERR=0x{frame.ErrorCode:X4}" : ""));
                        ResponseReceived?.Invoke(frame);
                        any = true;
                    }

                    // Rebuild _rxBuf as: (unconsumed SIRS tail) + (Noptel bytes we held back).
                    var tail = new List<byte>(_sirsBuf);
                    _sirsBuf.Clear();
                    if (nextNoptel >= 0)
                        tail.AddRange(_rxBuf.GetRange(take, _rxBuf.Count - take));
                    _rxBuf.Clear();
                    _rxBuf.AddRange(tail);

                    if (any) progress = true;
                    else if (nextNoptel > 0) progress = true;   // Noptel bytes await
                    else break;   // incomplete SIRS frame, wait for more bytes
                }
                else
                {
                    // Unknown leading byte — drop one and resync.
                    _rxBuf.RemoveAt(0);
                    progress = true;
                }
            }
        }
        catch (Exception ex)
        {
            Log("ERR", $"RX error: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to frame one Noptel LRX reply at the front of <paramref name="buf"/>.
    /// Reply layout: [0x59 SYNC][cmdEcho][payload...][check], where
    /// check = (sum of all preceding bytes) XOR 0x50.
    ///
    /// Length is not fixed per command and the ICD does not tabulate every
    /// reply size, so we frame by checksum: try increasing end positions and
    /// accept the shortest span whose trailing byte matches the computed check.
    /// Returns the number of bytes consumed (0 if a complete, valid reply is
    /// not yet available).
    /// </summary>
    private int TryExtractNoptelReply(List<byte> buf)
    {
        // Minimum reply = SYNC + cmdEcho + check = 3 bytes.
        const int minLen = 3;
        const int maxLen = 64;   // generous upper bound for any LRX reply
        if (buf.Count < minLen) return 0;

        int limit = Math.Min(buf.Count, maxLen);

        // Minimum inner-payload length per command echo, to avoid a short
        // false-positive checksum match inside a longer real reply.
        byte echo = buf.Count > 1 ? buf[1] : (byte)0x00;
        int minPayload = echo switch
        {
            0xCC => 18,   // range measurement: 3×(float32 range + u16 signal)
            0xC7 => 29,   // status query
            0xC2 => 24,   // diagnostics
            0xC0 => 10,   // identification
            0xDE => 1,    // optical crosstalk
            _ => 0,       // acks (stop, pointer, baud, reset, set/get range)
        };
        int minEnd = Math.Max(minLen, 2 + minPayload + 1);   // SYNC+echo+payload+check

        for (int end = minEnd; end <= limit; end++)
        {
            byte check = buf[end - 1];
            byte calc = NoptelChecksum.Compute(System.Runtime.InteropServices.CollectionsMarshal
                                               .AsSpan(buf)[..(end - 1)]);
            if (check == calc)
            {
                // Valid frame [0..end-1]. Strip SYNC(0) + cmdEcho(1) ... check(end-1).
                byte cmdEcho = buf[1];
                int payloadLen = end - 3;            // exclude SYNC, cmdEcho, check
                var payload = new byte[payloadLen < 0 ? 0 : payloadLen];
                for (int i = 0; i < payload.Length; i++)
                    payload[i] = buf[2 + i];
                DispatchNoptelReply(cmdEcho, payload);
                return end;
            }
        }

        // No valid checksum within range. If we've buffered well beyond maxLen,
        // drop the stale SYNC to avoid deadlock; otherwise wait for more bytes.
        return buf.Count > maxLen ? 1 : 0;
    }

    /// <summary>
    /// Map a Noptel reply (command echo + inner payload) to the SIRS-equivalent
    /// DeviceCommandId and raise ResponseReceived so the VM's existing switch
    /// decodes it — exactly as the simulator path does.
    /// </summary>
    private void DispatchNoptelReply(byte cmdEcho, byte[] payload)
    {
        byte sirsCmd = cmdEcho switch
        {
            0xCC => DeviceCommandId.LrfRangeMeasurement,
            0xC6 => DeviceCommandId.LrfStopCmm,
            0x30 or 0x31 or 0x32 => DeviceCommandId.LrfMeasurementRange,
            0xC7 => DeviceCommandId.LrfStatusQuery,
            0xDE => DeviceCommandId.LrfOpticalCrosstalk,
            0xC5 => DeviceCommandId.LrfAlignmentPointer,
            0xC8 => DeviceCommandId.LrfBaudRate,
            0xC0 => DeviceCommandId.LrfIdentification,
            0xC2 => DeviceCommandId.LrfDiagnostics,
            0xCB => DeviceCommandId.LrfResetErrorCounter,
            _ => 0x00,
        };
        if (sirsCmd == 0x00)
        {
            Log("RX", $"NOPTEL LRF unknown reply cmd 0x{cmdEcho:X2} [{BitConverter.ToString(payload)}]");
            return;
        }

        Log("RX", $"NOPTEL LRF reply cmd 0x{cmdEcho:X2} → 0x{sirsCmd:X2} " +
                  $"[{BitConverter.ToString(payload)}]");

        var frame = new DeviceParsedFrame
        {
            ProtocolVersion = DeviceFramer.ProtocolVer,
            ErrorByte1 = 0x00,
            ErrorByte2 = 0x00,
            DestinationId = DeviceFramer.HostSrcId,
            SourceId = DeviceFramer.SystemControllerDstId,
            SequenceId = 0x00,
            CommandId = sirsCmd,
            Payload = payload,
        };
        ResponseReceived?.Invoke(frame);
    }

    // -----------------------------------------------------------------------
    // Simulation – minimal loopback for UI development
    // -----------------------------------------------------------------------

    private void SimulateResponse(byte commandId, byte seqId, byte dstId)
    {
        var f = _faults;

        // PTSC commands (dst 0x20) share command bytes with EOA commands
        // (e.g. 0x09 = PTSC MotorSet vs MwirFovChange), so disambiguate by
        // destination first. The simulated response is sourced from 0x20.
        if (dstId == DeviceFramer.PtscDstId)
        {
            // MotorSet / StabSet are fire-and-forget motion commands — the
            // hardware does not echo telemetry for them, and synthesising a
            // response on every controller tick churns the UI bindings and
            // spikes the CPU. Acknowledge silently (no ResponseReceived).
            if (commandId == DeviceCommandId.Ptsc.MotorSet ||
                commandId == DeviceCommandId.Ptsc.StabSet)
                return;

            byte[] ptscPayload = commandId switch
            {
                DeviceCommandId.Ptsc.GeneralStatusGet => BuildSimGeneralStatus(f),
                DeviceCommandId.Ptsc.Ibit => BuildSimIbit(f),
                DeviceCommandId.Ptsc.MotorStatusGet => BuildSimMotorStatus(),
                DeviceCommandId.Ptsc.StabStatusGet => BuildSimStabStatus(),
                _ => [0x00],
            };
            var ptscFrame = new DeviceParsedFrame
            {
                ProtocolVersion = DeviceFramer.ProtocolVer,
                ErrorByte1 = 0x00,
                ErrorByte2 = 0x00,
                DestinationId = DeviceFramer.HostSrcId,
                SourceId = DeviceFramer.PtscDstId,
                SequenceId = seqId,
                CommandId = commandId,
                Payload = ptscPayload,
            };
            ResponseReceived?.Invoke(ptscFrame);
            return;
        }

        byte[] payload = commandId switch
        {
            DeviceCommandId.GeneralStatus => BuildSimGeneralStatus(f),
            DeviceCommandId.Ibit => BuildSimIbit(f),

            // NUC: echo back [nucType, 0x00, 0x00] as the ack so the VM's
            // response handler can flip the feedback label to "Started".
            DeviceCommandId.RgbStartNuc => new byte[] { _lastRgbNucType, 0x00, 0x00 },
            DeviceCommandId.NirStartNuc => new byte[] { _lastNirNucType, 0x00, 0x00 },

            // EOA-direct NIR FOV reply: 2-byte payload = [fov, reached?]
            0x84 =>
                f.FovNotReached
                    ? new byte[] { 0x01, 0x00 }   // WFOV, not reached
                    : new byte[] { 0x01, 0x01 },  // WFOV, reached

            // Legacy SIRS NIR/MWIR FOV (unchanged shape)
            DeviceCommandId.NirFovChange or DeviceCommandId.MwirFovChange =>
                f.FovNotReached
                    ? [0x01, 0x00, 0x00, 0x00]
                    : [0x01, 0x01, 0x00, 0x00],

            // EOA-direct NIR Focus reply (cmd 0x85) — 7-byte payload
            // shape per DeviceFocusResponse.Parse:
            //   [0] status: bits 0-1 = mode, bit 2 = pos reached,
            //               bits 3-4 = endstop
            //   [1..4] position (int32 LE, encoder ticks)
            //   [5..6] speed (uint16 LE)
            // Static placeholder values are echoed back so the VM
            // can show non-zero "Status:" and "Position:" feedback.
            // Also covers legacy cmd 0x0A for safety.
            0x85 or DeviceCommandId.NirFocusChange or DeviceCommandId.MwirFocusChange =>
                BuildSimFocus(f),

            DeviceCommandId.VideoSourceSelection =>
                [0x00, 0x00, 0x00],

            DeviceCommandId.LrfRangeMeasurement => BuildSimLrf(f),
            DeviceCommandId.LrfStatusQuery => BuildSimLrfStatus(f),
            DeviceCommandId.LrfOpticalCrosstalk => BuildSimLrfOpticalCrosstalk(),
            DeviceCommandId.LrfAlignmentPointer => BuildSimLrfPointerAck(),
            DeviceCommandId.LrfBaudRate => [0x00],     // ack only
            DeviceCommandId.LrfIdentification => BuildSimLrfIdentification(),
            DeviceCommandId.LrfDiagnostics => BuildSimLrfDiagnostics(f),
            DeviceCommandId.LrfResetErrorCounter => [0x00],     // ack only

            _ => [0x00]
        };

        // Pick the simulated response source based on which subsystem
        // would have answered. Motor and stab commands are forwarded
        // to PTSC, so their responses should appear to come from 0x20.
        byte simSrcId = commandId switch
        {
            0x84 or 0x85 => DeviceFramer.VisNirDstId,  // EOA-direct NIR FOV/Focus
            _ => DeviceFramer.SystemControllerDstId,
        };

        var simFrame = new DeviceParsedFrame
        {
            ProtocolVersion = DeviceFramer.ProtocolVer,
            ErrorByte1 = 0x00,
            ErrorByte2 = 0x00,
            // Response goes back from the responder to our host.
            DestinationId = DeviceFramer.HostSrcId,
            SourceId = simSrcId,
            SequenceId = seqId,
            CommandId = commandId,
            Payload = payload
        };

        ResponseReceived?.Invoke(simFrame);
    }

    private static byte[] BuildSimGeneralStatus(DeviceSimFaultConfig f)
    {
        // §3.3.1 General Status (SRS v2): 10-byte payload.
        //   p[0] state + health bits, p[1] ADC/brake, p[2] pan motor,
        //   p[3] tilt motor, p[4] control mode, p[5..6] bus voltage,
        //   p[7..9] reserve.
        var p = new byte[10];

        // p[0]: bits0-2 state; bit3 ready; bit4 IMU err; bit5 pan-enc err;
        //       bit6 tilt-enc err; bit7 power fail.
        byte state = (byte)(f.SystemStateError ? 0x03 : 0x00);   // 0=Op, 3=Error
        p[0] = state;
        if (!f.SystemStateError) p[0] |= 0x08;        // ready
        if (f.ImuError) p[0] |= 0x10;
        if (f.PowerFailure) p[0] |= 0x80;

        // p[1]: pan/tilt ADC, motor brake — all OK in sim.
        // p[2]/p[3]: pan/tilt motor status (bit2 = fault).
        if (f.PanMotorFault) p[2] |= 0x04;
        if (f.TiltMotorFault) p[3] |= 0x04;

        // p[4]: control mode — Position (2) on both axes => bits = 0b1010 = 0x0A.
        p[4] = 0x0A;

        // p[5..6]: bus voltage raw ADC (example ~28V scaled count).
        ushort busRaw = 2840;
        p[5] = (byte)(busRaw & 0xFF);
        p[6] = (byte)((busRaw >> 8) & 0xFF);

        return p;
    }

    /// <summary>
    /// Simulated §3.3 GET Stab Control response (52-byte payload). Uses the
    /// provisional field layout in DeviceStabStatusResponse so the GUI decode
    /// can be exercised without hardware.
    /// </summary>
    /// <summary>
    /// Simulated Motor Status response (DevicePanTiltResponse layout):
    ///   [0] panMode bit0, [1] tiltMode bit0,
    ///   [2..5] pan angle int32 LE (milli-deg), [6..9] tilt angle int32 LE,
    ///   [10..11] pan speed u16, [12..13] tilt speed u16.
    /// Emits a non-zero sample so "Poll Motor" shows visible feedback in sim.
    /// </summary>
    private byte[] BuildSimMotorStatus()
    {
        // §3.3.5 Motor Status (SRS v2 Table 19): 18-byte payload, echoing the
        // last commanded Motor Control state so a sim poll reflects what was sent.
        var p = new byte[18];
        p[0] = _simPanCtrl;
        p[1] = _simTiltCtrl;
        WriteI32(p, 2, _simPanPosTicks);
        WriteI32(p, 6, _simTiltPosTicks);
        WriteI32(p, 10, _simPanSpdTicks);
        WriteI32(p, 14, _simTiltSpdTicks);
        return p;
    }

    private byte[] BuildSimStabStatus()
    {
        var p = new byte[52];
        // Uptime (u32) — reuse a small rolling value.
        WriteI32(p, 0, Environment.TickCount / 1000);
        // IMU status (u16) bit0 = OK.
        p[4] = 0x01; p[5] = 0x00;
        WriteF32(p, 6, 0.01f);    // Accel X
        WriteF32(p, 10, -0.02f);  // Accel Y
        WriteF32(p, 14, 9.81f);   // Accel Z (gravity)
        WriteF32(p, 18, 0.0f);    // Gyro X
        WriteF32(p, 22, 0.0f);    // Gyro Y
        WriteF32(p, 26, 0.0f);    // Gyro Z
        WriteF32(p, 30, 32.5f);   // Temperature °C
        // Echo the last commanded EKF targets (sim state holds ticks → degrees).
        WriteF32(p, 34, (float)(_simEkfPitch / TicksPerDegree));   // EKF Pitch
        WriteF32(p, 38, (float)(_simEkfYaw / TicksPerDegree));     // EKF Yaw
        WriteI32(p, 42, 0);       // Pan nudge
        WriteI32(p, 46, 0);       // Tilt nudge
        // p[50..51] reserve
        return p;
    }

    // Sim-only IBIT progress state: advances on each Read poll to emulate the
    // C2000 running the self-test, then reports 100% with (optionally) faults.
    private int _simIbitProgress;
    private DateTime _lastMotionLog = DateTime.MinValue;

    // Sim-only mirror of the last commanded Motor Control state, so a simulated
    // Motor Status (0x08) poll reflects what was last sent.
    private byte _simPanCtrl = 0x04, _simTiltCtrl = 0x04;   // default Position, active
    private int _simPanPosTicks, _simTiltPosTicks, _simPanSpdTicks, _simTiltSpdTicks;

    private byte[] BuildSimIbit(DeviceSimFaultConfig f)
    {
        // §3.3.16 IBIT response (SRS v2): 11-byte payload.
        //   p[0] status byte, p[1] progress %, p[2] pan, p[3] tilt,
        //   p[4] sensor, p[5] pan-ext, p[6] tilt-ext, p[7..8] IMU, p[9..10] reserve.
        var p = new byte[11];

        _simIbitProgress = Math.Min(100, _simIbitProgress + 20);
        p[1] = (byte)_simIbitProgress;

        if (_simIbitProgress >= 100)
        {
            p[0] = f.IbitFailed ? (byte)0x04 : (byte)0x02;   // bit2 FAILED / bit1 PASSED
            if (f.IbitFailed)
            {
                p[2] = 0x01;   // Pan: motor-start fail (example)
                p[4] = 0x01;   // Sensor: power fail (example)
            }
            // IMU bytes use 1 = PASS.
            p[7] = f.IbitFailed ? (byte)0x00 : (byte)0xFF;
            p[8] = f.IbitFailed ? (byte)0x00 : (byte)0x03;
        }
        else
        {
            p[0] = 0x01;       // test in progress
            p[7] = 0xFF; p[8] = 0x03;
        }
        return p;
    }

    /// <summary>Reset sim IBIT progress when a new Full/Silent test starts.</summary>
    private void ResetSimIbit() => _simIbitProgress = 0;

    private static byte[] BuildSimLrf(DeviceSimFaultConfig f)
    {
        // Range reply payload (sync + cmd stripped): 18 bytes
        //   [0..3] Range1 float32 LE (m), [4..5] Sig1, [6..9] Range2, ...
        var p = new byte[18];
        if (f.LrfNoReturn)
            return p;   // all zero → ranges decode to 0 (no return)

        void WriteRange(int rangeIdx, float metres, ushort signalMs)
        {
            int off = rangeIdx * 6;
            WriteF32(p, off, metres);                    // IEEE-754 LE float32
            p[off + 4] = (byte)(signalMs & 0xFF);
            p[off + 5] = (byte)((signalMs >> 8) & 0xFF);
        }
        WriteRange(0, 1234.5f, 120);
        WriteRange(1, 2500.0f, 95);
        WriteRange(2, 0.0f, 0);
        return p;
    }

    // -----------------------------------------------------------------------

    private static void WriteI32(byte[] buf, int off, int v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteF32(byte[] buf, int off, float v)
        => WriteI32(buf, off, (int)BitConverter.SingleToUInt32Bits(v));

    private void Log(string src, string msg)
        => LogMessage?.Invoke($"{System.DateTime.Now:HH:mm:ss.fff}  {src,-4}  {msg}");

    public void Dispose()
    {
        Disconnect();
        _reconnect.Dispose();
        _txLock.Dispose();
    }

    // ── Simulation builders for extended LRF commands ────────────────

    /// <summary>
    /// Status-query payload (3 bytes + the SIRS framer adds its own
    /// metadata). When fault flags are injected we map them onto the
    /// nearest LRX status bits; otherwise we return all-clear.
    /// </summary>
    private static byte[] BuildSimLrfStatus(DeviceSimFaultConfig f)
    {
        byte sb1 = 0;
        byte sb2 = 0;
        byte sb3 = 0;

        // Map injected faults onto LRX status bits where reasonable.
        if (f.LrfFault) sb3 |= 0x10;     // ERR
        if (f.LrfNoReturn) sb3 |= 0x20;     // NT (no targets)
        // Always indicate "rebooted since last status" on first poll —
        // helps differentiate a cold-boot sim from a warm one.
        sb1 |= 0x20;

        return [sb1, sb2, sb3];
    }

    /// <summary>
    /// Crosstalk-check response. Returns a small (good) value in sim
    /// unless LRF fault injection is on, in which case we report a
    /// problematic 250 m crosstalk distance.
    /// </summary>
    private byte[] BuildSimLrfOpticalCrosstalk()
    {
        ushort range = _faults.LrfFault ? (ushort)250 : (ushort)45;
        return [(byte)(range & 0xFF), (byte)(range >> 8)];
    }

    private static byte[] BuildSimLrfPointerAck()
    {
        // Standard ack frame echoes the pointer state. The VM toggles
        // its own LrfPointerOn before the call, so just echo the
        // expected state. In real firmware the LRX will respond with
        // the actual current state.
        return [0x02];
    }

    /// <summary>
    /// Identification frame (Noptel §3.10) — 70 payload bytes with
    /// CR/LF separators preserved exactly as the LRX would emit them.
    /// </summary>
    private static byte[] BuildSimLrfIdentification()
    {
        var p = new byte[70];

        // Bytes 0-14: device ID string
        var devId = System.Text.Encoding.ASCII.GetBytes("LRX-25A");
        System.Array.Copy(devId, 0, p, 0, devId.Length);

        p[15] = 0x0D; p[16] = 0x0A;     // CR LF

        // Bytes 17-31: reserved/additional info (left blank in sim)
        p[32] = 0x0D; p[33] = 0x0A;     // CR LF

        // Bytes 34-43: serial number
        var serial = System.Text.Encoding.ASCII.GetBytes("SN12345678");
        System.Array.Copy(serial, 0, p, 34, serial.Length);

        p[44] = 0x0D; p[45] = 0x0A;     // CR LF

        // Bytes 46-47: firmware version (LSB, MSB) — 2.6 -> 0x0206
        p[46] = 0x06;
        p[47] = 0x02;

        // Bytes 48-49: electronics / optics type
        p[48] = 0xB1;
        p[49] = 0xB0;

        // Bytes 50-57: firmware date YY-MM-DD
        var date = System.Text.Encoding.ASCII.GetBytes("24-06-26");
        System.Array.Copy(date, 0, p, 50, date.Length);

        p[58] = 0x0D; p[59] = 0x0A;     // CR LF

        // Bytes 60-67: firmware time HH:MM:SS
        var time = System.Text.Encoding.ASCII.GetBytes("12:34:56");
        System.Array.Copy(time, 0, p, 60, time.Length);

        p[68] = 0x0D; p[69] = 0x0A;     // CR LF

        return p;
    }

    /// <summary>
    /// Diagnostic-data frame (Noptel §3.11) — 37 payload bytes with
    /// realistic operating values. Battery and pulse counter wander
    /// slightly each poll so the UI clearly shows live updates.
    /// </summary>
    private byte[] BuildSimLrfDiagnostics(DeviceSimFaultConfig f)
    {
        var p = new byte[37];
        var rnd = new System.Random();

        // Bytes 0-7: opaque diagnostic data (left zero)

        // Bytes 8-13: target distances
        ushort t1 = (ushort)rnd.Next(800, 1500);
        ushort t2 = (ushort)rnd.Next(1500, 3000);
        ushort t3 = 0;
        p[8] = (byte)(t1 & 0xFF); p[9] = (byte)(t1 >> 8);
        p[10] = (byte)(t2 & 0xFF); p[11] = (byte)(t2 >> 8);
        p[12] = (byte)(t3 & 0xFF); p[13] = (byte)(t3 >> 8);

        // Bytes 14-16: magnitudes
        p[14] = 0x42; p[15] = 0x28; p[16] = 0x00;

        // Byte 17: not in use

        // Bytes 18-19: battery mV (12.0 V)
        ushort batt = (ushort)(12000 + rnd.Next(-200, 200));
        p[18] = (byte)(batt & 0xFF); p[19] = (byte)(batt >> 8);

        // Bytes 20-21: power consumption mW (3.6 W during measure)
        ushort pwr = 3600;
        p[20] = (byte)(pwr & 0xFF); p[21] = (byte)(pwr >> 8);

        // Bytes 22-23: IO voltage mV (3.3 V)
        ushort io = 3300;
        p[22] = (byte)(io & 0xFF); p[23] = (byte)(io >> 8);

        // Bytes 24-25: detector bias in 0.01 V (so 60.00 V → 6000)
        ushort det = 6000;
        p[24] = (byte)(det & 0xFF); p[25] = (byte)(det >> 8);

        // Bytes 26-27: +5 V rail mV
        ushort fv = 5050;
        p[26] = (byte)(fv & 0xFF); p[27] = (byte)(fv >> 8);

        // Bytes 28-29: RX temperature in 0.01 °C (so 35.50 °C → 3550)
        short rxt = (short)(3550 + rnd.Next(-50, 50));
        p[28] = (byte)(rxt & 0xFF); p[29] = (byte)((rxt >> 8) & 0xFF);

        // Bytes 30-32: status bytes
        p[30] = 0x00;
        p[31] = 0x00;
        p[32] = (byte)(f.LrfFault ? 0x10 : 0x00);

        // Bytes 33-35: pulse counter in millions (24-bit LE)
        uint pulses = (uint)rnd.Next(150, 200);
        p[33] = (byte)(pulses & 0xFF);
        p[34] = (byte)((pulses >> 8) & 0xFF);
        p[35] = (byte)((pulses >> 16) & 0xFF);

        // Byte 36: RS error counter
        p[36] = 0;

        return p;
    }

    // ── Simulated focus motor state (time-based) ────────────────────
    //
    // Models real hardware: the motor is a continuously-moving device.
    // Speed mode sets a signed VELOCITY in ticks/second; the motor
    // then accumulates ticks over wall-clock time. Get is purely a
    // read — it never changes velocity or position. Manual teleports
    // and stops the motor.
    //
    // Position is recomputed lazily via TickSimMotor() before each
    // capture/build call rather than via a background timer, which
    // keeps the service free of long-lived threading and disposal
    // concerns. The math gives the same result either way.
    //
    // Mode semantics (TX side, per FocusModeOpts):
    //   0 = Get        — read current position (no side effects)
    //   1 = Manual     — teleport to requestedPos + stop motor
    //   2 = Speed      — set continuous velocity (signed ticks/sec)
    //                    speed=0 stops the motor
    //   3 = One-Shot AF — no-op in sim
    //   4 = Infinity   — teleport to max + stop motor
    private double _simFocusPosition;        // ticks (kept as double so partial
                                             // ticks accumulate cleanly between updates)
    private short _simMotorVelocity;        // signed ticks per second
    private DateTime _simMotorLastTick = DateTime.UtcNow;

    private const int SimFocusMin = 0;
    private const int SimFocusMax = 100_000;

    /// <summary>
    /// Advance the simulated motor position by velocity × elapsed-wall-time.
    /// Called both when the GUI sends a new command (to "settle" the motor's
    /// position at the moment of the request) AND when the sim builds its
    /// response (so Get reads back the latest position).
    ///
    /// If the position hits a hard stop the velocity is zeroed — same as a
    /// real motor running into an end-stop.
    /// </summary>
    private void TickSimMotor()
    {
        DateTime now = DateTime.UtcNow;
        double elapsedS = (now - _simMotorLastTick).TotalSeconds;
        _simMotorLastTick = now;

        if (_simMotorVelocity == 0 || elapsedS <= 0) return;

        _simFocusPosition += _simMotorVelocity * elapsedS;

        if (_simFocusPosition <= SimFocusMin)
        {
            _simFocusPosition = SimFocusMin;
            _simMotorVelocity = 0;   // hit lower end-stop
        }
        else if (_simFocusPosition >= SimFocusMax)
        {
            _simFocusPosition = SimFocusMax;
            _simMotorVelocity = 0;   // hit upper end-stop
        }
    }

    /// <summary>
    /// Called from each NIR Focus TX in simulation mode. Translates the
    /// requested mode (per Focus_Commands_Updated.txt §"Focus Control Byte")
    /// into a state change on the virtual motor.
    ///
    /// Mode mapping (matches FocusModeOpts):
    ///   0 = Get          — read only, no side effects
    ///   1 = Set Position — teleport to requestedPos, stop motor
    ///   2 = Set Speed    — continuous motion at the signed speed
    ///   3 = Infinity     — teleport to far end (SimFocusMax), stop motor
    ///   4 = Stop         — halt the motor without teleporting (Set Speed = 0)
    /// </summary>
    private void CaptureFocusRequest(byte mode, int requestedPos, int signedSpeed)
    {
        TickSimMotor();   // settle position to "now" before applying the new command

        // Motor velocity is stored as a short to mirror the previous behaviour
        // (sub-second tick accumulation). Clamp the int command into that range.
        short velClamped = (short)Math.Clamp(signedSpeed, short.MinValue, short.MaxValue);

        switch (mode)
        {
            case 0:  // Get — no side effects
                break;
            case 1:  // Set Position — teleport + stop motor
                _simFocusPosition = requestedPos;
                _simMotorVelocity = 0;
                break;
            case 2:  // Set Speed — continuous motion at signed velocity
                _simMotorVelocity = velClamped;
                break;
            case 3:  // Infinity — teleport to max + stop motor
                _simFocusPosition = SimFocusMax;
                _simMotorVelocity = 0;
                break;
            case 4:  // Stop control — halt the motor in place
                _simMotorVelocity = 0;
                break;
        }

        // Hard clamp on any teleport
        if (_simFocusPosition < SimFocusMin) _simFocusPosition = SimFocusMin;
        if (_simFocusPosition > SimFocusMax) _simFocusPosition = SimFocusMax;
    }

    /// <summary>
    /// Builds a 10-byte Focus response payload for simulation mode.
    /// Format matches <see cref="DeviceFocusResponse.Parse"/>:
    ///   [0]    Focus Control Byte: bits 0:2 = mode (echo of last command)
    ///   [1]    Focus Status Byte: bit0 = control active,
    ///                             bit1 = position reached,
    ///                             bits 2:3 = movement freedom
    ///                                        (0=Free, 1=Min, 2=Max, 3=Blocked)
    ///   [2..5] Position (int32 LE)
    ///   [6..9] Speed    (int32 LE, signed)
    /// </summary>
    private byte[] BuildSimFocus(DeviceSimFaultConfig f)
    {
        TickSimMotor();   // catch up to the current time before reporting

        int pos = (int)_simFocusPosition;
        int spd = _simMotorVelocity;

        // ── Control byte: reported mode ─────────────────────────────
        //   Moving     → "Set Speed" (2)
        //   At limit   → "Stop" (4)
        //   Stationary → "Get" feedback (0)
        byte mode;
        if (spd != 0) mode = 2;
        else if (pos <= SimFocusMin || pos >= SimFocusMax) mode = 4;
        else mode = 0;

        // ── Status byte ─────────────────────────────────────────────
        bool controlActive = spd != 0;
        bool posReached = spd == 0;       // motor settled
        byte freedom =
            pos <= SimFocusMin ? (byte)1 :   // Min reached
            pos >= SimFocusMax ? (byte)2 :   // Max reached
            (byte)0;                          // Free

        byte status = (byte)(
            (controlActive ? 0x01 : 0x00) |
            (posReached ? 0x02 : 0x00) |
            ((freedom & 0x03) << 2));

        return new byte[]
        {
            mode,
            status,
            (byte)( pos        & 0xFF),
            (byte)((pos >> 8)  & 0xFF),
            (byte)((pos >> 16) & 0xFF),
            (byte)((pos >> 24) & 0xFF),
            (byte)( spd        & 0xFF),
            (byte)((spd >> 8)  & 0xFF),
            (byte)((spd >> 16) & 0xFF),
            (byte)((spd >> 24) & 0xFF),
        };
    }
}