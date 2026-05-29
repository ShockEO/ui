namespace ShockUI.Models.OpticalModules;

public sealed class OpticalModuleDefinition
{
    public required string Name { get; init; }
    public required byte DeviceId { get; init; }

    public static OpticalModuleDefinition VisNIR { get; } = new()
    {
        Name = "VisNIR",
        DeviceId = 0x54
    };

    public static OpticalModuleDefinition SWIR { get; } = new()
    {
        Name = "SWIR",
        DeviceId = 0x53
    };
}