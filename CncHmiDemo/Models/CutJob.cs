using CncHmiDemo.Common;

namespace CncHmiDemo.Models;

public sealed class CutJob : ObservableObject
{
    private string _status = "排队中";
    private int _progress;

    public string Name { get; init; } = string.Empty;
    public string Material { get; init; } = string.Empty;
    public string Thickness { get; init; } = string.Empty;
    public int EstimatedSeconds { get; init; } = 30;

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public int Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }
}
