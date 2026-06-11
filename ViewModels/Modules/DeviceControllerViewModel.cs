using System;
using System.Collections.ObjectModel;
using System.Threading;
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
    private readonly XboxControllerService _controller;

    [ObservableProperty] private XboxControllerState? controllerState;
    [ObservableProperty] private bool showControllerVisual = true;
    [ObservableProperty] private bool isPhysicalControllerConnected;

    // ── Controller drive mode ────────────────────────────────────────────
    // The controller may only drive the turret in Rate/Velocity (0x01) or
    // Stabilised (0x03) mode. 0x00 = OFF: sticks are ignored. The operator
    // chooses the mode explicitly (buttons in the controller card), which
    // both arms the sticks AND sends the matching mode to the PTSC.
    [ObservableProperty] private byte controllerDriveMode;   // 0=Off, 1=Rate, 3=Stabilised

    /// <summary>True when the controller is allowed to drive motion.</summary>
    public bool IsControllerArmed => ControllerDriveMode is 0x01 or 0x03;

    /// <summary>Human label for the active controller mode.</summary>
    public string ControllerModeText => ControllerDriveMode switch
    {
        0x01 => "RATE / VELOCITY",
        0x03 => "STABILISED",
        _ => "OFF — select a mode to enable sticks",
    };

    public bool IsControllerModeRate => ControllerDriveMode == 0x01;
    public bool IsControllerModeStab => ControllerDriveMode == 0x03;
    public bool IsControllerModeOff => ControllerDriveMode == 0x00;

    partial void OnControllerDriveModeChanged(byte value)
    {
        OnPropertyChanged(nameof(IsControllerArmed));
        OnPropertyChanged(nameof(ControllerModeText));
        OnPropertyChanged(nameof(IsControllerModeRate));
        OnPropertyChanged(nameof(IsControllerModeStab));
        OnPropertyChanged(nameof(IsControllerModeOff));
        OnPropertyChanged(nameof(IsControllerInteractionEnabled));
    }

    [RelayCommand]
    private void SetControllerModeOff() => ControllerDriveMode = 0x00;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SetControllerModeRate()
    {
        ControllerDriveMode = 0x01;
        // Arm the axes in Rate mode (Active) on the PTSC.
        await _service.SendStabControlAsync(panMode: 0x01, panDisengage: false,
                                            tiltMode: 0x01, tiltDisengage: false);
        ShowInfo("Controller mode: RATE / VELOCITY.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SetControllerModeStab()
    {
        ControllerDriveMode = 0x03;
        await _service.SendStabControlAsync(panMode: 0x03, panDisengage: false,
                                            tiltMode: 0x03, tiltDisengage: false);
        ShowInfo("Controller mode: STABILISED.");
    }

    public bool IsControllerInteractionEnabled =>
        IsConnected && !IsPhysicalControllerConnected && IsControllerArmed;
    partial void OnIsPhysicalControllerConnectedChanged(bool value)
        => OnPropertyChanged(nameof(IsControllerInteractionEnabled));

    /// <summary>Trigger-driven speed multiplier: LT=0.1×, RT=2×, neither=1×.</summary>
    private double _speedMultiplier = 1.0;

    /// <summary>Max raw motor speed value when stick is fully deflected. SIRS uses ushort for speed so this is the magnitude only.</summary>
    // Full stick deflection maps to this slew rate (degrees/second). The
    // service converts deg/s → PTSC ticks (2^21/360) when building the frame.
    private const double MaxStickSpeedDps = 30.0;

    // Maintenance-only fine motor control: scales the stick slew rate down so
    // the operator can test slow, precise movement. When enabled, full stick
    // deflection maps to MaxStickSpeedDps * FineControlFactor instead.
    private const double FineControlFactor = 0.15;
    [ObservableProperty] private bool fineControlEnabled;

    public string FineControlText => FineControlEnabled
        ? $"Fine control ON ({MaxStickSpeedDps * FineControlFactor:0.#}°/s max)"
        : $"Fine control OFF ({MaxStickSpeedDps:0.#}°/s max)";

    partial void OnFineControlEnabledChanged(bool value)
        => OnPropertyChanged(nameof(FineControlText));

    [RelayCommand]
    private void ToggleFineControl() => FineControlEnabled = !FineControlEnabled;

    // Invert controller axes (both pan and tilt) for operators who prefer the
    // opposite stick convention.
    [ObservableProperty] private bool invertControls;

    public string InvertControlsText => InvertControls ? "Inverted" : "Normal";

    partial void OnInvertControlsChanged(bool value)
        => OnPropertyChanged(nameof(InvertControlsText));

    [RelayCommand]
    private void ToggleInvertControls() => InvertControls = !InvertControls;

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
    [ObservableProperty] private bool systemReady;
    [ObservableProperty] private bool powerFailure;
    [ObservableProperty] private bool imuError;
    [ObservableProperty] private bool panEncoderError;
    [ObservableProperty] private bool tiltEncoderError;
    [ObservableProperty] private bool panAdcError;
    [ObservableProperty] private bool tiltAdcError;
    [ObservableProperty] private bool motorBrakeOn;
    [ObservableProperty] private bool panMotorFault;
    [ObservableProperty] private bool tiltMotorFault;
    [ObservableProperty] private string genPanMode = "—";
    [ObservableProperty] private string genTiltMode = "—";
    [ObservableProperty] private string busVoltage = "—";

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
    [ObservableProperty] private byte panMode = 2;   // 0=Safe 1=Rate 2=Position 3=Stabilised
    [ObservableProperty] private byte tiltMode = 2;

    // Maintenance-only Pan/Tilt debugger: last-sent control bytes + scaled values.
    [ObservableProperty] private string ptDbgPanCtrl = "—";

    // §3.3 GET Stab Control (0x0A) decoded telemetry (52-byte response).
    [ObservableProperty] private string stabUptime = "—";
    [ObservableProperty] private string stabImu = "—";
    [ObservableProperty] private string stabAccel = "—";
    [ObservableProperty] private string stabGyro = "—";
    [ObservableProperty] private string stabTemp = "—";
    [ObservableProperty] private string stabEkf = "—";
    [ObservableProperty] private string stabNudge = "—";
    [ObservableProperty] private string lastStabStatusPolled = "—";

    // §3.3.5 Motor Status (0x08) decoded telemetry.
    [ObservableProperty] private string motorPanAngle = "—";
    [ObservableProperty] private string motorTiltAngle = "—";
    [ObservableProperty] private string motorPanSpeed = "—";
    [ObservableProperty] private string motorTiltSpeed = "—";
    [ObservableProperty] private string motorPanMode = "—";
    [ObservableProperty] private string motorTiltMode = "—";
    [ObservableProperty] private string lastMotorStatusPolled = "—";
    [ObservableProperty] private string ptDbgTiltCtrl = "—";
    [ObservableProperty] private string ptDbgPanAngle = "—";
    [ObservableProperty] private string ptDbgTiltAngle = "—";
    [ObservableProperty] private string ptDbgPanSpeed = "—";
    [ObservableProperty] private string ptDbgTiltSpeed = "—";
    [ObservableProperty] private double panAngleDeg;
    [ObservableProperty] private double tiltAngleDeg;
    // Speed in deg/s. Signed: in Rate mode the sign sets direction
    // (negative = reverse). In Position mode only magnitude is used.
    [ObservableProperty] private int panSpeed = 100;
    [ObservableProperty] private int tiltSpeed = 100;
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
    [ObservableProperty] private bool mwirDeadPixelEnable = false;
    [ObservableProperty] private bool mwirNoiseSuppEnable = false;
    [ObservableProperty] private bool mwirUpscaleEnable = true;
    [ObservableProperty] private byte mwirPolarityMode;   // 0=white hot,1=black hot,2=colour

    // -----------------------------------------------------------------------
    // NIR Image Enhancement (§3.3.10)
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte nirEdgeMode;
    [ObservableProperty] private byte nirContrastMode;
    [ObservableProperty] private byte nirColourMatrix;    // 0=factory,1=AWB
    [ObservableProperty] private bool nirDeadPixelEnable = false;
    [ObservableProperty] private bool nirNoiseSuppEnable = false;

    // RGB (visible-spectrum) image-enhancement state. Mirrors the NIR
    // controls; same enum codes (Off/Low/Med/High for edge, None/Low/
    // Med/High for contrast).
    [ObservableProperty] private byte rgbEdgeMode;
    [ObservableProperty] private byte rgbContrastMode;
    [ObservableProperty] private bool rgbDeadPixelEnable = false;
    [ObservableProperty] private bool rgbNoiseSuppEnable = false;

    // Exposure §3.3.x — NIR and VIS sensors get auto/manual + gain +
    // manual exposure value (milliseconds, IEEE-754 float on the wire).
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
    [ObservableProperty] private double lrfRange1;
    [ObservableProperty] private double lrfRange2;
    [ObservableProperty] private double lrfRange3;
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

    [ObservableProperty] private string ibitPanMotor = "—";
    [ObservableProperty] private string ibitTiltMotor = "—";
    [ObservableProperty] private string ibitSensor = "—";
    [ObservableProperty] private string ibitPanExt = "—";
    [ObservableProperty] private string ibitTiltExt = "—";
    [ObservableProperty] private string ibitImu = "—";

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
    // When the user (un)ticks the checkbox, the property setter fires the
    // matching ON/OFF command. _suppressPointerSend stops the response
    // handler — which mirrors the device echo back into LrfPointerOn —
    // from triggering a second (looping) send.
    [ObservableProperty] private bool lrfPointerOn;

    private bool _suppressPointerSend;

    partial void OnLrfPointerOnChanged(bool value)
    {
        if (_suppressPointerSend) return;
        if (!CanSend) return;
        _ = SendLrfPointerAsync(value);
    }

    private async Task SendLrfPointerAsync(bool on)
    {
        await _service.SendLrfAlignmentPointerAsync(on);
        ShowInfo($"LRF pointer command sent: {(on ? "ON" : "OFF")}.");
    }

    /// <summary>
    /// Updates the checkbox state from a device echo/status frame WITHOUT
    /// re-issuing a pointer command (avoids the send/echo loop).
    /// </summary>
    private void SetPointerStateFromDevice(bool on)
    {
        _suppressPointerSend = true;
        try { LrfPointerOn = on; }
        finally { _suppressPointerSend = false; }
    }

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
    // Index maps directly to the PTSC ControlMode enum value:
    //   0 = Safe/Damping, 1 = Rate/Velocity, 2 = Position, 3 = Stabilised.
    // Control byte = (mode << 1) | active-bit, so e.g. Position+Active = 0x04.
    public string[] PanTiltModeOpts => ["Safe / Damping", "Rate / Velocity", "Position", "Stabilised"];
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
    public DeviceControllerViewModel(DeviceSerialService service, XboxControllerService controller)
    {
        _service = service;
        _controller = controller;
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
            // Keep the on-screen controller visible so the mode selector stays
            // accessible. The virtual sticks become non-interactive (physical
            // pad drives instead) but the card and mode buttons remain shown.
        });
        _controller.ControllerDisconnected += () => Dispatcher.UIThread.Post(() =>
        {
            IsPhysicalControllerConnected = false;
            // Physical pad gone — bring the on-screen (virtual) controller back.
            ShowControllerVisual = true;
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
    // Mechanical travel limits (deg). The C2000 Safety Supervisor enforces its
    // own soft-stops; these GUI limits prevent obviously-out-of-range commands
    // and surface a clear error instead of silently truncating the input.
    private const double TiltMinDeg = -40.0;
    private const double TiltMaxDeg = 80.0;
    private const double PanMinDeg = -180.0;
    private const double PanMaxDeg = 180.0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendPanTilt()
    {
        // In Position mode, validate against mechanical limits. If out of range,
        // show an error and hold at the current feedback position rather than
        // sending a value the Supervisor would reject (notes §Safety Stops).
        bool panIsPos = PanMode == 0x02;
        bool tiltIsPos = TiltMode == 0x02;

        double panTarget = PanAngleDeg;
        double tiltTarget = TiltAngleDeg;
        bool clamped = false;

        if (panIsPos && (PanAngleDeg < PanMinDeg || PanAngleDeg > PanMaxDeg))
        {
            double limited = Math.Clamp(PanAngleDeg, PanMinDeg, PanMaxDeg);
            ShowWarning($"Pan target {PanAngleDeg:F1}° is outside the allowed range " +
                        $"({PanMinDeg:F0}° to {PanMaxDeg:F0}°). Not sent.");
            PanAngleDeg = limited;     // snap the box to the nearest valid limit
            clamped = true;
        }
        if (tiltIsPos && (TiltAngleDeg < TiltMinDeg || TiltAngleDeg > TiltMaxDeg))
        {
            double limited = Math.Clamp(TiltAngleDeg, TiltMinDeg, TiltMaxDeg);
            ShowWarning($"Tilt target {TiltAngleDeg:F1}° is outside the allowed range " +
                        $"({TiltMinDeg:F0}° to {TiltMaxDeg:F0}°). Not sent.");
            TiltAngleDeg = limited;
            clamped = true;
        }
        if (clamped) return;   // do not send an out-of-range command

        // Service scales degrees / deg-s to PTSC ticks (2^21/360) internally.
        await _service.SendPanTiltAsync(PanMode, TiltMode,
            panTarget, tiltTarget, PanSpeed, TiltSpeed);

        // Maintenance debugger: reflect exactly what was encoded.
        byte panCtrl = (byte)((PanMode & 0x03) << 1);   // active (bit0=0)
        byte tiltCtrl = (byte)((TiltMode & 0x03) << 1);
        PtDbgPanCtrl = $"0x{panCtrl:X2}  (mode {PanMode} = {PtModeName(PanMode)}, Active)";
        PtDbgTiltCtrl = $"0x{tiltCtrl:X2}  (mode {TiltMode} = {PtModeName(TiltMode)}, Active)";
        PtDbgPanAngle = $"{panTarget:F2}°  →  {(int)Math.Round(panTarget * 2097152.0 / 360.0)} ticks";
        PtDbgTiltAngle = $"{tiltTarget:F2}°  →  {(int)Math.Round(tiltTarget * 2097152.0 / 360.0)} ticks";
        PtDbgPanSpeed = $"{PanSpeed}°/s  →  {(int)Math.Round(PanSpeed * 2097152.0 / 360.0)} ticks";
        PtDbgTiltSpeed = $"{TiltSpeed}°/s  →  {(int)Math.Round(TiltSpeed * 2097152.0 / 360.0)} ticks";

        ShowInfo("Pan/Tilt command sent.");
    }

    private static string PtModeName(byte m) => m switch
    {
        0 => "Safe",
        1 => "Rate",
        2 => "Position",
        3 => "Stabilised",
        _ => "?"
    };

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task GetMotorStatus()
    {
        await _service.SendMotorStatusAsync();
        ShowInfo("Motor status poll sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task GetStabStatus()
    {
        await _service.SendStabStatusAsync();
        ShowInfo("Stab status poll sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.6  Stab Control
    // -----------------------------------------------------------------------
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendStabControl()
    {
        // The Stab card mode is always Stabilised (3); the dropdown selects the
        // Active/Deactivated state (the bit-0 of the control byte) or a Get poll.
        //   Activate   → mode 3, Active       → (3<<1)|0 = 0x06
        //   Deactivate → mode 3, Deactivated  → (3<<1)|1 = 0x07
        //   Get        → Stab Status poll (0x0A)
        switch (StabCommandMode)
        {
            case 0: // Activate
                await _service.SendStabControlAsync(
                    panMode: 0x03, panDisengage: false,
                    tiltMode: 0x03, tiltDisengage: false);
                ShowInfo("Stab activated (0x06).");
                break;
            case 1: // Deactivate
                await _service.SendStabControlAsync(
                    panMode: 0x03, panDisengage: true,
                    tiltMode: 0x03, tiltDisengage: true);
                ShowInfo("Stab deactivated (0x07).");
                break;
            default: // Get
                await _service.SendStabStatusAsync();
                ShowInfo("Stab status poll sent.");
                break;
        }
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
            ? $"NIR exposure set to {NirExposureValue:F1} ms (manual, gain {NirExposureGain})."
            : $"NIR exposure set to AUTO (gain {NirExposureGain}).");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendVisExposure()
    {
        await _service.SendVisExposureAsync(
            VisExposureManual, VisExposureGain, VisExposureValue);
        ShowInfo(VisExposureManual
            ? $"VIS exposure set to {VisExposureValue:F1} ms (manual, gain {VisExposureGain})."
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
    private async Task GetLrfRange()
    {
        await _service.SendLrfGetRangeAsync();
        ShowInfo("LRF Get Range command sent.");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SetLrfMinRange()
    {
        await _service.SendLrfSetMinRangeAsync(LrfMinRange);
        ShowInfo($"LRF Set Min Range sent ({LrfMinRange} m).");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SetLrfMaxRange()
    {
        await _service.SendLrfSetMaxRangeAsync(LrfMaxRange);
        ShowInfo($"LRF Set Max Range sent ({LrfMaxRange} m).");
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
    [ObservableProperty] private double ibitProgress;          // 0-100
    [ObservableProperty] private bool ibitRunning;
    [ObservableProperty] private string ibitResultText = "—";

    private CancellationTokenSource? _ibitPollCts;

    /// <summary>Full IBIT (Action 0x01): runs the physical self-test sweep.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task RunFullIbit()
    {
        ShowInfo("Full IBIT initiated…");
        await StartIbitAsync(0x01);
    }

    /// <summary>Silent IBIT (Action 0x02): self-test without moving the gimbal.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task RunSilentIbit()
    {
        ShowInfo("Silent IBIT initiated…");
        await StartIbitAsync(0x02);
    }

    /// <summary>Single manual progress read (Action 0x00).</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task ReadIbit()
    {
        await _service.SendIbitReadAsync();
    }

    private async Task StartIbitAsync(byte action)
    {
        // Cancel any in-flight poll loop, reset UI.
        _ibitPollCts?.Cancel();
        IbitProgress = 0;
        IbitResultText = "Running…";
        IbitRunning = true;

        // Kick off the test.
        await _service.SendIbitAsync(action);

        // Poll Read (0x00) at 10 Hz. The loop terminates as soon as IbitRunning
        // is cleared (by the RX handler on completion) or the safety cap hits.
        _ibitPollCts?.Dispose();
        _ibitPollCts = new CancellationTokenSource();
        var ct = _ibitPollCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Safety cap: ~30 s at 10 Hz so the loop can't run forever.
                for (int i = 0; i < 300; i++)
                {
                    if (ct.IsCancellationRequested || !IbitRunning || !IsConnected)
                        break;
                    await _service.SendIbitReadAsync();
                    await Task.Delay(100, ct);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // Whatever happened, ensure we are no longer "running" so a
                // stale loop can never keep polling.
                Dispatcher.UIThread.Post(() =>
                {
                    if (IbitRunning && IbitProgress >= 100) IbitRunning = false;
                });
            }
        }, ct);
    }

    /// <summary>Populate the General Status panel from a decoded response (SRS v2).</summary>
    private void ApplyGeneralStatus(DeviceGeneralStatusResponse r)
    {
        LastGeneralStatusPolled = DateTime.Now;
        SystemState = r.SystemStateText;
        SystemReady = r.SystemReady;
        PowerFailure = r.PowerFailure;
        ImuError = r.ImuError;
        PanEncoderError = r.PanEncoderError;
        TiltEncoderError = r.TiltEncoderError;
        PanAdcError = r.PanAdcError;
        TiltAdcError = r.TiltAdcError;
        MotorBrakeOn = r.MotorBrakeOn;
        PanMotorFault = r.PanMotorFault;
        TiltMotorFault = r.TiltMotorFault;
        GenPanMode = r.PanModeText;
        GenTiltMode = r.TiltModeText;
        BusVoltage = $"{r.BusVoltageRaw} (raw)";
    }

    /// <summary>Populate the IBIT panel from a decoded response (SRS v2 layout).</summary>
    private void ApplyIbitResponse(DeviceIbitResponse r)
    {
        // RX runs off the UI thread; marshal all binding updates onto it.
        Dispatcher.UIThread.Post(() =>
        {
            IbitProgress = r.ProgressPercent;
            string state = r.TestInProgress ? "In progress"
                         : r.LastTestFailed ? "Last: FAILED"
                         : r.LastTestPassed ? "Last: PASSED" : "Idle";
            IbitGeneralStatus = $"{state}  •  {r.ProgressPercent}%  •  {r.FaultSummary}";
            IbitPanMotor = r.PanMotorSummary;
            IbitTiltMotor = r.TiltMotorSummary;
            IbitSensor = r.SensorSummary;
            IbitPanExt = r.PanExtSummary;
            IbitTiltExt = r.TiltExtSummary;
            IbitImu = r.ImuSummary;

            // The test is finished when it reports 100% OR the status byte says
            // the test is no longer in progress (PASSED/FAILED). Either way,
            // stop polling immediately and deterministically.
            bool finished = r.IsComplete || (!r.TestInProgress && IbitRunning);
            if (finished && IbitRunning)
            {
                _ibitPollCts?.Cancel();   // stop the poll loop right now
                OnIbitComplete(r.NoFaults, r.FaultSummary);
            }
        });
    }

    /// <summary>Called from the IBIT RX handler to stop polling on completion.</summary>
    private void OnIbitComplete(bool noFaults, string faultSummary)
    {
        _ibitPollCts?.Cancel();
        IbitRunning = false;
        IbitProgress = 100;
        IbitResultText = noFaults ? "PASS — no faults" : faultSummary;
        if (noFaults) ShowSuccess("IBIT complete — no faults.");
        else ShowWarning($"IBIT complete — {faultSummary}");
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
        // PTSC (dst/src 0x20) shares command bytes with EOA commands
        // (e.g. 0x09 = PTSC MotorSet vs MwirFovChange at an EOA), so route
        // PTSC responses by SourceId first, before the command-byte switch.
        if (frame.SourceId == DeviceFramer.PtscDstId)
        {
            switch (frame.CommandId)
            {
                case DeviceCommandId.Ptsc.GeneralStatusGet:
                    {
                        var r = DeviceGeneralStatusResponse.Parse(frame.Payload);
                        if (r is null) break;
                        ApplyGeneralStatus(r);
                        break;
                    }
                case DeviceCommandId.Ptsc.MotorSet:
                case DeviceCommandId.Ptsc.MotorStatusGet:
                    {
                        var r = DevicePanTiltResponse.Parse(frame.Payload);
                        if (r is null) break;
                        PanFeedbackDeg = r.PanAngleDeg;
                        TiltFeedbackDeg = r.TiltAngleDeg;
                        LastMotorStatusPolled = DateTime.Now.ToString("HH:mm:ss");
                        MotorPanAngle = $"{r.PanAngleDeg:F2}°";
                        MotorTiltAngle = $"{r.TiltAngleDeg:F2}°";
                        MotorPanSpeed = $"{r.PanSpeed:F2} °/s";
                        MotorTiltSpeed = $"{r.TiltSpeed:F2} °/s";
                        MotorPanMode = $"{DevicePanTiltResponse.ModeName(r.PanMode)}" +
                                       $" ({(r.PanActive ? "Active" : "Deactivated")})";
                        MotorTiltMode = $"{DevicePanTiltResponse.ModeName(r.TiltMode)}" +
                                        $" ({(r.TiltActive ? "Active" : "Deactivated")})";
                        break;
                    }
                case DeviceCommandId.Ptsc.Ibit:
                    {
                        var r = DeviceIbitResponse.Parse(frame.Payload);
                        if (r is null) break;
                        ApplyIbitResponse(r);
                        break;
                    }
                case DeviceCommandId.Ptsc.StabStatusGet:
                    {
                        var r = DeviceStabStatusResponse.Parse(frame.Payload);
                        if (r is null) break;
                        LastStabStatusPolled = DateTime.Now.ToString("HH:mm:ss");
                        StabUptime = $"{r.UptimeMicros / 1_000_000.0:F1} s  ({r.UptimeMicros} µs)";
                        StabImu = r.ImuOk ? $"OK (0x{r.ImuStatus:X4})" : $"FAULT (0x{r.ImuStatus:X4})";
                        StabAccel = $"X {r.AccelX:F3}  Y {r.AccelY:F3}  Z {r.AccelZ:F3}";
                        StabGyro = $"X {r.GyroX:F3}  Y {r.GyroY:F3}  Z {r.GyroZ:F3}";
                        StabTemp = $"{r.TemperatureC:F1} °C";
                        StabEkf = $"Pitch {r.EkfPitchDeg:F2}°  Yaw {r.EkfYawDeg:F2}°";
                        StabNudge = $"Pan {r.PanNudgeTicks} t/s  Tilt {r.TiltNudgeTicks} t/s";
                        break;
                    }
                    // StabSet has no structured feedback beyond the Active flag
                    // (handled elsewhere); acked silently.
            }
            return;
        }

        switch (frame.CommandId)
        {
            case DeviceCommandId.GeneralStatus:
                {
                    var r = DeviceGeneralStatusResponse.Parse(frame.Payload);
                    if (r is null) break;
                    ApplyGeneralStatus(r);
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
            case DeviceCommandId.Ibit:
                {
                    var r = DeviceIbitResponse.Parse(frame.Payload);
                    if (r is null) break;
                    ApplyIbitResponse(r);
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
                    SetPointerStateFromDevice(r.PointerOn);
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
                        SetPointerStateFromDevice(frame.Payload[0] == 0x02);
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
        GetMotorStatusCommand.NotifyCanExecuteChanged();
        GetStabStatusCommand.NotifyCanExecuteChanged();
        SetControllerModeRateCommand.NotifyCanExecuteChanged();
        SetControllerModeStabCommand.NotifyCanExecuteChanged();
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
        GetLrfRangeCommand.NotifyCanExecuteChanged();
        SetLrfMinRangeCommand.NotifyCanExecuteChanged();
        SetLrfMaxRangeCommand.NotifyCanExecuteChanged();
        RequestLrfStatusCommand.NotifyCanExecuteChanged();
        RequestLrfOpticalCrosstalkCommand.NotifyCanExecuteChanged();
        RequestLrfIdentificationCommand.NotifyCanExecuteChanged();
        RequestLrfDiagnosticsCommand.NotifyCanExecuteChanged();
        ResetLrfErrorCounterCommand.NotifyCanExecuteChanged();
        ApplyLrfBaudRateCommand.NotifyCanExecuteChanged();
        SendNirBcCommand.NotifyCanExecuteChanged();
        SendMwirBcCommand.NotifyCanExecuteChanged();
        SendStream1SymbologyCommand.NotifyCanExecuteChanged();
        SendStream2SymbologyCommand.NotifyCanExecuteChanged();
        RunFullIbitCommand.NotifyCanExecuteChanged();
        RunSilentIbitCommand.NotifyCanExecuteChanged();
        ReadIbitCommand.NotifyCanExecuteChanged();
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

        // Controller only drives motion in Rate or Stabilised mode.
        if (!IsControllerArmed)
        {
            _wasMoving = false;
            return;
        }

        double boost = 1.0 + s.RightTrigger * 1.0;
        double slow = 1.0 - s.LeftTrigger * 0.9;
        _speedMultiplier = boost * slow;

        // Read sticks. Tilt has a fixed base inversion (stick up = tilt up).
        double rawPanX = s.LeftStickX + s.RightStickX * 0.2;
        double rawTiltY = -(s.LeftStickY + s.RightStickY * 0.2);

        // Optional inverted-controls toggle flips BOTH axes independently.
        double panX = InvertControls ? -rawPanX : rawPanX;
        double tiltY = InvertControls ? -rawTiltY : rawTiltY;

        panX = Clamp(panX * _speedMultiplier, -1.0, 1.0);
        tiltY = Clamp(tiltY * _speedMultiplier, -1.0, 1.0);

        bool isMoving = Math.Abs(panX) > 0.001 || Math.Abs(tiltY) > 0.001;
        var now = DateTime.UtcNow;
        bool dueByTime = (now - _lastMotorTx).TotalMilliseconds >= MotorTxIntervalMs;
        bool edgeMoving = isMoving && !_wasMoving;
        bool edgeStop = !isMoving && _wasMoving;

        // A STOP (edge from moving → not moving) is a safety action and must
        // always be sent, even if a send is currently in flight. Movement
        // updates are dropped (coalesced) while a send is busy so commands
        // never pile up on the wire and outlive the stick — which is what
        // caused the gimbal to keep moving after the stick was released.
        if (edgeStop)
        {
            _wasMoving = false;
            _lastMotorTx = now;
            // Force a definitive zero-velocity command; bypass the busy guard.
            _ = SendControllerStopAsync();
            return;
        }

        if (!(edgeMoving || (isMoving && dueByTime)))
            return;

        // Drop-if-busy: if the previous controller frame is still being written,
        // skip this tick. Only the most recent stick value matters, so there is
        // no point queuing stale movement frames behind a slow wire.
        if (System.Threading.Interlocked.CompareExchange(ref _controllerTxBusy, 1, 0) != 0)
            return;

        byte mode = ControllerDriveMode;
        double maxDps = MaxStickSpeedDps * (FineControlEnabled ? FineControlFactor : 1.0);
        double panSpeedDps = panX * maxDps;
        double tiltSpeedDps = tiltY * maxDps;

        _lastMotorTx = now;
        _wasMoving = isMoving;

        _ = SendControllerMoveAsync(mode, panSpeedDps, tiltSpeedDps);
    }

    // In-flight guard so controller movement frames coalesce instead of queuing.
    private int _controllerTxBusy;

    private async Task SendControllerMoveAsync(byte mode, double panDps, double tiltDps)
    {
        try
        {
            await _service.SendPanTiltAsync(
                panMode: mode, tiltMode: mode,
                panAngleDeg: 0, tiltAngleDeg: 0,
                panSpeedDps: panDps, tiltSpeedDps: tiltDps);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _controllerTxBusy, 0);
        }
    }

    private async Task SendControllerStopAsync()
    {
        // Zero velocity in the current armed mode = halt. Sent unconditionally.
        byte mode = ControllerDriveMode;
        await _service.SendPanTiltAsync(
            panMode: mode, tiltMode: mode,
            panAngleDeg: 0, tiltAngleDeg: 0,
            panSpeedDps: 0, tiltSpeedDps: 0);
    }

    public void HandleControllerButton(XboxButton btn)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (btn)
            {
                case XboxButton.A:
                    // Stab ON — Stabilised mode, Active (control byte 0x06).
                    _ = _service.SendStabControlAsync(
                        panMode: 0x03, panDisengage: false,
                        tiltMode: 0x03, tiltDisengage: false);
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
                    // Home — position mode + angles 0, modest slew (10°/s)
                    _ = _service.SendPanTiltAsync(0x01, 0x01, 0, 0, 10, 10);
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