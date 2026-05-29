namespace ShockUI.Models.Camera;

public enum CameraCommand : byte
{
    Handshake = 0,
    Calibrate = 1,
    PosControl = 2,
    SpeedControl = 3,
    TempFeedback = 4,
    StepControl = 5,
    DataLogger = 6,
    InvalidCmdId = 7
}