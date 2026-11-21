#include "tcp_socket.h"

#include <cerrno>
#include <cstring>

#ifdef _WIN32
#include <winsock2.h>
#include <ws2tcpip.h>
namespace {
using NativeSocket = SOCKET;
constexpr NativeSocket kInvalidNative = INVALID_SOCKET;

struct WinsockInitializer {
    WinsockInitializer() {
        WSADATA data;
        WSAStartup(MAKEWORD(2, 2), &data);
    }
    ~WinsockInitializer() { WSACleanup(); }
};

WinsockInitializer g_winsock_initializer;
}  // namespace
#else
#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
namespace {
using NativeSocket = int;
constexpr NativeSocket kInvalidNative = -1;
}  // namespace
#endif

namespace {

NativeSocket to_native(std::uintptr_t handle) {
    return static_cast<NativeSocket>(handle);
}

std::uintptr_t to_handle(NativeSocket socket) {
    return static_cast<std::uintptr_t>(socket);
}

std::string last_socket_error(const std::string& prefix) {
#ifdef _WIN32
    return prefix + " (WSA error " + std::to_string(WSAGetLastError()) + ")";
#else
    return prefix + " (" + std::strerror(errno) + ")";
#endif
}

}  // namespace

TcpSocket::TcpSocket() : handle_(kInvalidHandle) {}

TcpSocket::TcpSocket(std::uintptr_t handle) : handle_(handle) {}

TcpSocket::~TcpSocket() { close_socket(); }

TcpSocket::TcpSocket(TcpSocket&& other) noexcept : handle_(other.handle_) {
    other.handle_ = kInvalidHandle;
}

TcpSocket& TcpSocket::operator=(TcpSocket&& other) noexcept {
    if (this != &other) {
        close_socket();
        handle_ = other.handle_;
        other.handle_ = kInvalidHandle;
    }
    return *this;
}

bool TcpSocket::is_valid() const { return handle_ != kInvalidHandle; }

void TcpSocket::close_socket() {
    if (!is_valid()) return;
    const NativeSocket socket = to_native(handle_);
#ifdef _WIN32
    ::closesocket(socket);
#else
    ::close(socket);
#endif
    handle_ = kInvalidHandle;
}

TcpSocket TcpSocket::connect_to(const std::string& host, uint16_t port,
                                std::string& error) {
    const NativeSocket socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socket == kInvalidNative) {
        error = last_socket_error("创建 socket 失败");
        return TcpSocket();
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    if (::inet_pton(AF_INET, host.c_str(), &address.sin_addr) != 1) {
        error = "地址格式错误: " + host;
#ifdef _WIN32
        ::closesocket(socket);
#else
        ::close(socket);
#endif
        return TcpSocket();
    }

    if (::connect(socket, reinterpret_cast<sockaddr*>(&address),
                  sizeof(address)) != 0) {
        error = last_socket_error("连接失败");
#ifdef _WIN32
        ::closesocket(socket);
#else
        ::close(socket);
#endif
        return TcpSocket();
    }

    return TcpSocket(to_handle(socket));
}

TcpSocket TcpSocket::listen_on(uint16_t port, std::string& error) {
    const NativeSocket socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socket == kInvalidNative) {
        error = last_socket_error("创建监听 socket 失败");
        return TcpSocket();
    }

    int reuse = 1;
    ::setsockopt(socket, SOL_SOCKET, SO_REUSEADDR,
                 reinterpret_cast<const char*>(&reuse), sizeof(reuse));

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    address.sin_port = htons(port);

    if (::bind(socket, reinterpret_cast<sockaddr*>(&address), sizeof(address)) !=
        0) {
        error = last_socket_error("bind 失败");
#ifdef _WIN32
        ::closesocket(socket);
#else
        ::close(socket);
#endif
        return TcpSocket();
    }

    if (::listen(socket, 4) != 0) {
        error = last_socket_error("listen 失败");
#ifdef _WIN32
        ::closesocket(socket);
#else
        ::close(socket);
#endif
        return TcpSocket();
    }

    return TcpSocket(to_handle(socket));
}

TcpSocket TcpSocket::accept(std::string& error) const {
    const NativeSocket socket = to_native(handle_);
    const NativeSocket client = ::accept(socket, nullptr, nullptr);
    if (client == kInvalidNative) {
        error = last_socket_error("accept 失败");
        return TcpSocket();
    }
    return TcpSocket(to_handle(client));
}

bool TcpSocket::send_all(const std::vector<uint8_t>& data,
                         std::string& error) const {
    const NativeSocket socket = to_native(handle_);
    size_t offset = 0;
    while (offset < data.size()) {
        const int remaining = static_cast<int>(data.size() - offset);
        const auto sent = ::send(socket,
                                 reinterpret_cast<const char*>(data.data() + offset),
                                 remaining, 0);
        if (sent <= 0) {
            error = last_socket_error("send 失败");
            return false;
        }
        offset += static_cast<size_t>(sent);
    }
    return true;
}

bool TcpSocket::recv_exact(std::vector<uint8_t>& out, size_t count,
                           int timeout_ms, std::string& error) const {
    out.assign(count, 0);
    const NativeSocket socket = to_native(handle_);
    size_t offset = 0;
    while (offset < count) {
        fd_set read_set;
        FD_ZERO(&read_set);
        FD_SET(socket, &read_set);
        timeval timeout{};
        timeout.tv_sec = timeout_ms / 1000;
        timeout.tv_usec = (timeout_ms % 1000) * 1000;
#ifdef _WIN32
        const int ready = ::select(0, &read_set, nullptr, nullptr, &timeout);
#else
        const int ready = ::select(socket + 1, &read_set, nullptr, nullptr, &timeout);
#endif
        if (ready < 0) {
            error = last_socket_error("select 失败");
            return false;
        }
        if (ready == 0) {
            error = "接收超时";
            return false;
        }

        const auto received =
            ::recv(socket, reinterpret_cast<char*>(out.data() + offset),
                   static_cast<int>(count - offset), 0);
        if (received <= 0) {
            error = "连接已关闭";
            return false;
        }
        offset += static_cast<size_t>(received);
    }
    return true;
}

uint16_t TcpSocket::local_port() const {
    if (!is_valid()) return 0;
    sockaddr_in address{};
#ifdef _WIN32
    int length = sizeof(address);
#else
    socklen_t length = sizeof(address);
#endif
    if (::getsockname(to_native(handle_),
                      reinterpret_cast<sockaddr*>(&address), &length) != 0) {
        return 0;
    }
    return ntohs(address.sin_port);
}
