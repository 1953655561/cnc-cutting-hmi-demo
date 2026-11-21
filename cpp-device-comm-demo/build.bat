@echo off
chcp 65001 >nul
g++ -std=c++17 -O2 -pthread src\main.cpp src\modbus.cpp src\tcp_socket.cpp src\device_simulator.cpp -o cpp-device-demo.exe -lws2_32
if errorlevel 1 (
    echo build failed
    exit /b 1
)
echo build ok: cpp-device-demo.exe
