namespace ShockUI.Models.OpticalModules;

public enum OpticalModuleState : byte
{
    Operational = 0x00,
    Maintenance = 0x01,
    BuiltInTest = 0x02,
    Error = 0x03,
    Initialization = 0x04,
    Unknown = 0xFF
}

public enum OpticalModuleRequestedState : byte
{
    GetCurrentState = 0x00,
    Operational = 0x01,
    Maintenance = 0x02,
    BuiltInTest = 0x03
}