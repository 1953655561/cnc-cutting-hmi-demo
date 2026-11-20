using System.IO;
using System.Net.Sockets;
using CncHmiDemo.Models;

namespace CncHmiDemo.Services;

/// <summary>Modbus TCP 客户端：后台轮询保持寄存器 + 下发写寄存器指令。</summary>
public sealed class TcpModbusClient : IDeviceClient
{
    private const int RegisterCount = 10;
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private ushort _transactionId;
    private bool _disposed;

    public TcpModbusClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public bool IsConnected => _tcp is { Connected: true };

    public event Action<DeviceSnapshot>? SnapshotReceived;
    public event Action<string, string, string>? FrameLogged;
    public event Action<bool, string>? ConnectionChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), CancellationToken.None);
        ConnectionChanged?.Invoke(true, $"TCP 已连接 {_host}:{_port}");
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = await PollOnceAsync(ct).ConfigureAwait(false);
                SnapshotReceived?.Invoke(snapshot);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnectionChanged?.Invoke(false, "通信中断：" + ex.Message);
                break;
            }

            try
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<DeviceSnapshot> PollOnceAsync(CancellationToken ct)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var request = ModbusCodec.BuildTcpRead(NextTransactionId(), 1, 0, RegisterCount);
            var body = await SendAndReceiveAsync(request, ct).ConfigureAwait(false);
            var registers = ModbusCodec.ParseReadRegisters(body, RegisterCount);

            ushort R(int index) => index >= 0 && index < registers.Length ? registers[index] : (ushort)0;
            return new DeviceSnapshot(
                X: R(0) / 10.0,
                Y: R(1) / 10.0,
                SpindleRpm: R(2),
                CutSpeed: R(3),
                Power: R(4),
                State: (MachineState)R(5),
                AlarmCode: R(6),
                Progress: R(7),
                PulseFreq: R(8),
                Accel: R(9),
                Timestamp: DateTime.Now);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    public async Task WriteRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = ModbusCodec.BuildTcpWriteSingle(NextTransactionId(), 1, address, value);
            await SendAndReceiveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    private async Task<byte[]> SendAndReceiveAsync(byte[] request, CancellationToken ct)
    {
        if (_stream is null) throw new InvalidOperationException("TCP 尚未连接");
        await _stream.WriteAsync(request, ct).ConfigureAwait(false);
        var isRead = request[7] == ModbusCodec.ReadHoldingRegisters;
        FrameLogged?.Invoke("TX", ModbusCodec.Hex(request), isRead ? "FC03 读保持寄存器 0~9" : "FC06 写单个寄存器");

        var header = new byte[7];
        await ReadExactAsync(_stream, header, ct).ConfigureAwait(false);
        var length = (header[4] << 8) | header[5];
        var body = new byte[Math.Max(0, length - 1)];
        await ReadExactAsync(_stream, body, ct).ConfigureAwait(false);

        var frame = new byte[7 + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, 7);
        FrameLogged?.Invoke("RX", ModbusCodec.Hex(frame), body.Length > 0 && body[0] == ModbusCodec.ReadHoldingRegisters ? "FC03 响应" : "FC06 回显");
        return body;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read <= 0) throw new IOException("Modbus TCP 连接已关闭");
            offset += read;
        }
    }

    private ushort NextTransactionId() => ++_transactionId;

    public async Task DisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
        ConnectionChanged?.Invoke(false, "TCP 已断开");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        _stream?.Dispose();
        _tcp?.Dispose();
        _cts?.Dispose();
        _ioLock.Dispose();
    }
}
