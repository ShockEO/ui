namespace ShockUI.Models.Camera;

public sealed class CameraModuleState
{
    public bool IsConnected { get; set; }
    public bool IsCalibrated { get; set; }
    public CameraType CameraType { get; set; } = CameraType.None;

    public int MaxPos1 { get; set; }
    public int MaxPos2 { get; set; }
    public int MaxPos3 { get; set; }

    public int SetPos1 { get; set; }
    public int SetPos2 { get; set; }
    public int SetPos3 { get; set; }

    public int MeasuredPos1 { get; set; }
    public int MeasuredPos2 { get; set; }
    public int MeasuredPos3 { get; set; }

    public double Temp1 { get; set; }
    public double Temp2 { get; set; }

    public bool SupportsZg3 => CameraType == CameraType.MWIR;
}