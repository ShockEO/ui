using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI.Models.App;
using ShockUI.Models.Camera;
using ShockUI.Services;
using ShockUI.Services.App;
using ShockUI.Services.Camera;
using ShockUI.Services.Device;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ShockUI.ViewModels.Modules;

public sealed partial class CameraControllerViewModel : ViewModelBase
{
    private readonly CameraSerialService _cameraSerialService;
    private readonly FovPresetService _fovPresetService;
    private readonly AppSettingsService _appSettingsService;
    private readonly INavigationService _navigationService;
    private readonly AppSettings _settings;
    private CameraType _currentCameraType = CameraType.None;
    private bool _isLoadingSettings;

    [ObservableProperty]
    private string moduleTitle = "Camera Controller";

    [ObservableProperty]
    private ObservableCollection<string> availablePorts = [];

    [ObservableProperty]
    private string? selectedPort;

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    private bool isSimulationMode = false;

    [ObservableProperty]
    private bool isCalibrated;

    [ObservableProperty]
    private string detectedCamera = "NONE";

    [ObservableProperty]
    private int maxPos1 = 10000;

    [ObservableProperty]
    private int maxPos2 = 10000;

    [ObservableProperty]
    private int maxPos3 = 10000;

    [ObservableProperty]
    private int setPos1;

    [ObservableProperty]
    private int setPos2;

    [ObservableProperty]
    private int setPos3;

    [ObservableProperty]
    private int measuredPos1;

    [ObservableProperty]
    private int measuredPos2;

    [ObservableProperty]
    private int measuredPos3;

    [ObservableProperty]
    private double temp1;

    [ObservableProperty]
    private double temp2;

    [ObservableProperty]
    private bool supportsZg3;

    [ObservableProperty]
    private string selectedFov = "WFOV";

    [ObservableProperty]
    private string statusText = "Disconnected";

    [ObservableProperty]
    private int sentCount;

    [ObservableProperty]
    private int receivedCount;

    [ObservableProperty]
    private string remoteState = "-";

    [ObservableProperty]
    private bool zg1Reached;

    [ObservableProperty]
    private bool zg2Reached;

    [ObservableProperty]
    private bool zg3Reached;

    [ObservableProperty]
    private int speedZg1 = 100;

    [ObservableProperty]
    private int speedZg2 = 100;

    [ObservableProperty]
    private int speedZg3 = 100;

    [ObservableProperty]
    private int selectedStepAxis;

    [ObservableProperty]
    private int stepAmplitudePercent = 50;

    [ObservableProperty]
    private int stepDurationMs = 1000;

    [ObservableProperty]
    private string loggerStatus = "Idle";

    [ObservableProperty]
    private ushort loggerExpectedCount;

    [ObservableProperty]
    private ushort loggerNextIndex;

    [ObservableProperty]
    private int logVersion;

    [ObservableProperty]
    private int traceVersion;

    [ObservableProperty]
    private int selectedTabIndex;

    [ObservableProperty]
    private bool isFovEditUnlocked;

    [ObservableProperty]
    private string fovUnlockPassword = string.Empty;

    [ObservableProperty]
    private string fovEditTarget = "WFOV";

    [ObservableProperty]
    private int fovEditZg1;

    [ObservableProperty]
    private int fovEditZg2;

    [ObservableProperty]
    private int fovEditZg3;

    [ObservableProperty]
    private string fovEditStatus = "Locked";

    [ObservableProperty]
    private int selectedLoggerAxis;

    [ObservableProperty]
    private bool isConnecting;

    [ObservableProperty]
    private bool isCalibrating;

    [ObservableProperty]
    private bool isMoving;

    [ObservableProperty]
    private bool isStepBusy;

    [ObservableProperty]
    private bool isLoggerBusy;

    [ObservableProperty]
    private string bannerText = string.Empty;

    [ObservableProperty]
    private bool isBannerVisible;

    [ObservableProperty]
    private IBrush bannerBackground = Brush.Parse("#1E3A5F");

    [ObservableProperty]
    private IBrush bannerBorderBrush = Brush.Parse("#2563EB");

    [ObservableProperty]
    private IBrush bannerForeground = Brush.Parse("#E2E8F0");

    public ObservableCollection<string> FovOptions { get; } =
    [
        "WFOV",
        "MWFOV",
        "MNFOV",
        "NFOV"
    ];

    public ObservableCollection<LogDataPoint> LoggedPoints { get; } = [];
    public ObservableCollection<PacketTraceEntry> PacketTrace { get; } = [];

    /// <summary>
    /// Decoded view of the most recent frame seen on the wire. Populated each
    /// time OnPacketTraceReceived fires, by parsing entry.Hex back into bytes.
    /// </summary>
    public ObservableCollection<DecodedFrameRow> LastDecodedFrameRows { get; } = [];

    /// <summary>Header line for the Decoded Frame panel.</summary>
    [ObservableProperty] private string lastDecodedFrameHeader = "No frame received yet.";
    public ObservableCollection<string> LogLines { get; } = [];

    public bool CanOperate => IsConnected && !IsConnecting;
    public bool CanUseZg3 => IsConnected && SupportsZg3 && !IsConnecting;
    public bool CanRunLogger => IsConnected && !IsLoggerBusy && !IsConnecting;
    public bool CanRunStepTest => IsConnected && !IsStepBusy && !IsConnecting;
    public bool CanMove => IsConnected && !IsMoving && !IsConnecting;
    public bool CanCalibrate => IsConnected && !IsCalibrating && !IsConnecting;
    public bool HasLoggedData => LoggedPoints.Count > 0;
    public bool HasLogLines => LogLines.Count > 0;
    public bool HasPacketTrace => PacketTrace.Count > 0;
    public bool CanEditFovPresets => _currentCameraType != CameraType.None;
    public bool CanSaveEditedFov => IsFovEditUnlocked && CanEditFovPresets;

    public string ConnectionStateText => IsConnected ? "Connected" : IsConnecting ? "Connecting" : "Disconnected";
    public string ModeStateText => IsSimulationMode ? "Simulation" : "Hardware";
    public string CalibrationStateText => IsCalibrated ? "Calibrated" : IsCalibrating ? "Calibrating" : "Not Calibrated";
    public string SettingsPath => _appSettingsService.GetSettingsPath();
    public string CurrentFovFilePath => _currentCameraType == CameraType.None
        ? "No camera selected"
        : _fovPresetService.GetFilePath(_currentCameraType);
    public string FovLockButtonText => IsFovEditUnlocked ? "Lock FOV Updates" : "Allow FOV Updates";
    public string FovLockStateText => IsFovEditUnlocked ? "Unlocked" : "Locked";

    public string StepAmplitudeLabel => $"Duty Cycle: {StepAmplitudePercent}%";
    public string StepDurationLabel => $"Step Duration: {StepDurationMs} ms";

    public string LoggerAxisText => SelectedLoggerAxis switch
    {
        0 => "ZG1",
        1 => "ZG2",
        2 => "ZG3",
        _ => "ZG1"
    };

    public string LoggerAxisNote =>
        "Logger ZG selection is tracked in the UI and exports. The current protocol still uses the shared logger request frame until firmware exposes axis-specific logger commands.";

    public string BusyStateText
    {
        get
        {
            if (IsConnecting) return "Connecting...";
            if (IsCalibrating) return "Calibrating...";
            if (IsMoving) return "Moving...";
            if (IsStepBusy) return "Running step/pulse...";
            if (IsLoggerBusy) return "Reading logger...";
            return "Idle";
        }
    }

    public CameraControllerViewModel(INavigationService navigationService, CameraSerialService cameraSerialService)
    {
        _navigationService = navigationService;
        _cameraSerialService = cameraSerialService;
        _fovPresetService = new FovPresetService();
        _appSettingsService = new AppSettingsService();
        _settings = _appSettingsService.Load();

        _cameraSerialService.StatusChanged += OnStatusChanged;
        _cameraSerialService.HandshakeCompleted += OnHandshakeCompleted;
        _cameraSerialService.CalibrationCompleted += OnCalibrationCompleted;
        _cameraSerialService.PositionFeedbackReceived += OnPositionFeedbackReceived;
        _cameraSerialService.TemperatureReceived += OnTemperatureReceived;
        _cameraSerialService.LoggerCountReceived += OnLoggerCountReceived;
        _cameraSerialService.LoggerPointReceived += OnLoggerPointReceived;
        _cameraSerialService.LoggerStatusChanged += HandleLoggerStatusChanged;
        _cameraSerialService.PacketTraceReceived += OnPacketTraceReceived;
        _cameraSerialService.Disconnected += OnDisconnected;
        _cameraSerialService.LogMessage += OnLogMessage;
        _cameraSerialService.CommsChanged += OnCommsChanged;

        SelectedStepAxis = 0;
        SelectedLoggerAxis = 0;

        ApplySavedSettings();
        RefreshPorts();

        AddLog("GUI", "Workspace initialized.");
        AddLog("GUI", "Waiting operator input.");
        AddLog("GUI", $"Settings loaded from {SettingsPath}");
        NotifyStateChanged();
        NotifyFovEditStateChanged();
        NotifyStepLabelsChanged();
        NotifyBusyStateChanged();
    }

    [RelayCommand]
    private void ToggleSimulationMode()
    {
        IsSimulationMode = !IsSimulationMode;
        _cameraSerialService.SetSimulationMode(IsSimulationMode);
        RefreshPorts();
        SaveSettings();
        NotifyStateChanged();
        ShowInfo(IsSimulationMode
            ? "Simulation mode enabled. Commands are routed to the simulator."
            : "Hardware mode enabled.");
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts = _cameraSerialService.GetAvailablePorts();

        if (AvailablePorts.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(_settings.LastSelectedPort) &&
                AvailablePorts.Contains(_settings.LastSelectedPort))
            {
                SelectedPort = _settings.LastSelectedPort;
            }
            else if (string.IsNullOrWhiteSpace(SelectedPort) || !AvailablePorts.Contains(SelectedPort))
            {
                SelectedPort = AvailablePorts[0];
            }
        }
        else
        {
            SelectedPort = null;
        }

        AddLog("GUI", "Serial port list refreshed.");
    }

    [RelayCommand]
    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            ShowWarning("Select a COM port or SIMULATED before connecting.");
            return;
        }

        IsConnecting = true;
        NotifyStateChanged();
        NotifyBusyStateChanged();
        ShowInfo("Opening connection and waiting for handshake...");

        if (_cameraSerialService.Connect(SelectedPort))
        {
            IsConnected = true;
            StatusText = IsSimulationMode ? "Connected to simulated device" : $"Connected to {SelectedPort}";
            DetectedCamera = "Waiting for handshake...";
            RemoteState = "Connected";
            SaveSettings();
            NotifyStateChanged();
        }
        else
        {
            IsConnecting = false;
            NotifyStateChanged();
            NotifyBusyStateChanged();
            ShowError("Connection failed.");
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _cameraSerialService.Disconnect();
        ShowInfo("Disconnected.");
    }

    [RelayCommand]
    private void Calibrate()
    {
        IsCalibrating = true;
        NotifyStateChanged();
        NotifyBusyStateChanged();
        _cameraSerialService.SendCalibrate();
        AddLog("GUI", "Calibration requested.");
        ShowInfo("Calibration command sent.");
    }

    [RelayCommand]
    private void ApplyFov()
    {
        if (_currentCameraType == CameraType.None)
        {
            ShowWarning("Cannot apply FOV because no camera has been detected yet.");
            AddLog("GUI", "Cannot apply FOV: camera type unknown.");
            return;
        }

        var preset = _fovPresetService.GetPreset(_currentCameraType, SelectedFov);
        if (preset is null)
        {
            ShowError($"No preset found for {_currentCameraType} / {SelectedFov}.");
            AddLog("GUI", $"No preset found for {_currentCameraType} / {SelectedFov}.");
            return;
        }

        SetPos1 = ClampToRange((int)preset.ZG1, 0, MaxPos1);
        SetPos2 = ClampToRange((int)preset.ZG2, 0, MaxPos2);
        SetPos3 = ClampToRange((int)preset.ZG3, 0, MaxPos3);

        StatusText = $"FOV preset applied: {SelectedFov}";
        AddLog("GUI",
            $"FOV applied from file -> Camera={_currentCameraType}, FOV={SelectedFov}, ZG1={SetPos1}, ZG2={SetPos2}, ZG3={SetPos3}");
        AddLog("GUI", $"Preset source -> {CurrentFovFilePath}");
        SaveSettings();
        ShowSuccess($"Applied {SelectedFov} from preset file.");
    }

    [RelayCommand]
    private void SaveCurrentAsFov()
    {
        if (_currentCameraType == CameraType.None)
        {
            ShowWarning("Cannot save FOV because no camera has been detected yet.");
            AddLog("GUI", "Cannot save FOV: camera type unknown.");
            return;
        }

        _fovPresetService.UpdatePreset(
            _currentCameraType,
            SelectedFov,
            (uint)Math.Max(SetPos1, 0),
            (uint)Math.Max(SetPos2, 0),
            (uint)Math.Max(SetPos3, 0));

        AddLog("GUI",
            $"FOV saved to file -> Camera={_currentCameraType}, FOV={SelectedFov}, ZG1={SetPos1}, ZG2={SetPos2}, ZG3={SetPos3}");
        AddLog("GUI", $"Preset destination -> {CurrentFovFilePath}");
        StatusText = $"FOV preset saved: {SelectedFov}";
        SaveSettings();
        LoadFovEditorValues();
        ShowSuccess($"Saved {SelectedFov} to preset file.");
    }

    [RelayCommand]
    private void ToggleFovEditLock()
    {
        if (IsFovEditUnlocked)
        {
            IsFovEditUnlocked = false;
            FovUnlockPassword = string.Empty;
            FovEditStatus = "Locked";
            AddLog("GUI", "FOV preset editor locked.");
            NotifyFovEditStateChanged();
            ShowInfo("FOV editing locked.");
            return;
        }

        if (FovUnlockPassword != "shock")
        {
            FovEditStatus = "Incorrect password";
            AddLog("GUI", "FOV preset editor unlock failed.");
            NotifyFovEditStateChanged();
            ShowError("Incorrect FOV unlock password.");
            return;
        }

        IsFovEditUnlocked = true;
        FovUnlockPassword = string.Empty;
        FovEditStatus = "Unlocked";
        LoadFovEditorValues();
        AddLog("GUI", "FOV preset editor unlocked.");
        NotifyFovEditStateChanged();
        ShowSuccess("FOV editing unlocked.");
    }

    [RelayCommand]
    private void LoadFovEditorValues()
    {
        if (_currentCameraType == CameraType.None)
        {
            FovEditStatus = "No camera selected";
            NotifyFovEditStateChanged();
            ShowWarning("Cannot load FOV editor values because no camera is selected.");
            return;
        }

        var preset = _fovPresetService.GetPreset(_currentCameraType, FovEditTarget);
        if (preset is null)
        {
            FovEditStatus = "Preset not found";
            NotifyFovEditStateChanged();
            ShowError("Requested FOV preset was not found.");
            return;
        }

        FovEditZg1 = ClampToRange((int)preset.ZG1, 0, MaxPos1);
        FovEditZg2 = ClampToRange((int)preset.ZG2, 0, MaxPos2);
        FovEditZg3 = ClampToRange((int)preset.ZG3, 0, MaxPos3);
        FovEditStatus = $"Loaded {FovEditTarget}";
        AddLog("GUI",
            $"FOV editor loaded -> Camera={_currentCameraType}, FOV={FovEditTarget}, ZG1={FovEditZg1}, ZG2={FovEditZg2}, ZG3={FovEditZg3}");
        NotifyFovEditStateChanged();
    }

    [RelayCommand]
    private void SaveEditedFov()
    {
        if (!IsFovEditUnlocked)
        {
            FovEditStatus = "Locked";
            NotifyFovEditStateChanged();
            ShowWarning("Unlock FOV editing before updating presets.");
            return;
        }

        if (_currentCameraType == CameraType.None)
        {
            FovEditStatus = "No camera selected";
            NotifyFovEditStateChanged();
            ShowWarning("Cannot update FOV preset because no camera is selected.");
            return;
        }

        int clampedZg1 = ClampToRange(FovEditZg1, 0, MaxPos1);
        int clampedZg2 = ClampToRange(FovEditZg2, 0, MaxPos2);
        int clampedZg3 = SupportsZg3 ? ClampToRange(FovEditZg3, 0, MaxPos3) : 0;

        FovEditZg1 = clampedZg1;
        FovEditZg2 = clampedZg2;
        FovEditZg3 = clampedZg3;

        _fovPresetService.UpdatePreset(
            _currentCameraType,
            FovEditTarget,
            (uint)clampedZg1,
            (uint)clampedZg2,
            (uint)clampedZg3);

        FovEditStatus = $"Updated {FovEditTarget}";
        AddLog("GUI",
            $"FOV editor saved -> Camera={_currentCameraType}, FOV={FovEditTarget}, ZG1={clampedZg1}, ZG2={clampedZg2}, ZG3={clampedZg3}");
        AddLog("GUI", $"Preset destination -> {CurrentFovFilePath}");
        NotifyFovEditStateChanged();
        ShowSuccess($"Updated preset {FovEditTarget}.");
    }

    [RelayCommand]
    private void MoveToSetpoints()
    {
        int? zg3 = SupportsZg3 ? SetPos3 : null;

        IsMoving = true;
        NotifyStateChanged();
        NotifyBusyStateChanged();

        _cameraSerialService.SendPositionControl(SetPos1, SetPos2, zg3);

        Zg1Reached = false;
        Zg2Reached = false;
        Zg3Reached = false;
        NotifyStateChanged();

        AddLog("GUI", SupportsZg3
            ? $"Move request sent. ZG1={SetPos1}, ZG2={SetPos2}, ZG3={SetPos3}"
            : $"Move request sent. ZG1={SetPos1}, ZG2={SetPos2}");
        ShowInfo("Move command sent.");
    }

    [RelayCommand]
    private void SpeedZg1Negative() => SendAxisSpeed(1, -Math.Abs(SpeedZg1));

    [RelayCommand]
    private void SpeedZg1Positive() => SendAxisSpeed(1, Math.Abs(SpeedZg1));

    [RelayCommand]
    private void SpeedZg2Negative() => SendAxisSpeed(2, -Math.Abs(SpeedZg2));

    [RelayCommand]
    private void SpeedZg2Positive() => SendAxisSpeed(2, Math.Abs(SpeedZg2));

    [RelayCommand]
    private void SpeedZg3Negative() => SendAxisSpeed(3, -Math.Abs(SpeedZg3));

    [RelayCommand]
    private void SpeedZg3Positive() => SendAxisSpeed(3, Math.Abs(SpeedZg3));

    [RelayCommand]
    private void StopZg1() => StopAxis(1);

    [RelayCommand]
    private void StopZg2() => StopAxis(2);

    [RelayCommand]
    private void StopZg3() => StopAxis(3);

    [RelayCommand]
    private async Task PerformStep()
    {
        await RunStepOrPulseAsync(0, "Step");
    }

    [RelayCommand]
    private async Task PerformPulse()
    {
        await RunStepOrPulseAsync(1, "Pulse");
    }

    private async Task RunStepOrPulseAsync(uint stepType, string modeName)
    {
        int axis = Math.Clamp(SelectedStepAxis + 1, 1, 3);

        if (axis == 3 && !SupportsZg3)
        {
            AddLog("GUI", $"{modeName} rejected: ZG3 not supported.");
            ShowWarning($"{modeName} rejected because ZG3 is not supported.");
            return;
        }

        float amplitudeFraction = Math.Clamp(StepAmplitudePercent, 0, 100) / 100.0f;
        uint duration = (uint)Math.Max(StepDurationMs, 1);

        IsStepBusy = true;
        NotifyStateChanged();
        NotifyBusyStateChanged();

        _cameraSerialService.SendStepControl(stepType, (uint)axis, amplitudeFraction, duration);
        AddLog("GUI", $"{modeName} started -> Type={stepType}, Axis={axis}, Amp={amplitudeFraction:F2}, Duration={duration} ms");
        ShowInfo($"{modeName} command sent.");

        await Task.Delay(700);
        IsStepBusy = false;
        NotifyStateChanged();
        NotifyBusyStateChanged();
    }

    [RelayCommand]
    private void RequestLogCount()
    {
        if (SelectedLoggerAxis == 2 && !SupportsZg3)
        {
            AddLog("GUI", "Logger request rejected: ZG3 not supported.");
            ShowWarning("Logger request rejected because ZG3 is not supported.");
            return;
        }

        LoggedPoints.Clear();
        LoggerExpectedCount = 0;
        LoggerNextIndex = 0;
        LoggerStatus = $"Requesting count for {LoggerAxisText}";
        IsLoggerBusy = true;
        OnPropertyChanged(nameof(HasLoggedData));
        NotifyStateChanged();
        NotifyBusyStateChanged();

        _cameraSerialService.RequestLoggerCount();
        AddLog("GUI", $"Requested logger point count for {LoggerAxisText}.");
        ShowInfo($"Logger count request sent for {LoggerAxisText}.");
    }

    [RelayCommand]
    private void RequestNextLogPoint()
    {
        if (LoggerExpectedCount == 0)
        {
            AddLog("GUI", "No logger count available yet.");
            ShowWarning("No logger count is available yet.");
            return;
        }

        if (LoggerNextIndex >= LoggerExpectedCount)
        {
            LoggerStatus = "Complete";
            AddLog("GUI", "All available logger points already requested.");
            ShowInfo("All available logger points have already been requested.");
            return;
        }

        IsLoggerBusy = true;
        NotifyStateChanged();
        NotifyBusyStateChanged();

        _cameraSerialService.RequestLoggerPoint(LoggerNextIndex);
        AddLog("GUI", $"Requested logger point {LoggerNextIndex} for {LoggerAxisText}.");
    }

    [RelayCommand]
    private void RequestAllLoggedData()
    {
        if (SelectedLoggerAxis == 2 && !SupportsZg3)
        {
            AddLog("GUI", "Logger request rejected: ZG3 not supported.");
            ShowWarning("Logger request rejected because ZG3 is not supported.");
            return;
        }

        LoggedPoints.Clear();
        LoggerExpectedCount = 0;
        LoggerNextIndex = 0;
        LoggerStatus = $"Bulk request started for {LoggerAxisText}";
        IsLoggerBusy = true;
        OnPropertyChanged(nameof(HasLoggedData));
        NotifyStateChanged();
        NotifyBusyStateChanged();

        _cameraSerialService.RequestLoggerCount();
        AddLog("GUI", $"Started bulk logger retrieval for {LoggerAxisText}.");
        ShowInfo($"Bulk logger retrieval started for {LoggerAxisText}.");
    }

    [RelayCommand]
    private void ExportLoggedCsv()
    {
        if (LoggedPoints.Count == 0)
        {
            AddLog("GUI", "CSV export skipped: no logged points.");
            ShowWarning("No logged data available to export.");
            return;
        }

        string path = Path.Combine(
            AppContext.BaseDirectory,
            $"logged_data_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        var sb = new StringBuilder();
        sb.AppendLine("Index,TimeMs,LoggerAxis,ZG1,ZG2,ZG3,Temp1,Temp2,Notes");

        foreach (var point in LoggedPoints)
        {
            sb.Append(point.Index).Append(',')
              .Append(point.TimeMs.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(LoggerAxisText).Append(',')
              .Append(point.ZG1).Append(',')
              .Append(point.ZG2).Append(',')
              .Append(point.ZG3).Append(',')
              .Append(point.Temp1.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(point.Temp2.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append('"').Append(point.Notes.Replace("\"", "\"\"")).Append('"')
              .AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        AddLog("GUI", $"Logged CSV exported -> {path}");
        LoggerStatus = "CSV exported";
        ShowSuccess($"Logged CSV exported to {path}");
    }

    [RelayCommand]
    private void ClearLoggedData()
    {
        LoggedPoints.Clear();
        LoggerExpectedCount = 0;
        LoggerNextIndex = 0;
        LoggerStatus = "Cleared";
        IsLoggerBusy = false;
        OnPropertyChanged(nameof(HasLoggedData));
        NotifyStateChanged();
        NotifyBusyStateChanged();
        AddLog("GUI", "Logged data cache cleared.");
    }

    [RelayCommand]
    private void ExportConsoleLog()
    {
        if (LogLines.Count == 0)
        {
            AddLog("GUI", "Console log export skipped: no log lines.");
            ShowWarning("No console log lines available to export.");
            return;
        }

        string path = Path.Combine(
            AppContext.BaseDirectory,
            $"console_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        File.WriteAllLines(path, LogLines, Encoding.UTF8);
        AddLog("GUI", $"Console log exported -> {path}");
        ShowSuccess($"Console log exported to {path}");
    }

    [RelayCommand]
    private void ExportPacketTrace()
    {
        if (PacketTrace.Count == 0)
        {
            AddLog("GUI", "Packet trace export skipped: no trace lines.");
            ShowWarning("No packet trace lines available to export.");
            return;
        }

        string path = Path.Combine(
            AppContext.BaseDirectory,
            $"packet_trace_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        var lines = new List<string>(PacketTrace.Count * 3);
        foreach (var entry in PacketTrace)
        {
            lines.Add($"{entry.Timestamp}  {entry.Direction}  {entry.Summary}");
            lines.Add(entry.Hex);
            lines.Add(string.Empty);
        }

        File.WriteAllLines(path, lines, Encoding.UTF8);
        AddLog("GUI", $"Packet trace exported -> {path}");
        ShowSuccess($"Packet trace exported to {path}");
    }

    [RelayCommand]
    private void ClearPacketTrace()
    {
        PacketTrace.Clear();
        TraceVersion++;
        OnPropertyChanged(nameof(HasPacketTrace));
        AddLog("GUI", "Packet trace cleared.");
    }

    /// <summary>Clear the Decoded Frame panel.</summary>
    [RelayCommand]
    private void ClearDecodedFrame()
    {
        LastDecodedFrameRows.Clear();
        LastDecodedFrameHeader = "No frame received yet.";
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        LogVersion++;
        OnPropertyChanged(nameof(HasLogLines));
        AddLog("GUI", "Log cleared.");
    }

    [RelayCommand]
    private void DismissBanner()
    {
        IsBannerVisible = false;
        BannerText = string.Empty;
    }

    private void SendAxisSpeed(int axis, int speed)
    {
        if (axis == 3 && !SupportsZg3)
        {
            AddLog("GUI", "ZG3 speed command ignored: current camera does not support ZG3.");
            ShowWarning("ZG3 speed command ignored because the current camera does not support ZG3.");
            return;
        }

        _cameraSerialService.SendSpeedControl(axis, speed);
        AddLog("GUI", $"Speed command -> Axis={axis}, Speed={speed}");
    }

    private void StopAxis(int axis)
    {
        if (axis == 3 && !SupportsZg3)
            return;

        _cameraSerialService.StopAxis(axis);
        AddLog("GUI", $"Stop command -> Axis={axis}");
    }

    private void ApplySavedSettings()
    {
        _isLoadingSettings = true;

        IsSimulationMode = _settings.IsSimulationMode;
        SelectedFov = string.IsNullOrWhiteSpace(_settings.SelectedFov) ? "WFOV" : _settings.SelectedFov;
        SpeedZg1 = _settings.SpeedZg1 <= 0 ? 100 : _settings.SpeedZg1;
        SpeedZg2 = _settings.SpeedZg2 <= 0 ? 100 : _settings.SpeedZg2;
        SpeedZg3 = _settings.SpeedZg3 <= 0 ? 100 : _settings.SpeedZg3;
        SelectedPort = _settings.LastSelectedPort;
        SelectedTabIndex = Math.Max(_settings.CameraSelectedTabIndex, 0);

        _cameraSerialService.SetSimulationMode(IsSimulationMode);

        _isLoadingSettings = false;
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings)
            return;

        _settings.IsSimulationMode = IsSimulationMode;
        _settings.LastSelectedPort = SelectedPort;
        _settings.SelectedFov = SelectedFov;
        _settings.SpeedZg1 = SpeedZg1;
        _settings.SpeedZg2 = SpeedZg2;
        _settings.SpeedZg3 = SpeedZg3;
        _settings.CameraSelectedTabIndex = SelectedTabIndex;

        _appSettingsService.Save(_settings);
    }

    private void OnStatusChanged(string status)
    {
        StatusText = status;
    }

    private void OnHandshakeCompleted(CameraDeviceInfo info)
    {
        _currentCameraType = info.CameraType;
        DetectedCamera = info.CameraType.ToString();
        MaxPos1 = (int)info.MaxPos1;
        MaxPos2 = (int)info.MaxPos2;
        MaxPos3 = (int)info.MaxPos3;
        SupportsZg3 = info.SupportsZg3;
        IsCalibrated = false;
        IsConnecting = false;
        RemoteState = "Handshake OK";
        StatusText = $"Camera ready: {DetectedCamera}";
        LoggerStatus = "Idle";

        SetPos1 = 0;
        SetPos2 = 0;
        SetPos3 = 0;
        MeasuredPos1 = 0;
        MeasuredPos2 = 0;
        MeasuredPos3 = 0;
        Zg1Reached = false;
        Zg2Reached = false;
        Zg3Reached = false;

        IsFovEditUnlocked = false;
        FovUnlockPassword = string.Empty;
        FovEditStatus = "Locked";

        AddLog("GUI", $"Preset store ready for {_currentCameraType}.");
        AddLog("GUI", $"FOV preset file -> {CurrentFovFilePath}");

        LoadFovEditorValues();
        NotifyStateChanged();
        NotifyFovEditStateChanged();
        NotifyBusyStateChanged();
        OnPropertyChanged(nameof(CurrentFovFilePath));
        ShowSuccess($"Handshake complete. Camera detected: {DetectedCamera}");
    }

    private void OnCalibrationCompleted(bool ok)
    {
        IsCalibrating = false;
        IsCalibrated = ok;
        RemoteState = ok ? "Calibrated" : "Calibration Reply";
        StatusText = ok ? "Calibration complete." : "Calibration response received.";
        NotifyStateChanged();
        NotifyBusyStateChanged();

        if (ok)
            ShowSuccess("Calibration completed successfully.");
        else
            ShowWarning("Calibration response received, but it did not report success.");
    }

    private void OnPositionFeedbackReceived(int zg1, int zg2, int zg3, bool r1, bool r2, bool r3)
    {
        MeasuredPos1 = zg1;
        MeasuredPos2 = zg2;
        MeasuredPos3 = zg3;
        Zg1Reached = r1;
        Zg2Reached = r2;
        Zg3Reached = r3;
        RemoteState = "Position OK";
        IsMoving = false;
        NotifyStateChanged();
        NotifyBusyStateChanged();
    }

    private void OnTemperatureReceived(double t1, double t2)
    {
        Temp1 = t1;
        Temp2 = t2;
    }

    private void OnLoggerCountReceived(ushort count)
    {
        LoggerExpectedCount = count;
        LoggerNextIndex = 0;
        LoggerStatus = $"Count received: {count} for {LoggerAxisText}";

        if (count == 0)
        {
            IsLoggerBusy = false;
            NotifyStateChanged();
            NotifyBusyStateChanged();
            AddLog("GUI", "Logger reports zero points.");
            ShowWarning("Logger returned zero points.");
            return;
        }

        _cameraSerialService.RequestLoggerPoint(0);
    }

    private void OnLoggerPointReceived(LogDataPoint point)
    {
        point.Notes = string.IsNullOrWhiteSpace(point.Notes)
            ? $"Axis={LoggerAxisText}"
            : $"{point.Notes}; Axis={LoggerAxisText}";

        LoggedPoints.Add(point);
        OnPropertyChanged(nameof(HasLoggedData));

        if (point.Index >= LoggerNextIndex)
            LoggerNextIndex = (ushort)(point.Index + 1);

        if (LoggerExpectedCount > 0 && LoggerNextIndex < LoggerExpectedCount)
        {
            _cameraSerialService.RequestLoggerPoint(LoggerNextIndex);
            LoggerStatus = $"Receiving {LoggerNextIndex}/{LoggerExpectedCount} for {LoggerAxisText}";
        }
        else
        {
            LoggerStatus = $"Complete ({LoggedPoints.Count} points) for {LoggerAxisText}";
            IsLoggerBusy = false;
            NotifyStateChanged();
            NotifyBusyStateChanged();
            ShowSuccess($"Logger retrieval complete for {LoggerAxisText}.");
        }
    }

    private void HandleLoggerStatusChanged(string status)
    {
        LoggerStatus = status;
    }

    private void OnPacketTraceReceived(PacketTraceEntry entry)
    {
        PacketTrace.Add(entry);

        while (PacketTrace.Count > 500)
            PacketTrace.RemoveAt(0);

        TraceVersion++;
        OnPropertyChanged(nameof(HasPacketTrace));

        // Refresh the Decoded Frame panel from the new entry. Camera traces
        // store frames as formatted hex strings (entry.Hex); we parse back
        // to raw bytes for the decoder.
        byte[] frame = CameraFrameDecoder.ParseHexString(entry.Hex);
        LastDecodedFrameRows.Clear();
        foreach (var row in CameraFrameDecoder.Decode(frame))
            LastDecodedFrameRows.Add(row);
        LastDecodedFrameHeader = $"{entry.Direction} @ {entry.Timestamp}  —  {frame.Length} bytes  ({entry.Summary})";
    }

    private void OnDisconnected()
    {
        _currentCameraType = CameraType.None;
        IsConnected = false;
        IsConnecting = false;
        IsCalibrating = false;
        IsMoving = false;
        IsStepBusy = false;
        IsLoggerBusy = false;
        IsCalibrated = false;
        DetectedCamera = "NONE";
        SupportsZg3 = false;
        StatusText = "Disconnected";
        RemoteState = "-";
        LoggerStatus = "Idle";

        MaxPos1 = 10000;
        MaxPos2 = 10000;
        MaxPos3 = 10000;

        SetPos1 = 0;
        SetPos2 = 0;
        SetPos3 = 0;

        MeasuredPos1 = 0;
        MeasuredPos2 = 0;
        MeasuredPos3 = 0;

        Temp1 = 0;
        Temp2 = 0;

        Zg1Reached = false;
        Zg2Reached = false;
        Zg3Reached = false;

        IsFovEditUnlocked = false;
        FovUnlockPassword = string.Empty;
        FovEditStatus = "Locked";

        NotifyStateChanged();
        NotifyFovEditStateChanged();
        NotifyBusyStateChanged();
        OnPropertyChanged(nameof(CurrentFovFilePath));
    }

    private void OnLogMessage(string line)
    {
        LogLines.Add(line);

        while (LogLines.Count > 300)
            LogLines.RemoveAt(0);

        LogVersion++;
        OnPropertyChanged(nameof(HasLogLines));
    }

    private void OnCommsChanged(int sent, int received)
    {
        SentCount = sent;
        ReceivedCount = received;
    }

    private void AddLog(string source, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {source,-4}  {message}";
        LogLines.Add(line);

        while (LogLines.Count > 300)
            LogLines.RemoveAt(0);

        LogVersion++;
        OnPropertyChanged(nameof(HasLogLines));
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(CanOperate));
        OnPropertyChanged(nameof(CanUseZg3));
        OnPropertyChanged(nameof(CanRunLogger));
        OnPropertyChanged(nameof(CanRunStepTest));
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(CanCalibrate));
        OnPropertyChanged(nameof(HasLoggedData));
        OnPropertyChanged(nameof(ConnectionStateText));
        OnPropertyChanged(nameof(ModeStateText));
        OnPropertyChanged(nameof(CalibrationStateText));
    }

    private void NotifyFovEditStateChanged()
    {
        OnPropertyChanged(nameof(CanEditFovPresets));
        OnPropertyChanged(nameof(CanSaveEditedFov));
        OnPropertyChanged(nameof(FovLockButtonText));
        OnPropertyChanged(nameof(FovLockStateText));
        OnPropertyChanged(nameof(CurrentFovFilePath));
    }

    private void NotifyStepLabelsChanged()
    {
        OnPropertyChanged(nameof(StepAmplitudeLabel));
        OnPropertyChanged(nameof(StepDurationLabel));
    }

    private void NotifyBusyStateChanged()
    {
        OnPropertyChanged(nameof(BusyStateText));
    }

    private void ShowInfo(string text)
    {
        BannerText = text;
        BannerBackground = Brush.Parse("#1E3A5F");
        BannerBorderBrush = Brush.Parse("#2563EB");
        BannerForeground = Brush.Parse("#E2E8F0");
        IsBannerVisible = true;
    }

    private void ShowSuccess(string text)
    {
        BannerText = text;
        BannerBackground = Brush.Parse("#14532D");
        BannerBorderBrush = Brush.Parse("#166534");
        BannerForeground = Brush.Parse("#DCFCE7");
        IsBannerVisible = true;
    }

    private void ShowWarning(string text)
    {
        BannerText = text;
        BannerBackground = Brush.Parse("#7C2D12");
        BannerBorderBrush = Brush.Parse("#9A3412");
        BannerForeground = Brush.Parse("#FFEDD5");
        IsBannerVisible = true;
    }

    private void ShowError(string text)
    {
        BannerText = text;
        BannerBackground = Brush.Parse("#7F1D1D");
        BannerBorderBrush = Brush.Parse("#991B1B");
        BannerForeground = Brush.Parse("#FEE2E2");
        IsBannerVisible = true;
    }

    partial void OnIsConnectedChanged(bool value)
    {
        NotifyStateChanged();
        SaveSettings();
    }

    partial void OnIsSimulationModeChanged(bool value)
    {
        NotifyStateChanged();
        SaveSettings();
    }

    partial void OnIsCalibratedChanged(bool value)
    {
        NotifyStateChanged();
    }

    partial void OnSupportsZg3Changed(bool value)
    {
        NotifyStateChanged();
        NotifyFovEditStateChanged();
    }

    partial void OnSelectedPortChanged(string? value)
    {
        SaveSettings();
    }

    partial void OnSelectedFovChanged(string value)
    {
        SaveSettings();
    }

    partial void OnSpeedZg1Changed(int value)
    {
        SaveSettings();
    }

    partial void OnSpeedZg2Changed(int value)
    {
        SaveSettings();
    }

    partial void OnSpeedZg3Changed(int value)
    {
        SaveSettings();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        SaveSettings();
    }

    partial void OnIsFovEditUnlockedChanged(bool value)
    {
        NotifyFovEditStateChanged();
    }

    partial void OnFovEditTargetChanged(string value)
    {
        if (!_isLoadingSettings)
        {
            LoadFovEditorValues();
        }
    }

    partial void OnStepAmplitudePercentChanged(int value)
    {
        NotifyStepLabelsChanged();
    }

    partial void OnStepDurationMsChanged(int value)
    {
        NotifyStepLabelsChanged();
    }

    partial void OnSelectedLoggerAxisChanged(int value)
    {
        OnPropertyChanged(nameof(LoggerAxisText));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        NotifyStateChanged();
        NotifyBusyStateChanged();
    }

    partial void OnIsCalibratingChanged(bool value)
    {
        NotifyStateChanged();
        NotifyBusyStateChanged();
    }

    partial void OnIsMovingChanged(bool value)
    {
        NotifyStateChanged();
        NotifyBusyStateChanged();
    }

    partial void OnIsStepBusyChanged(bool value)
    {
        NotifyStateChanged();
        NotifyBusyStateChanged();
    }

    partial void OnIsLoggerBusyChanged(bool value)
    {
        NotifyStateChanged();
        NotifyBusyStateChanged();
    }

    private static int ClampToRange(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    // ─────────────────────────────────────────────────────────────────
    // Transport state — reflects the currently-selected transport from
    // _cameraSerialService.GetPortSettings(). Refreshed via RefreshTransportState()
    // whenever settings change or we connect/disconnect.
    // ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool isUartTransport = true;
    [ObservableProperty] private bool isUdpTransport = false;
    [ObservableProperty] private string transportSummary = "UART";

    private void RefreshTransportState()
    {
        var s = _cameraSerialService.GetPortSettings();
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

}