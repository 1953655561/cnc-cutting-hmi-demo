#include "modbus.h"

#include <iomanip>
#include <sstream>

namespace modbus {

namespace {

void append_crc(std::vector<uint8_t>& frame) {
    const uint16_t crc = crc16(frame.data(), frame.size());
    frame.push_back(static_cast<uint8_t>(crc & 0xFF));
    frame.push_back(static_cast<uint8_t>(crc >> 8));
}

void write_mbap(std::vector<uint8_t>& frame, uint16_t transaction_id,
                uint8_t unit, uint16_t length) {
    frame.push_back(static_cast<uint8_t>(transaction_id >> 8));
    frame.push_back(static_cast<uint8_t>(transaction_id & 0xFF));
    frame.push_back(0);
    frame.push_back(0);
    frame.push_back(static_cast<uint8_t>(length >> 8));
    frame.push_back(static_cast<uint8_t>(length & 0xFF));
    frame.push_back(unit);
}

}  // namespace

uint16_t crc16(const uint8_t* data, size_t length) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < length; ++i) {
        crc ^= static_cast<uint16_t>(data[i]);
        for (int bit = 0; bit < 8; ++bit) {
            if ((crc & 0x0001) != 0) {
                crc = static_cast<uint16_t>((crc >> 1) ^ 0xA001);
            } else {
                crc = static_cast<uint16_t>(crc >> 1);
            }
        }
    }
    return crc;
}

std::vector<uint8_t> build_rtu_read(uint8_t unit, uint16_t start, uint16_t count) {
    std::vector<uint8_t> frame = {
        unit,
        kFuncReadHolding,
        static_cast<uint8_t>(start >> 8),
        static_cast<uint8_t>(start & 0xFF),
        static_cast<uint8_t>(count >> 8),
        static_cast<uint8_t>(count & 0xFF),
    };
    append_crc(frame);
    return frame;
}

std::vector<uint8_t> build_rtu_write_single(uint8_t unit, uint16_t address,
                                            uint16_t value) {
    std::vector<uint8_t> frame = {
        unit,
        kFuncWriteSingle,
        static_cast<uint8_t>(address >> 8),
        static_cast<uint8_t>(address & 0xFF),
        static_cast<uint8_t>(value >> 8),
        static_cast<uint8_t>(value & 0xFF),
    };
    append_crc(frame);
    return frame;
}

std::vector<uint8_t> build_tcp_read(uint16_t transaction_id, uint8_t unit,
                                   uint16_t start, uint16_t count) {
    std::vector<uint8_t> frame;
    frame.reserve(12);
    write_mbap(frame, transaction_id, unit, 6);
    frame.push_back(kFuncReadHolding);
    frame.push_back(static_cast<uint8_t>(start >> 8));
    frame.push_back(static_cast<uint8_t>(start & 0xFF));
    frame.push_back(static_cast<uint8_t>(count >> 8));
    frame.push_back(static_cast<uint8_t>(count & 0xFF));
    return frame;
}

std::vector<uint8_t> build_tcp_write_single(uint16_t transaction_id, uint8_t unit,
                                            uint16_t address, uint16_t value) {
    std::vector<uint8_t> frame;
    frame.reserve(12);
    write_mbap(frame, transaction_id, unit, 6);
    frame.push_back(kFuncWriteSingle);
    frame.push_back(static_cast<uint8_t>(address >> 8));
    frame.push_back(static_cast<uint8_t>(address & 0xFF));
    frame.push_back(static_cast<uint8_t>(value >> 8));
    frame.push_back(static_cast<uint8_t>(value & 0xFF));
    return frame;
}

TcpResponse parse_tcp_response(const std::vector<uint8_t>& frame,
                               uint16_t expected_transaction,
                               size_t expected_register_count) {
    TcpResponse response;
    if (frame.size() < 9) {
        response.error = "响应帧长度不足";
        return response;
    }

    const uint16_t transaction_id =
        static_cast<uint16_t>((frame[0] << 8) | frame[1]);
    if (transaction_id != expected_transaction) {
        response.error = "事务号不匹配";
        return response;
    }
    if (frame[2] != 0 || frame[3] != 0) {
        response.error = "协议标识不是 Modbus";
        return response;
    }

    const size_t length = static_cast<size_t>((frame[4] << 8) | frame[5]);
    if (length < 2 || frame.size() < 6 + length) {
        response.error = "MBAP 长度字段异常";
        return response;
    }

    const uint8_t function = frame[7];
    if ((function & 0x80) != 0) {
        response.exception = true;
        response.exception_code = frame[8];
        response.error = "Modbus 异常响应，异常码 " +
                         std::to_string(response.exception_code);
        return response;
    }

    if (function == kFuncReadHolding) {
        const uint8_t byte_count = frame[8];
        const size_t register_count = byte_count / 2;
        if (expected_register_count != 0 &&
            register_count < expected_register_count) {
            response.error = "响应寄存器数量不足";
            return response;
        }
        if (frame.size() < 9 + byte_count) {
            response.error = "响应数据长度不足";
            return response;
        }
        response.registers.reserve(register_count);
        for (size_t i = 0; i < register_count; ++i) {
            const uint16_t value = static_cast<uint16_t>(
                (frame[9 + i * 2] << 8) | frame[10 + i * 2]);
            response.registers.push_back(value);
        }
        response.ok = true;
        return response;
    }

    if (function == kFuncWriteSingle) {
        response.ok = true;
        return response;
    }

    response.error = "未知功能码: " + std::to_string(function);
    return response;
}

std::string to_hex(const std::vector<uint8_t>& data) {
    std::ostringstream oss;
    for (size_t i = 0; i < data.size(); ++i) {
        if (i != 0) oss << ' ';
        oss << std::hex << std::uppercase << std::setw(2) << std::setfill('0')
            << static_cast<int>(data[i]);
    }
    return oss.str();
}

}  // namespace modbus
