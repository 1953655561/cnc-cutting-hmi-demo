using CncHmiDemo.Models;

namespace CncHmiDemo.Services;

public interface IDeviceClient : IDisposable
{
    bool IsConnected { get; }

    event Action<DeviceSnapshot>? SnapshotReceived;
    event Action<string, string, string>? FrameLogged; // direction(TX/RX), hex, note
    event Action<bool, string>? ConnectionChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task WriteRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default);
}
