namespace ShockUI.Models.App;

public sealed class AppSettings
{
    public bool IsSimulationMode { get; set; } = false;
    public string? LastSelectedPort { get; set; }
    public string SelectedFov { get; set; } = "WFOV";
    public int SpeedZg1 { get; set; } = 100;
    public int SpeedZg2 { get; set; } = 100;
    public int SpeedZg3 { get; set; } = 100;

    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool IsMaximized { get; set; }

    public string SelectedModuleKey { get; set; } = "camera";
    public int CameraSelectedTabIndex { get; set; }
}