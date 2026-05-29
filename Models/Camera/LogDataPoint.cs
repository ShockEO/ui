namespace ShockUI.Models.Camera;

public sealed class LogDataPoint
{
    public int Index { get; set; }
    public double TimeMs { get; set; }
    public int ZG1 { get; set; }
    public int ZG2 { get; set; }
    public int ZG3 { get; set; }
    public double Temp1 { get; set; }
    public double Temp2 { get; set; }
    public string Notes { get; set; } = string.Empty;
}