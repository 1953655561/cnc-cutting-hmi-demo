using System.IO;
using System.IO.Ports;
using CncHmiDemo.Models;

namespace CncHmiDemo.Services;

/// <summary>Modbus RTU 串口客户端（需配合真实 PLC / 虚拟串口对使用）。</summary>
public sealed class SerialModbusClient : IDeviceClient
{
    private const int RegisterCount = 10;
    private readonly SerialPort _port;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private bool _disposed;

    public SerialModbusClient(string portName, int baudRate = 9600)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 700,
            WriteTimeout = 700,
            Handshake = Handshake.None
        };
    }

    public bool IsConnected => _port.IsOpen;

    public event Action<DeviceSnapshot>? SnapshotReceived;
    public event Action<string, string, string>? FrameLogged;
    public event Action<bool, string>? ConnectionChanged;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _port.Open();
        _cts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoop(_cts.Token), CancellationToken.None);
        ConnectionChanged?.Invoke(true, $"串口 {_port.PortName} 已打开 @ {_port.BaudRate}");
        return Task.CompletedTask;
    }

    private void PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SnapshotReceived?.Invoke(PollOnce());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ConnectionChanged?.Invoke(false, "串口通信异常：" + ex.Message);
                break;
            }

            try
            {
                Thread.Sleep(150);
            }
            catch
            {
                break;
            }
        }
    }

    private DeviceSnapshot PollOnce()
    {
        lock (_sync)
        {
            var request = ModbusCodec.BuildRtuRead(1, 0, RegisterCount);
            _port.Write(request, 0, request.Length);
            FrameLogged?.Invoke("TX", ModbusCodec.Hex(request), "FC03 读保持寄存器 0~9（RTU）");
            var body = ReadRtuReadResponse();
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
    }

    private byte[] ReadRtuReadResponse()
    {
        var header = new byte[3];
        ReadExact(header);
        if ((header[1] & 0x80) != 0)
        {
            var exception = new byte[2];
            ReadExact(exception);
            throw new InvalidDataException($"Modbus 异常响应，异常码 0x{header[2]:X2}");
        }

        var byteCount = header[2];
        var payload = new byte[byteCount + 2];
        ReadExact(payload);

        var frame = new byte[3 + payload.Length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, 3);
        FrameLogged?.Invoke("RX", ModbusCodec.Hex(frame), "FC03 响应（RTU）");

        var crc = ModbusCodec.Crc16(frame.AsSpan(0, frame.Length - 2));
        if ((byte)(crc & 0xFF) != frame[^2] || (byte)(crc >> 8) != frame[^1])
        {
            throw new InvalidDataException("Modbus RTU CRC 校验失败");
        }

        var body = new byte[2 + byteCount];
        body[0] = header[1];
        body[1] = header[2];
        Array.Copy(payload, 0, body, 2, byteCount);
        return body;
    }

    public Task WriteRegisterAsync(ushort address, ushort value, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            lock (_sync)
            {
                var request = ModbusCodec.BuildRtuWriteSingle(1, address, value);
                _port.Write(request, 0, request.Length);
                FrameLogged?.Invoke("TX", ModbusCodec.Hex(request), "FC06 写单个寄存器（RTU）");
                var response = new byte[8];
                ReadExact(response);
                FrameLogged?.Invoke("RX", ModbusCodec.Hex(response), "FC06 回显（RTU）");
            }
        }, cancellationToken);
    }

    private void ReadExact(byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = _port.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) throw new IOException("串口读取超时");
            offset += read;
        }
    }

    public Task DisconnectAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch
        {
            // ignored
        }

        ConnectionChanged?.Invoke(false, "串口已关闭");
        return Task.CompletedTask;
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

        try
        {
            if (_port.IsOpen) _port.Close();
        }
        catch
        {
            // ignored
        }

        _port.Dispose();
        _cts?.Dispose();
    }
}
