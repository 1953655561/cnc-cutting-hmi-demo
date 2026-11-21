cmake_minimum_required(VERSION 3.15)
project(cpp-device-comm-demo LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

add_executable(cpp-device-demo
    src/main.cpp
    src/modbus.cpp
    src/tcp_socket.cpp
    src/device_simulator.cpp
)

if (WIN32)
    target_link_libraries(cpp-device-demo PRIVATE ws2_32)
endif ()

find_package(Threads REQUIRED)
target_link_libraries(cpp-device-demo PRIVATE Threads::Threads)

if (MSVC)
    target_compile_options(cpp-device-demo PRIVATE /W4)
else ()
    target_compile_options(cpp-device-demo PRIVATE -Wall -Wextra)
endif ()
