namespace ShockUI.Models.OpticalModules;

public sealed class OpticalModuleGeneralStatus
{
    public bool MessageCounterReset { get; set; }
    public bool CrcErrorCounterReset { get; set; }
    public bool MessageFormatErrorCounterReset { get; set; }

    public int MessageCounter { get; set; }
    public ushort CrcErrorCount { get; set; }
    public ushort MessageFormatErrorCount { get; set; }

    public byte ControllerPbitStatus { get; set; }
    public byte ZoomGroup1PbitStatus { get; set; }
    public byte ZoomGroup2PbitStatus { get; set; }
    public byte TemperatureSensorsPbitStatus { get; set; }
    public byte EtiAndStateByte { get; set; }

    public OpticalModuleState CurrentState { get; set; } = OpticalModuleState.Unknown;

    public bool ExternalDeviceAlarmActive { get; set; }
    public bool ProcessorAlarmActive { get; set; }
}