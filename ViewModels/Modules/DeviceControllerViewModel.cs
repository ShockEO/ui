using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI.Models.App;
using ShockUI.Models.Device;
using ShockUI.Services.App;
using ShockUI.Services.Device;

using Avalonia.Controls;
using ShockUI.Views;

namespace ShockUI.ViewModels.Modules;

public sealed partial class DeviceControllerViewModel : ViewModelBase
{
    private readonly DeviceSerialService _service;

    /// <summary>Xbox controller polling service. Same instance shape as the PanTilt module — but here mapped to the SIRS §3.3.5 PanTilt command.</summary>
    private readonly XboxControllerService _controller = new();

    [ObservableProperty] private XboxControllerState? controllerState;
    [ObservableProperty] private bool showControllerVisual = true;
    [ObservableProperty] private bool isPhysicalControllerConnected;

    public bool IsControllerInteractionEnabled =>
        IsConnected && !IsPhysicalControllerConnected;
    partial void OnIsPhysicalControllerConnectedChanged(bool value)
        => OnPropertyChanged(nameof(IsControllerInteractionEnabled));

    /// <summary>Trigger-driven speed multiplier: LT=0.1×, RT=2×, neither=1×.</summary>
    private double _speedMultiplier = 1.0;

    /// <summary>Max raw motor speed value when stick is fully deflected. SIRS uses ushort for speed so this is the magnitude only.</summary>
    private const ushort MaxStickSpeed = 1000;

    /// <summary>True if last frame had non-zero stick — for explicit stop on return-to-centre.</summary>
    private bool _wasMoving;

    /// <summary>Throttle: ≥50 ms between motor frames so we don't flood SIRS.</summary>
    private const int MotorTxIntervalMs = 50;
    private DateTime _lastMotorTx = DateTime.MinValue;


    // -----------------------------------------------------------------------
    // Connection
    // -----------------------------------------------------------------------
    [ObservableProperty] private ObservableCollection<string> availablePorts = [];
    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private bool isSimulationMode = false;
    [ObservableProperty] private bool isConnecting;
    [ObservableProperty] private string statusText = "Disconnected";
    [ObservableProperty] private string moduleTitle = "System Controller";

    // -----------------------------------------------------------------------
    // General Status (§3.3.1)
    // -----------------------------------------------------------------------
    [ObservableProperty] private string systemState = "—";
    [ObservableProperty] private uint operationalHours;
    [ObservableProperty] private bool powerFailure;
    [ObservableProperty] private bool fibreError;
    [ObservableProperty] private bool imuError;
    [ObservableProperty] private bool lrfNotSafe;
    [ObservableProperty] private bool lpiNotSafe;
    [ObservableProperty] private ushort humidityPercent;
    [ObservableProperty] private uint pressurePsi;
    [ObservableProperty] private short temperatureCelsius;
    [ObservableProperty] private bool humidityTriggered;
    [ObservableProperty] private bool pressureTriggered;
    [ObservableProperty] private bool tempOutOfRange;
    [ObservableProperty] private string mwirNucMode = "—";
    [ObservableProperty] private bool mwirCoolerBusy;
    [ObservableProperty] private bool mwirMotorFault;
    [ObservableProperty] private bool nirMotorFault;
    [ObservableProperty] private bool panMotorFault;
    [ObservableProperty] private bool tiltMotorFault;
    [ObservableProperty] private ushort lastError;
    [ObservableProperty] private bool lrfFault;
    [ObservableProperty] private bool lpiFault;

    // -----------------------------------------------------------------------
    // Boresight (§3.3.2)
    // -----------------------------------------------------------------------
    [ObservableProperty] private bool boresightNirRight;
    [ObservableProperty] private bool boresightNirDown;
    [ObservableProperty] private bool boresightSave;
    [ObservableProperty] private ushort boresightNirPixels;
    [ObservableProperty] private bool boresightRetRight;
    [ObservableProperty] private bool boresightRetDown;
    [ObservableProperty] private ushort boresightRetPixels;

    // -----------------------------------------------------------------------
    // Pan / Tilt (§3.3.5)
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte panMode = 1;  // 1=position, 2=speed
    [ObservableProperty] private byte tiltMode = 1;
    [ObservableProperty] private double panAngleDeg;
    [ObservableProperty] private double tiltAngleDeg;
    [ObservableProperty] private ushort panSpeed = 100;
    [ObservableProperty] private ushort tiltSpeed = 100;
    [ObservableProperty] private double panFeedbackDeg;
    [ObservableProperty] private double tiltFeedbackDeg;

    // -----------------------------------------------------------------------
    // Stabilisation (§3.3.6)
    // -----------------------------------------------------------------------
    [ObservableProperty] private bool stabActive;
    [ObservableProperty] private byte stabCommandMode;   // 0=activate,1=deactivate,2=get

    // -----------------------------------------------------------------------
    // Video Source (§3.3.7)
    // -----------------------------------------------------------------------
    [ObservableProperty] private bool sdi1IsNir;
    [ObservableProperty] private bool sdi2IsNir;

    // -----------------------------------------------------------------------
    // FOV (§3.3.8)
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte nirFovCommand = 1; // 0=get,1=WFOV..4=VNFOV
    [ObservableProperty] private byte mwirFovCommand = 1;
    [ObservableProperty] private string nirFovFeedback = "—";
    [ObservableProperty] private bool nirFovReached;
    [ObservableProperty] private string mwirFovFeedback = "—";
    [ObservableProperty] private bool mwirFovReached;

    // -----------------------------------------------------------------------
    // Focus (§3.3.9)
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte nirFocusMode;      // 0=get,1=manual,2=speed,3=AF,4=infinity
    [ObservableProperty] private int nirFocusPosition;
    [ObservableProperty] private int nirFocusSpeed = 0;   // signed int32: negative = reverse motor direction
    [ObservableProperty] private string nirFocusFeedback = "—";
    [ObservableProperty] private int nirFocusPosFeedback;

    [ObservableProperty] private byte mwirFocusMode;
    [ObservableProperty] private int mwirFocusPosition;
    [ObservableProperty] private int mwirFocusSpeed = 100;
    [ObservableProperty] private string mwirFocusFeedback = "—";
    [ObservableProperty] private int mwirFocusPosFeedback;

    // -----------------------------------------------------------------------
    // MWIR Image Enhancement (§3.3.10)
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte mwirEdgeMode;       // 0=off,1-3=sharpness
    [ObservableProperty] private byte mwirContrastMode;   // 0=none,1=global,2=LACE
    [ObservableProperty] private byte mwirNucRequest;     // 0=1pt,1=2pt,2=3pt,3=factory
    [ObservableProperty] private bool mwirDeadPixelEnable = true;
    [ObservableProperty] private bool mwirNoiseSuppEnable = true;
    [ObservableProperty] private bool mwirUpscaleEnable = true;
    [ObservableProperty] private byte mwirPolarityMode;   // 0=white hot,1=black hot,2=colour

    // -----------------------------------------------------------------------
    // NIR Image Enhancement (§3.3.10)
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte nirEdgeMode;
    [ObservableProperty] private byte nirContrastMode;
    [ObservableProperty] private byte nirColourMatrix;    // 0=factory,1=AWB
    [ObservableProperty] private bool nirDeadPixelEnable = true;
    [ObservableProperty] private bool nirNoiseSuppEnable = true;

    // RGB (visible-spectrum) image-enhancement state. Mirrors the NIR
    // controls; same enum codes (Off/Low/Med/High for edge, None/Low/
    // Med/High for contrast).
    [ObservableProperty] private byte rgbEdgeMode;
    [ObservableProperty] private byte rgbContrastMode;
    [ObservableProperty] private bool rgbDeadPixelEnable = true;
    [ObservableProperty] private bool rgbNoiseSuppEnable = true;

    // Exposure §3.3.x — NIR and VIS sensors get auto/manual + gain +
    // manual exposure value (microseconds, IEEE-754 float on the wire).
    [ObservableProperty] private bool nirExposureManual;
    [ObservableProperty] private byte nirExposureGain = 4;
    [ObservableProperty] private float nirExposureValue = 30.0f;
    [ObservableProperty] private bool visExposureManual;
    [ObservableProperty] private byte visExposureGain = 4;
    [ObservableProperty] private float visExposureValue = 30.0f;

    // -----------------------------------------------------------------------
    // LRF (§3.3.11 – §3.3.13)
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte lrfMeasurementMode; // dropdown index; mapped via LrfModeBytes
    [ObservableProperty] private uint lrfRange1;
    [ObservableProperty] private uint lrfRange2;
    [ObservableProperty] private uint lrfRange3;
    [ObservableProperty] private ushort lrfMinRange = 50;
    [ObservableProperty] private ushort lrfMaxRange = 20000;
    [ObservableProperty] private bool lrfSetRange;

    // -----------------------------------------------------------------------
    // Brightness & Contrast (§3.3.14)
    // -----------------------------------------------------------------------
    [ObservableProperty] private ushort nirBrightness = 128;
    [ObservableProperty] private ushort nirContrast = 128;
    [ObservableProperty] private ushort mwirBrightness = 128;
    [ObservableProperty] private ushort mwirContrast = 128;
    [ObservableProperty] private bool nirBcSet;
    [ObservableProperty] private bool mwirBcSet;

    // -----------------------------------------------------------------------
    // ── MWIR feature flag ────────────────────────────────────────────
    // MWIR sensor isn't ready in the field yet, so all MWIR-related UI
    // is gated on this single property. Flip it to true here (or wire it
    // to a config setting) once the MWIR module is available.
    public bool IsMwirEnabled => false;

    // Symbology (§3.3.15)
    // -----------------------------------------------------------------------
    // System has two independent video streams; symbology overlay
    // (reticle, crosshairs, OSD elements) can be enabled per-stream.
    [ObservableProperty] private bool stream1SymbologyOn;
    [ObservableProperty] private bool stream2SymbologyOn;

    // -----------------------------------------------------------------------
    // IBIT (§3.3.16)
    // -----------------------------------------------------------------------
    [ObservableProperty] private string ibitGeneralStatus = "—";

    /// <summary>
    /// Timestamp of the most recently received General Status frame, or
    /// null if the view has never received one. Drives the "Last polled"
    /// indicator in the General Status sub-card so the operator knows
    /// how fresh the displayed health flags actually are.
    /// </summary>
    [ObservableProperty] private DateTime? lastGeneralStatusPolled;

    /// <summary>Human-readable "Last polled" label string for the UI.</summary>
    public string LastGeneralStatusPolledText
        => LastGeneralStatusPolled is { } t
            ? $"Last polled: {t:HH:mm:ss}"
            : "Last polled: —";

    partial void OnLastGeneralStatusPolledChanged(DateTime? value)
        => OnPropertyChanged(nameof(LastGeneralStatusPolledText));

    [ObservableProperty] private string ibitMwirZg1 = "—";
    [ObservableProperty] private string ibitMwirZg2 = "—";
    [ObservableProperty] private string ibitMwirSolenoid = "—";
    [ObservableProperty] private string ibitMwirTec = "—";
    [ObservableProperty] private string ibitNirZg1 = "—";
    [ObservableProperty] private string ibitNirZg2 = "—";
    [ObservableProperty] private string ibitPanMotor = "—";
    [ObservableProperty] private string ibitTiltMotor = "—";
    [ObservableProperty] private string ibitLrfStatus = "—";

    // ── Extended LRF state (Noptel LRX ICD O50090DE) ─────────────────
    // Driven by the LRX Status, Diagnostics, Identification, and
    // Optical-Crosstalk responses forwarded by the System Controller.

    // Status query (Noptel §3.4) — raw status bytes + ergonomic bool
    // accessors mirrored from DeviceLrfStatusResponse.
    [ObservableProperty] private byte lrfStatusByte1;
    [ObservableProperty] private byte lrfStatusByte2;
    [ObservableProperty] private byte lrfStatusByte3;

    [ObservableProperty] private bool lrfGeneralProblems;
    [ObservableProperty] private bool lrfTransmitterProblem;
    [ObservableProperty] private bool lrfRebooted;
    [ObservableProperty] private bool lrfNotReady;
    [ObservableProperty] private bool lrfReceiverProblem;
    [ObservableProperty] private bool lrfLaserPowerProblem;

    [ObservableProperty] private bool lrfHighVoltageOutOfRange;
    [ObservableProperty] private bool lrfDcDcOutOfRange;
    [ObservableProperty] private bool lrfMemoryProblem;
    [ObservableProperty] private bool lrfLowBattery;
    [ObservableProperty] private bool lrfCommunicationProblem;

    [ObservableProperty] private bool lrfMultipleTargets;
    [ObservableProperty] private bool lrfNoTargets;
    [ObservableProperty] private bool lrfErrorReported;
    [ObservableProperty] private bool lrfTransmitterTiming;

    [ObservableProperty] private DateTime? lastLrfStatusPolled;
    public string LastLrfStatusPolledText
        => LastLrfStatusPolled is { } t ? $"Last polled: {t:HH:mm:ss}" : "Last polled: —";
    partial void OnLastLrfStatusPolledChanged(DateTime? value)
        => OnPropertyChanged(nameof(LastLrfStatusPolledText));

    // Optical-crosstalk (Noptel §3.3)
    [ObservableProperty] private ushort lrfOpticalCrosstalkM;
    [ObservableProperty] private bool lrfHasOpticalCrosstalkResult;

    // Alignment pointer (Noptel §3.5)
    [ObservableProperty] private bool lrfPointerOn;

    // Identification (Noptel §3.10)
    [ObservableProperty] private string lrfDeviceId = "—";
    [ObservableProperty] private string lrfFirmwareVersion = "—";
    [ObservableProperty] private string lrfSerialNumber = "—";
    [ObservableProperty] private byte lrfElectronicsType;
    [ObservableProperty] private byte lrfOpticsType;
    [ObservableProperty] private string lrfFirmwareDate = "—";
    [ObservableProperty] private string lrfFirmwareTime = "—";
    [ObservableProperty] private DateTime? lastLrfIdentificationPolled;
    public string LastLrfIdentificationPolledText
        => LastLrfIdentificationPolled is { } t ? $"Last polled: {t:HH:mm:ss}" : "Last polled: —";
    partial void OnLastLrfIdentificationPolledChanged(DateTime? value)
        => OnPropertyChanged(nameof(LastLrfIdentificationPolledText));

    // Diagnostic data (Noptel §3.11)
    [ObservableProperty] private double lrfBatteryVolts;
    [ObservableProperty] private double lrfPowerWatts;
    [ObservableProperty] private double lrfIoVolts;
    [ObservableProperty] private double lrfDetectorBiasVolts;
    [ObservableProperty] private double lrfFiveVoltVolts;
    [ObservableProperty] private double lrfRxTemperatureC;
    [ObservableProperty] private uint lrfPulseCounterMillions;
    [ObservableProperty] private byte lrfRsErrorCounter;
    [ObservableProperty] private DateTime? lastLrfDiagnosticsPolled;
    public string LastLrfDiagnosticsPolledText
        => LastLrfDiagnosticsPolled is { } t ? $"Last polled: {t:HH:mm:ss}" : "Last polled: —";
    partial void OnLastLrfDiagnosticsPolledChanged(DateTime? value)
        => OnPropertyChanged(nameof(LastLrfDiagnosticsPolledText));

    // Baud rate selection (Noptel §3.9). UI dropdown bound to this
    // index; 0 means "save current settings", 1..7 are the rate slots.
    [ObservableProperty] private int lrfBaudRateSelection = 5; // 115200 default

    public string[] LrfBaudRateOpts => new[]
    {
        "0: Save settings",
        "1: 9600",
        "2: 19200",
        "3: 38400",
        "4: 57600",
        "5: 115200 (default)",
        "6: 230400",
        "7: 460800",
    };

    [ObservableProperty] private string ibitEnvironmental = "—";

    // -----------------------------------------------------------------------
    // UI / Log
    // -----------------------------------------------------------------------
    [ObservableProperty] private string bannerText = string.Empty;
    [ObservableProperty] private bool isBannerVisible;
    [ObservableProperty] private IBrush bannerBackground = Brush.Parse("#1E3A5F");
    [ObservableProperty] private IBrush bannerBorderBrush = Brush.Parse("#2563EB");
    [ObservableProperty] private IBrush bannerForeground = Brush.Parse("#E2E8F0");
    [ObservableProperty] private int logVersion;
    [ObservableProperty] private int traceVersion;

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>
    /// Joined view of <see cref="LogLines"/> for binding to a single
    /// read-only TextBox that supports mouse selection and Ctrl+C copy.
    /// Recomputed whenever a line is added/cleared (we increment
    /// <see cref="LogVersion"/> for that signal already, so we just
    /// notify both properties together).
    /// </summary>
    public string LogText => string.Join("\n", LogLines);

    public ObservableCollection<string> TraceLines { get; } = [];

    /// <summary>
    /// Decoded view of the most recent frame seen on the wire (TX or RX).
    /// Populated on every RawFrame event. Cleared automatically when a new
    /// frame arrives. Bound to the "Decoded Frame" panel in the UI.
    /// </summary>
    public ObservableCollection<DecodedFrameRow> LastDecodedFrameRows { get; } = [];

    /// <summary>
    /// Header line shown above the decoded rows (e.g. "TX @ 10:46:52 – 12 bytes").
    /// </summary>
    [ObservableProperty] private string lastDecodedFrameHeader = "No frame received yet.";

    // -----------------------------------------------------------------------
    // Computed
    // -----------------------------------------------------------------------
    public bool IsConnected => _service.IsConnected;
    public bool CanConnect => !IsConnected && !IsConnecting;
    public bool CanDisconnect => IsConnected && !IsConnecting;
    public bool CanSend => IsConnected && !IsConnecting;

    public string ConnectionStateText => IsConnected ? "Connected" : IsConnecting ? "Connecting" : "Disconnected";

    // ─────────────────────────────────────────────────────────────────
    // Transport state — reflects the currently-selected transport from
    // _service.GetPortSettings(). Refreshed via RefreshTransportState()
    // whenever settings change or we connect/disconnect.
    // ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool isUartTransport = true;
    [ObservableProperty] private bool isUdpTransport = false;
    [ObservableProperty] private string transportSummary = "UART";

    private void RefreshTransportState()
    {
        var s = _service.GetPortSettings();
        IsUartTransport = s.Transport == TransportKind.Uart;
        IsUdpTransport = s.Transport == TransportKind.Udp;
        TransportSummary = s.Transport == TransportKind.Udp
            ? $"UDP — {s.RemoteHost}:{s.RemotePort} ← :{s.LocalPort}"
            : $"UART — {s.BaudRate} {s.DataBits}-{ParityShort(s.Parity)}-{StopBitsShort(s.StopBits)}";
    }

    private static string ParityShort(System.IO.Ports.Parity p) => p switch
    {
        System.IO.Ports.Parity.None => "N",
        System.IO.Ports.Parity.Odd => "O",
        System.IO.Ports.Parity.Even => "E",
        System.IO.Ports.Parity.Mark => "M",
        System.IO.Ports.Parity.Space => "S",
        _ => "?"
    };

    private static string StopBitsShort(System.IO.Ports.StopBits s) => s switch
    {
        System.IO.Ports.StopBits.One => "1",
        System.IO.Ports.StopBits.OnePointFive => "1.5",
        System.IO.Ports.StopBits.Two => "2",
        _ => "?"
    };
    public string ModeStateText => IsSimulationMode ? "Simulation" : "Hardware";
    public string CalibrationStateText => IsConnecting ? "Connecting…" : IsConnected ? "Ready" : "—";

    public string[] FovOptions => ["Get", "WFOV", "MFOV", "NFOV", "VNFOV"];
    public string[] FocusModeOpts => ["Get", "Set Position", "Set Speed", "Infinity", "Stop"];
    public string[] PanTiltModeOpts => ["Get", "Position", "Speed"];
    public string[] StabModeOpts => ["Activate", "Deactivate", "Get"];
    public string[] NucModeOpts => ["2 Point", "3 Point", "Factory Map"];
    public string[] EdgeModeOpts => ["Off", "Sharpness 1", "Sharpness 2", "Sharpness 3"];
    public string[] ContrastOpts => ["None", "Global", "LACE"];
    public string[] PolarityOpts => ["White Hot", "Black Hot", "Colour Maps"];
    public string[] LrfModeOpts => ["SMM", "QSMM 1", "QSMM 2", "CMM 1 Hz", "CMM 4 Hz",
                                        "CMM 10 Hz", "CMM 20 Hz", "CMM 100 Hz", "CMM 200 Hz", "CMM 500 Hz"];

    /// <summary>
    /// Dropdown-index -> Noptel ICD mode byte. The ICD assigns
    /// non-sequential values (e.g. QSMM1=0x10, QSMM2=0x20, CMM1Hz=0x01),
    /// so we map them explicitly rather than send the index as-is.
    /// </summary>
    private static readonly byte[] LrfModeBytes =
    {
        0x00, // SMM
        0x10, // QSMM 1
        0x20, // QSMM 2
        0x01, // CMM 1 Hz
        0x02, // CMM 4 Hz
        0x03, // CMM 10 Hz
        0x04, // CMM 20 Hz
        0x05, // CMM 100 Hz
        0x06, // CMM 200 Hz
        0x07, // CMM 500 Hz
    };

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------
    public DeviceControllerViewModel(DeviceSerialService service)
    {
        _service = service;
        _service.SetSimulationMode(IsSimulationMode);

        _service.StatusChanged += s =>
        {
            StatusText = s;
            NotifyConnectionState();
        };

        _service.LogMessage += line =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
            LogVersion++;
            OnPropertyChanged(nameof(LogLines));
            OnPropertyChanged(nameof(LogText));
        };

        _service.RawFrame += (frame, isTx) =>
        {
            string prefix = isTx ? "TX" : "RX";
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            TraceLines.Add($"{ts}  {prefix}  {BitConverter.ToString(frame).Replace("-", " ")}");
            while (TraceLines.Count > 500) TraceLines.RemoveAt(0);
            TraceVersion++;

            // Refresh the "Decoded Frame" panel from the new bytes.
            // Replacing the collection contents in-place lets the UI observe
            // the change without re-binding the panel.
            LastDecodedFrameRows.Clear();
            foreach (var row in SirsFrameDecoder.Decode(frame))
                LastDecodedFrameRows.Add(row);
            LastDecodedFrameHeader = $"{prefix} @ {ts}  —  {frame.Length} bytes";
        };

        _service.ResponseReceived += OnResponseReceived;

        RefreshPorts();
        RefreshTransportState();

        // Xbox controller — drives the SIRS §3.3.5 Pan/Tilt command set
        _controller.StateChanged += HandleControllerState;
        _controller.ButtonPressed += HandleControllerButton;
        _controller.ControllerConnected += () => Dispatcher.UIThread.Post(() =>
        {
            IsPhysicalControllerConnected = true;
            ShowControllerVisual = false;
        });
        _controller.ControllerDisconnected += () => Dispatcher.UIThread.Post(() =>
        {
            IsPhysicalControllerConnected = false;
        });
        _controller.Start();
    }

    // -----------------------------------------------------------------------
    // Connection commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var p in _service.GetAvailablePorts())
            AvailablePorts.Add(p);
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (SelectedPort is null) return;
        IsConnecting = true;
        NotifyConnectionState();
        try
        {
            await _service.ConnectAsync(SelectedPort);
            ShowSuccess("Connected successfully.");

            // Auto-poll sequence on connect: General Status, then LRX
            // Identification, then LRX Diagnostics. Each runs in its own
            // try/catch so a failure on one doesn't block the others.
            // Visible GUI log lines (not just Debug.WriteLine) make it
            // obvious at a glance which polls fired and which didn't.
            await TryAutoPollAsync("General Status", () => _service.SendGeneralStatusAsync());
            await TryAutoPollAsync("LRF Identification", () => _service.SendLrfIdentificationAsync());
            await TryAutoPollAsync("LRF Diagnostics", () => _service.SendLrfDiagnosticsAsync());
        }
        catch (Exception ex)
        {
            ShowError($"Connection failed: {ex.Message}");
        }
        finally
        {
            IsConnecting = false;
            NotifyConnectionState();
        }
    }


    /// <summary>
    /// Helper for auto-poll-on-connect that wraps a single Send*Async
    /// call in a try/catch and logs both success and failure as a GUI
    /// log line. Makes the auto-poll sequence visible in the Workspace
    /// Log so the operator can tell at a glance whether each step ran.
    /// </summary>
    private async Task TryAutoPollAsync(string label, Func<Task> sendOp)
    {
        var ts = DateTime.Now;
        LogLines.Add($"{ts:HH:mm:ss.fff}  GUI   Auto-poll {label} ...");
        LogVersion++;
        OnPropertyChanged(nameof(LogText));
        System.Diagnostics.Debug.WriteLine($"[DeviceControllerViewModel] Auto-poll attempt: {label}");

        try
        {
            await sendOp();
            System.Diagnostics.Debug.WriteLine($"[DeviceControllerViewModel] Auto-poll OK:      {label}");
        }
        catch (Exception ex)
        {
            LogLines.Add($"{DateTime.Now:HH:mm:ss.fff}  WARN  Auto-poll {label} failed: {ex.Message}");
            LogVersion++;
            OnPropertyChanged(nameof(LogText));
            System.Diagnostics.Debug.WriteLine($"[DeviceControllerViewModel] Auto-poll FAILED:  {label} -> {ex}");
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _service.Disconnect();
        NotifyConnectionState();
    }

    /// <summary>
    /// Opens the Port Settings popup. Changes flow back to the service via
    /// <c>ApplyPortSettings</c> on confirm.
    /// </summary>
    [RelayCommand]
    private void OpenPortSettings()
    {
        var window = new Window
        {
            Title = "System Controller Port Settings",
            Width = 380,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var vm = new ShockUI.ViewModels.PortSettingsViewModel(
            current: _service.GetPortSettings(),
            autoReconnectEnabled: _service.AutoReconnectEnabled,
            applySettings: s => { _service.ApplyPortSettings(s); RefreshTransportState(); },
            applyAutoReconnect: v => _service.AutoReconnectEnabled = v,
            onClose: () => window.Close());

        window.Content = new PortSettingsView { DataContext = vm };
        window.Show();
    }

    [RelayCommand]
    private void ToggleSimulationMode()
    {
        IsSimulationMode = !IsSimulationMode;
        _service.SetSimulationMode(IsSimulationMode);
        RefreshPorts();
        NotifyConnectionState();
    }

    // -----------------------------------------------------------------------
    // §3.3.1  General Status
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task RequestGeneralStatus()
    {
        await _service.SendGeneralStatusAsync();
    }

    // -----------------------------------------------------------------------
    // §3.3.2  Boresight
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendBoresight()
    {
        await _service.SendBoresightAsync(
            BoresightNirRight, BoresightNirDown, BoresightSave, BoresightNirPixels,
            BoresightRetRight, BoresightRetDown, BoresightRetPixels);
        ShowInfo("Boresight command sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.5  Pan / Tilt
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendPanTilt()
    {
        int panMilli = (int)(PanAngleDeg * 1000);
        int tiltMilli = (int)(TiltAngleDeg * 1000);
        await _service.SendPanTiltAsync(PanMode, TiltMode, panMilli, tiltMilli, PanSpeed, TiltSpeed);
        ShowInfo("Pan/Tilt command sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.6  Stab Control
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendStabControl()
    {
        await _service.SendStabControlAsync(StabCommandMode);
        ShowInfo("Stab control sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.7  Video Source
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendVideoSource()
    {
        await _service.SendVideoSourceAsync(Sdi1IsNir, Sdi2IsNir);
        ShowInfo("Video source selection sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.8  FOV
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendNirFov() { await _service.SendNirFovAsync(NirFovCommand); ShowInfo($"NIR FOV command sent ({FovOptions[NirFovCommand]})."); }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMwirFov() { await _service.SendMwirFovAsync(MwirFovCommand); ShowInfo($"MWIR FOV command sent ({FovOptions[MwirFovCommand]})."); }

    // -----------------------------------------------------------------------
    // §3.3.9  Focus
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendNirFocus()
    {
        await _service.SendNirFocusAsync(NirFocusMode, NirFocusPosition, NirFocusSpeed);   // int32 LE on wire
        ShowInfo("NIR Focus command sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMwirFocus()
    {
        await _service.SendMwirFocusAsync(MwirFocusMode, MwirFocusPosition, MwirFocusSpeed);
        ShowInfo("MWIR Focus command sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.10  Image Enhancement
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMwirEnhancement()
    {
        await _service.SendMwirImageEnhancementAsync(
            MwirEdgeMode, MwirContrastMode, MwirNucRequest,
            MwirDeadPixelEnable, MwirNoiseSuppEnable, MwirUpscaleEnable, MwirPolarityMode);
        ShowInfo("MWIR Image Enhancement sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendNirEnhancement()
    {
        await _service.SendNirImageEnhancementAsync(
            NirEdgeMode, NirContrastMode, NirColourMatrix,
            NirDeadPixelEnable, NirNoiseSuppEnable);
        ShowInfo("NIR Image Enhancement sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendRgbEnhancement()
    {
        await _service.SendRgbImageEnhancementAsync(
            RgbEdgeMode, RgbContrastMode,
            RgbDeadPixelEnable, RgbNoiseSuppEnable);
        ShowInfo("RGB enhancement settings applied.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendNirExposure()
    {
        await _service.SendNirExposureAsync(
            NirExposureManual, NirExposureGain, NirExposureValue);
        ShowInfo(NirExposureManual
            ? $"NIR exposure set to {NirExposureValue:F1} µs (manual, gain {NirExposureGain})."
            : $"NIR exposure set to AUTO (gain {NirExposureGain}).");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendVisExposure()
    {
        await _service.SendVisExposureAsync(
            VisExposureManual, VisExposureGain, VisExposureValue);
        ShowInfo(VisExposureManual
            ? $"VIS exposure set to {VisExposureValue:F1} µs (manual, gain {VisExposureGain})."
            : $"VIS exposure set to AUTO (gain {VisExposureGain}).");
    }

    // -----------------------------------------------------------------------
    // §3.3.11  LRF Measurement
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendLrfMeasurement()
    {
        await _service.SendLrfMeasurementAsync(LrfModeBytes[LrfMeasurementMode]);
        ShowInfo($"LRF Measurement sent ({LrfModeOpts[LrfMeasurementMode]}).");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendLrfStopCmm()
    {
        await _service.SendLrfStopCmmAsync();
        ShowInfo("LRF Stop CMM sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendLrfRange()
    {
        await _service.SendLrfMeasurementRangeAsync(LrfSetRange, LrfMinRange, LrfMaxRange);
        ShowInfo($"LRF Range command sent ({(LrfSetRange ? "Set" : "Get")}).");
    }

    // ── Extended LRF commands (Noptel LRX ICD) ───────────────────────

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task RequestLrfStatus()
    {
        await _service.SendLrfStatusQueryAsync();
        ShowInfo("LRF status request sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task RequestLrfOpticalCrosstalk()
    {
        await _service.SendLrfOpticalCrosstalkAsync();
        ShowInfo("LRF optical-crosstalk check sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task ToggleLrfPointer()
    {
        bool target = !LrfPointerOn;
        await _service.SendLrfAlignmentPointerAsync(target);
        ShowInfo($"LRF pointer command sent: {(target ? "ON" : "OFF")}.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task RequestLrfIdentification()
    {
        await _service.SendLrfIdentificationAsync();
        ShowInfo("LRF identification request sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task RequestLrfDiagnostics()
    {
        await _service.SendLrfDiagnosticsAsync();
        ShowInfo("LRF diagnostics request sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task ResetLrfErrorCounter()
    {
        await _service.SendLrfResetErrorCounterAsync();
        ShowInfo("LRF serial-error counter reset.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task ApplyLrfBaudRate()
    {
        // Selection 0 = "save settings (no rate change)". Anything 1..7
        // changes the rate immediately on the LRX side.
        await _service.SendLrfBaudRateAsync((byte)LrfBaudRateSelection);
        ShowInfo(LrfBaudRateSelection == 0
            ? "LRF settings save requested."
            : $"LRF baud rate change requested ({LrfBaudRateOpts[LrfBaudRateSelection]}).");
    }

    // -----------------------------------------------------------------------
    // §3.3.14  Brightness & Contrast
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendNirBc()
    {
        await _service.SendNirBrightnessContrastAsync(NirBcSet, NirBrightness, NirContrast);
        ShowInfo("NIR Brightness/Contrast sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMwirBc()
    {
        await _service.SendMwirBrightnessContrastAsync(MwirBcSet, MwirBrightness, MwirContrast);
        ShowInfo("MWIR Brightness/Contrast sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.15  Symbology (per-stream)
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendStream1Symbology()
    {
        await _service.SendStream1SymbologyAsync(Stream1SymbologyOn);
        ShowInfo($"Stream 1 symbology {(Stream1SymbologyOn ? "ON" : "OFF")}.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendStream2Symbology()
    {
        await _service.SendStream2SymbologyAsync(Stream2SymbologyOn);
        ShowInfo($"Stream 2 symbology {(Stream2SymbologyOn ? "ON" : "OFF")}.");
    }

    // -----------------------------------------------------------------------
    // §3.3.16  IBIT
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendIbit()
    {
        ShowInfo("IBIT initiated…");
        await _service.SendIbitAsync();
    }

    // -----------------------------------------------------------------------
    // Maintenance: Start NUC (RGB + NIR)
    // -----------------------------------------------------------------------
    // Two sensors, two commands. Payload per the May 2026 NUC spec:
    //   [0] NUC type (1 = 1-Point, 2 = 2-Point)
    //   [1..2] reserve (0x00 0x00)
    // The cards in the View are gated behind IsMaintenanceUnlocked, so
    // these commands are only reachable when the operator has unlocked
    // maintenance from the sidebar.

    /// <summary>Dropdown options for the NUC type selector. Index = wire value.
    /// 1-Point at index 1, 2-Point at index 2. Index 0 is reserved/unused
    /// (matches typical firmware enum so 0 = "no-op").</summary>
    public string[] NucTypeOpts => ["—", "1-Point NUC", "2-Point NUC"];

    /// <summary>NUC type to send when the user clicks <em>Start RGB NUC</em>.
    /// Default 2 (2-Point) — the most common factory recommendation.</summary>
    [ObservableProperty] private byte rgbNucType = 2;

    /// <summary>NUC type for the NIR sensor's Start-NUC command.</summary>
    [ObservableProperty] private byte nirNucType = 2;

    /// <summary>Last RGB NUC response status text — populated by the
    /// response handler when the firmware acks the command.</summary>
    [ObservableProperty] private string rgbNucFeedback = "—";

    /// <summary>Last NIR NUC response status text.</summary>
    [ObservableProperty] private string nirNucFeedback = "—";

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task StartRgbNuc()
    {
        ShowInfo($"RGB NUC ({NucTypeOpts[Math.Clamp(RgbNucType, 0, NucTypeOpts.Length - 1)]}) requested…");
        RgbNucFeedback = "Requested…";
        await _service.SendRgbStartNucAsync(RgbNucType);
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task StartNirNuc()
    {
        ShowInfo($"NIR NUC ({NucTypeOpts[Math.Clamp(NirNucType, 0, NucTypeOpts.Length - 1)]}) requested…");
        NirNucFeedback = "Requested…";
        await _service.SendNirStartNucAsync(NirNucType);
    }

    // -----------------------------------------------------------------------
    // Log
    // -----------------------------------------------------------------------
    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        LogVersion++;
        OnPropertyChanged(nameof(LogText));
    }

    [RelayCommand]
    private void ClearTrace()
    {
        TraceLines.Clear();
        TraceVersion++;
    }

    /// <summary>
    /// Clear the Decoded Frame panel. Used when the user wants to reset the
    /// view (e.g. between two unrelated tests).
    /// </summary>
    [RelayCommand]
    private void ClearDecodedFrame()
    {
        LastDecodedFrameRows.Clear();
        LastDecodedFrameHeader = "No frame received yet.";
    }

    [RelayCommand]
    private void DismissBanner()
    {
        IsBannerVisible = false;
        BannerText = string.Empty;
    }

    // -----------------------------------------------------------------------
    // Response handler
    // -----------------------------------------------------------------------
    private void OnResponseReceived(DeviceParsedFrame frame)
    {
        switch (frame.CommandId)
        {
            case DeviceCommandId.GeneralStatus:
                {
                    var r = DeviceGeneralStatusResponse.Parse(frame.Payload);
                    if (r is null) break;
                    LastGeneralStatusPolled = DateTime.Now;
                    SystemState = r.SystemStateText;
                    OperationalHours = r.OperationalHours;
                    PowerFailure = r.PowerFailure;
                    FibreError = r.FibreError;
                    ImuError = r.ImuError;
                    LrfNotSafe = r.LrfNotSafe;
                    LpiNotSafe = r.LpiNotSafe;
                    HumidityPercent = r.HumidityPercent;
                    PressurePsi = r.PressurePsi;
                    TemperatureCelsius = r.TemperatureCelsius;
                    HumidityTriggered = r.HumidityTriggered;
                    PressureTriggered = r.PressureTriggered;
                    TempOutOfRange = r.TemperatureOutOfRange;
                    MwirNucMode = r.MwirNucText;
                    MwirCoolerBusy = r.MwirCoolerBusy;
                    MwirMotorFault = r.MwirMotorFault;
                    NirMotorFault = r.NirMotorFault;
                    PanMotorFault = r.PanMotorFault;
                    TiltMotorFault = r.TiltMotorFault;
                    LastError = r.LastError;
                    LrfFault = r.LrfFault;
                    LpiFault = r.LpiFault;
                    break;
                }
            case DeviceCommandId.PanTiltMotorControl:
                {
                    var r = DevicePanTiltResponse.Parse(frame.Payload);
                    if (r is null) break;
                    PanFeedbackDeg = r.PanAngleDeg;
                    TiltFeedbackDeg = r.TiltAngleDeg;
                    break;
                }
            // NIR FOV — accept BOTH the legacy SIRS cmd (0x08) and the
            // EOA-direct cmd (0x84 / 0x0084) that's now the production path.
            case DeviceCommandId.NirFovChange:
            case 0x84:
                {
                    var r = DeviceFovResponse.Parse(frame.Payload);
                    if (r is null) break;
                    NirFovFeedback = r.FovName;
                    NirFovReached = r.FovReached;
                    break;
                }
            case DeviceCommandId.MwirFovChange:
                {
                    var r = DeviceFovResponse.Parse(frame.Payload);
                    if (r is null) break;
                    MwirFovFeedback = r.FovName;
                    MwirFovReached = r.FovReached;
                    break;
                }
            // NIR Focus — accept BOTH the legacy SIRS cmd (0x0A) and the
            // EOA-direct cmd (0x85 / 0x0085) that's now the production path.
            // Position field carries the encoder value back to the GUI.
            case DeviceCommandId.NirFocusChange:
            case 0x85:
                {
                    var r = DeviceFocusResponse.Parse(frame.Payload);
                    if (r is null) break;
                    // New 10-byte response: mode + active/reached/freedom + pos32 + spd32.
                    NirFocusFeedback = $"{r.ModeText} | {r.StatusText}";
                    NirFocusPosFeedback = r.Position;   // encoder ticks
                    break;
                }
            case DeviceCommandId.MwirFocusChange:
                {
                    var r = DeviceFocusResponse.Parse(frame.Payload);
                    if (r is null) break;
                    MwirFocusFeedback = $"{r.ModeText} | {r.StatusText}";
                    MwirFocusPosFeedback = r.Position;
                    break;
                }
            case DeviceCommandId.StabControl:
                {
                    var r = DeviceStabResponse.Parse(frame.Payload);
                    if (r is null) break;
                    StabActive = r.StabActive;
                    break;
                }
            case DeviceCommandId.VideoSourceSelection:
                {
                    var r = DeviceVideoSourceResponse.Parse(frame.Payload);
                    if (r is null) break;
                    Sdi1IsNir = r.Sdi1IsNir;
                    Sdi2IsNir = r.Sdi2IsNir;
                    break;
                }
            case DeviceCommandId.LrfRangeMeasurement:
                {
                    var r = DeviceLrfResponse.Parse(frame.Payload);
                    if (r is null) break;
                    LrfRange1 = r.Range1Meters;
                    LrfRange2 = r.Range2Meters;
                    LrfRange3 = r.Range3Meters;
                    break;
                }
            case DeviceCommandId.NirBrightnessContrast:
                {
                    var r = DeviceBrightnessContrastResponse.Parse(frame.Payload);
                    if (r is null) break;
                    NirBrightness = r.Brightness;
                    NirContrast = r.Contrast;
                    break;
                }
            case DeviceCommandId.MwirBrightnessContrast:
                {
                    var r = DeviceBrightnessContrastResponse.Parse(frame.Payload);
                    if (r is null) break;
                    MwirBrightness = r.Brightness;
                    MwirContrast = r.Contrast;
                    break;
                }
            case DeviceCommandId.RgbStartNuc:
                {
                    // Firmware ack — payload[0] echoes the NUC type that was
                    // accepted. Use it to flip the feedback label.
                    byte echoed = frame.Payload.Length > 0 ? frame.Payload[0] : (byte)0;
                    RgbNucFeedback = $"Started ({NucTypeOpts[Math.Clamp(echoed, 0, NucTypeOpts.Length - 1)]})";
                    break;
                }
            case DeviceCommandId.NirStartNuc:
                {
                    byte echoed = frame.Payload.Length > 0 ? frame.Payload[0] : (byte)0;
                    NirNucFeedback = $"Started ({NucTypeOpts[Math.Clamp(echoed, 0, NucTypeOpts.Length - 1)]})";
                    break;
                }
            case DeviceCommandId.Ibit:
                {
                    var r = DeviceIbitResponse.Parse(frame.Payload);
                    if (r is null) break;
                    IbitGeneralStatus = $"Power:{(r.PowerFailure ? "FAIL" : "OK")}  " +
                                        $"Fibre:{(r.FibreError ? "FAIL" : "OK")}  " +
                                        $"IMU:{(r.ImuError ? "FAIL" : "OK")}";
                    IbitMwirZg1 = r.MwirZg1Summary;
                    IbitMwirZg2 = r.MwirZg2Summary;
                    IbitMwirSolenoid = r.MwirSolSummary;
                    IbitMwirTec = r.MwirTecSummary;
                    IbitNirZg1 = r.NirZg1Summary;
                    IbitNirZg2 = r.NirZg2Summary;
                    IbitPanMotor = r.PanSummary;
                    IbitTiltMotor = r.TiltSummary;
                    IbitLrfStatus = $"B1=0x{r.LrfStatus1:X2}  B2=0x{r.LrfStatus2:X2}  B3=0x{r.LrfStatus3:X2}";
                    IbitEnvironmental = $"Humidity:{(r.HumidityOutOfRange ? "OOR" : "OK")}  " +
                                        $"Pressure:{(r.PressureOutOfRange ? "OOR" : "OK")}  " +
                                        $"Temp:{(r.TempOutOfRange ? "OOR" : "OK")}";
                    ShowSuccess("IBIT complete.");
                    break;
                }
            case DeviceCommandId.LrfStatusQuery:
                {
                    var r = DeviceLrfStatusResponse.Parse(frame.Payload);
                    if (r is null) break;
                    LrfStatusByte1 = r.StatusByte1;
                    LrfStatusByte2 = r.StatusByte2;
                    LrfStatusByte3 = r.StatusByte3;
                    LrfGeneralProblems = r.GeneralProblems;
                    LrfTransmitterProblem = r.TransmitterProblem;
                    LrfRebooted = r.Rebooted;
                    LrfNotReady = r.NotReady;
                    LrfReceiverProblem = r.ReceiverProblem;
                    LrfLaserPowerProblem = r.LaserPowerProblem;
                    LrfPointerOn = r.PointerOn;
                    LrfHighVoltageOutOfRange = r.HighVoltageOutOfRange;
                    LrfDcDcOutOfRange = r.DcDcOutOfRange;
                    LrfMemoryProblem = r.MemoryProblem;
                    LrfLowBattery = r.LowBattery;
                    LrfCommunicationProblem = r.CommunicationProblem;
                    LrfMultipleTargets = r.MultipleTargets;
                    LrfNoTargets = r.NoTargets;
                    LrfErrorReported = r.ErrorReported;
                    LrfTransmitterTiming = r.TransmitterTiming;
                    LastLrfStatusPolled = DateTime.Now;
                    break;
                }
            case DeviceCommandId.LrfOpticalCrosstalk:
                {
                    var r = DeviceLrfOpticalCrosstalkResponse.Parse(frame.Payload);
                    if (r is null) break;
                    LrfOpticalCrosstalkM = r.EffectRangeMeters;
                    LrfHasOpticalCrosstalkResult = true;
                    ShowInfo($"LRF optical crosstalk = {r.EffectRangeMeters} m" +
                             (r.EffectRangeMeters > 100 ? " (HIGH)" : " (OK)"));
                    break;
                }
            case DeviceCommandId.LrfAlignmentPointer:
                {
                    // Standard acknowledgement frame — pointer state echoed
                    // back as payload[0] (0 = OFF, 2 = visible ON).
                    if (frame.Payload.Length >= 1)
                        LrfPointerOn = frame.Payload[0] == 0x02;
                    break;
                }
            case DeviceCommandId.LrfIdentification:
                {
                    var r = DeviceLrfIdentificationResponse.Parse(frame.Payload);
                    if (r is null) break;
                    LrfDeviceId = r.DeviceId;
                    LrfFirmwareVersion = $"{r.FirmwareVersion >> 8}.{r.FirmwareVersion & 0xFF}";
                    LrfSerialNumber = r.SerialNumber;
                    LrfElectronicsType = r.ElectronicsType;
                    LrfOpticsType = r.OpticsType;
                    LrfFirmwareDate = r.FirmwareDate;
                    LrfFirmwareTime = r.FirmwareTime;
                    LastLrfIdentificationPolled = DateTime.Now;
                    break;
                }
            case DeviceCommandId.LrfDiagnostics:
                {
                    var r = DeviceLrfDiagnosticsResponse.Parse(frame.Payload);
                    if (r is null) break;
                    LrfBatteryVolts = r.BatteryVolts;
                    LrfPowerWatts = r.PowerWatts;
                    LrfIoVolts = r.IoVolts;
                    LrfDetectorBiasVolts = r.DetectorBiasVolts;
                    LrfFiveVoltVolts = r.FiveVoltVolts;
                    LrfRxTemperatureC = r.RxTemperatureC;
                    LrfPulseCounterMillions = r.PulseCounterMillions;
                    LrfRsErrorCounter = r.RsErrorCounter;
                    // Diagnostic frame also includes status bytes; mirror them.
                    LrfStatusByte1 = r.StatusByte1;
                    LrfStatusByte2 = r.StatusByte2;
                    LrfStatusByte3 = r.StatusByte3;
                    LastLrfDiagnosticsPolled = DateTime.Now;
                    break;
                }
        }

        if (frame.HasError)
            ShowWarning($"Error 0x{frame.ErrorCode:X4} in response to CMD 0x{frame.CommandId:X2}");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private void NotifyConnectionState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(ModeStateText));
        OnPropertyChanged(nameof(IsControllerInteractionEnabled));
        OnPropertyChanged(nameof(CalibrationStateText));

        // Every RelayCommand gated by CanSend must be re-queried so buttons
        // enable/disable as the connection state changes.
        RequestGeneralStatusCommand.NotifyCanExecuteChanged();
        SendBoresightCommand.NotifyCanExecuteChanged();
        SendPanTiltCommand.NotifyCanExecuteChanged();
        SendStabControlCommand.NotifyCanExecuteChanged();
        SendVideoSourceCommand.NotifyCanExecuteChanged();
        SendNirFovCommand.NotifyCanExecuteChanged();
        SendMwirFovCommand.NotifyCanExecuteChanged();
        SendNirFocusCommand.NotifyCanExecuteChanged();
        SendMwirFocusCommand.NotifyCanExecuteChanged();
        SendMwirEnhancementCommand.NotifyCanExecuteChanged();
        SendNirEnhancementCommand.NotifyCanExecuteChanged();
        SendRgbEnhancementCommand.NotifyCanExecuteChanged();
        SendNirExposureCommand.NotifyCanExecuteChanged();
        SendVisExposureCommand.NotifyCanExecuteChanged();
        SendLrfMeasurementCommand.NotifyCanExecuteChanged();
        SendLrfStopCmmCommand.NotifyCanExecuteChanged();
        SendLrfRangeCommand.NotifyCanExecuteChanged();
        RequestLrfStatusCommand.NotifyCanExecuteChanged();
        RequestLrfOpticalCrosstalkCommand.NotifyCanExecuteChanged();
        ToggleLrfPointerCommand.NotifyCanExecuteChanged();
        RequestLrfIdentificationCommand.NotifyCanExecuteChanged();
        RequestLrfDiagnosticsCommand.NotifyCanExecuteChanged();
        ResetLrfErrorCounterCommand.NotifyCanExecuteChanged();
        ApplyLrfBaudRateCommand.NotifyCanExecuteChanged();
        SendNirBcCommand.NotifyCanExecuteChanged();
        SendMwirBcCommand.NotifyCanExecuteChanged();
        SendStream1SymbologyCommand.NotifyCanExecuteChanged();
        SendStream2SymbologyCommand.NotifyCanExecuteChanged();
        SendIbitCommand.NotifyCanExecuteChanged();
        StartRgbNucCommand.NotifyCanExecuteChanged();
        StartNirNucCommand.NotifyCanExecuteChanged();
    }

    private void ShowInfo(string text) => ShowBanner(text, "#1E3A5F", "#2563EB", "#E2E8F0");
    private void ShowSuccess(string text) => ShowBanner(text, "#14532D", "#166534", "#DCFCE7");
    private void ShowWarning(string text) => ShowBanner(text, "#7C2D12", "#9A3412", "#FFEDD5");
    private void ShowError(string text) => ShowBanner(text, "#7F1D1D", "#991B1B", "#FEE2E2");

    private void ShowBanner(string text, string bg, string border, string fg)
    {
        BannerText = text;
        BannerBackground = Brush.Parse(bg);
        BannerBorderBrush = Brush.Parse(border);
        BannerForeground = Brush.Parse(fg);
        IsBannerVisible = true;
    }

    partial void OnIsSimulationModeChanged(bool value)
    {
        _service.SetSimulationMode(value);
        RefreshPorts();
        NotifyConnectionState();
    }

    // -----------------------------------------------------------------------
    // Sim fault injection
    // -----------------------------------------------------------------------
    [ObservableProperty] private bool isSimFaultMode;

    // System health
    [ObservableProperty] private bool faultPowerFailure;
    [ObservableProperty] private bool faultFibreError;
    [ObservableProperty] private bool faultImuError;
    [ObservableProperty] private bool faultLrfNotSafe;
    [ObservableProperty] private bool faultLpiNotSafe;
    [ObservableProperty] private bool faultSystemStateError;
    [ObservableProperty] private bool faultHumidityTriggered;
    [ObservableProperty] private bool faultPressureTriggered;
    [ObservableProperty] private bool faultTemperatureOutOfRange;

    // Subsystem faults
    [ObservableProperty] private bool faultMwirCoolerBusy;
    [ObservableProperty] private bool faultMwirMotorFault;
    [ObservableProperty] private bool faultNirMotorFault;
    [ObservableProperty] private bool faultPanMotorFault;
    [ObservableProperty] private bool faultTiltMotorFault;
    [ObservableProperty] private bool faultLrfFault;
    [ObservableProperty] private bool faultLpiFault;

    // Functional faults
    [ObservableProperty] private bool faultFovNotReached;
    [ObservableProperty] private bool faultLrfNoReturn;
    [ObservableProperty] private bool faultIbitFailed;

    public bool HasAnyFaultActive =>
        FaultPowerFailure || FaultFibreError || FaultImuError || FaultLrfNotSafe || FaultLpiNotSafe ||
        FaultSystemStateError || FaultHumidityTriggered || FaultPressureTriggered || FaultTemperatureOutOfRange ||
        FaultMwirCoolerBusy || FaultMwirMotorFault || FaultNirMotorFault ||
        FaultPanMotorFault || FaultTiltMotorFault || FaultLrfFault || FaultLpiFault ||
        FaultFovNotReached || FaultLrfNoReturn || FaultIbitFailed;

    [RelayCommand]
    private void ApplyFaults()
    {
        _service.SetSimFaults(new ShockUI.Services.Device.DeviceSimFaultConfig
        {
            PowerFailure = FaultPowerFailure,
            FibreError = FaultFibreError,
            ImuError = FaultImuError,
            LrfNotSafe = FaultLrfNotSafe,
            LpiNotSafe = FaultLpiNotSafe,
            SystemStateError = FaultSystemStateError,
            HumidityTriggered = FaultHumidityTriggered,
            PressureTriggered = FaultPressureTriggered,
            TemperatureOutOfRange = FaultTemperatureOutOfRange,
            MwirCoolerBusy = FaultMwirCoolerBusy,
            MwirMotorFault = FaultMwirMotorFault,
            NirMotorFault = FaultNirMotorFault,
            PanMotorFault = FaultPanMotorFault,
            TiltMotorFault = FaultTiltMotorFault,
            LrfFault = FaultLrfFault,
            LpiFault = FaultLpiFault,
            FovNotReached = FaultFovNotReached,
            LrfNoReturn = FaultLrfNoReturn,
            IbitFailed = FaultIbitFailed,
        });
        IsSimFaultMode = HasAnyFaultActive;
        if (IsSimFaultMode)
            ShowWarning("Fault injection ACTIVE — sim will return injected faults.");
        else
            ShowInfo("All faults cleared — sim returning healthy state.");
    }

    [RelayCommand]
    private void ClearAllFaults()
    {
        FaultPowerFailure = FaultFibreError = FaultImuError = FaultLrfNotSafe = FaultLpiNotSafe =
        FaultSystemStateError = FaultHumidityTriggered = FaultPressureTriggered = FaultTemperatureOutOfRange =
        FaultMwirCoolerBusy = FaultMwirMotorFault = FaultNirMotorFault =
        FaultPanMotorFault = FaultTiltMotorFault = FaultLrfFault = FaultLpiFault =
        FaultFovNotReached = FaultLrfNoReturn = FaultIbitFailed = false;
        ApplyFaults();
    }


    // ─────────────────────────────────────────────────────────────────
    // Xbox controller handlers — mirrors PanTilt module's mapping but
    // routes through SIRS §3.3.5 PanTilt Motor Control.
    // Public so the View can wire virtual click/drag events to them too.
    // ─────────────────────────────────────────────────────────────────

    public void HandleControllerState(XboxControllerState s)
    {
        Dispatcher.UIThread.Post(() => ControllerState = s);

        double boost = 1.0 + s.RightTrigger * 1.0;
        double slow = 1.0 - s.LeftTrigger * 0.9;
        _speedMultiplier = boost * slow;

        double panX = s.LeftStickX + s.RightStickX * 0.2;
        double tiltY = s.LeftStickY + s.RightStickY * 0.2;
        tiltY = -tiltY;

        panX = Clamp(panX * _speedMultiplier, -1.0, 1.0);
        tiltY = Clamp(tiltY * _speedMultiplier, -1.0, 1.0);

        bool isMoving = Math.Abs(panX) > 0.001 || Math.Abs(tiltY) > 0.001;
        var now = DateTime.UtcNow;
        bool dueByTime = (now - _lastMotorTx).TotalMilliseconds >= MotorTxIntervalMs;
        bool edgeMoving = isMoving && !_wasMoving;
        bool edgeStop = !isMoving && _wasMoving;

        if (edgeMoving || edgeStop || (isMoving && dueByTime))
        {
            // SIRS speed is ushort (unsigned) — direction lives in the mode byte.
            // We use mode 0x02 to mean "move at speed" and embed direction with
            // a high bit: 0x02 = forward, 0x03 = reverse. If your SRS uses a
            // different convention, tweak panMode/tiltMode below.
            byte panMode = panX == 0 ? (byte)0x00 : (panX > 0 ? (byte)0x02 : (byte)0x03);
            byte tiltMode = tiltY == 0 ? (byte)0x00 : (tiltY > 0 ? (byte)0x02 : (byte)0x03);
            ushort panSpeed = (ushort)Math.Abs(panX * MaxStickSpeed);
            ushort tiltSpeed = (ushort)Math.Abs(tiltY * MaxStickSpeed);

            _ = _service.SendPanTiltAsync(
                panMode: panMode,
                tiltMode: tiltMode,
                panAngleMilli: 0,
                tiltAngleMilli: 0,
                panSpeed: panSpeed,
                tiltSpeed: tiltSpeed);

            _lastMotorTx = now;
            _wasMoving = isMoving;
        }
    }

    public void HandleControllerButton(XboxButton btn)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (btn)
            {
                case XboxButton.A:
                    _ = _service.SendStabControlAsync(stabMode: 0x01);
                    break;
                case XboxButton.B:
                    // Emergency stop — Stop mode (0x00) + speed 0
                    _ = _service.SendPanTiltAsync(0x00, 0x00, 0, 0, 0, 0);
                    break;
                case XboxButton.X:
                    _ = _service.SendGeneralStatusAsync();
                    break;
                case XboxButton.Y:
                    _ = _service.SendIbitAsync();
                    break;
                case XboxButton.LeftBumper:
                    LogLines.Add($"{DateTime.Now:HH:mm:ss.fff}  CTL  LB pressed — FOV down");
                    break;
                case XboxButton.RightBumper:
                    LogLines.Add($"{DateTime.Now:HH:mm:ss.fff}  CTL  RB pressed — FOV up");
                    break;
                case XboxButton.Start:
                    // Home — position mode + angles 0, low speed
                    _ = _service.SendPanTiltAsync(0x01, 0x01, 0, 0, 500, 500);
                    break;
                case XboxButton.Back:
                    ShowControllerVisual = !ShowControllerVisual;
                    break;
            }
            LogVersion++;
            OnPropertyChanged(nameof(LogText));
        });
    }

    [RelayCommand]
    private void ToggleControllerVisual() => ShowControllerVisual = !ShowControllerVisual;

    private static double Clamp(double v, double min, double max)
        => v < min ? min : v > max ? max : v;

}