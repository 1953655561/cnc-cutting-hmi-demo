using System.IO;
namespace CncHmiDemo.Services;

/// <summary>Modbus RTU / TCP 帧编解码（FC03 读保持寄存器、FC06 写单个寄存器）。</summary>
public static class ModbusCodec
{
    public const byte ReadHoldingRegisters = 0x03;
    public const byte WriteSingleRegister = 0x06;

    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
        }
        return crc;
    }

    public static byte[] BuildRtuRead(byte unitId, ushort startAddress, ushort count)
    {
        var frame = new byte[8];
        frame[0] = unitId;
        frame[1] = ReadHoldingRegisters;
        frame[2] = (byte)(startAddress >> 8);
        frame[3] = (byte)startAddress;
        frame[4] = (byte)(count >> 8);
        frame[5] = (byte)count;
        var crc = Crc16(frame.AsSpan(0, 6));
        frame[6] = (byte)(crc & 0xFF);
        frame[7] = (byte)(crc >> 8);
        return frame;
    }

    public static byte[] BuildRtuWriteSingle(byte unitId, ushort address, ushort value)
    {
        var frame = new byte[8];
        frame[0] = unitId;
        frame[1] = WriteSingleRegister;
        frame[2] = (byte)(address >> 8);
        frame[3] = (byte)address;
        frame[4] = (byte)(value >> 8);
        frame[5] = (byte)value;
        var crc = Crc16(frame.AsSpan(0, 6));
        frame[6] = (byte)(crc & 0xFF);
        frame[7] = (byte)(crc >> 8);
        return frame;
    }

    public static byte[] BuildTcpRead(ushort transactionId, byte unitId, ushort startAddress, ushort count)
    {
        var frame = new byte[12];
        WriteMbap(frame, transactionId, unitId, 6);
        frame[7] = ReadHoldingRegisters;
        frame[8] = (byte)(startAddress >> 8);
        frame[9] = (byte)startAddress;
        frame[10] = (byte)(count >> 8);
        frame[11] = (byte)count;
        return frame;
    }

    public static byte[] BuildTcpWriteSingle(ushort transactionId, byte unitId, ushort address, ushort value)
    {
        var frame = new byte[12];
        WriteMbap(frame, transactionId, unitId, 6);
        frame[7] = WriteSingleRegister;
        frame[8] = (byte)(address >> 8);
        frame[9] = (byte)address;
        frame[10] = (byte)(value >> 8);
        frame[11] = (byte)value;
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

    /// <summary>解析 FC03 响应 PDU（从功能码开始）。</summary>
    public static ushort[] ParseReadRegisters(ReadOnlySpan<byte> pdu, int count)
    {
        if (pdu.Length < 2 + count * 2) throw new InvalidDataException("Modbus 读响应长度不足");
        if (pdu[0] != ReadHoldingRegisters) throw new InvalidDataException($"非 FC03 响应: 0x{pdu[0]:X2}");
        var byteCount = pdu[1];
        if (byteCount < count * 2) throw new InvalidDataException("Modbus 读响应寄存器数量不足");

        var registers = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            registers[i] = (ushort)((pdu[2 + i * 2] << 8) | pdu[3 + i * 2]);
        }
        return registers;
    }

    public static string Hex(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;
        var sb = new System.Text.StringBuilder(data.Length * 3);
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}
