// ============================================================
//  Models/Device/DeviceLrfExtendedResponses.cs
//  Response parsers for the extended LRF commands (Noptel LRX
//  ICD O50090DE), forwarded by the System Controller over SIRS.
// ============================================================
using System;
using System.Text;

namespace ShockUI.Models.Device;

/// <summary>
/// Response to LrfStatusQuery (Noptel §3.4). Wraps the LRX's three
/// status bytes and provides ergonomic boolean accessors for each flag.
/// </summary>
public sealed record DeviceLrfStatusResponse(byte StatusByte1, byte StatusByte2, byte StatusByte3)
{
    // Status byte 1: GP TP REB NR - - RP LP
    public bool GeneralProblems => (StatusByte1 & 0x80) != 0;
    public bool TransmitterProblem => (StatusByte1 & 0x40) != 0;
    public bool Rebooted => (StatusByte1 & 0x20) != 0;
    public bool NotReady => (StatusByte1 & 0x10) != 0;
    public bool ReceiverProblem => (StatusByte1 & 0x02) != 0;
    public bool LaserPowerProblem => (StatusByte1 & 0x01) != 0;

    // Status byte 2: VPOINT HV - DC MEM - LB CP
    public bool PointerOn => (StatusByte2 & 0x80) != 0;
    public bool HighVoltageOutOfRange => (StatusByte2 & 0x40) != 0;
    public bool DcDcOutOfRange => (StatusByte2 & 0x10) != 0;
    public bool MemoryProblem => (StatusByte2 & 0x08) != 0;
    public bool LowBattery => (StatusByte2 & 0x02) != 0;
    public bool CommunicationProblem => (StatusByte2 & 0x01) != 0;

    // Status byte 3: - MT NT ERR - TTE - -
    public bool MultipleTargets => (StatusByte3 & 0x40) != 0;
    public bool NoTargets => (StatusByte3 & 0x20) != 0;
    public bool ErrorReported => (StatusByte3 & 0x10) != 0;
    public bool TransmitterTiming => (StatusByte3 & 0x04) != 0;

    public static DeviceLrfStatusResponse? Parse(byte[] payload)
    {
        if (payload is null || payload.Length < 3) return null;
        return new DeviceLrfStatusResponse(payload[0], payload[1], payload[2]);
    }
}

/// <summary>
/// Response to LrfOpticalCrosstalk (Noptel §3.3). Returns the maximum
/// effective distance at which crosstalk could impact ranging; ideally
/// under 100 m.
/// </summary>
public sealed record DeviceLrfOpticalCrosstalkResponse(ushort EffectRangeMeters)
{
    public static DeviceLrfOpticalCrosstalkResponse? Parse(byte[] payload)
    {
        if (payload is null || payload.Length < 2) return null;
        ushort range = (ushort)(payload[0] | (payload[1] << 8));
        return new DeviceLrfOpticalCrosstalkResponse(range);
    }
}

/// <summary>
/// Response to LrfIdentification (Noptel §3.10). Carries device ID,
/// firmware version, serial number, electronics/optics type, and the
/// build date/time of the firmware. Layout follows the Noptel response
/// frame exactly so a real LRX response can be passed through unchanged.
/// </summary>
public sealed record DeviceLrfIdentificationResponse(
    string DeviceId,
    string Reserved,
    string SerialNumber,
    ushort FirmwareVersion,
    byte ElectronicsType,
    byte OpticsType,
    string FirmwareDate,
    string FirmwareTime)
{
    public static DeviceLrfIdentificationResponse? Parse(byte[] p)
    {
        // Layout per Noptel §3.10 (bytes 0..69 of the response, without
        // the SIRS framing). CR/LF separators are preserved by the LRX
        // and we just strip them on read.
        if (p is null || p.Length < 70) return null;
        try
        {
            string deviceId = Encoding.ASCII.GetString(p, 0, 15).TrimEnd('\0', ' ');
            string reserved = Encoding.ASCII.GetString(p, 17, 15).TrimEnd('\0', ' ');
            string serial = Encoding.ASCII.GetString(p, 34, 10).TrimEnd('\0', ' ');
            ushort fwVersion = (ushort)(p[46] | (p[47] << 8));
            byte electronicsType = p[48];
            byte opticsType = p[49];
            string fwDate = Encoding.ASCII.GetString(p, 50, 8).TrimEnd('\0', ' ');
            string fwTime = Encoding.ASCII.GetString(p, 60, 8).TrimEnd('\0', ' ');

            return new DeviceLrfIdentificationResponse(
                deviceId, reserved, serial, fwVersion,
                electronicsType, opticsType, fwDate, fwTime);
        }
        catch { return null; }
    }
}

/// <summary>
/// Response to LrfDiagnostics (Noptel §3.11). Captures the full
/// diagnostic block: ranging, signal magnitudes, power rails, RX
/// temperature, pulse counter and serial error counter.
/// </summary>
public sealed record DeviceLrfDiagnosticsResponse(
    ushort Target1DistanceMeters,
    ushort Target2DistanceMeters,
    ushort Target3DistanceMeters,
    byte Target1Magnitude,
    byte Target2Magnitude,
    byte Target3Magnitude,
    ushort BatteryMillivolts,
    ushort PowerMilliwatts,
    ushort IoMillivolts,
    ushort DetectorBiasCv,
    ushort FiveVoltMillivolts,
    short RxTemperatureHundredthsC,
    byte StatusByte1,
    byte StatusByte2,
    byte StatusByte3,
    uint PulseCounterMillions,
    byte RsErrorCounter)
{
    public double BatteryVolts => BatteryMillivolts / 1000.0;
    public double PowerWatts => PowerMilliwatts / 1000.0;
    public double IoVolts => IoMillivolts / 1000.0;
    public double DetectorBiasVolts => DetectorBiasCv / 100.0;
    public double FiveVoltVolts => FiveVoltMillivolts / 1000.0;
    public double RxTemperatureC => RxTemperatureHundredthsC / 100.0;

    public static DeviceLrfDiagnosticsResponse? Parse(byte[] p)
    {
        // Per Noptel §3.11 — bytes 0..36 of the response payload.
        if (p is null || p.Length < 37) return null;
        try
        {
            ushort t1 = (ushort)(p[8] | (p[9] << 8));
            ushort t2 = (ushort)(p[10] | (p[11] << 8));
            ushort t3 = (ushort)(p[12] | (p[13] << 8));
            byte m1 = p[14];
            byte m2 = p[15];
            byte m3 = p[16];
            ushort batt = (ushort)(p[18] | (p[19] << 8));
            ushort pwr = (ushort)(p[20] | (p[21] << 8));
            ushort io = (ushort)(p[22] | (p[23] << 8));
            ushort det = (ushort)(p[24] | (p[25] << 8));
            ushort fv = (ushort)(p[26] | (p[27] << 8));
            short rxt = (short)(p[28] | (p[29] << 8));
            byte sb1 = p[30];
            byte sb2 = p[31];
            byte sb3 = p[32];
            uint pulses = (uint)(p[33] | (p[34] << 8) | (p[35] << 16));
            byte rsErr = p[36];

            return new DeviceLrfDiagnosticsResponse(
                t1, t2, t3, m1, m2, m3,
                batt, pwr, io, det, fv, rxt,
                sb1, sb2, sb3, pulses, rsErr);
        }
        catch { return null; }
    }
}