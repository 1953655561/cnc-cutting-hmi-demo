#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "tcp_socket.h"

/// 内置下位机模拟器：Modbus TCP 从站 + 设备状态机 + 报警。
class DeviceSimulator {
public:
    explicit DeviceSimulator(uint16_t port = 1502);
    ~DeviceSimulator();

    DeviceSimulator(const DeviceSimulator&) = delete;
    DeviceSimulator& operator=(const DeviceSimulator&) = delete;

    bool start(std::string& error);
    void stop();
    uint16_t port() const { return port_; }
    bool is_running() const { return running_.load(); }

private:
    enum class State : uint16_t {
        Idle = 0,
        Running = 1,
        Paused = 2,
        Alarm = 3,
    };

    void accept_loop();
    void simulation_loop();
    void handle_client(TcpSocket client);
    std::vector<uint8_t> process_request(const std::vector<uint8_t>& request);
    void tick();

    static std::vector<uint8_t> make_response(uint16_t transaction_id,
                                              uint8_t unit,
                                              const std::vector<uint8_t>& pdu);
    static std::vector<uint8_t> make_exception(uint16_t transaction_id,
                                               uint8_t unit, uint8_t function,
                                               uint8_t exception_code);

    uint16_t port_;
    TcpSocket listener_;
    std::atomic<bool> running_{false};
    std::thread accept_thread_;
    std::thread simulation_thread_;
    mutable std::mutex mutex_;
    std::array<uint16_t, 64> registers_{};
    State state_ = State::Idle;
    double progress_ = 0.0;
    int alarm_code_ = 0;
    int alarm_ticks_ = 0;
    std::uint64_t tick_ = 0;
};
