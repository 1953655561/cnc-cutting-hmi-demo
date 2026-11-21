# C++ 设备通信与状态机 Demo

C++17 实现的设备通信 / 上位机方向练习项目：内置 **Modbus TCP 从站模拟器 + 客户端**，
覆盖寄存器读写、设备状态机、报警、多线程轮询和命令行控制。无第三方依赖，Windows / Linux 均可编译。

## 功能

- **Modbus TCP**：MBAP 报文头、FC03 读保持寄存器、FC06 写单个寄存器；附带 Modbus RTU 帧与 CRC16 编解码工具。
- **设备模拟器**：64 个保持寄存器、待机 / 运行 / 暂停 / 报警状态机、加工进度、报警码生成。
- **客户端**：轮询 10 个状态寄存器，命令行下发控制字与工艺参数（速度 / 功率 / 脉冲频率 / 加速度）。
- **多线程**：accept 线程 + 仿真线程 + 客户端轮询；寄存器读写加锁，线程安全。
- **跨平台 Socket**：Windows Winsock / Linux POSIX 统一封装，支持 loopback 演示。
- **自测与演示**：`--self-test` 验证 CRC16、TCP 帧解析与状态切换；`--auto` 脚本化演示完整加工流程。

## 编译与运行

### Windows（MinGW / MSYS2）

```bat
g++ -std=c++17 -O2 -pthread src\*.cpp -o cpp-device-demo.exe -lws2_32
cpp-device-demo.exe --auto
```

也可以直接 `make`（MinGW 的 make），或使用 `build.bat`。

### Linux

```bash
make
./cpp-device-demo --auto
```

### CMake（Windows / Linux 通用）

```bash
cmake -S . -B build
cmake --build build
```

> 需要支持 C++17 的编译器：GCC 9+ / Clang 10+ / MSVC 2019+。

## 运行方式

```bash
cpp-device-demo                # 交互模式，输入 help 查看命令
cpp-device-demo --auto         # 自动演示：启动 -> 加工 -> 暂停 -> 停止
cpp-device-demo --self-test    # 自测：CRC16 / TCP 帧 / 状态切换
```

交互命令：

```
start | pause | stop | reset       控制设备状态
speed <n> | power <n> | pulse <n> | accel <n>   写工艺参数
status                             读取一次状态
quit                               退出
```

## 示例输出（--auto）

```text
[simulator] listening on 127.0.0.1:1502
[client] connected
state=Idle     X=0mm    Y=0mm    spindle=0rpm     speed=800mm/min  power=60%  progress=0%
state=Running  X=43.1mm Y=42.6mm spindle=12054rpm speed=800mm/min  power=60%  progress=1%
state=Running  X=50.9mm Y=49.2mm spindle=12126rpm speed=800mm/min  power=60%  progress=4%
...
state=Paused   X=85.2mm Y=72.9mm spindle=0rpm     speed=800mm/min  power=60%  progress=17%
state=Idle     X=85.2mm Y=72.9mm spindle=0rpm     speed=800mm/min  power=60%  progress=0%
[demo] finished
```

## Modbus 寄存器映射（模拟器）

| 地址 | 含义 |
| --- | --- |
| 0 | X 坐标（×0.1 mm） |
| 1 | Y 坐标（×0.1 mm） |
| 2 | 主轴转速（rpm） |
| 3 | 切割速度（mm/min，可写） |
| 4 | 激光功率（%，可写） |
| 5 | 设备状态：0 待机 / 1 运行 / 2 暂停 / 3 报警 |
| 6 | 报警码（0 表示无报警） |
| 7 | 加工进度（%） |
| 8 | 脉冲频率（kHz，可写） |
| 9 | 加速度（mm/s²，可写） |
| 20 | 控制字：1 启动 / 2 暂停 / 3 停止 / 4 报警复位（FC06 写入） |

## 目录结构

```
cpp-device-comm-demo/
├─ src/
│  ├─ modbus.h / modbus.cpp              Modbus RTU/TCP 帧编码、CRC16、响应解析
│  ├─ tcp_socket.h / tcp_socket.cpp      跨平台 TCP Socket 封装
│  ├─ device_simulator.h / .cpp          Modbus TCP 从站 + 设备状态机 + 报警
│  └─ main.cpp                           客户端轮询、命令行控制、自动演示、自测
├─ CMakeLists.txt
├─ Makefile
└─ build.bat（Windows MinGW 构建脚本）
```

## 说明

- 内置模拟下位机，无需真实 PLC 即可运行；没有接真实串口 / CAN 设备。
- 已在 GCC 14.2（Linux）下通过 `--self-test`；MinGW GCC 14.3（Windows）编译检查通过。
- 本项目用于 C++ / 设备通信方向的学习与求职演示，不是生产级工控软件。

## 与 C++ 开发实习生岗位的对应

| 岗位要求 | 本项目对应实现 |
| --- | --- |
| C/C++ 基础语法、指针/内存、类与对象 | 全部代码 C++17；RAII、类封装、STL、智能指针思想 |
| 数据结构与算法 | 寄存器数组、环形状态更新、协议帧解析、CRC16 |
| 设备接口 / 通信模块 | Modbus TCP 从站/客户端、MBAP、FC03/FC06、CRC16 |
| 设备控制 / 状态获取 / 数据处理 | 设备状态机、控制字、坐标/转速/进度/报警寄存器 |
| 多线程 / 调试 | 服务端与仿真线程、客户端轮询、互斥锁保护共享寄存器 |
| Linux / 构建工具 | Makefile、CMake、POSIX Socket 分支；GCC 自测 |
| 文档与联调 | README、寄存器表、`--auto` / `--self-test` 演示 |
