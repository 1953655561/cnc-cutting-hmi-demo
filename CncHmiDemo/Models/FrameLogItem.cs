namespace CncHmiDemo.Models;

public sealed class FrameLogItem
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Direction { get; init; } = "TX";
    public string Hex { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;

    public string TimeText => Time.ToString("HH:mm:ss.fff");
}
