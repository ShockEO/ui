using System;
using ShockUI.Models.App;
using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI.Models.PanTilt;
using ShockUI.Services.Device;   // DecodedFrameRow
using ShockUI.Services.Eos;
using ShockUI.Services.App;
using ShockUI.Services.PanTilt;

using Avalonia.Controls;
using ShockUI.Views;

namespace ShockUI.ViewModels.Modules;

public sealed partial class PanTiltControllerViewModel : ViewModelBase
{
    private readonly PanTiltSerialService _service;

    /// <summary>Xbox-controller polling/event service. Created in the ctor; runs in the background.</summary>
    private readonly XboxControllerService _controller = new();

    /// <summary>Last snapshot — bound to the visual.</summary>
    [ObservableProperty] private XboxControllerState? controllerState;

    /// <summary>Whether to render the on-screen controller visual.</summary>
    [ObservableProperty] private bool showControllerVisual = true;

    /// <summary>True when a real Xbox controller is currently connected.</summary>
    [ObservableProperty] private bool isPhysicalControllerConnected;

    /// <summary>
    /// True when the on-screen visual should accept clicks. We default to
    /// "yes, accept clicks" so virtual control works out of the box. As soon
    /// as a real controller plugs in we flip this to false so the real input
    /// takes priority.
    /// </summary>
    public bool IsControllerInteractionEnabled =>
        IsConnected && !IsPhysicalControllerConnected;

    partial void OnIsPhysicalControllerConnectedChanged(bool value)
        => OnPropertyChanged(nameof(IsControllerInteractionEnabled));

    /// <summary>
    /// Speed multiplier applied to stick magnitudes. Adjusted by the triggers:
    ///   LT held → 0.10 (precision)
    ///   RT held → 2.00 (slew)
    ///   Both / neither → 1.00
    /// </summary>
    private double _speedMultiplier = 1.0;

    /// <summary>Max raw motor speed value sent when a stick is fully deflected.</summary>
    private const int MaxStickSpeed = 1000;

    /// <summary>Set when the last frame had non-zero stick input. Used to fire a single
    /// explicit stop frame when sticks return to centre.</summary>
    private bool _wasMoving;

    /// <summary>Throttle: send motor frames at most once per this many ms.</summary>
    private const int MotorTxIntervalMs = 50;
    private DateTime _lastMotorTx = DateTime.MinValue;


    // -----------------------------------------------------------------------
    // Module
    // -----------------------------------------------------------------------
    [ObservableProperty] private string moduleTitle = "Pan/Tilt Controller";

    // -----------------------------------------------------------------------
    // Connection
    // -----------------------------------------------------------------------
    [ObservableProperty] private ObservableCollection<string> availablePorts = [];
    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private bool isSimulationMode = false;
    [ObservableProperty] private bool isSimFaultMode;          // fault injection toggle
    [ObservableProperty] private string statusText = "Disconnected";

    // -----------------------------------------------------------------------
    // §3.3.1  General Status
    // -----------------------------------------------------------------------
    [ObservableProperty] private string systemState = "—";
    [ObservableProperty] private bool systemReady;
    [ObservableProperty] private bool powerGood = true;
    [ObservableProperty] private bool imuOk = true;
    [ObservableProperty] private bool panEncoderOk = true;
    [ObservableProperty] private bool tiltEncoderOk = true;
    [ObservableProperty] private bool panAdcOk = true;
    [ObservableProperty] private bool tiltAdcOk = true;
    [ObservableProperty] private bool motorBrakeOn;
    [ObservableProperty] private bool panMotorActive = true;
    [ObservableProperty] private bool panCalDone = true;
    [ObservableProperty] private bool panMotorFault;
    [ObservableProperty] private bool tiltMotorActive = true;
    [ObservableProperty] private bool tiltCalDone = true;
    [ObservableProperty] private bool tiltMotorFault;
    [ObservableProperty] private byte panControlMode;
    [ObservableProperty] private byte tiltControlMode;
    [ObservableProperty] private double busVoltage;

    // -----------------------------------------------------------------------
    // §3.3.2  Motor Control – SET parameters
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte setMotorPanMode = 3;  // 0=Safety,1=Rate,2=Pos,3=Stab
    [ObservableProperty] private bool setPanDisengage;
    [ObservableProperty] private byte setMotorTiltMode = 3;
    [ObservableProperty] private bool setTiltDisengage;
    [ObservableProperty] private uint setPanPosition;
    [ObservableProperty] private int setPanSpeed;
    [ObservableProperty] private uint setTiltPosition;
    [ObservableProperty] private int setTiltSpeed;

    // §3.3.2  Motor Control – GET feedback
    [ObservableProperty] private byte fbPanMode;
    [ObservableProperty] private bool fbPanActive;
    [ObservableProperty] private byte fbTiltMode;
    [ObservableProperty] private bool fbTiltActive;
    [ObservableProperty] private uint fbPanPosition;
    [ObservableProperty] private int fbPanSpeed;
    [ObservableProperty] private uint fbTiltPosition;
    [ObservableProperty] private int fbTiltSpeed;
    [ObservableProperty] private bool fbPanPositionValid;
    [ObservableProperty] private bool fbTiltPositionValid;

    // Derived display
    public double PanAngleDeg => FbPanPosition * 0.000171661;
    public double TiltAngleDeg => FbTiltPosition * 0.000171661;

    // -----------------------------------------------------------------------
    // §3.3.3  Stab Control
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte setStabPanMode = 3;
    [ObservableProperty] private bool setStabPanDisengage;
    [ObservableProperty] private byte setStabTiltMode = 3;
    [ObservableProperty] private bool setStabTiltDisengage;

    // EKF world-angle targets (Stab SET §3.3.3.3) – encoder ticks, int32
    [ObservableProperty] private int setEkfPitchTarget;
    [ObservableProperty] private int setEkfYawTarget;

    // EKF estimation feedback (Stab SET response §3.3.3.4)
    [ObservableProperty] private int fbEkfPitch;
    [ObservableProperty] private int fbEkfYaw;
    public double FbEkfPitchDeg => FbEkfPitch * 0.000171661;
    public double FbEkfYawDeg => FbEkfYaw * 0.000171661;

    // IMU feedback (from Stab GET response)
    [ObservableProperty] private bool imuCommsOk = true;
    [ObservableProperty] private bool imuBitOk = true;
    [ObservableProperty] private bool imuAccelRangeOk = true;
    [ObservableProperty] private bool imuGyroRangeOk = true;
    [ObservableProperty] private bool imuGyroHighRange;
    [ObservableProperty] private double imuAccelX;
    [ObservableProperty] private double imuAccelY;
    [ObservableProperty] private double imuAccelZ;
    [ObservableProperty] private double imuGyroX;    // rad/s
    [ObservableProperty] private double imuGyroY;
    [ObservableProperty] private double imuGyroZ;
    [ObservableProperty] private double imuTempC;
    [ObservableProperty] private uint imuTimestamp;
    [ObservableProperty] private string imuSummary = "—";

    // -----------------------------------------------------------------------
    // §3.3.4  IBIT
    // -----------------------------------------------------------------------
    [ObservableProperty] private byte ibitMode = 0x01;  // 0=Read,1=Full,2=Silent
    [ObservableProperty] private bool ibitInProgress;
    [ObservableProperty] private bool ibitLastPassed;
    [ObservableProperty] private bool ibitLastFailed;
    [ObservableProperty] private int ibitProgress;
    [ObservableProperty] private string ibitPanResult = "—";
    [ObservableProperty] private string ibitTiltResult = "—";
    [ObservableProperty] private string ibitSensorResult = "—";   // payload[4] – Sensor/Central Electronics
    [ObservableProperty] private string ibitSummary = "—";

    // ── §3.3.4.2 May 2026 SRS additions ────────────────────────────────
    /// <summary>Pan Encoder Zero Offset Test (Pan Motor Test Bits bit 6).
    /// "OK" = in factory safe range, "RECAL NEEDED" = out of range.</summary>
    [ObservableProperty] private string ibitPanZeroOffset = "—";

    /// <summary>Tilt Encoder Zero Offset Test (Tilt Motor Test Bits bit 7).</summary>
    [ObservableProperty] private string ibitTiltZeroOffset = "—";

    /// <summary>Pan Extended Faults summary (payload[5] / SRS byte 16).</summary>
    [ObservableProperty] private string ibitPanCbitResult = "—";

    /// <summary>Tilt Extended Faults summary (payload[6] / SRS byte 17).</summary>
    [ObservableProperty] private string ibitTiltCbitResult = "—";

    /// <summary>IMU PBIT/CBIT bits (payload[7] / SRS byte 18).</summary>
    [ObservableProperty] private string ibitImuStatusResult = "—";

    /// <summary>IMU range bits (payload[8] / SRS byte 19).</summary>
    [ObservableProperty] private string ibitImuRangeResult = "—";

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
    public ObservableCollection<string> TraceLines { get; } = [];

    /// <summary>
    /// Decoded view of the most recent frame seen on the wire (TX or RX).
    /// Populated by the FrameTransmitted/FrameReceived event handlers.
    /// </summary>
    public ObservableCollection<DecodedFrameRow> LastDecodedFrameRows { get; } = [];

    /// <summary>
    /// Header line shown above the decoded rows (e.g. "TX @ 10:46:52 – 14 bytes").
    /// </summary>
    [ObservableProperty] private string lastDecodedFrameHeader = "No frame received yet.";

    // -----------------------------------------------------------------------
    // Computed
    // -----------------------------------------------------------------------
    public bool IsConnected => _service.IsConnected;
    public bool CanConnect => !IsConnected;
    public bool CanDisconnect => IsConnected;
    public bool CanSend => IsConnected;

    public string ConnectionStateText => IsConnected ? "Connected" : "Disconnected";
    public string ModeStateText => IsSimulationMode ? "Simulation" : "Hardware";
    public string CalibrationStateText => SystemReady ? "Ready" : IsConnected ? "Initialising" : "—";
    public string FaultModeText => IsSimFaultMode ? "Fault ON" : "Fault OFF";

    public string[] ControlModeOpts => ["Safety/Damping", "Rate/Velocity", "Position", "Stabilised"];
    public string[] IbitModeOpts => ["Read Previous", "Full IBIT (motors)", "Silent IBIT (sensors)"];

    public string PanModeText => PanControlMode < ControlModeOpts.Length ? ControlModeOpts[PanControlMode] : "—";
    public string TiltModeText => TiltControlMode < ControlModeOpts.Length ? ControlModeOpts[TiltControlMode] : "—";

    // -----------------------------------------------------------------------
    // Ctor
    // -----------------------------------------------------------------------
    public PanTiltControllerViewModel(PanTiltSerialService service)
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
        };

        _service.FrameTransmitted += frame =>
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            TraceLines.Add($"{ts}  TX  {BitConverter.ToString(frame).Replace("-", " ")}");
            while (TraceLines.Count > 500) TraceLines.RemoveAt(0);
            TraceVersion++;
            DecodeFrame(frame, "TX", ts);
        };

        _service.FrameReceived += frame =>
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            TraceLines.Add($"{ts}  RX  {BitConverter.ToString(frame).Replace("-", " ")}");
            while (TraceLines.Count > 500) TraceLines.RemoveAt(0);
            TraceVersion++;
            DecodeFrame(frame, "RX", ts);
        };

        _service.ResponseReceived += OnResponseReceived;

        RefreshPorts();
        RefreshTransportState();

        // Xbox controller — start polling so the visual shows live state
        // even before maintenance unlocks anything else. ButtonPressed is
        // edge-triggered so we can wire discrete actions (A/B/X/Y) without
        // spam. StateChanged drives the analog stick/trigger mapping.
        _controller.StateChanged += HandleControllerState;
        _controller.ButtonPressed += HandleControllerButton;
        _controller.ControllerConnected += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsPhysicalControllerConnected = true;
                // Auto-hide the visual when a real controller plugs in
                ShowControllerVisual = false;
            });
        };
        _controller.ControllerDisconnected += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsPhysicalControllerConnected = false;
            });
        };
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

        // Auto-select a default port so the master-connection cascade
        // (driven from the System Controller) can connect Pan/Tilt without
        // the user having to pick a port on this module first. We prefer
        // "SIMULATED" if it's offered, otherwise fall back to the first
        // entry in the list. If SelectedPort is already set to something
        // valid we leave it alone.
        if (AvailablePorts.Count > 0)
        {
            // Pick the first real port. The simulation port (if it
            // appears at all) is only present when sim mode has been
            // explicitly enabled by the user, and even then we don't
            // auto-prefer it so startup defaults to hardware.
            if (string.IsNullOrWhiteSpace(SelectedPort) || !AvailablePorts.Contains(SelectedPort))
                SelectedPort = AvailablePorts[0];
        }
        else
        {
            SelectedPort = null;
        }
    }

    [RelayCommand]
    private void Connect()
    {
        if (SelectedPort is null) return;
        if (_service.Connect(SelectedPort))
        {
            ShowInfo("Connected.");
            // Immediate General Status request so the user sees system state
            // (Pan/Tilt feedback, IBIT, errors) without clicking Poll manually.
            try
            {
                _service.SendGeneralStatusRequest();
            }
            catch (Exception ex)
            {
                LogLines.Add($"{DateTime.Now:HH:mm:ss.fff}  WARN  Initial poll failed: {ex.Message}");
                LogVersion++;
            }
        }
        else
        {
            ShowError("Connection failed.");
        }
        NotifyConnectionState();
    }

    [RelayCommand]
    private void Disconnect()
    {
        _service.Disconnect();
        ResetFeedback();
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
            Title = "Pan/Tilt Port Settings",
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
    // Fault injection – individual flags (only active in simulation mode)
    // -----------------------------------------------------------------------

    // System health
    [ObservableProperty] private bool faultPowerFailure;
    [ObservableProperty] private bool faultImuError;
    [ObservableProperty] private bool faultPanEncoderError;
    [ObservableProperty] private bool faultTiltEncoderError;
    [ObservableProperty] private bool faultPanAdcError;
    [ObservableProperty] private bool faultTiltAdcError;
    [ObservableProperty] private bool faultMotorBrakeOn;
    [ObservableProperty] private bool faultSystemStateError;

    // Motor
    [ObservableProperty] private bool faultPanDisengaged;
    [ObservableProperty] private bool faultPanMotorFault;
    [ObservableProperty] private bool faultTiltDisengaged;
    [ObservableProperty] private bool faultTiltMotorFault;
    [ObservableProperty] private bool faultPanPositionInvalid;
    [ObservableProperty] private bool faultTiltPositionInvalid;

    // IBIT – Pan
    [ObservableProperty] private bool faultPanIbitStart;
    [ObservableProperty] private bool faultPanIbitConnection;
    [ObservableProperty] private bool faultPanIbitPolarity;
    [ObservableProperty] private bool faultPanIbitStall;
    [ObservableProperty] private bool faultPanIbitEncoder;
    [ObservableProperty] private bool faultPanIbitMotion;

    // IBIT – Tilt
    [ObservableProperty] private bool faultTiltIbitStart;
    [ObservableProperty] private bool faultTiltIbitConnection;
    [ObservableProperty] private bool faultTiltIbitPolarity;
    [ObservableProperty] private bool faultTiltIbitStall;
    [ObservableProperty] private bool faultTiltIbitEncoder;
    [ObservableProperty] private bool faultTiltIbitMinStop;
    [ObservableProperty] private bool faultTiltIbitMaxStop;

    // IBIT – Sensor / Central Electronics
    [ObservableProperty] private bool faultSensorPower;
    [ObservableProperty] private bool faultSensorBrake;
    [ObservableProperty] private bool faultSensorImuSelfTest;
    [ObservableProperty] private bool faultSensorImuComms;
    [ObservableProperty] private bool faultSensorPanAdc;
    [ObservableProperty] private bool faultSensorTiltAdc;

    [RelayCommand]
    private void ApplyFaults()
    {
        _service.SetSimFaults(new PanTiltSimFaultConfig
        {
            // System
            PowerFailure = FaultPowerFailure,
            ImuError = FaultImuError,
            PanEncoderError = FaultPanEncoderError,
            TiltEncoderError = FaultTiltEncoderError,
            PanAdcError = FaultPanAdcError,
            TiltAdcError = FaultTiltAdcError,
            MotorBrakeOn = FaultMotorBrakeOn,
            SystemStateError = FaultSystemStateError,
            // Motor
            PanDisengaged = FaultPanDisengaged,
            PanMotorFault = FaultPanMotorFault,
            TiltDisengaged = FaultTiltDisengaged,
            TiltMotorFault = FaultTiltMotorFault,
            PanPositionInvalid = FaultPanPositionInvalid,
            TiltPositionInvalid = FaultTiltPositionInvalid,
            // IBIT Pan
            PanIbitStartFail = FaultPanIbitStart,
            PanIbitConnectionFail = FaultPanIbitConnection,
            PanIbitPolarityFail = FaultPanIbitPolarity,
            PanIbitStall = FaultPanIbitStall,
            PanIbitEncoderFail = FaultPanIbitEncoder,
            PanIbitMotionFail = FaultPanIbitMotion,
            // IBIT Tilt
            TiltIbitStartFail = FaultTiltIbitStart,
            TiltIbitConnectionFail = FaultTiltIbitConnection,
            TiltIbitPolarityFail = FaultTiltIbitPolarity,
            TiltIbitStall = FaultTiltIbitStall,
            TiltIbitEncoderFail = FaultTiltIbitEncoder,
            TiltIbitMinStopFail = FaultTiltIbitMinStop,
            TiltIbitMaxStopFail = FaultTiltIbitMaxStop,
            // IBIT Sensor
            SensorIbitPowerFail = FaultSensorPower,
            SensorIbitBrakeOn = FaultSensorBrake,
            SensorIbitImuFail = FaultSensorImuSelfTest,
            SensorIbitImuComms = FaultSensorImuComms,
            SensorIbitPanAdc = FaultSensorPanAdc,
            SensorIbitTiltAdc = FaultSensorTiltAdc,
        });
        IsSimFaultMode = HasAnyFaultActive;
        ShowWarning(IsSimFaultMode
            ? "Fault injection ACTIVE — sim will return injected faults."
            : "All faults cleared — sim returning healthy state.");
    }

    [RelayCommand]
    private void ClearAllFaults()
    {
        FaultPowerFailure = FaultImuError = FaultPanEncoderError = FaultTiltEncoderError =
        FaultPanAdcError = FaultTiltAdcError = FaultMotorBrakeOn = FaultSystemStateError =
        FaultPanDisengaged = FaultPanMotorFault = FaultTiltDisengaged = FaultTiltMotorFault =
        FaultPanPositionInvalid = FaultTiltPositionInvalid =
        FaultPanIbitStart = FaultPanIbitConnection = FaultPanIbitPolarity =
        FaultPanIbitStall = FaultPanIbitEncoder = FaultPanIbitMotion =
        FaultTiltIbitStart = FaultTiltIbitConnection = FaultTiltIbitPolarity =
        FaultTiltIbitStall = FaultTiltIbitEncoder = FaultTiltIbitMinStop = FaultTiltIbitMaxStop =
        FaultSensorPower = FaultSensorBrake = FaultSensorImuSelfTest =
        FaultSensorImuComms = FaultSensorPanAdc = FaultSensorTiltAdc = false;

        ApplyFaults();
    }

    public bool HasAnyFaultActive =>
        FaultPowerFailure || FaultImuError || FaultPanEncoderError || FaultTiltEncoderError ||
        FaultPanAdcError || FaultTiltAdcError || FaultMotorBrakeOn || FaultSystemStateError ||
        FaultPanDisengaged || FaultPanMotorFault || FaultTiltDisengaged || FaultTiltMotorFault ||
        FaultPanPositionInvalid || FaultTiltPositionInvalid ||
        FaultPanIbitStart || FaultPanIbitConnection || FaultPanIbitPolarity ||
        FaultPanIbitStall || FaultPanIbitEncoder || FaultPanIbitMotion ||
        FaultTiltIbitStart || FaultTiltIbitConnection || FaultTiltIbitPolarity ||
        FaultTiltIbitStall || FaultTiltIbitEncoder || FaultTiltIbitMinStop || FaultTiltIbitMaxStop ||
        FaultSensorPower || FaultSensorBrake || FaultSensorImuSelfTest ||
        FaultSensorImuComms || FaultSensorPanAdc || FaultSensorTiltAdc;

    // -----------------------------------------------------------------------
    // §3.3.1  General Status
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void PollGeneralStatus() => _service.SendGeneralStatusRequest();

    // -----------------------------------------------------------------------
    // §3.3.2  Motor Control
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void GetMotorControl() => _service.SendMotorControlGet();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SetMotorControl()
    {
        _service.SendMotorControlSet(
            SetMotorPanMode, SetPanDisengage,
            SetMotorTiltMode, SetTiltDisengage,
            SetPanPosition, SetPanSpeed,
            SetTiltPosition, SetTiltSpeed);
        ShowInfo("Motor SET command sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.3  Stab Control
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void GetStabControl() => _service.SendStabControlGet();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void SetStabControl()
    {
        _service.SendStabControlSet(
            SetStabPanMode, SetStabPanDisengage,
            SetStabTiltMode, SetStabTiltDisengage,
            SetEkfPitchTarget, SetEkfYawTarget);
        ShowInfo("Stab SET command sent.");
    }

    // -----------------------------------------------------------------------
    // §3.3.4  IBIT
    // -----------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void RunIbit()
    {
        IbitSummary = "Running…";
        IbitProgress = 0;
        _service.SendIbit((byte)(IbitMode & 0x03));
        ShowInfo($"IBIT sent — mode: {IbitModeOpts[IbitMode]}");
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void ReadIbitResults() => _service.SendIbit(0x00);

    // -----------------------------------------------------------------------
    // Log
    // -----------------------------------------------------------------------

    [RelayCommand] private void ClearLog() { LogLines.Clear(); LogVersion++; }
    [RelayCommand] private void ClearTrace() { TraceLines.Clear(); TraceVersion++; }

    /// <summary>Clear the Decoded Frame panel.</summary>
    [RelayCommand]
    private void ClearDecodedFrame()
    {
        LastDecodedFrameRows.Clear();
        LastDecodedFrameHeader = "No frame received yet.";
    }

    /// <summary>
    /// Decode a raw EOS frame and push the labelled rows into the
    /// LastDecodedFrameRows collection. Maps PTSC command IDs to friendly
    /// names from <see cref="PanTiltCommandId"/>.
    /// </summary>
    private void DecodeFrame(byte[] frame, string direction, string timestamp)
    {
        LastDecodedFrameRows.Clear();
        foreach (var row in EosFrameDecoder.Decode(
            frame,
            cmdId => cmdId switch
            {
                PanTiltCommandId.GeneralStatus => "GeneralStatus (§3.3.1)",
                PanTiltCommandId.MotorControlGet => "MotorControl Get (§3.3.2.1)",
                PanTiltCommandId.MotorControlSet => "MotorControl Set (§3.3.2.3)",
                PanTiltCommandId.StabControlGet => "StabControl Get (§3.3.3.1)",
                PanTiltCommandId.StabControlSet => "StabControl Set (§3.3.3.3)",
                PanTiltCommandId.Ibit => "IBIT (§3.3.4)",
                _ => null
            },
            hostSrcId: 0x10,   // SCP
            targetDstId: 0x20))  // PTSC
        {
            LastDecodedFrameRows.Add(row);
        }
        LastDecodedFrameHeader = $"{direction} @ {timestamp}  —  {frame.Length} bytes";
    }
    [RelayCommand] private void DismissBanner() { IsBannerVisible = false; BannerText = string.Empty; }

    // -----------------------------------------------------------------------
    // Response handling
    // -----------------------------------------------------------------------

    private void OnResponseReceived(EosParsedFrame frame)
    {
        switch (frame.Command)
        {
            case PanTiltCommandId.GeneralStatus: ParseGeneralStatus(frame.Payload); break;
            case PanTiltCommandId.MotorControlGet:
            case PanTiltCommandId.MotorControlSet: ParseMotorControl(frame.Payload); break;
            case PanTiltCommandId.StabControlGet:
            case PanTiltCommandId.StabControlSet: ParseStabControl(frame.Payload); break;
            case PanTiltCommandId.Ibit: ParseIbit(frame.Payload); break;
        }

        if (frame.HasError)
            ShowWarning($"Error 0x{frame.ErrorCode:X4} in response CMD=0x{frame.Command:X4}");
    }

    private void ParseGeneralStatus(byte[] p)
    {
        if (p.Length < 6) return;

        byte sys = p[0];
        byte sys2 = p[1];
        byte pan = p[2];
        byte tilt = p[3];
        byte mode = p[4];

        SystemState = (sys & 0x07) switch
        {
            0 => "Operational (C)",
            1 => "Maintenance (D)",
            2 => "Built-In-Test (E)",
            3 => "Error (F)",
            4 => "Initialization (B)",
            _ => "Unknown"
        };
        SystemReady = (sys & 0x08) != 0;
        ImuOk = (sys & 0x10) == 0;   // 0 = good
        PanEncoderOk = (sys & 0x20) == 0;
        TiltEncoderOk = (sys & 0x40) == 0;
        PowerGood = (sys & 0x80) == 0;

        PanAdcOk = (sys2 & 0x01) == 0;
        TiltAdcOk = (sys2 & 0x02) == 0;
        MotorBrakeOn = (sys2 & 0x04) != 0;

        PanMotorActive = (pan & 0x01) == 0;
        PanCalDone = (pan & 0x02) == 0;
        PanMotorFault = (pan & 0x04) != 0;

        TiltMotorActive = (tilt & 0x01) == 0;
        TiltCalDone = (tilt & 0x02) == 0;
        TiltMotorFault = (tilt & 0x04) != 0;

        PanControlMode = (byte)(mode & 0x03);
        TiltControlMode = (byte)((mode >> 2) & 0x03);

        if (p.Length >= 8)
        {
            short rawV = (short)(p[5] | (p[6] << 8));
            // Scale: Vpin = rawV * (2.5 / 16384), then apply hardware divider
            // Divider ratio TBD per hardware — display raw ADC code for now
            BusVoltage = rawV;
        }

        OnPropertyChanged(nameof(PanModeText));
        OnPropertyChanged(nameof(TiltModeText));
        OnPropertyChanged(nameof(CalibrationStateText));
    }

    private void ParseMotorControl(byte[] p)
    {
        if (p.Length < 18) return;

        byte panCtrl = p[0];
        byte tiltCtrl = p[1];

        FbPanActive = (panCtrl & 0x01) == 0;
        FbPanMode = (byte)((panCtrl >> 1) & 0x03);
        FbTiltActive = (tiltCtrl & 0x01) == 0;
        FbTiltMode = (byte)((tiltCtrl >> 1) & 0x03);

        uint rawPan = ReadUInt32Le(p, 2);
        uint rawTilt = ReadUInt32Le(p, 6);

        // Bit 30 = Position Valid flag in the raw SSI word
        FbPanPositionValid = (rawPan & (1u << 30)) != 0;
        FbTiltPositionValid = (rawTilt & (1u << 30)) != 0;

        // Position = bits 27:7 of the 32-bit SSI word
        FbPanPosition = (rawPan >> 7) & 0x1FFFFFu;
        FbTiltPosition = (rawTilt >> 7) & 0x1FFFFFu;

        FbPanSpeed = ReadInt32Le(p, 10);
        FbTiltSpeed = ReadInt32Le(p, 14);

        OnPropertyChanged(nameof(PanAngleDeg));
        OnPropertyChanged(nameof(TiltAngleDeg));
    }

    private void ParseStabControl(byte[] p)
    {
        if (p.Length < 2) return;

        // Bytes 0-1 are common to both GET and SET responses:
        // Control Status: bit0=disengaged, bits1:2=mode
        byte panCtrl = p[0];
        byte tiltCtrl = p[1];
        SetStabPanMode = (byte)((panCtrl >> 1) & 0x03);
        SetStabPanDisengage = (panCtrl & 0x01) != 0;
        SetStabTiltMode = (byte)((tiltCtrl >> 1) & 0x03);
        SetStabTiltDisengage = (tiltCtrl & 0x01) != 0;

        if (p.Length == 12)
        {
            // ── Stab Control SET response (Length=0x0C) ──────────────────
            // p[2..5] = EKF Pitch Estimation (int32 LE)
            // p[6..9] = EKF Yaw Estimation (int32 LE)
            FbEkfPitch = ReadInt32Le(p, 2);
            FbEkfYaw = ReadInt32Le(p, 6);
            OnPropertyChanged(nameof(FbEkfPitchDeg));
            OnPropertyChanged(nameof(FbEkfYawDeg));
        }
        else if (p.Length >= 34)
        {
            // ── Stab Control GET response ────────────────────────────────
            // May 2026 SRS: Length=0x34 (52 bytes), payload bytes shown below
            //   p[2..5]   = IMU Timestamp (uint32 LE)
            //   p[6]      = IMU Status Byte 1  (comms, BIT, accel/gyro axis health)
            //   p[7]      = IMU Status Byte 2  (range, high-range flag)
            //   p[8..11]  = IMU Accel X (int32 LE)
            //   p[12..15] = IMU Accel Y (int32 LE)
            //   p[16..19] = IMU Accel Z (int32 LE)
            //   p[20..23] = IMU Gyro X  (int32 LE)
            //   p[24..27] = IMU Gyro Y  (int32 LE)
            //   p[28..31] = IMU Gyro Z  (int32 LE)
            //   p[32..33] = IMU Temp    (int16 LE, unit = 0.01°C)
            //   p[34..37] = EKF Pitch Estimation (int32 LE)        [NEW]
            //   p[38..41] = EKF Yaw   Estimation (int32 LE)        [NEW]
            //   p[42..45] = Current Pan Speed   (int32 LE, ticks/s) [NEW]
            //   p[46..49] = Current Tilt Speed  (int32 LE, ticks/s) [NEW]
            ImuTimestamp = ReadUInt32Le(p, 2);

            byte imust1 = p[6];
            byte imust2 = p[7];
            ImuCommsOk = (imust1 & 0x01) != 0;
            ImuBitOk = (imust1 & 0x02) != 0;
            ImuAccelRangeOk = (imust2 & 0x01) != 0;
            ImuGyroRangeOk = (imust2 & 0x02) != 0;
            ImuGyroHighRange = (imust2 & 0x04) != 0;

            double gyroScale = ImuGyroHighRange ? 12304174.0 : 67108864.0;

            ImuAccelX = ReadInt32Le(p, 8);
            ImuAccelY = ReadInt32Le(p, 12);
            ImuAccelZ = ReadInt32Le(p, 16);
            ImuGyroX = ReadInt32Le(p, 20) / gyroScale;
            ImuGyroY = ReadInt32Le(p, 24) / gyroScale;
            ImuGyroZ = ReadInt32Le(p, 28) / gyroScale;

            if (p.Length >= 34)
                ImuTempC = (short)(p[32] | (p[33] << 8)) / 100.0;

            // May 2026 additions
            if (p.Length >= 42)
            {
                FbEkfPitch = ReadInt32Le(p, 34);
                FbEkfYaw = ReadInt32Le(p, 38);
                OnPropertyChanged(nameof(FbEkfPitchDeg));
                OnPropertyChanged(nameof(FbEkfYawDeg));
            }
            if (p.Length >= 50)
            {
                FbPanSpeed = ReadInt32Le(p, 42);
                FbTiltSpeed = ReadInt32Le(p, 46);
            }

            ImuSummary = $"Gyro: X={ImuGyroX:F4} Y={ImuGyroY:F4} Z={ImuGyroZ:F4} rad/s | " +
                         $"Comms:{(ImuCommsOk ? "OK" : "ERR")} BIT:{(ImuBitOk ? "OK" : "ERR")}";
        }
    }

    private void ParseIbit(byte[] p)
    {
        if (p.Length < 2) return;

        byte status = p[0];
        IbitInProgress = (status & 0x01) != 0;
        IbitLastPassed = (status & 0x02) != 0;
        IbitLastFailed = (status & 0x04) != 0;
        IbitProgress = p[1];

        // Per SRS §3.3.4.2.1: specific failure flags are only valid when LastFailed is set.
        // When test is in progress or no test has run, show N/A.
        bool flagsValid = IbitLastPassed || IbitLastFailed;

        if (p.Length >= 3)
        {
            IbitPanResult = flagsValid ? DecodePanIbit(p[2]) : "N/A";
            // NEW (May 2026): bit 6 = Pan Encoder Zero Offset Test
            //   0 = in safe factory range, 1 = needs recalibration
            IbitPanZeroOffset = !flagsValid ? "N/A"
                              : (p[2] & 0x40) != 0 ? "RECAL NEEDED"
                              : "OK";
        }

        if (p.Length >= 4)
        {
            IbitTiltResult = flagsValid ? DecodeTiltIbit(p[3]) : "N/A";
            // NEW: bit 7 = Tilt Encoder Zero Offset Test
            IbitTiltZeroOffset = !flagsValid ? "N/A"
                               : (p[3] & 0x80) != 0 ? "RECAL NEEDED"
                               : "OK";
        }

        if (p.Length >= 5)
            IbitSensorResult = flagsValid ? DecodeSensorIbit(p[4]) : "N/A";

        // May 2026 additions ─ Pan/Tilt CBIT + IMU status
        if (p.Length >= 6)
            IbitPanCbitResult = flagsValid ? DecodeCbitFaults("Pan", p[5]) : "N/A";
        if (p.Length >= 7)
            IbitTiltCbitResult = flagsValid ? DecodeCbitFaults("Tilt", p[6]) : "N/A";
        if (p.Length >= 8)
            IbitImuStatusResult = flagsValid ? DecodeImuStatus(p[7]) : "N/A";
        if (p.Length >= 9)
            IbitImuRangeResult = flagsValid ? DecodeImuRange(p[8]) : "N/A";

        IbitSummary = IbitInProgress
            ? $"Running… {IbitProgress}%"
            : IbitLastPassed ? "PASSED"
            : IbitLastFailed ? "FAILED"
            : "No result";

        if (!IbitInProgress && IbitLastPassed) ShowSuccess("IBIT complete — PASSED.");
        if (!IbitInProgress && IbitLastFailed) ShowError("IBIT complete — FAILED.");
    }

    /// <summary>
    /// §3.3.4.2 Pan Motor Test Bits (payload[2]).
    /// bit0=Start, bit1=Connection, bit2=Polarity, bit3=Stall,
    /// bit4=Encoder, bit5=Motion Range (360° rotation)
    /// </summary>
    private static string DecodePanIbit(byte bits)
    {
        if (bits == 0) return "Pan: All Pass";
        string[] labels = ["Start", "Connection", "Polarity", "Stall", "Encoder", "Motion Range (360°)"];
        var fails = new System.Collections.Generic.List<string>();
        for (int i = 0; i < labels.Length; i++)
            if ((bits & (1 << i)) != 0) fails.Add(labels[i]);
        return $"Pan FAIL: {string.Join(", ", fails)}";
    }

    /// <summary>
    /// §3.3.4.2 Tilt Motor Test Bits (payload[3]).
    /// bit0=Start, bit1=Connection, bit2=Polarity, bit3=Stall,
    /// bit4=Encoder, bit5=Min End Stop (−30°), bit6=Max End Stop (+75°)
    /// </summary>
    private static string DecodeTiltIbit(byte bits)
    {
        if (bits == 0) return "Tilt: All Pass";
        string[] labels = ["Start", "Connection", "Polarity", "Stall", "Encoder", "Min End Stop (−30°)", "Max End Stop (+75°)"];
        var fails = new System.Collections.Generic.List<string>();
        for (int i = 0; i < labels.Length; i++)
            if ((bits & (1 << i)) != 0) fails.Add(labels[i]);
        return $"Tilt FAIL: {string.Join(", ", fails)}";
    }

    /// <summary>
    /// §3.3.4.2 Sensor/Central Electronics Test Bits (payload[4]).
    /// bit0=Power Good, bit1=Motor Brake (should be OFF), bit2=IMU Self-Test,
    /// bit3=IMU Comms, bit4=Pan ADC, bit5=Tilt ADC
    /// </summary>
    private static string DecodeSensorIbit(byte bits)
    {
        if (bits == 0) return "Sensors: All Pass";
        string[] labels = ["Power Good", "Motor Brake ON (fault)", "IMU Self-Test", "IMU Comms", "Pan ADC", "Tilt ADC"];
        var fails = new System.Collections.Generic.List<string>();
        for (int i = 0; i < labels.Length; i++)
            if ((bits & (1 << i)) != 0) fails.Add(labels[i]);
        return $"Sensor FAIL: {string.Join(", ", fails)}";
    }

    /// <summary>
    /// §3.3.4.2 May 2026 — Pan/Tilt Extended Faults (CBIT). Bytes 16/17.
    /// bit0=ISR Overcurrent, bit1=ISR Control Saturation (PI Flatline),
    /// bit2=PASSIVE ADC KCL Error, bit3=PASSIVE ADC Rail Short,
    /// bit4=PASSIVE ADC Phantom Voltage. Bits 5-7 reserved.
    /// </summary>
    private static string DecodeCbitFaults(string axis, byte bits)
    {
        if (bits == 0) return $"{axis} CBIT: All Pass";
        string[] labels = [
            "ISR Overcurrent",
            "ISR Control Saturation (PI Flatline)",
            "ADC KCL Error",
            "ADC Rail Short",
            "ADC Phantom Voltage",
        ];
        var fails = new System.Collections.Generic.List<string>();
        for (int i = 0; i < labels.Length; i++)
            if ((bits & (1 << i)) != 0) fails.Add(labels[i]);
        return $"{axis} CBIT FAIL: {string.Join(", ", fails)}";
    }

    /// <summary>
    /// §3.3.4.2 May 2026 — IMU Status bits (byte 18 / payload[7]).
    /// Each bit: 1 = pass, 0 = fail.
    /// bit0=Communication, bit1=IMU BIT,
    /// bit2-4=Acc X/Y/Z PBIT+CBIT, bit5-7=Gyro X/Y/Z PBIT+CBIT.
    /// </summary>
    private static string DecodeImuStatus(byte bits)
    {
        if (bits == 0xFF) return "IMU PBIT: All Pass";
        string[] labels = ["Communication", "IMU BIT",
                           "Acc X", "Acc Y", "Acc Z",
                           "Gyro X", "Gyro Y", "Gyro Z"];
        var fails = new System.Collections.Generic.List<string>();
        for (int i = 0; i < labels.Length; i++)
            if ((bits & (1 << i)) == 0) fails.Add(labels[i]);     // 0 = fail
        return $"IMU PBIT FAIL: {string.Join(", ", fails)}";
    }

    /// <summary>
    /// §3.3.4.2 May 2026 — IMU Status bits 2 (byte 19 / payload[8]).
    /// bit0=Accelerometer Operational Range (1=ok),
    /// bit1=Gyroscope Operational Range (1=ok),
    /// bit2=Gyroscope High Range Active (informational, 1=above 1833°/s).
    /// Bits 3-7 reserved / padding.
    /// </summary>
    private static string DecodeImuRange(byte bits)
    {
        bool accOk = (bits & 0x01) != 0;
        bool gyroOk = (bits & 0x02) != 0;
        bool gyroHigh = (bits & 0x04) != 0;

        if (!accOk || !gyroOk)
        {
            var fails = new System.Collections.Generic.List<string>();
            if (!accOk) fails.Add("Accel Range");
            if (!gyroOk) fails.Add("Gyro Range");
            return $"IMU Range FAIL: {string.Join(", ", fails)}";
        }
        return gyroHigh
            ? "IMU Range: All Pass (Gyro HIGH-RANGE active)"
            : "IMU Range: All Pass";
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private void ResetFeedback()
    {
        SystemState = "—"; SystemReady = false;
        FbPanPosition = 0; FbTiltPosition = 0;
        FbPanSpeed = 0; FbTiltSpeed = 0;
        ImuSummary = "—"; IbitSummary = "—";
        IbitPanZeroOffset = "—"; IbitTiltZeroOffset = "—";
        IbitPanCbitResult = "—"; IbitTiltCbitResult = "—";
        IbitImuStatusResult = "—"; IbitImuRangeResult = "—";
        OnPropertyChanged(nameof(PanAngleDeg));
        OnPropertyChanged(nameof(TiltAngleDeg));
    }

    private void NotifyConnectionState()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(ModeStateText));
        OnPropertyChanged(nameof(CalibrationStateText));
        OnPropertyChanged(nameof(IsControllerInteractionEnabled));
        PollGeneralStatusCommand.NotifyCanExecuteChanged();
        GetMotorControlCommand.NotifyCanExecuteChanged();
        SetMotorControlCommand.NotifyCanExecuteChanged();
        GetStabControlCommand.NotifyCanExecuteChanged();
        SetStabControlCommand.NotifyCanExecuteChanged();
        RunIbitCommand.NotifyCanExecuteChanged();
        ReadIbitResultsCommand.NotifyCanExecuteChanged();
    }

    private static uint ReadUInt32Le(byte[] p, int off)
        => (uint)(p[off] | (p[off + 1] << 8) | (p[off + 2] << 16) | (p[off + 3] << 24));

    private static int ReadInt32Le(byte[] p, int off)
        => p[off] | (p[off + 1] << 8) | (p[off + 2] << 16) | (p[off + 3] << 24);

    private void ShowInfo(string text) => ShowBanner(text, "#1E3A5F", "#2563EB", "#E2E8F0");
    private void ShowSuccess(string text) => ShowBanner(text, "#14532D", "#166534", "#DCFCE7");
    private void ShowWarning(string text) => ShowBanner(text, "#7C2D12", "#9A3412", "#FFEDD5");
    private void ShowError(string text) => ShowBanner(text, "#7F1D1D", "#991B1B", "#FEE2E2");

    private void ShowBanner(string text, string bg, string border, string fg)
    {
        BannerText = text; BannerBackground = Brush.Parse(bg);
        BannerBorderBrush = Brush.Parse(border); BannerForeground = Brush.Parse(fg);
        IsBannerVisible = true;
    }

    partial void OnIsSimulationModeChanged(bool value)
    {
        _service.SetSimulationMode(value);
        RefreshPorts();
        NotifyConnectionState();
    }

    partial void OnIsSimFaultModeChanged(bool value)
    {
        OnPropertyChanged(nameof(FaultModeText));
        OnPropertyChanged(nameof(HasAnyFaultActive));
    }

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


    // ─────────────────────────────────────────────────────────────────
    // Xbox controller event handlers — per-mapping defined in the SRS
    // discussion notes. See conversation transcript for the rationale.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Continuous-mapping callback (sticks + triggers). Runs at controller
    /// poll rate (50 ms). Decides whether to send a motor frame based on
    /// the TX throttle and whether the stick state actually changed.
    /// </summary>
    public void HandleControllerState(XboxControllerState s)
    {
        Dispatcher.UIThread.Post(() => ControllerState = s);

        // Trigger-driven speed scaler. Right trigger boosts; left trigger
        // slows down. Both held cancels out at 1.0.
        double boost = 1.0 + s.RightTrigger * 1.0;     // up to 2.0×
        double slow = 1.0 - s.LeftTrigger * 0.9;     // down to 0.1×
        _speedMultiplier = boost * slow;

        // Sum left stick (slew) and right stick (fine trim, scaled 0.2×)
        double panX = s.LeftStickX + s.RightStickX * 0.2;
        double tiltY = s.LeftStickY + s.RightStickY * 0.2;
        // Y is up on the stick but up in the world is +tilt, so invert sign
        tiltY = -tiltY;

        // Apply speed multiplier and clamp to [-1, 1]
        panX = Clamp(panX * _speedMultiplier, -1.0, 1.0);
        tiltY = Clamp(tiltY * _speedMultiplier, -1.0, 1.0);

        bool isMoving = Math.Abs(panX) > 0.001 || Math.Abs(tiltY) > 0.001;

        // Throttle TX so we don't flood the link at 20 Hz when the user
        // barely moved the sticks. Always send the first frame after
        // start-of-motion AND the first frame after stop-of-motion.
        var now = DateTime.UtcNow;
        bool dueByTime = (now - _lastMotorTx).TotalMilliseconds >= MotorTxIntervalMs;
        bool edgeMoving = isMoving && !_wasMoving;       // started moving
        bool edgeStop = !isMoving && _wasMoving;       // returned to centre

        if (edgeMoving || edgeStop || (isMoving && dueByTime))
        {
            int panSpeed = (int)(panX * MaxStickSpeed);
            int tiltSpeed = (int)(tiltY * MaxStickSpeed);

            // Mode=Speed for both axes; positions ignored in speed mode but
            // still passed as zeros per SRS.
            _service.SendMotorControlSet(
                panMode: 0x01,    // 01 = Speed mode (bit 1)
                panDisengage: false,
                tiltMode: 0x01,
                tiltDisengage: false,
                panPosition: 0u,
                panSpeed: panSpeed,
                tiltPosition: 0u,
                tiltSpeed: tiltSpeed);

            _lastMotorTx = now;
            _wasMoving = isMoving;
        }
    }

    /// <summary>
    /// Discrete-mapping callback (button presses — edge-triggered).
    /// </summary>
    public void HandleControllerButton(XboxButton btn)
    {
        Dispatcher.UIThread.Post(() =>
        {
            switch (btn)
            {
                case XboxButton.A:
                    // Toggle stabilisation
                    _service.SendStabControlSet(panMode: 0x01, panDisengage: false, tiltMode: 0x01, tiltDisengage: false);
                    break;

                case XboxButton.B:
                    // Emergency stop — speed=0 on both axes
                    _service.SendMotorControlSet(0x01, false, 0x01, false, 0u, 0, 0u, 0);
                    break;

                case XboxButton.X:
                    // Refresh motor feedback
                    _service.SendMotorControlGet();
                    break;

                case XboxButton.Y:
                    // Run IBIT
                    _service.SendIbit(mode: 0x01);
                    break;

                case XboxButton.LeftBumper:
                    // FOV cycle down — handled by VisNIR/SWIR module in practice;
                    // here we just log it so the user knows the binding is alive.
                    PublishLog("CTL", "LB pressed — FOV down (cross-module action, not yet wired)");
                    break;

                case XboxButton.RightBumper:
                    PublishLog("CTL", "RB pressed — FOV up (cross-module action, not yet wired)");
                    break;

                case XboxButton.DPadUp:
                case XboxButton.DPadDown:
                case XboxButton.DPadLeft:
                case XboxButton.DPadRight:
                    // Discrete nudge ±1° on the chosen axis — for now log only.
                    PublishLog("CTL", $"D-pad {btn} pressed — nudge action");
                    break;

                case XboxButton.Start:
                    // Home position
                    _service.SendMotorControlSet(0x00, false, 0x00, false, 0u, 0, 0u, 0);
                    break;

                case XboxButton.Back:
                    // Toggle visual
                    ShowControllerVisual = !ShowControllerVisual;
                    break;

                case XboxButton.LeftStickClick:
                    // Reset pan
                    _service.SendMotorControlSet(0x00, false, 0x01, false, 0u, 0, 0u, 0);
                    break;

                case XboxButton.RightStickClick:
                    // Reset tilt
                    _service.SendMotorControlSet(0x01, false, 0x00, false, 0u, 0, 0u, 0);
                    break;
            }
        });
    }

    /// <summary>Manual toggle command for the on-screen controller visual.</summary>
    [RelayCommand]
    private void ToggleControllerVisual() => ShowControllerVisual = !ShowControllerVisual;

    private static double Clamp(double v, double min, double max)
        => v < min ? min : v > max ? max : v;

    private void PublishLog(string source, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogLines.Add($"{DateTime.Now:HH:mm:ss.fff}  {source}  {message}");
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
            LogVersion++;
        });
    }

}