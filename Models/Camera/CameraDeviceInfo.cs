namespace ShockUI.Models.Camera;

public sealed class CameraDeviceInfo
{
    public CameraType CameraType { get; set; } = CameraType.None;
    public uint MaxPos1 { get; set; }
    public uint MaxPos2 { get; set; }
    public uint MaxPos3 { get; set; }

    public bool SupportsZg3 => CameraType == CameraType.MWIR;
}