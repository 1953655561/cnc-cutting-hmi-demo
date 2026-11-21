#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

/// 跨平台 TCP Socket 封装（Windows Winsock / Linux POSIX）。
class TcpSocket {
public:
    TcpSocket();
    ~TcpSocket();

    TcpSocket(const TcpSocket&) = delete;
    TcpSocket& operator=(const TcpSocket&) = delete;
    TcpSocket(TcpSocket&& other) noexcept;
    TcpSocket& operator=(TcpSocket&& other) noexcept;

    bool is_valid() const;
    void close_socket();

    static TcpSocket connect_to(const std::string& host, uint16_t port,
                                std::string& error);
    static TcpSocket listen_on(uint16_t port, std::string& error);

    TcpSocket accept(std::string& error) const;
    bool send_all(const std::vector<uint8_t>& data, std::string& error) const;
    bool recv_exact(std::vector<uint8_t>& out, size_t count, int timeout_ms,
                    std::string& error) const;
    uint16_t local_port() const;

private:
    static constexpr std::uintptr_t kInvalidHandle = ~static_cast<std::uintptr_t>(0);
    explicit TcpSocket(std::uintptr_t handle);

    std::uintptr_t handle_;
};
