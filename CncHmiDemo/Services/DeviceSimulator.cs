using System.IO;
using System.Net;
using System.Net.Sockets;
using CncHmiDemo.Models;

namespace CncHmiDemo.Services;

/// <summary>
/// 内置下位机模拟器：在本机启动一个 Modbus TCP 从站，
/// 模拟数控切割设备的寄存器、状态机、加工进度与报警。
/// </summary>
public sealed class DeviceSimulator : IDisposable
{
    private readonly TcpListener _listener;
    private readonly object _sync = new();
    private readonly ushort[] _registers = new ushort[64];
    private CancellationTokenSource? _cts;
    private int _tick;
    private MachineState _state = MachineState.Idle;
    private int _alarmTicks;
    private int _alarmCode;
    private double _progress;
    private bool _disposed;

    public DeviceSimulator(int port = 1502)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        // 默认参数：切割速度 800 mm/min、功率 60%、脉冲频率 20 kHz、加速度 500 mm/s²
        _registers[3] = 800;
        _registers[4] = 60;
        _registers[8] = 20;
        _registers[9] = 500;
    }

    public int Port { get; }
    public bool IsRunning { get; private set; }
    public event Action<string>? Log;

    public void Start()
    {
        if (IsRunning) return;
        _listener.Start();
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
        _ = Task.Run(() => SimulationLoopAsync(_cts.Token), CancellationToken.None);
        Log?.Invoke($"模拟下位机已启动：127.0.0.1:{Port}（Modbus TCP / Unit 1）");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
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
            _listener.Stop();
        }
        catch
        {
            // ignored
        }

        Log?.Invoke("模拟下位机已停止");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log?.Invoke("模拟器监听异常：" + ex.Message);
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var header = new byte[7];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
                    var length = (header[4] << 8) | header[5];
                    if (length < 2) continue;
                    var pdu = new byte[length - 1];
                    await ReadExactAsync(stream, pdu, ct).ConfigureAwait(false);

                    var transactionId = (ushort)((header[0] << 8) | header[1]);
                    var unitId = header[6];
                    var functionCode = pdu[0];

                    var response = functionCode switch
                    {
                        ModbusCodec.ReadHoldingRegisters => BuildReadResponse(transactionId, unitId, pdu),
                        ModbusCodec.WriteSingleRegister => BuildWriteResponse(transactionId, unitId, pdu),
                        _ => BuildExceptionResponse(transactionId, unitId, functionCode, 0x01)
                    };

                    await stream.WriteAsync(response, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (IOException)
            {
                // 客户端断开
            }
            catch (Exception ex)
            {
                Log?.Invoke("模拟器客户端异常：" + ex.Message);
            }
        }
    }

    private byte[] BuildReadResponse(ushort transactionId, byte unitId, byte[] pdu)
    {
        if (pdu.Length < 5) return BuildExceptionResponse(transactionId, unitId, ModbusCodec.ReadHoldingRegisters, 0x03);
        var start = (ushort)((pdu[1] << 8) | pdu[2]);
        var count = (ushort)((pdu[3] << 8) | pdu[4]);
        if (count == 0 || count > 32) return BuildExceptionResponse(transactionId, unitId, ModbusCodec.ReadHoldingRegisters, 0x03);

        var values = new ushort[count];
        lock (_sync)
        {
            for (var i = 0; i < count; i++)
            {
                var address = start + i;
                values[i] = address < _registers.Length ? _registers[address] : (ushort)0;
            }
        }

        var frame = new byte[9 + count * 2];
        WriteMbap(frame, transactionId, unitId, (ushort)(3 + count * 2));
        frame[7] = ModbusCodec.ReadHoldingRegisters;
        frame[8] = (byte)(count * 2);
        for (var i = 0; i < count; i++)
        {
            frame[9 + i * 2] = (byte)(values[i] >> 8);
            frame[10 + i * 2] = (byte)values[i];
        }
        return frame;
    }

    private byte[] BuildWriteResponse(ushort transactionId, byte unitId, byte[] pdu)
    {
        if (pdu.Length < 5) return BuildExceptionResponse(transactionId, unitId, ModbusCodec.WriteSingleRegister, 0x03);
        var address = (ushort)((pdu[1] << 8) | pdu[2]);
        var value = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_sync)
        {
            if (address < _registers.Length) _registers[address] = value;
        }

        var frame = new byte[12];
        WriteMbap(frame, transactionId, unitId, 6);
        pdu.CopyTo(frame, 7);
        return frame;
    }

    private static byte[] BuildExceptionResponse(ushort transactionId, byte unitId, byte functionCode, byte exceptionCode)
    {
        var frame = new byte[9];
        WriteMbap(frame, transactionId, unitId, 3);
        frame[7] = (byte)(functionCode | 0x80);
        frame[8] = exceptionCode;
        return frame;
    }

    private static void WriteMbap(byte[] frame, ushort transactionId, byte unitId, ushort length)
    {
        frame[0] = (byte)(transactionId >> 8);
        frame[1] = (byte)transactionId;
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)length;
        frame[6] = unitId;
    }

    private async Task SimulationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log?.Invoke("模拟器运行异常：" + ex.Message);
            }

            try
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Tick()
    {
        _tick++;
        lock (_sync)
        {
            var control = _registers[20];
            if (control != 0)
            {
                _registers[20] = 0;
                switch (control)
                {
                    case 1:
                        _state = MachineState.Running;
                        _progress = 0;
                        _alarmCode = 0;
                        _alarmTicks = 0;
                        Log?.Invoke("收到启动指令，开始加工");
                        break;
                    case 2:
                        if (_state == MachineState.Running)
                        {
                            _state = MachineState.Paused;
                            Log?.Invoke("收到暂停指令");
                        }
                        break;
                    case 3:
                        _state = MachineState.Idle;
                        _progress = 0;
                        _alarmCode = 0;
                        _alarmTicks = 0;
                        Log?.Invoke("收到停止指令");
                        break;
                    case 4:
                        if (_alarmCode != 0) Log?.Invoke($"报警复位（原代码 {_alarmCode}）");
                        _alarmCode = 0;
                        _alarmTicks = 0;
                        if (_state == MachineState.Alarm) _state = MachineState.Paused;
                        break;
                }
            }

            if (_alarmTicks > 0)
            {
                _alarmTicks--;
                if (_alarmTicks == 0)
                {
                    _alarmCode = 0;
                    if (_state == MachineState.Alarm) _state = MachineState.Running;
                }
            }
            else if (_state == MachineState.Running && _tick % 220 == 0)
            {
                TriggerAlarm();
            }

            if (_state == MachineState.Running && _alarmTicks == 0)
            {
                _progress += 0.45;
                if (_progress >= 100)
                {
                    _progress = 100;
                    _state = MachineState.Idle;
                    Log?.Invoke("加工完成");
                }

                var point = SamplePath.At(_progress);
                _registers[0] = (ushort)Math.Clamp(point.X * 10, 0, ushort.MaxValue);
                _registers[1] = (ushort)Math.Clamp(point.Y * 10, 0, ushort.MaxValue);
                _registers[2] = (ushort)(12000 + (int)(Math.Sin(_tick / 8.0) * 180));
            }
            else
            {
                _registers[2] = 0;
            }

            _registers[5] = (ushort)_state;
            _registers[6] = (ushort)_alarmCode;
            _registers[7] = (ushort)Math.Clamp((int)_progress, 0, 100);
        }
    }

    private void TriggerAlarm()
    {
        var index = _tick / 220 % 2;
        var (code, message) = index == 0 ? (101, "冷却液液位低") : (102, "伺服过载");
        _alarmCode = code;
        _alarmTicks = 25;
        _state = MachineState.Alarm;
        Log?.Invoke($"报警 [{code}] {message}");
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read <= 0) throw new IOException("模拟器连接已关闭");
            offset += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }
}
