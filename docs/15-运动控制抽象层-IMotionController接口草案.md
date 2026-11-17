# 15 · 运动控制抽象层：`IMotionController` 接口草案

> 面向 AOI / PL 检测上位机的精密运动平台（X-Y 扫描台、Z 对焦轴、θ 旋转轴）。
> 目标：把"运动控制硬件能力"从具体厂商 SDK 中解耦，让 ViewModel 可单测、换控制器只换实现。
> 对应的代码在 `src/WaferMapStudio.Motion`，测试在 `tests/WaferMapStudio.Motion.Tests`。

---

## 1. 为什么要抽象，而不是直接引用 ACS 库

ACS `SPiiPlusNET.dll`（`ACS.SPiiPlusNET` 命名空间，`Api` 类）是步进/伺服运动控制的常见选择，半导体 AOI/量测设备高频使用。但直接在上位机里 `using ACS.SPiiPlusNET` 有三个绕不开的问题：

| 问题 | 后果 |
|---|---|
| **位数锁死**：官方 DLL 是 32 位 / .NET Framework 程序集 | 64 位 net10.0 主进程引用时抛 `BadImageFormatException`；整个 App 被迫降到 x86 + .NET Framework |
| **强耦合**：ViewModel 里到处是 `AXIS.X`、`MOVE_MODE.REL`、ACS 错误码 | 换雷赛/固高/倍福，或接入模拟器时改动面巨大 |
| **不可测**：运动逻辑与硬件 SDK 绑死 | 没有真机就无法单测扫描流程、异常分支 |

因此采用与 `IFileDialogService` 相同的思路，但更彻底：**接口定义在纯 net10.0 契约层，真机实现放进程外**。

```
WaferMapStudio.App (64-bit net10.0)
  └─ ViewModel ──► IMotionController (抽象，本层)
                       ├─ SimulatedMotionController (单测/开发)
                       └─ AcsOutOfProcessController (IPC 代理)
                                    │ 命名管道 + JSON
                                    ▼
                         ACS Bridge Service (32-bit 进程)
                                    │ 引用 ACS.SPiiPlusNET.dll
                                    ▼
                              SPiiPlus 控制器 → 伺服/电机
```

---

## 2. 接口设计的四条原则

1. **全异步 + 取消令牌**：`MoveAsync/HomeAsync/JogAsync` 都返回 `Task`，运动完成（或取消/故障）才返回。`CancellationToken` 取消时实现方必须停止轴，避免"UI 取消了但机器还在走"。
2. **事件驱动状态**：`AxisStateChanged`、`Faulted`、`ConnectionStateChanged` 上报状态，上位机不必轮询控制器。这正是 ACS .NET 库支持的 callback（motion complete / motion interrupted / estop）在抽象层的对应物。
3. **厂商类型零泄漏**：位置单位用 `MotionUnit`、轴用 `MotionAxis`、运动模式用 `MoveMode` 等中立枚举；ACS 的 `AXIS`、`MOVE_MODE` 由适配器内部映射。厂商私有参数收进 `MotionConnectionOptions.VendorOptions` 字典。
4. **可测性优先**：`SimulatedMotionController` 按"距离/速度"模拟行程并触发同样的事件，ViewModel 面向 `IMotionController` 编程即可在无硬件环境下完整测试。

---

## 3. 接口全景

`IMotionController` 成员（详见源码 `IMotionController.cs`）：

| 类别 | 成员 | 语义 |
|---|---|---|
| 链路 | `ConnectAsync` / `DisconnectAsync` / `ConnectionState` | 建立/断开通信，状态用事件上报 |
| 轴管理 | `EnableAxisAsync` / `DisableAxisAsync` | 电机使能/失能 |
| 回零 | `HomeAsync(axis, mode)` | 支持负向/正向/当前即原点三种模式 |
| 运动 | `MoveAsync(axis, pos, mode, profile)` | 点到点，绝对/相对，可带速度曲线 |
| 多轴 | `MoveMultiAsync(targets, mode, profile)` | X-Y 同步启动（扫描路径的基础） |
| 点动 | `JogAsync(axis, velocity)` | 连续运动直到停止或取消 |
| 停止 | `StopAsync(axis?, mode)` | 减速/立即/急停；不传轴停全部 |
| 状态 | `GetStatusAsync` / `GetAllStatusAsync` | 一次性读使能+运动+指令/反馈位置快照 |
| I/O | `ReadInputAsync` / `WriteOutputAsync` | 传感器输入、光源/电磁阀输出 |

配套类型：`MotionAxis`（X/Y/Z/θ/Aux1/Aux2）、`MotionConnectionOptions`、`MotionProfile`、`AxisStatus`、`MotionUnit`、`HomingMode`、`StopMode`，以及三个事件参数类。
`IMotionControllerFactory` 负责按连接配置创建实现（模拟器 / IPC 代理），由宿主 DI 注入。

---

## 4. 进程外桥接：32 位 DLL 的隔离方案（核心）

### 4.1  why

`AnyCPU` 或 `x64` 的主进程无法加载 32 位程序集；退一步把主程序整体设为 x86 又会牺牲内存寻址、GPU 推理（深度学习判片常需 x64 原生库）和生态兼容。正确做法是**把唯一需要 32 位的代码塞进独立进程**。

### 4.2  桥接进程职责

一个独立的可执行文件 `AcsBridgeService`（目标框架 `net10.0-windows-x86`，或 .NET Framework 4.8 x86 视 ACS DLL 依赖而定）：

- 引用 `ACS.SPiiPlusNET.dll`，持有 `Api` 实例；
- 启动后监听命名管道，例如 `Local\\WaferMapStudio.AcsBridge.<session>`；
- 收到请求 → 按命令名分派到对应 ACS API → 把返回值/异常封装成 `MotionIpcResponse` 回写；
- 把 ACS callback（motion complete / fault）转成"事件通知"消息主动推给主程序。

### 4.3  IPC 契约

契约 DTO 定义在 `src/WaferMapStudio.Motion/Ipc/MotionIpcContracts.cs`，**两端共用同一程序集**，避免字符串/结构漂移：

- 请求信封 `MotionIpcRequest(Id, Command, Payload)`；
- 响应信封 `MotionIpcResponse(Id, Success, Payload, ErrorCode, ErrorMessage)`；
- 命令常量 `MotionCommands`（connect / move / home / stop / get_status / ...）；
- 各命令参数 record（`MoveRequest`、`AxisRequest`、`WriteOutputRequest` 等）。

序列化建议用 `System.Text.Json`（net10.0 内置，无额外包）。传输层可选命名管道（本机、低延迟）或 gRPC（需跨机时）。**注意**：`IReadOnlyDictionary<MotionAxis,double>` 的 JSON 反序列化需配置枚举键转换器，落地时补一个 `JsonConverter`。

### 4.4  代理实现

主程序侧 `AcsOutOfProcessController : IMotionController` 实现接口：每个方法把调用序列化成 `MotionIpcRequest` 发到管道、等待 `MotionIpcResponse`、反序列化结果；收到"事件通知"时在代理侧触发对应的 `AxisStateChanged`/`Faulted`。它通过 `MotionControllerFactory` 在 `OutOfProcess` 分支创建（源码中已留 TODO 注释位）。

### 4.5  真机映射参考（ACS → 抽象）

| 抽象接口 | ACS.SPiiPlusNET 对应 |
|---|---|
| `ConnectAsync(Tcp)` | `Api.OpenCommEthernetTCP(host, port)` |
| `EnableAxisAsync` | `Api.Enable(axis)` |
| `MoveAsync(Absolute)` | `Api.ToPoint(MOVE_MODE.ABS, pos, axis)` |
| `JogAsync` | `Api.Jog(...)` |
| `StopAsync(Emergency)` | `Api.Kill(axis)` / ESTOP |
| `GetStatusAsync` | `Api.GetFPosition` + `GetMotorState` + `GetAxisState` |
| `AxisStateChanged` 事件 | ACS callback（motion complete / interrupted） |
| `Faulted` 事件 | `ACSException`（含错误码/描述） |

> 微秒级同步、电子齿轮、PVT/NURBS 轨迹应由控制器内的 **ACSPL+ 程序**执行，上位机只下发"启动程序/触发/读结果"。本接口刻意不逐条暴露这些底层原语，保持上层简洁；确需时通过 `VendorOptions` 传程序名触发。

---

## 5. 目录与分层归属

```
src/WaferMapStudio.Motion/          新增：纯契约层（net10.0，无厂商依赖）
  IMotionController.cs              基础接口（点位运动）
  IAdvancedMotionController.cs      高级接口（PEG / History / ACSPL+，继承自基础接口）
  IMotionControllerFactory.cs       工厂接口
  MotionControllerFactory.cs        默认工厂
  Enums.cs                          中立枚举
  MotionModels.cs                   连接配置/状态/事件参数
  AdvancedModels.cs                 PEG/采集/程序状态模型
  SimulatedMotionController.cs      模拟器（基础运动）
  SimulatedMotionController.Advanced.cs  模拟器（高级能力，partial）
  Ipc/MotionIpcContracts.cs         进程外桥接线协议 DTO

tests/WaferMapStudio.Motion.Tests/  新增：19 个单测
```

依赖方向保持不变：ViewModel/App 可向下引用 Motion，Motion 不引用任何 UI/Windows 类型，符合 `AGENTS.md` §2 的单向分层。

---

## 6. 验证状态

- `dotnet build src/WaferMapStudio.Motion -c Release`：**0 警告 0 错误**
- `dotnet test tests/WaferMapStudio.Motion.Tests -c Release`：**19/19 通过**
  - 基础（8）：连接状态、使能校验、绝对/相对运动、事件序列、急停、I/O、回零、非法轴
  - 高级（11）：PEG 距离/位置列表/次数上限触发、采集容量与清空、程序状态机、变量读写、工厂

---

## 7. 高级能力补强（方案 2：AOI 扫描刚需）

2026-11-17 追加 `IAdvancedMotionController`（继承基础接口），把 ACS 全部运动能力中
AOI 扫描时序真正用得到的三块落到契约层，其余仍按"外推 ACSPL+ 程序"处理。

| ACS 功能 | 本次状态 | 落地位置 |
|---|---|---|
| PTP / JOG / 回零 / I-O | ✅ 已实现 | `IMotionController` + 模拟器 |
| 多轴同步启动 | ⚠️ 并发到位，非真插补 | `MoveMultiAsync`（真机可下推 ACSPL+ 插补） |
| **PEG 位置事件生成** | ✅ 已实现 | `ConfigurePeg/StartPeg/StopPeg/GetPegStatus` + `PegTriggered` 事件；模拟器按位置跨越判定，支持 Distance / PositionList / Count |
| **History Buffer 数据采集** | ✅ 已实现 | `Start/Stop/Read/ClearDataCollection`，定容环形缓冲 + 溢出计数，指令/反馈位置分列 |
| **ACSPL+ 程序与变量** | ✅ 已实现 | `Load/Run/StopProgram/GetProgramInfo` + `Read/WriteVariable`，轻量状态机 |
| 电子齿轮/凸轮、PVT、NURBS/SPATH、混合分段 | ⏸ 外推 ACSPL+ | 由 `RunProgramAsync` 加载控制器程序执行，本层不逐条暴露原语 |

**PEG 为什么是刚需**：每走固定距离由硬件输出脉冲触发相机曝光，触发与位置严格绑定，
规避 Windows 定时器抖动导致的图像错位/丢帧。History Buffer 记录实际位置-时间轨迹，
供图像配准与运动误差补偿。两者配合才是完整的高速扫描采集链路。

**设计取舍**：高级能力独立成接口而非并入基础接口——不是所有机型都需要 PEG，
ViewModel 只在扫描流程依赖 `IAdvancedMotionController`，依赖更精确；通过工厂的
`CreateAdvancedSimulator()` 可在无硬件下测试整条扫描时序。

---

## 8. 后续待办（本次未落地部分）

- [ ] 新建 `tools/AcsBridgeService`（x86），引用真实 `ACS.SPiiPlusNET.dll`，实现 IPC 服务端与事件转发；
- [ ] 实现主程序侧 `AcsOutOfProcessController` 代理，注册进 `MotionControllerFactory` 的 `OutOfProcess` 分支；
- [ ] 为 `MotionAxis` 字典键补 `JsonConverter`；
- [ ] 将 `IMotionController` 接入 App 的 DI（`AppServices.cs`）与扫描流程 ViewModel；
- [ ] 增加对真机行为的契约测试（录制 ACS 响应回放），保证代理与模拟器行为一致。
