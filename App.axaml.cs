using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ShockUI.Services.App;
using ShockUI.Services.Camera;
using ShockUI.Services.Device;
using ShockUI.Services.OpticalModules;
using ShockUI.Services.PanTilt;
using ShockUI.ViewModels;
using ShockUI.ViewModels.Modules;
using ShockUI.Views;

namespace ShockUI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var navigationService = new NavigationService();

            // ── Services ──────────────────────────────────────────────────
            var cameraSerialService = new CameraSerialService();
            var visnirSerialService = new OpticalModuleSerialService();
            var swirSerialService = new OpticalModuleSerialService();
            var panTiltSerialService = new PanTiltSerialService();
            var deviceSerialService = new DeviceSerialService();

            // ── View models ───────────────────────────────────────────────
            var cameraVm = new CameraControllerViewModel(navigationService, cameraSerialService);
            var visnirVm = new VisNIRControllerViewModel(visnirSerialService);
            var swirVm = new SwirControllerViewModel(swirSerialService);
            var panTiltVm = new PanTiltControllerViewModel(panTiltSerialService);
            var deviceVm = new DeviceControllerViewModel(deviceSerialService);

            // ── Master connection cascade ─────────────────────────────────
            // The System Controller (deviceVm) acts as the master. When it
            // connects, the coordinator triggers Connect on every other
            // module sequentially; when it disconnects, it cascades the
            // disconnect. Each follower still owns its own port and serial
            // service — the coordinator just invokes the existing
            // Connect / Disconnect commands so the operator only has to
            // touch the System Controller's Connection card.
            var coordinator = new MasterConnectionCoordinator();
            coordinator.RegisterFollower(
                "Camera Controller",
                cameraVm.ConnectCommand,
                cameraVm.DisconnectCommand,
                () => cameraVm.IsConnected);
            coordinator.RegisterFollower(
                "VisNIR Controller",
                visnirVm.ConnectCommand,
                visnirVm.DisconnectCommand,
                () => visnirVm.IsConnected);
            coordinator.RegisterFollower(
                "SWIR Controller",
                swirVm.ConnectCommand,
                swirVm.DisconnectCommand,
                () => swirVm.IsConnected);
            coordinator.RegisterFollower(
                "Pan/Tilt Controller",
                panTiltVm.ConnectCommand,
                panTiltVm.DisconnectCommand,
                () => panTiltVm.IsConnected);

            // Listen for the System Controller's IsConnected toggling and
            // cascade. Marshal back onto the UI thread because some sub-VM
            // Connect commands may touch ObservableCollections that have
            // UI bindings.
            deviceVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != nameof(deviceVm.IsConnected)) return;

                Dispatcher.UIThread.Post(async () =>
                {
                    if (deviceVm.IsConnected)
                        await coordinator.ConnectFollowersAsync();
                    else
                        coordinator.DisconnectFollowers();
                });
            };

            // ── Navigation ────────────────────────────────────────────────
            // Top-level (always visible)
            navigationService.RegisterModule("System Controller", deviceVm, isEngineering: false);

            // Engineering section (password protected)
            navigationService.RegisterModule("Camera Controller", cameraVm, isEngineering: true);
            navigationService.RegisterModule("VisNir Controller", visnirVm, isEngineering: true);
            navigationService.RegisterModule("SWIR Controller", swirVm, isEngineering: true);
            navigationService.RegisterModule("Pan/Tilt Controller", panTiltVm, isEngineering: true);

            // Start on System Controller
            navigationService.NavigateTo(deviceVm);

            // ── Shell ─────────────────────────────────────────────────────
            var shellVm = new ShellViewModel(navigationService);
            var mainWindowVm = new MainWindowViewModel(shellVm);

            desktop.MainWindow = new MainWindow { DataContext = mainWindowVm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}