#include <algorithm>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "device_simulator.h"
#include "modbus.h"
#include "tcp_socket.h"

namespace {

struct Snapshot {
    uint16_t x = 0;
    uint16_t y = 0;
    uint16_t spindle = 0;
    uint16_t speed = 0;
    uint16_t power = 0;
    uint16_t state = 0;
    uint16_t alarm = 0;
    uint16_t progress = 0;
    uint16_t pulse = 0;
    uint16_t accel = 0;
};

std::string state_name(uint16_t value) {
    switch (value) {
        case 0:
            return "Idle";
        case 1:
            return "Running";
        case 2:
            return "Paused";
        case 3:
            return "Alarm";
        default:
            return "Unknown";
    }
}

std::string trim(const std::string& text) {
    const auto begin = text.find_first_not_of(" \t\r\n");
    if (begin == std::string::npos) return "";
    const auto end = text.find_last_not_of(" \t\r\n");
    return text.substr(begin, end - begin + 1);
}

bool transact(TcpSocket& client, const std::vector<uint8_t>& request,
              std::vector<uint8_t>& frame, std::string& error) {
    if (!client.send_all(request, error)) return false;

    std::vector<uint8_t> header;
    if (!client.recv_exact(header, 7, 1500, error)) return false;
    const size_t length = static_cast<size_t>((header[4] << 8) | header[5]);
    if (length < 2) {
        error = "响应 MBAP 长度异常";
        return false;
    }

    std::vector<uint8_t> body;
    if (!client.recv_exact(body, length - 1, 1500, error)) return false;

    frame = header;
    frame.insert(frame.end(), body.begin(), body.end());
    return true;
}

bool read_snapshot(TcpSocket& client, uint16_t& transaction_id, Snapshot& snapshot,
                   std::string& error) {
    const auto request = modbus::build_tcp_read(++transaction_id, 1, 0, 10);
    std::vector<uint8_t> frame;
    if (!transact(client, request, frame, error)) return false;

    const auto response =
        modbus::parse_tcp_response(frame, transaction_id, 10);
    if (!response.ok) {
        error = response.error;
        return false;
    }

    snapshot.x = response.registers[0];
    snapshot.y = response.registers[1];
    snapshot.spindle = response.registers[2];
    snapshot.speed = response.registers[3];
    snapshot.power = response.registers[4];
    snapshot.state = response.registers[5];
    snapshot.alarm = response.registers[6];
    snapshot.progress = response.registers[7];
    snapshot.pulse = response.registers[8];
    snapshot.accel = response.registers[9];
    return true;
}

bool write_register(TcpSocket& client, uint16_t& transaction_id,
                    uint16_t address, uint16_t value, std::string& error) {
    const auto request =
        modbus::build_tcp_write_single(++transaction_id, 1, address, value);
    std::vector<uint8_t> frame;
    if (!transact(client, request, frame, error)) return false;
    const auto response = modbus::parse_tcp_response(frame, transaction_id);
    if (!response.ok) {
        error = response.error;
        return false;
    }
    return true;
}

TcpSocket connect_with_retry(uint16_t port, std::string& error) {
    for (int attempt = 0; attempt < 30; ++attempt) {
        error.clear();
        TcpSocket client = TcpSocket::connect_to("127.0.0.1", port, error);
        if (client.is_valid()) return client;
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }
    return TcpSocket();
}

void print_status(const Snapshot& snapshot) {
    std::cout << "state=" << state_name(snapshot.state)
              << "  X=" << (snapshot.x / 10.0) << "mm"
              << "  Y=" << (snapshot.y / 10.0) << "mm"
              << "  spindle=" << snapshot.spindle << "rpm"
              << "  speed=" << snapshot.speed << "mm/min"
              << "  power=" << snapshot.power << "%"
              << "  progress=" << snapshot.progress << "%";
    if (snapshot.alarm != 0) {
        std::cout << "  ALARM=" << snapshot.alarm;
    }
    std::cout << std::endl;
}

int run_auto(uint16_t port) {
    DeviceSimulator simulator(port);
    std::string error;
    if (!simulator.start(error)) {
        std::cerr << "[simulator] start failed: " << error << std::endl;
        return 1;
    }
    std::cout << "[simulator] listening on 127.0.0.1:" << simulator.port()
              << std::endl;

    TcpSocket client = connect_with_retry(simulator.port(), error);
    if (!client.is_valid()) {
        std::cerr << "[client] connect failed: " << error << std::endl;
        simulator.stop();
        return 1;
    }
    std::cout << "[client] connected" << std::endl;

    uint16_t transaction_id = 0;
    Snapshot snapshot;
    auto poll = [&]() -> bool {
        if (!read_snapshot(client, transaction_id, snapshot, error)) {
            std::cerr << "[client] read failed: " << error << std::endl;
            return false;
        }
        print_status(snapshot);
        return true;
    };

    if (!poll()) {
        client.close_socket();
        simulator.stop();
        return 1;
    }

    write_register(client, transaction_id, 20, 1, error);
    for (int i = 0; i < 12; ++i) {
        std::this_thread::sleep_for(std::chrono::milliseconds(250));
        if (!poll()) break;
    }
    write_register(client, transaction_id, 20, 2, error);
    std::this_thread::sleep_for(std::chrono::milliseconds(250));
    if (!poll()) { /* 输出一次暂停状态 */ }
    write_register(client, transaction_id, 20, 3, error);
    std::this_thread::sleep_for(std::chrono::milliseconds(250));
    if (!poll()) { /* 输出一次停止状态 */ }

    client.close_socket();
    simulator.stop();
    std::cout << "[demo] finished" << std::endl;
    return 0;
}

void print_help() {
    std::cout << "commands:\n"
              << "  start | pause | stop | reset   控制设备状态\n"
              << "  speed <n> | power <n> | pulse <n> | accel <n>   写工艺参数\n"
              << "  status                          读取一次状态\n"
              << "  quit                            退出\n";
}

int run_interactive(uint16_t port) {
    DeviceSimulator simulator(port);
    std::string error;
    if (!simulator.start(error)) {
        std::cerr << "[simulator] start failed: " << error << std::endl;
        return 1;
    }
    std::cout << "[simulator] listening on 127.0.0.1:" << simulator.port()
              << std::endl;

    TcpSocket client = connect_with_retry(simulator.port(), error);
    if (!client.is_valid()) {
        std::cerr << "[client] connect failed: " << error << std::endl;
        simulator.stop();
        return 1;
    }

    uint16_t transaction_id = 0;
    Snapshot snapshot;
    if (!read_snapshot(client, transaction_id, snapshot, error)) {
        std::cerr << "[client] read failed: " << error << std::endl;
        client.close_socket();
        simulator.stop();
        return 1;
    }

    std::cout << "[client] connected, type 'help' for commands" << std::endl;
    print_status(snapshot);

    std::string line;
    while (true) {
        std::cout << "> " << std::flush;
        if (!std::getline(std::cin, line)) break;
        line = trim(line);
        if (line.empty()) continue;

        std::istringstream iss(line);
        std::string command;
        iss >> command;

        if (command == "quit" || command == "exit") break;
        if (command == "help") {
            print_help();
            continue;
        }

        uint16_t address = 0;
        uint16_t value = 0;
        bool is_write = false;
        if (command == "start") {
            address = 20;
            value = 1;
            is_write = true;
        } else if (command == "pause") {
            address = 20;
            value = 2;
            is_write = true;
        } else if (command == "stop") {
            address = 20;
            value = 3;
            is_write = true;
        } else if (command == "reset") {
            address = 20;
            value = 4;
            is_write = true;
        } else if (command == "speed") {
            iss >> value;
            address = 3;
            is_write = true;
        } else if (command == "power") {
            iss >> value;
            address = 4;
            is_write = true;
        } else if (command == "pulse") {
            iss >> value;
            address = 8;
            is_write = true;
        } else if (command == "accel") {
            iss >> value;
            address = 9;
            is_write = true;
        } else if (command != "status") {
            std::cout << "unknown command: " << command << std::endl;
            continue;
        }

        if (is_write && !write_register(client, transaction_id, address, value,
                                        error)) {
            std::cerr << "write failed: " << error << std::endl;
            continue;
        }

        std::this_thread::sleep_for(std::chrono::milliseconds(300));
        if (read_snapshot(client, transaction_id, snapshot, error)) {
            print_status(snapshot);
        } else {
            std::cerr << "read failed: " << error << std::endl;
        }
    }

    client.close_socket();
    simulator.stop();
    return 0;
}

int run_self_test() {
    bool ok = true;

    const uint8_t sample[] = {0x01, 0x03, 0x00, 0x00, 0x00, 0x0A};
    if (modbus::crc16(sample, sizeof(sample)) != 0xCDC5) {
        std::cerr << "[self-test] CRC16 failed" << std::endl;
        ok = false;
    }

    const auto read_request = modbus::build_tcp_read(0x1234, 1, 0, 10);
    const std::vector<uint8_t> expected_header = {0x12, 0x34, 0x00, 0x00,
                                                  0x00, 0x06, 0x01, 0x03};
    if (read_request.size() != 12 ||
        !std::equal(expected_header.begin(), expected_header.end(),
                    read_request.begin())) {
        std::cerr << "[self-test] TCP read frame failed" << std::endl;
        ok = false;
    }

    std::vector<uint8_t> response = {0x12, 0x34, 0x00, 0x00, 0x00, 0x17,
                                     0x01, 0x03, 0x14};
    for (uint16_t i = 0; i < 10; ++i) {
        response.push_back(static_cast<uint8_t>(i >> 8));
        response.push_back(static_cast<uint8_t>(i & 0xFF));
    }
    const auto parsed = modbus::parse_tcp_response(response, 0x1234, 10);
    if (!parsed.ok || parsed.registers.size() != 10 || parsed.registers[9] != 9) {
        std::cerr << "[self-test] TCP response parse failed" << std::endl;
        ok = false;
    }

    const uint16_t port = 15020;
    DeviceSimulator simulator(port);
    std::string error;
    if (!simulator.start(error)) {
        std::cerr << "[self-test] simulator start failed: " << error << std::endl;
        return 1;
    }

    TcpSocket client = connect_with_retry(simulator.port(), error);
    if (!client.is_valid()) {
        std::cerr << "[self-test] connect failed: " << error << std::endl;
        simulator.stop();
        return 1;
    }

    uint16_t transaction_id = 0;
    Snapshot snapshot;
    if (!read_snapshot(client, transaction_id, snapshot, error)) {
        std::cerr << "[self-test] read failed: " << error << std::endl;
        ok = false;
    }
    if (!write_register(client, transaction_id, 20, 1, error)) {
        std::cerr << "[self-test] write control failed: " << error << std::endl;
        ok = false;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(350));
    if (!read_snapshot(client, transaction_id, snapshot, error)) {
        std::cerr << "[self-test] read after start failed: " << error << std::endl;
        ok = false;
    } else if (snapshot.state != 1) {
        std::cerr << "[self-test] state should be Running, got "
                  << snapshot.state << std::endl;
        ok = false;
    }

    client.close_socket();
    simulator.stop();

    if (ok) {
        std::cout << "[self-test] PASS" << std::endl;
        return 0;
    }
    std::cout << "[self-test] FAIL" << std::endl;
    return 1;
}

}  // namespace

int main(int argc, char** argv) {
    uint16_t port = 1502;
    bool auto_mode = false;
    bool self_test = false;

    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--port" && i + 1 < argc) {
            port = static_cast<uint16_t>(std::atoi(argv[++i]));
        } else if (arg == "--auto") {
            auto_mode = true;
        } else if (arg == "--self-test") {
            self_test = true;
        } else if (arg == "--help" || arg == "-h") {
            std::cout << "usage: cpp-device-demo [--port N] [--auto] [--self-test]\n";
            return 0;
        }
    }

    if (self_test) return run_self_test();
    if (auto_mode) return run_auto(port);
    return run_interactive(port);
}
