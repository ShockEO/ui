namespace ShockUI.Models.Camera;

public sealed class ZgPositions
{
    public uint ZG1 { get; set; }
    public uint ZG2 { get; set; }
    public uint ZG3 { get; set; }
}

public sealed class FovPositions
{
    public ZgPositions WFOV { get; set; } = new();
    public ZgPositions MWFOV { get; set; } = new();
    public ZgPositions MNFOV { get; set; } = new();
    public ZgPositions NFOV { get; set; } = new();
}