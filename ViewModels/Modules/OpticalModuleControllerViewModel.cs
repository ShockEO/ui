using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;
using ShockUI.Models.App;
using ShockUI.Models.OpticalModules;
using ShockUI.Services.Device;   // DecodedFrameRow
using ShockUI.Services.Eos;
using ShockUI.Services.OpticalModules;

namespace ShockUI.ViewModels.Modules;

public partial class OpticalModuleControllerViewModel : ViewModelBase
{
    private readonly OpticalModuleSerialService _serialService;
    private readonly OpticalModuleDefinition _module;

    [ObservableProperty]
    private string moduleTitle = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> availablePorts = [];

    [ObservableProperty]
    private string? selectedPort;

    [ObservableProperty]
    private string statusText = "Disconnected";

    [ObservableProperty]
    private OpticalModuleState currentState = OpticalModuleState.Unknown;

    [ObservableProperty]
    private bool stateReached;

    [ObservableProperty]
    private int messageCounter;

    [ObservableProperty]
    private int crcErrorCount;

    [ObservableProperty]
    private int messageFormatErrorCount;

    [ObservableProperty]
    private bool externalDeviceAlarmActive;

    [ObservableProperty]
    private bool processorAlarmActive;

    [ObservableProperty]
    private byte controllerPbitStatus;

    [ObservableProperty]
    private byte zoomGroup1PbitStatus;

    [ObservableProperty]
    private byte zoomGroup2PbitStatus;

    [ObservableProperty]
    private byte temperatureSensorsPbitStatus;



    [ObservableProperty]
    private string lastCommandSummary = "-";

    [ObservableProperty]
    private string lastProtocolSummary = "-";

    [ObservableProperty]
    private string selectedFov = "WFOV";

    [ObservableProperty]
    private string fovSummary = "-";

    [ObservableProperty]
    private string focusSummary = "-";

    [ObservableProperty]
    private string currentFovText = "-";

    [ObservableProperty]
    private string focusMovementText = "-";

    // Focus target values (host → module)
    [ObservableProperty]
    private int focusTargetPosition;

    [ObservableProperty]
    private int focusTargetSpeed;

    // Focus feedback values (module → host)
    [ObservableProperty]
    private int currentFocusPosition;

    [ObservableProperty]
    private int currentFocusSpeed;

    [ObservableProperty]
    private bool isSimulationMode = false;

    [ObservableProperty]
    private bool isConnecting;

    [ObservableProperty]
    private bool isStateBusy;

    [ObservableProperty]
    private bool isStatusBusy;

    [ObservableProperty]
    private bool isFovBusy;

    [ObservableProperty]
    private bool isFocusBusy;

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

    [ObservableProperty]
    private int logVersion;

    [ObservableProperty]
    private int traceVersion;

    public ObservableCollection<string> FovOptions { get; } =
    [
        "WFOV",
        "MWFOV",
        "MNFOV",
        "NFOV"
    ];

    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<string> PacketTraceLines { get; } = [];

    /// <summary>
    /// Decoded view of the most recent frame seen on the wire (TX or RX).
    /// Populated by the FrameTransmitted/FrameReceived event handlers.
    /// </summary>
    public ObservableCollection<DecodedFrameRow> LastDecodedFrameRows { get; } = [];

    /// <summary>Header line for the "Decoded Frame" panel.</summary>
    [ObservableProperty] private string lastDecodedFrameHeader = "No frame received yet.";

    public bool IsConnected => _serialService.IsConnected;
    public bool CanConnect => !IsConnecting && !IsConnected;
    public bool CanDisconnect => IsConnected && !IsConnecting;
    public bool CanSendCommands => IsConnected && !IsConnecting;
    public bool HasLogLines => LogLines.Count > 0;
    public bool HasTraceLines => PacketTraceLines.Count > 0;

    public string ConnectionStateText => IsConnected ? "Connected" : IsConnecting ? "Connecting" : "Disconnected";
    public string ModeStateText => CurrentState == OpticalModuleState.Unknown ? "State Unknown" : CurrentState.ToString();
    public string CalibrationStateText => BusyStateText;

    public string BusyStateText
    {
        get
        {
            if (IsConnecting) return "Connecting";
            if (IsStateBusy) return "State Busy";
            if (IsStatusBusy) return "Status Busy";
            if (IsFovBusy) return "FOV Busy";
            if (IsFocusBusy) return "Focus Busy";
            return "Idle";
        }
    }

    public string ControllerPbitSummary => DecodeBits("Controller", ControllerPbitStatus, ControllerPbitMap);
    public string Zg1PbitSummary => DecodeBits("ZG1", ZoomGroup1PbitStatus, Zg1PbitMap);
    public string Zg2PbitSummary => DecodeBits("ZG2", ZoomGroup2PbitStatus, Zg2PbitMap);
    public string TemperaturePbitSummary => DecodeBits("Temp", TemperatureSensorsPbitStatus, TemperaturePbitMap);

    public string AlarmSummary =>
        $"External Alarm: {(ExternalDeviceAlarmActive ? "Active" : "Clear")} | Processor Alarm: {(ProcessorAlarmActive ? "Active" : "Clear")}";

    public string GeneralStatusSummary =>
        $"Msg={MessageCounter}, CRC={CrcErrorCount}, Format={MessageFormatErrorCount}";

    private static readonly string[] ControllerPbitMap =
    [
        "Input Voltage Fail",
        "Controller PSU Fail",
        "Controller Temperature Out Of Range",
        "Flash Fail"
    ];

    private static readonly string[] Zg1PbitMap =
    [
        "ADC Fail",
        "Motor Connection Fail",
        "Encoder Connection Fail",
        "Encoder Polarity Fail",
        "Motor Stall Fail",
        "Motor Not Calibrated"
    ];

    private static readonly string[] Zg2PbitMap =
    [
        "Motor Start Fail",
        "Motor Connection Fail",
        "Motor Polarity Fail",
        "Min End Stop Not Reached",
        "Max End Stop Not Reached",
        "Motor Stall Fail",
        "Quadrature Encoder Fail"
    ];

    private static readonly string[] TemperaturePbitMap =
    [
        "Temperature Sensor 1 Connection Fail",
        "Temperature Sensor 1 Out Of Range",
        "Temperature Sensor 2 Connection Fail",
        "Temperature Sensor 2 Out Of Range",
        "Temperature Sensor 3 Connection Fail",
        "Temperature Sensor 3 Out Of Range"
    ];

    public OpticalModuleControllerViewModel(OpticalModuleDefinition module, OpticalModuleSerialService serialService)
    {
        _module = module;
        _serialService = serialService;
        ModuleTitle = $"{module.Name} Controller";

        _serialService.SetSimulationMode(IsSimulationMode);

        _serialService.StatusChanged += s =>
        {
            StatusText = s;
            OnPropertyChanged(nameof(IsConnected));
            NotifyConnectionStateChanged();
        };

        _serialService.LogMessage += line =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 300)
                LogLines.RemoveAt(0);

            LogVersion++;
            OnPropertyChanged(nameof(HasLogLines));
        };

        _serialService.FrameTransmitted += frame =>
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            PacketTraceLines.Add($"TX  {BitConverter.ToString(frame).Replace("-", " ")}");
            while (PacketTraceLines.Count > 300)
                PacketTraceLines.RemoveAt(0);

            TraceVersion++;
            OnPropertyChanged(nameof(HasTraceLines));
            DecodeFrame(frame, "TX", ts);
        };

        _serialService.FrameReceived += frame =>
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            PacketTraceLines.Add($"RX  {BitConverter.ToString(frame).Replace("-", " ")}");
            while (PacketTraceLines.Count > 300)
                PacketTraceLines.RemoveAt(0);

            TraceVersion++;
            OnPropertyChanged(nameof(HasTraceLines));
            LastProtocolSummary = $"Last RX {DateTime.Now:HH:mm:ss}";
            DecodeFrame(frame, "RX", ts);
        };

        _serialService.StateSelectionReceived += (state, reached) =>
        {
            CurrentState = state;
            StateReached = reached;
            IsStateBusy = false;
            NotifyBusyChanged();
            ShowSuccess($"State response received: {state} (Reached={reached})");
        };

        _serialService.GeneralStatusReceived += status =>
        {
            CurrentState = status.CurrentState;
            MessageCounter = status.MessageCounter;
            CrcErrorCount = status.CrcErrorCount;
            MessageFormatErrorCount = status.MessageFormatErrorCount;
            ExternalDeviceAlarmActive = status.ExternalDeviceAlarmActive;
            ProcessorAlarmActive = status.ProcessorAlarmActive;
            ControllerPbitStatus = status.ControllerPbitStatus;
            ZoomGroup1PbitStatus = status.ZoomGroup1PbitStatus;
            ZoomGroup2PbitStatus = status.ZoomGroup2PbitStatus;
            TemperatureSensorsPbitStatus = status.TemperatureSensorsPbitStatus;
            IsStatusBusy = false;
            NotifyDecodedStateChanged();
            NotifyBusyChanged();
            ShowSuccess("General status response received.");
        };

        _serialService.FovFeedbackReceived += feedback =>
        {
            FovSummary = feedback.Summary;
            CurrentFovText = feedback.CurrentFov;
            IsFovBusy = false;
            NotifyBusyChanged();
            ShowSuccess($"FOV response: {feedback.CurrentFov}");
        };

        _serialService.FocusFeedbackReceived += feedback =>
        {
            FocusSummary = feedback.Summary;
            FocusMovementText = feedback.MovementLimit;
            CurrentFocusPosition = feedback.CurrentPosition;
            CurrentFocusSpeed = feedback.CurrentSpeed;
            IsFocusBusy = false;
            NotifyBusyChanged();
            ShowSuccess($"Focus response: {feedback.CommandState}");
        };

        RefreshPorts();
        RefreshTransportState();
    }

    [RelayCommand]
    private void ToggleSimulationMode()
    {
        IsSimulationMode = !IsSimulationMode;
        _serialService.SetSimulationMode(IsSimulationMode);
        RefreshPorts();
        ShowInfo(IsSimulationMode ? "Simulation mode enabled." : "Hardware mode enabled.");
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts = _serialService.GetAvailablePorts();

        if (AvailablePorts.Count > 0)
        {
            // Always default to the first real port; SIMULATED is only
            // selected if the user has explicitly turned on sim mode AND
            // there's nothing else to pick from.
            if (string.IsNullOrWhiteSpace(SelectedPort) || !AvailablePorts.Contains(SelectedPort))
                SelectedPort = AvailablePorts[0];
        }

        AddLog("GUI", "Serial port list refreshed.");
    }

    [RelayCommand]
    private async Task Connect()
    {
        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            ShowWarning("Select a COM port before connecting.");
            return;
        }

        IsConnecting = true;
        NotifyBusyChanged();
        ShowInfo($"Opening {SelectedPort}...");

        bool ok = _serialService.Connect(SelectedPort);
        await Task.Delay(200);

        IsConnecting = false;
        NotifyBusyChanged();

        if (ok)
        {
            ShowSuccess($"Connected to {SelectedPort}.");
            // Immediate General Status request so the user sees module health
            // (state machine, motor health, encoder status) on connect.
            try
            {
                _serialService.SendGeneralStatusRequest(_module);
            }
            catch (Exception ex)
            {
                LogLines.Add($"{DateTime.Now:HH:mm:ss.fff}  WARN  Initial poll failed: {ex.Message}");
                LogVersion++;
                OnPropertyChanged(nameof(HasLogLines));
            }
        }
        else
        {
            ShowError("Connection failed.");
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _serialService.Disconnect();
        NotifyConnectionStateChanged();
        ShowInfo("Disconnected.");
    }

    /// <summary>
    /// Opens the Port Settings popup. Changes flow back to the service via
    /// <c>ApplyPortSettings</c> on confirm. The popup also toggles the
    /// service's auto-reconnect flag.
    /// </summary>
    [RelayCommand]
    private void OpenPortSettings()
    {
        var window = new Window
        {
            Title = $"{_module.Name} Port Settings",
            Width = 380,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };

        var vm = new PortSettingsViewModel(
            current: _serialService.GetPortSettings(),
            autoReconnectEnabled: _serialService.AutoReconnectEnabled,
            applySettings: s => { _serialService.ApplyPortSettings(s); RefreshTransportState(); },
            applyAutoReconnect: v => _serialService.AutoReconnectEnabled = v,
            onClose: () => window.Close());

        window.Content = new Views.PortSettingsView { DataContext = vm };
        window.Show();
    }

    [RelayCommand]
    private async Task GetCurrentState()
    {
        IsStateBusy = true;
        LastCommandSummary = "Get Current State";
        NotifyBusyChanged();
        _serialService.SendStateSelection(_module, OpticalModuleRequestedState.GetCurrentState);
        await AutoClearBusyAsync(() => IsStateBusy = false, 1500);
    }

    [RelayCommand]
    private async Task SetOperationalState()
    {
        IsStateBusy = true;
        LastCommandSummary = "Set Operational";
        NotifyBusyChanged();
        _serialService.SendStateSelection(_module, OpticalModuleRequestedState.Operational);
        await AutoClearBusyAsync(() => IsStateBusy = false, 1500);
    }

    [RelayCommand]
    private async Task SetMaintenanceState()
    {
        IsStateBusy = true;
        LastCommandSummary = "Set Maintenance";
        NotifyBusyChanged();
        _serialService.SendStateSelection(_module, OpticalModuleRequestedState.Maintenance);
        await AutoClearBusyAsync(() => IsStateBusy = false, 1500);
    }

    [RelayCommand]
    private async Task SetBitState()
    {
        IsStateBusy = true;
        LastCommandSummary = "Set BIT";
        NotifyBusyChanged();
        _serialService.SendStateSelection(_module, OpticalModuleRequestedState.BuiltInTest);
        await AutoClearBusyAsync(() => IsStateBusy = false, 1500);
    }

    [RelayCommand]
    private async Task RequestGeneralStatus()
    {
        IsStatusBusy = true;
        LastCommandSummary = "Request General Status";
        NotifyBusyChanged();
        _serialService.SendGeneralStatusRequest(_module);
        await AutoClearBusyAsync(() => IsStatusBusy = false, 1500);
    }

    // ── FOV commands (0x0084) ───────────────────────────────────────────

    [RelayCommand]
    private async Task RequestFovFeedback()
    {
        IsFovBusy = true;
        LastCommandSummary = "FOV Get Feedback";
        NotifyBusyChanged();
        _serialService.SendFovGetFeedback(_module);
        await AutoClearBusyAsync(() => IsFovBusy = false, 1500);
    }

    [RelayCommand]
    private async Task GoToFov()
    {
        IsFovBusy = true;
        LastCommandSummary = $"FOV -> {SelectedFov}";
        NotifyBusyChanged();
        _serialService.SendFovGoTo(_module, SelectedFov);
        ShowInfo($"FOV command sent: Go to {SelectedFov}");
        await AutoClearBusyAsync(() => IsFovBusy = false, 1500);
    }

    [RelayCommand]
    private async Task StopFov()
    {
        IsFovBusy = true;
        LastCommandSummary = "FOV Stop";
        NotifyBusyChanged();
        _serialService.SendFovStop(_module);
        ShowInfo("FOV stop sent.");
        await AutoClearBusyAsync(() => IsFovBusy = false, 1500);
    }

    // ── Focus commands (0x0085) — Get / MoveToInfinity / Stop only ──────

    [RelayCommand]
    private async Task RequestFocusFeedback()
    {
        IsFocusBusy = true;
        LastCommandSummary = "Focus Get Feedback";
        NotifyBusyChanged();
        _serialService.SendFocusGetFeedback(_module);
        await AutoClearBusyAsync(() => IsFocusBusy = false, 1500);
    }

    [RelayCommand]
    private async Task MoveFocusToInfinity()
    {
        IsFocusBusy = true;
        LastCommandSummary = "Focus Move to Infinity";
        NotifyBusyChanged();
        _serialService.SendFocusMoveToInfinity(_module);
        ShowInfo("Focus move-to-infinity sent.");
        await AutoClearBusyAsync(() => IsFocusBusy = false, 1500);
    }

    [RelayCommand]
    private async Task SetFocusPosition()
    {
        IsFocusBusy = true;
        LastCommandSummary = $"Focus Set Position -> {FocusTargetPosition}";
        NotifyBusyChanged();
        _serialService.SendFocusSetPosition(_module, FocusTargetPosition);
        ShowInfo($"Focus set position sent: {FocusTargetPosition}");
        await AutoClearBusyAsync(() => IsFocusBusy = false, 1500);
    }

    [RelayCommand]
    private async Task SetFocusSpeed()
    {
        IsFocusBusy = true;
        LastCommandSummary = $"Focus Set Speed -> {FocusTargetSpeed}";
        NotifyBusyChanged();
        _serialService.SendFocusSetSpeed(_module, FocusTargetSpeed);
        ShowInfo($"Focus set speed sent: {FocusTargetSpeed}");
        await AutoClearBusyAsync(() => IsFocusBusy = false, 1500);
    }

    [RelayCommand]
    private async Task StopFocus()
    {
        IsFocusBusy = true;
        LastCommandSummary = "Focus Stop";
        NotifyBusyChanged();
        _serialService.SendFocusStop(_module);
        ShowInfo("Focus stop sent.");
        await AutoClearBusyAsync(() => IsFocusBusy = false, 1500);
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
    private void ClearTrace()
    {
        PacketTraceLines.Clear();
        TraceVersion++;
        OnPropertyChanged(nameof(HasTraceLines));
        AddLog("GUI", "Packet trace cleared.");
    }

    /// <summary>Clear the Decoded Frame panel.</summary>
    [RelayCommand]
    private void ClearDecodedFrame()
    {
        LastDecodedFrameRows.Clear();
        LastDecodedFrameHeader = "No frame received yet.";
    }

    /// <summary>
    /// Decode an EOS frame for this Optical module (VisNIR or SWIR). Uses
    /// the module's DeviceId as the target DstID and the optical command
    /// IDs from OpticalModuleCommandBuilder.
    /// </summary>
    private void DecodeFrame(byte[] frame, string direction, string timestamp)
    {
        LastDecodedFrameRows.Clear();
        foreach (var row in EosFrameDecoder.Decode(
            frame,
            cmdId => cmdId switch
            {
                0x0080 => "State Selection",
                0x0081 => "General Status",
                0x0084 => "FOV (Get / GoTo / Stop)",
                0x0085 => "Focus (Get / Move / Stop)",
                _ => null
            },
            hostSrcId: 0x01,
            targetDstId: _module.DeviceId))
        {
            LastDecodedFrameRows.Add(row);
        }
        LastDecodedFrameHeader = $"{direction} @ {timestamp}  —  {frame.Length} bytes";
    }

    [RelayCommand]
    private void ExportLog()
    {
        if (LogLines.Count == 0)
        {
            ShowWarning("No log lines available to export.");
            return;
        }

        string path = Path.Combine(
            AppContext.BaseDirectory,
            $"{_module.Name.ToLowerInvariant()}_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        File.WriteAllLines(path, LogLines, Encoding.UTF8);
        ShowSuccess($"Log exported to {path}");
    }

    [RelayCommand]
    private void ExportTrace()
    {
        if (PacketTraceLines.Count == 0)
        {
            ShowWarning("No packet trace lines available to export.");
            return;
        }

        string path = Path.Combine(
            AppContext.BaseDirectory,
            $"{_module.Name.ToLowerInvariant()}_trace_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        File.WriteAllLines(path, PacketTraceLines, Encoding.UTF8);
        ShowSuccess($"Trace exported to {path}");
    }

    [RelayCommand]
    private void DismissBanner()
    {
        IsBannerVisible = false;
        BannerText = string.Empty;
    }

    private async Task AutoClearBusyAsync(Action clearAction, int delayMs)
    {
        await Task.Delay(delayMs);
        clearAction();
        NotifyBusyChanged();
    }

    private void NotifyConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
        OnPropertyChanged(nameof(CanSendCommands));
        OnPropertyChanged(nameof(ConnectionStateText));
    }

    private void NotifyBusyChanged()
    {
        NotifyConnectionStateChanged();
        OnPropertyChanged(nameof(ModeStateText));
        OnPropertyChanged(nameof(CalibrationStateText));
        OnPropertyChanged(nameof(BusyStateText));
    }

    private void NotifyDecodedStateChanged()
    {
        OnPropertyChanged(nameof(GeneralStatusSummary));
        OnPropertyChanged(nameof(AlarmSummary));
        OnPropertyChanged(nameof(ControllerPbitSummary));
        OnPropertyChanged(nameof(Zg1PbitSummary));
        OnPropertyChanged(nameof(Zg2PbitSummary));
        OnPropertyChanged(nameof(TemperaturePbitSummary));
        OnPropertyChanged(nameof(ModeStateText));
    }

    private void AddLog(string source, string message)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss.fff}  {source,-4}  {message}");
        while (LogLines.Count > 300)
            LogLines.RemoveAt(0);

        LogVersion++;
        OnPropertyChanged(nameof(HasLogLines));
    }

    private static string DecodeBits(string label, byte value, IReadOnlyList<string> names)
    {
        if (value == 0x00)
            return $"{label}: All clear";

        var parts = new List<string>();
        for (int bit = 0; bit < names.Count; bit++)
        {
            if ((value & (1 << bit)) != 0)
                parts.Add(names[bit]);
        }

        return parts.Count == 0
            ? $"{label}: 0x{value:X2}"
            : $"{label}: {string.Join(" | ", parts)}";
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

    partial void OnIsSimulationModeChanged(bool value)
    {
        _serialService.SetSimulationMode(value);
        RefreshPorts();
        RefreshTransportState();
    }

    // ─────────────────────────────────────────────────────────────────
    // Transport state — reflects the currently-selected transport from
    // _serialService.GetPortSettings(). Refreshed via RefreshTransportState()
    // whenever settings change or we connect/disconnect.
    // ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool isUartTransport = true;
    [ObservableProperty] private bool isUdpTransport = false;
    [ObservableProperty] private string transportSummary = "UART";

    private void RefreshTransportState()
    {
        var s = _serialService.GetPortSettings();
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