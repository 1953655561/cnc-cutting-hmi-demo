#include "device_simulator.h"

#include <chrono>
#include <cmath>
#include <utility>

#include "modbus.h"

DeviceSimulator::DeviceSimulator(uint16_t port) : port_(port) {
    registers_[3] = 800;   // 切割速度 mm/min
    registers_[4] = 60;    // 激光功率 %
    registers_[8] = 20;    // 脉冲频率 kHz
    registers_[9] = 500;   // 加速度 mm/s2
}

DeviceSimulator::~DeviceSimulator() { stop(); }

bool DeviceSimulator::start(std::string& error) {
    if (running_.load()) return true;
    listener_ = TcpSocket::listen_on(port_, error);
    if (!listener_.is_valid()) return false;
    port_ = listener_.local_port();
    running_ = true;
    accept_thread_ = std::thread(&DeviceSimulator::accept_loop, this);
    simulation_thread_ = std::thread(&DeviceSimulator::simulation_loop, this);
    return true;
}

void DeviceSimulator::stop() {
    if (!running_.exchange(false)) {
        return;
    }
    listener_.close_socket();
    if (accept_thread_.joinable()) accept_thread_.join();
    if (simulation_thread_.joinable()) simulation_thread_.join();
}

void DeviceSimulator::accept_loop() {
    while (running_.load()) {
        std::string error;
        TcpSocket client = listener_.accept(error);
        if (!running_.load()) break;
        if (!client.is_valid()) {
            continue;
        }
        handle_client(std::move(client));
    }
}

void DeviceSimulator::handle_client(TcpSocket client) {
    while (running_.load()) {
        std::string error;
        std::vector<uint8_t> header;
        if (!client.recv_exact(header, 7, 800, error)) break;

        const size_t length = static_cast<size_t>((header[4] << 8) | header[5]);
        if (length < 2) break;

        std::vector<uint8_t> body;
        if (!client.recv_exact(body, length - 1, 800, error)) break;

        std::vector<uint8_t> request = header;
        request.insert(request.end(), body.begin(), body.end());
        const std::vector<uint8_t> response = process_request(request);
        if (response.empty()) break;
        if (!client.send_all(response, error)) break;
    }
}

std::vector<uint8_t> DeviceSimulator::process_request(
    const std::vector<uint8_t>& request) {
    if (request.size() < 9) return {};

    const uint16_t transaction_id =
        static_cast<uint16_t>((request[0] << 8) | request[1]);
    const uint8_t unit = request[6];
    const uint8_t function = request[7];

    if (function == modbus::kFuncReadHolding) {
        if (request.size() < 12) {
            return make_exception(transaction_id, unit, function, 0x03);
        }
        const uint16_t start =
            static_cast<uint16_t>((request[8] << 8) | request[9]);
        const uint16_t count =
            static_cast<uint16_t>((request[10] << 8) | request[11]);
        if (count == 0 || count > 32) {
            return make_exception(transaction_id, unit, function, 0x03);
        }

        std::vector<uint8_t> pdu;
        pdu.reserve(2 + count * 2);
        pdu.push_back(modbus::kFuncReadHolding);
        pdu.push_back(static_cast<uint8_t>(count * 2));
        {
            std::lock_guard<std::mutex> lock(mutex_);
            for (uint16_t i = 0; i < count; ++i) {
                const uint16_t address = static_cast<uint16_t>(start + i);
                const uint16_t value =
                    address < registers_.size() ? registers_[address] : 0;
                pdu.push_back(static_cast<uint8_t>(value >> 8));
                pdu.push_back(static_cast<uint8_t>(value & 0xFF));
            }
        }
        return make_response(transaction_id, unit, pdu);
    }

    if (function == modbus::kFuncWriteSingle) {
        if (request.size() < 12) {
            return make_exception(transaction_id, unit, function, 0x03);
        }
        const uint16_t address =
            static_cast<uint16_t>((request[8] << 8) | request[9]);
        const uint16_t value =
            static_cast<uint16_t>((request[10] << 8) | request[11]);
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (address < registers_.size()) {
                registers_[address] = value;
            }
        }
        std::vector<uint8_t> pdu(request.begin() + 7, request.begin() + 12);
        return make_response(transaction_id, unit, pdu);
    }

    return make_exception(transaction_id, unit, function, 0x01);
}

std::vector<uint8_t> DeviceSimulator::make_response(
    uint16_t transaction_id, uint8_t unit, const std::vector<uint8_t>& pdu) {
    std::vector<uint8_t> frame;
    frame.reserve(7 + pdu.size());
    const uint16_t length = static_cast<uint16_t>(1 + pdu.size());
    frame.push_back(static_cast<uint8_t>(transaction_id >> 8));
    frame.push_back(static_cast<uint8_t>(transaction_id & 0xFF));
    frame.push_back(0);
    frame.push_back(0);
    frame.push_back(static_cast<uint8_t>(length >> 8));
    frame.push_back(static_cast<uint8_t>(length & 0xFF));
    frame.push_back(unit);
    frame.insert(frame.end(), pdu.begin(), pdu.end());
    return frame;
}

std::vector<uint8_t> DeviceSimulator::make_exception(
    uint16_t transaction_id, uint8_t unit, uint8_t function,
    uint8_t exception_code) {
    std::vector<uint8_t> pdu = {static_cast<uint8_t>(function | 0x80),
                                exception_code};
    return make_response(transaction_id, unit, pdu);
}

void DeviceSimulator::simulation_loop() {
    while (running_.load()) {
        tick();
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
}

void DeviceSimulator::tick() {
    std::lock_guard<std::mutex> lock(mutex_);
    ++tick_;

    const uint16_t control = registers_[20];
    if (control != 0) {
        registers_[20] = 0;
        switch (control) {
            case 1:
                state_ = State::Running;
                progress_ = 0.0;
                alarm_code_ = 0;
                alarm_ticks_ = 0;
                break;
            case 2:
                if (state_ == State::Running) state_ = State::Paused;
                break;
            case 3:
                state_ = State::Idle;
                progress_ = 0.0;
                alarm_code_ = 0;
                alarm_ticks_ = 0;
                break;
            case 4:
                alarm_code_ = 0;
                alarm_ticks_ = 0;
                if (state_ == State::Alarm) state_ = State::Paused;
                break;
            default:
                break;
        }
    }

    if (alarm_ticks_ > 0) {
        --alarm_ticks_;
        if (alarm_ticks_ == 0) {
            alarm_code_ = 0;
            if (state_ == State::Alarm) state_ = State::Running;
        }
    } else if (state_ == State::Running && tick_ % 180 == 0) {
        alarm_code_ = (tick_ / 180) % 2 == 0 ? 101 : 102;
        alarm_ticks_ = 25;
        state_ = State::Alarm;
    }

    if (state_ == State::Running && alarm_ticks_ == 0) {
        progress_ += 0.6;
        if (progress_ >= 100.0) {
            progress_ = 100.0;
            state_ = State::Idle;
        }
        const double x = 40.0 + progress_ * 2.6;
        const double y = 40.0 + 40.0 * std::sin(progress_ / 18.0);
        registers_[0] = static_cast<uint16_t>(x * 10.0);
        registers_[1] = static_cast<uint16_t>(y * 10.0);
        registers_[2] = static_cast<uint16_t>(
            12000.0 + std::sin(static_cast<double>(tick_) / 8.0) * 150.0);
    } else {
        registers_[2] = 0;
    }

    registers_[5] = static_cast<uint16_t>(state_);
    registers_[6] = static_cast<uint16_t>(alarm_code_);
    registers_[7] = static_cast<uint16_t>(progress_);
}
