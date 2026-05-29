namespace ShockUI.Models.OpticalModules;

public sealed class OpticalModuleMessage
{
    public const byte Sync1 = 0x0A;
    public const byte Sync2 = 0x88;
    public const byte ProtocolVersion = 0x01;

    public byte ErrorByte1 { get; set; }
    public byte ErrorByte2 { get; set; }
    public byte DestinationId { get; set; }
    public byte SourceId { get; set; }
    public byte SequenceId { get; set; }
    public ushort Command { get; set; }
    public byte Length { get; set; }
    public byte[] Payload { get; set; } = [];
}