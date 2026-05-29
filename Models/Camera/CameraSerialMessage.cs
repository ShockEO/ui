namespace ShockUI.Models.Camera;

public sealed class CameraSerialMessage
{
    public byte Command { get; set; }
    public int Length { get; set; }

    // Must be large enough for the biggest payload we parse.
    public byte[] Data { get; } = new byte[64];
}