#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace modbus {

constexpr uint8_t kFuncReadHolding = 0x03;
constexpr uint8_t kFuncWriteSingle = 0x06;

uint16_t crc16(const uint8_t* data, size_t length);

// Modbus RTU 帧（用于串口调试 / 协议自测）
std::vector<uint8_t> build_rtu_read(uint8_t unit, uint16_t start, uint16_t count);
std::vector<uint8_t> build_rtu_write_single(uint8_t unit, uint16_t address, uint16_t value);

// Modbus TCP 帧（MBAP + PDU）
std::vector<uint8_t> build_tcp_read(uint16_t transaction_id, uint8_t unit,
                                   uint16_t start, uint16_t count);
std::vector<uint8_t> build_tcp_write_single(uint16_t transaction_id, uint8_t unit,
                                           uint16_t address, uint16_t value);

struct TcpResponse {
    bool ok = false;
    bool exception = false;
    uint8_t exception_code = 0;
    std::string error;
    std::vector<uint16_t> registers;
};

TcpResponse parse_tcp_response(const std::vector<uint8_t>& frame,
                               uint16_t expected_transaction,
                               size_t expected_register_count = 0);

std::string to_hex(const std::vector<uint8_t>& data);

}  // namespace modbus
