using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI.Models.Device;
using ShockUI.Models.OpticalModules;
using ShockUI.Services.Device;
using ShockUI.Services.OpticalModules;

namespace ShockUI.ViewModels.Modules;

public sealed partial class VisNIRControllerViewModel : OpticalModuleControllerViewModel
{
    // The Start-NUC commands are SIRS frames addressed to the System
    // Controller (dst 0x10), not optical-module frames. They therefore go
    // out over the shared DeviceSerialService rather than this module's
    // OpticalModuleSerialService. The card that drives them lives in the
    // VisNir view but is gated behind AppState.Current.IsMaintenanceUnlocked.
    private readonly DeviceSerialService _deviceService;

    public VisNIRControllerViewModel(
        OpticalModuleSerialService serialService,
        DeviceSerialService deviceService)
        : base(OpticalModuleDefinition.VisNIR, serialService)
    {
        ModuleTitle = "VisNir Controller";
        _deviceService = deviceService;

        // Listen for the firmware ack on the SIRS bus so the feedback
        // labels flip to "Started (… NUC)".
        _deviceService.ResponseReceived += OnDeviceResponseReceived;

        // The NUC frame goes out on the device (SIRS) bus, whose TX/RX bytes
        // are not otherwise visible on this screen. Mirror them into this
        // module's packet trace so the operator sees the command land.
        _deviceService.RawFrame += OnDeviceRawFrame;

        // The NUC frame rides the System Controller's SIRS bus (dst 0x10),
        // which is a separate service instance from this module's optical
        // link. So that the maintenance NUC buttons work from this screen —
        // especially in simulation — mirror this module's simulation and
        // connection state onto the shared device service.
        //
        // Real-hardware note: we only auto-drive the device bus in SIMULATION.
        // On real hardware the device bus is owned/opened by the System
        // Controller (via the master-connection coordinator), so we must not
        // grab a COM port here — that would collide with the SC's own port.
        _deviceService.SetSimulationMode(IsSimulationMode);
        PropertyChanged += OnVmPropertyChanged;
        _ = SyncDeviceBusAsync();
    }

    private async void OnVmPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsSimulationMode))
        {
            _deviceService.SetSimulationMode(IsSimulationMode);
            await SyncDeviceBusAsync();
            OnPropertyChanged(nameof(CanSendNuc));
        }
        else if (e.PropertyName == nameof(IsConnected))
        {
            await SyncDeviceBusAsync();
            OnPropertyChanged(nameof(CanSendNuc));
            StartRgbNucCommand.NotifyCanExecuteChanged();
            StartNirNucCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Brings the shared device (SIRS) bus into the same simulated-connection
    /// state as this module, so NUC frames transmit and the simulator replies.
    /// Only acts in simulation mode; on real hardware the System Controller
    /// owns the device bus.
    /// </summary>
    private async Task SyncDeviceBusAsync()
    {
        if (!IsSimulationMode) return;

        if (IsConnected && !_deviceService.IsConnected)
            await _deviceService.ConnectAsync("SIM-VISNIR-NUC");
        else if (!IsConnected && _deviceService.IsConnected)
            _deviceService.Disconnect();
    }

    // ── Maintenance: Start NUC (RGB + NIR) ───────────────────────────
    // Payload per the May 2026 NUC spec: [type, 0x00, 0x00]
    //   type = 1 (1-Point) or 2 (2-Point)
    // Wire frames (dst 0x10, host src 0x00):
    //   RGB: 0A 88 01 00 00 10 src seq 00 19 03 <type> 00 00 crc_lsb crc_msb
    //   NIR: 0A 88 01 00 00 10 src seq 00 1A 03 <type> 00 00 crc_lsb crc_msb

    /// <summary>Dropdown options for the NUC type selector. Index = wire value.
    /// 1-Point at index 1, 2-Point at index 2. Index 0 is reserved/unused.</summary>
    public string[] NucTypeOpts => ["—", "1-Point NUC", "2-Point NUC"];

    /// <summary>NUC type to send when the user clicks <em>Start RGB NUC</em>.
    /// Default 2 (2-Point) — the most common factory recommendation.</summary>
    [ObservableProperty] private byte rgbNucType = 2;

    /// <summary>NUC type for the NIR sensor's Start-NUC command.</summary>
    [ObservableProperty] private byte nirNucType = 2;

    /// <summary>Last RGB NUC response status text.</summary>
    [ObservableProperty] private string rgbNucFeedback = "—";

    /// <summary>Last NIR NUC response status text.</summary>
    [ObservableProperty] private string nirNucFeedback = "—";

    /// <summary>
    /// True when the SIRS (device) bus is ready to carry a NUC frame.
    /// </summary>
    public bool CanSendNuc => _deviceService.IsConnected;

    [RelayCommand(CanExecute = nameof(CanSendNuc))]
    private async Task StartRgbNuc()
    {
        ShowInfo($"RGB NUC ({NucTypeOpts[Math.Clamp(RgbNucType, 0, NucTypeOpts.Length - 1)]}) requested…");
        RgbNucFeedback = "Requested…";
        await _deviceService.SendRgbStartNucAsync(RgbNucType);
    }

    [RelayCommand(CanExecute = nameof(CanSendNuc))]
    private async Task StartNirNuc()
    {
        ShowInfo($"NIR NUC ({NucTypeOpts[Math.Clamp(NirNucType, 0, NucTypeOpts.Length - 1)]}) requested…");
        NirNucFeedback = "Requested…";
        await _deviceService.SendNirStartNucAsync(NirNucType);
    }

    private void OnDeviceRawFrame(byte[] frame, bool isTx)
    {
        string dir = isTx ? "TX" : "RX";
        PacketTraceLines.Add($"{dir}  {BitConverter.ToString(frame).Replace("-", " ")}  (NUC bus)");
        while (PacketTraceLines.Count > 300)
            PacketTraceLines.RemoveAt(0);
        TraceVersion++;
        OnPropertyChanged(nameof(HasTraceLines));
    }

    private void OnDeviceResponseReceived(DeviceParsedFrame frame)
    {
        switch (frame.CommandId)
        {
            case DeviceCommandId.RgbStartNuc:
                {
                    byte echoed = frame.Payload.Length > 0 ? frame.Payload[0] : (byte)0;
                    RgbNucFeedback = $"Started ({NucTypeOpts[Math.Clamp(echoed, 0, NucTypeOpts.Length - 1)]}) at {DateTime.Now:HH:mm:ss}";
                    break;
                }
            case DeviceCommandId.NirStartNuc:
                {
                    byte echoed = frame.Payload.Length > 0 ? frame.Payload[0] : (byte)0;
                    NirNucFeedback = $"Started ({NucTypeOpts[Math.Clamp(echoed, 0, NucTypeOpts.Length - 1)]}) at {DateTime.Now:HH:mm:ss}";
                    break;
                }
        }
    }
}