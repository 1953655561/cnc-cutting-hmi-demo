namespace CncHmiDemo.Models;

public sealed class AlarmItem
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Level { get; init; } = "提示";
    public int Code { get; init; }
    public string Message { get; init; } = string.Empty;

    public string TimeText => Time.ToString("HH:mm:ss");
}
