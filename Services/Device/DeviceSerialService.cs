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

            _rxBuf.Clear();
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
        _rxBuf.Clear();
        StatusChanged?.Invoke("Disconnected");
        Log("SYS", "Disconnected.");
    }

    // -----------------------------------------------------------------------
    // Send helpers – one per SRS §3.3 command
    // -----------------------------------------------------------------------

    /// <summary>§3.3.1  General Status Command</summary>
    public Task SendGeneralStatusAsync()
        => SendAsync(DeviceCommandId.GeneralStatus, [0x55]);

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
    public Task SendPanTiltAsync(byte panMode, byte tiltMode,
                                 int panAngleMilli, int tiltAngleMilli,
                                 ushort panSpeed, ushort tiltSpeed)
        => SendAsync(DeviceCommandId.PanTiltMotorControl,
        [
            (byte)(panMode  & 0x03),
            (byte)(tiltMode & 0x03),
            (byte)(panAngleMilli & 0xFF),  (byte)((panAngleMilli  >> 8)  & 0xFF),
            (byte)((panAngleMilli  >> 16) & 0xFF), (byte)((panAngleMilli  >> 24) & 0xFF),
            (byte)(tiltAngleMilli & 0xFF), (byte)((tiltAngleMilli >> 8)  & 0xFF),
            (byte)((tiltAngleMilli >> 16) & 0xFF), (byte)((tiltAngleMilli >> 24) & 0xFF),
            (byte)(panSpeed  & 0xFF), (byte)(panSpeed  >> 8),
            (byte)(tiltSpeed & 0xFF), (byte)(tiltSpeed >> 8),
            0x00, 0x00   // reserve
        ], DeviceFramer.PtscDstId);

    /// <summary>
    /// §3.3.6  Stab Control — also sent DIRECTLY to PTSC. Same
    /// rationale as <see cref="SendPanTiltAsync"/> above.
    /// </summary>
    public Task SendStabControlAsync(byte stabMode)
        => SendAsync(DeviceCommandId.StabControl,
        [(byte)(stabMode & 0x03), 0x00, 0x00],
        DeviceFramer.PtscDstId);

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
    /// manual-exposure value (IEEE-754 float, microseconds).
    /// Payload (8 bytes):
    ///   [0]    1=Manual, 0=Auto
    ///   [1]    Analogue gain (default 4)
    ///   [2..5] Manual exposure as IEEE-754 single-precision, little-endian
    ///   [6][7] 0x00 0x00 (reserve)
    /// </summary>
    public Task SendNirExposureAsync(bool manual, byte gain, float exposureUs)
        => SendAsync(DeviceCommandId.NirExposure, BuildExposurePayload(manual, gain, exposureUs));

    /// <summary>
    /// VIS Exposure — same payload shape as NIR exposure; addresses
    /// the visible-spectrum (RGB) sensor's auto-exposure controller.
    /// </summary>
    public Task SendVisExposureAsync(bool manual, byte gain, float exposureUs)
        => SendAsync(DeviceCommandId.VisExposure, BuildExposurePayload(manual, gain, exposureUs));

    private static byte[] BuildExposurePayload(bool manual, byte gain, float exposureUs)
    {
        // IEEE-754 single-precision, little-endian on x64 Windows + Linux.
        // (BitConverter.IsLittleEndian == true on every supported host.)
        byte[] expBytes = BitConverter.GetBytes(exposureUs);
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
            simSirsCmdId: DeviceCommandId.LrfRangeMeasurement);

    /// <summary>
    /// LRF Stop CMM — raw Noptel passthrough. Noptel "break" command 0xC6
    /// ends an active continuous measurement. Verify against ICD.
    /// </summary>
    public Task SendLrfStopCmmAsync()
        => SendLrfNoptelAsync(0xC6, EmptyPayload, DeviceCommandId.LrfStopCmm);

    /// <summary>
    /// LRF Measurement Range — raw Noptel passthrough.
    /// setValues=true  -> Noptel SET range command (0xCA + min/max LE pairs)
    /// setValues=false -> Noptel GET range command (0xCD, no params)
    /// Both command bytes are best-guess; verify against Noptel LRX ICD.
    /// </summary>
    public Task SendLrfMeasurementRangeAsync(bool setValues, ushort minMeters, ushort maxMeters)
    {
        if (setValues)
        {
            byte[] p =
            {
                (byte)(minMeters & 0xFF), (byte)(minMeters >> 8),
                (byte)(maxMeters & 0xFF), (byte)(maxMeters >> 8),
            };
            return SendLrfNoptelAsync(0xCA, p, DeviceCommandId.LrfMeasurementRange);
        }
        return SendLrfNoptelAsync(0xCD, EmptyPayload, DeviceCommandId.LrfMeasurementRange);
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
    private async Task SendLrfNoptelAsync(byte cmdId, byte[] payload, byte simSirsCmdId)
    {
        await _txLock.WaitAsync();
        try
        {
            byte seq = _seqId++;
            byte[] packet = NoptelChecksum.BuildPacket(cmdId, payload);

            if (_simulationMode)
            {
                Log("TX", $"SIM NOPTEL LRF [{BitConverter.ToString(packet)}]");
                RawFrame?.Invoke(packet, true);
                SimulateResponse(simSirsCmdId, seq);
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

    /// <summary>§3.3.16.1  IBIT Command</summary>
    public Task SendIbitAsync()
        => SendAsync(DeviceCommandId.Ibit, [0x55]);

    // ── Maintenance: Start NUC on each sensor ─────────────────────────
    // Payload format (per the May 2026 NUC spec):
    //   [0] NUC type — 1 = 1-Point NUC, 2 = 2-Point NUC
    //   [1] reserve  (0x00)
    //   [2] reserve  (0x00)
    // Length byte on the wire = 0x03. Routed to the System Controller
    // (default dst 0x10), which forwards to the appropriate sensor.

    /// <summary>Start NUC on the RGB sensor. Maintenance only.</summary>
    public Task SendRgbStartNucAsync(byte nucType)
        => SendAsync(DeviceCommandId.RgbStartNuc, [nucType, 0x00, 0x00]);

    /// <summary>Start NUC on the NIR sensor. Maintenance only.</summary>
    public Task SendNirStartNucAsync(byte nucType)
        => SendAsync(DeviceCommandId.NirStartNuc, [nucType, 0x00, 0x00]);

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

            if (_simulationMode)
            {
                Log("TX", $"SIM CMD 0x{commandId:X2} seq={seq} [{BitConverter.ToString(payload)}]");
                RawFrame?.Invoke(frame, true);
                SimulateResponse(commandId, seq);
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
                    RawFrame?.Invoke(frame, true);
                    Log("TX", $"CMD 0x{commandId:X2} seq={seq} attempt={attempt}");
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
            // Bytes arrive raw from whichever transport (UART chunk or UDP datagram);
            // the framer handles partial / multi-frame buffers either way.
            RawFrame?.Invoke(buf, false);

            foreach (var frame in DeviceFramer.FeedBytes(buf, _rxBuf))
            {
                Log("RX", $"RSP 0x{frame.CommandId:X2} seq={frame.SequenceId}" +
                           (frame.HasError ? $" ERR=0x{frame.ErrorCode:X4}" : ""));
                ResponseReceived?.Invoke(frame);
            }
        }
        catch (Exception ex)
        {
            Log("ERR", $"RX error: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Simulation – minimal loopback for UI development
    // -----------------------------------------------------------------------

    private void SimulateResponse(byte commandId, byte seqId)
    {
        var f = _faults;

        byte[] payload = commandId switch
        {
            DeviceCommandId.GeneralStatus => BuildSimGeneralStatus(f),
            DeviceCommandId.Ibit => BuildSimIbit(f),

            // NUC: echo back [nucType, 0x00, 0x00] as the ack so the VM's
            // response handler can flip the feedback label to "Started".
            DeviceCommandId.RgbStartNuc or DeviceCommandId.NirStartNuc =>
                new byte[] { 0x02, 0x00, 0x00 },

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

            DeviceCommandId.PanTiltMotorControl => new byte[16],

            DeviceCommandId.StabControl =>
                [0x00, 0x00, 0x00],

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
            DeviceCommandId.PanTiltMotorControl => DeviceFramer.PtscDstId,
            DeviceCommandId.StabControl => DeviceFramer.PtscDstId,
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
        // §3.3.1.2  Length = 0x1D (29 payload bytes, indices 0-28)
        var p = new byte[29];

        // p[0] General Status Byte: bit0=power, bit1=fibre, bit2=IMU
        if (f.PowerFailure) p[0] |= (1 << 0);
        if (f.FibreError) p[0] |= (1 << 1);
        if (f.ImuError) p[0] |= (1 << 2);

        // p[1] Laser Status: bit0=LRF, bit1=LPI
        if (f.LrfNotSafe) p[1] |= (1 << 0);
        if (f.LpiNotSafe) p[1] |= (1 << 1);

        // p[2] System Operation Status: bits0:2=state
        p[2] = (byte)(f.SystemStateError ? 0x03 : 0x00);  // 0=Op, 3=Error

        // p[3..6] Operational hours = 0

        // p[7] Humidity/Pressure/Temp status
        if (f.HumidityTriggered) p[7] |= (1 << 0);
        if (f.PressureTriggered) p[7] |= (1 << 1);
        if (f.TemperatureOutOfRange) p[7] |= (1 << 2);

        // p[8..9]  Humidity % = 0
        // p[10..13] Pressure psi = 0
        // p[14..15] Temperature = 0
        // p[16] MWIR NUC mode = 0

        // p[17] MWIR calibration: bit0=cooler, bit4=motor fault
        if (f.MwirCoolerBusy) p[17] |= (1 << 0);
        if (f.MwirMotorFault) p[17] |= (1 << 4);

        // p[18] NIR calibration: bit3=motor fault
        if (f.NirMotorFault) p[18] |= (1 << 3);

        // p[19] Pan motor: bit1=fault
        if (f.PanMotorFault) p[19] |= (1 << 1);

        // p[20] Tilt motor: bit1=fault
        if (f.TiltMotorFault) p[20] |= (1 << 1);

        // p[21..22] Last Error = 0
        // p[23] LRF Status: bit0=fault
        if (f.LrfFault) p[23] |= (1 << 0);

        // p[24] LPI Status: bit0=fault
        if (f.LpiFault) p[24] |= (1 << 0);

        return p;
    }

    private static byte[] BuildSimIbit(DeviceSimFaultConfig f)
    {
        // §3.3.16.2  Length = 0x1E (30 payload bytes)
        var p = new byte[30];

        if (!f.IbitFailed) return p;  // all zeros = all pass

        // p[0] General Status: power/fibre/IMU
        p[0] = 0b00000111;   // Power FAIL | Fibre ERR | IMU ERR

        // p[9..10] MWIR ZG1 fails
        p[9] = 0b00111111;  // All 6 ZG1 motor tests fail

        // p[11..12] MWIR ZG2 fails
        p[11] = 0b01111111;  // All 7 ZG2 motor tests fail

        // p[17] Pan motor fails, p[18] Tilt motor fails
        p[17] = 0b00011111;  // Pan: all 5 tests fail
        p[18] = 0b01111111;  // Tilt: all 7 tests fail

        // p[22] Humidity OOR, p[25] Pressure OOR, p[30] Temp OOR
        p[22] = 0x01;
        if (p.Length > 25) p[25] = 0x01;

        return p;
    }

    private static byte[] BuildSimLrf(DeviceSimFaultConfig f)
    {
        // §3.3.11.2  Length = 0x15 (21 payload bytes)
        var p = new byte[21];
        if (f.LrfNoReturn)
        {
            // 0xFFFF = maximum out-of-range value for each range reading
            for (int i = 0; i < 3; i++)
            {
                p[1 + i * 6] = 0xFF;
                p[2 + i * 6] = 0xFF;
            }
        }
        return p;
    }

    // -----------------------------------------------------------------------

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