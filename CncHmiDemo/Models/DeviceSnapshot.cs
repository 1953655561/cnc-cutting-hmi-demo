namespace CncHmiDemo.Models;

public enum MachineState : ushort
{
    Idle = 0,
    Running = 1,
    Paused = 2,
    Alarm = 3,
    Offline = 4
}

public sealed record DeviceSnapshot(
    double X,
    double Y,
    int SpindleRpm,
    double CutSpeed,
    double Power,
    MachineState State,
    int AlarmCode,
    int Progress,
    int PulseFreq,
    double Accel,
    DateTime Timestamp);
