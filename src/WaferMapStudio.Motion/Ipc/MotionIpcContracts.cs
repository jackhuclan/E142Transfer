namespace WaferMapStudio.Motion.Ipc;

/// <summary>
/// 进程外桥接的线协议契约（草案）。
///
/// 背景：ACS.SPiiPlusNET.dll 是 32 位 / .NET Framework 程序集，无法被 64 位 net10.0
/// 主程序加载。解决方案是把它放进一个独立的 32 位辅助进程（x86，net framework 4.8
/// 或 net10.0-windows-x86），主程序通过命名管道 + JSON 调用它，它再调用真正的 ACS API。
///
/// 本文件只定义**与传输无关的消息 DTO**，主程序侧和桥接服务侧共用同一个程序集，
/// 从而保证两端类型一致。传输实现（NamedPipe / gRPC）不在契约层。
/// </summary>

/// <summary>IPC 请求信封。</summary>
/// <param name="Id">请求唯一 ID，用于匹配异步响应。</param>
/// <param name="Command">命令名，见 <see cref="MotionCommands"/>。</param>
/// <param name="Payload">命令参数，JSON 序列化的具体请求对象。</param>
public sealed record MotionIpcRequest(
    string Id,
    string Command,
    string Payload);

/// <summary>IPC 响应信封。</summary>
/// <param name="Id">对应的请求 ID。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Payload">成功时的返回值（JSON）。</param>
/// <param name="ErrorCode">失败时的错误码；成功为 null。</param>
/// <param name="ErrorMessage">失败时的可读描述。</param>
public sealed record MotionIpcResponse(
    string Id,
    bool Success,
    string? Payload,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>命令名常量。两端必须一致，集中定义避免字符串漂移。</summary>
public static class MotionCommands
{
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string EnableAxis = "enable_axis";
    public const string DisableAxis = "disable_axis";
    public const string Home = "home";
    public const string Move = "move";
    public const string MoveMulti = "move_multi";
    public const string Jog = "jog";
    public const string Stop = "stop";
    public const string GetStatus = "get_status";
    public const string GetAllStatus = "get_all_status";
    public const string ReadInput = "read_input";
    public const string WriteOutput = "write_output";
    public const string ConfigurePeg = "configure_peg";
    public const string StartPeg = "start_peg";
    public const string StopPeg = "stop_peg";
    public const string GetPegStatus = "get_peg_status";
    public const string StartDataCollection = "start_data_collection";
    public const string StopDataCollection = "stop_data_collection";
    public const string ReadHistory = "read_history";
    public const string ClearHistory = "clear_history";
    public const string LoadProgram = "load_program";
    public const string RunProgram = "run_program";
    public const string StopProgram = "stop_program";
    public const string GetProgramInfo = "get_program_info";
    public const string ReadVariable = "read_variable";
    public const string WriteVariable = "write_variable";
    public const string EventPegTriggered = "event_peg_triggered";
}

/// <summary>连接命令参数（对应 Connect）。</summary>
/// <param name="Options">连接配置。</param>
public sealed record ConnectRequest(MotionConnectionOptions Options);

/// <summary>单轴运动命令参数（对应 Move）。</summary>
public sealed record MoveRequest(
    MotionAxis Axis,
    double Position,
    MoveMode Mode,
    MotionProfile? Profile);

/// <summary>使能命令参数（对应 EnableAxis / DisableAxis）。</summary>
public sealed record AxisRequest(MotionAxis Axis);

/// <summary>回零命令参数（对应 Home）。</summary>
public sealed record HomeRequest(MotionAxis Axis, HomingMode Mode);

/// <summary>点动命令参数（对应 Jog）。</summary>
public sealed record JogRequest(MotionAxis Axis, double Velocity);

/// <summary>停止命令参数（对应 Stop）。</summary>
public sealed record StopRequest(MotionAxis? Axis, StopMode Mode);

/// <summary>写输出命令参数（对应 WriteOutput）。</summary>
public sealed record WriteOutputRequest(int Channel, bool Value);

/// <summary>读输入命令参数（对应 ReadInput）。</summary>
public sealed record ReadInputRequest(int Channel);

/// <summary>多轴运动命令参数（对应 MoveMulti）。</summary>
public sealed record MoveMultiRequest(
    IReadOnlyDictionary<MotionAxis, double> Targets,
    MoveMode Mode,
    MotionProfile? Profile);

/// <summary>配置 PEG 命令参数。</summary>
public sealed record ConfigurePegRequest(PegConfig Config);

/// <summary>采集配置命令参数。</summary>
public sealed record StartDataCollectionRequest(DataCollectionConfig Config);

/// <summary>加载程序命令参数。</summary>
public sealed record LoadProgramRequest(string Name, string Source);

/// <summary>程序名命令参数（RunProgram/StopProgram/GetProgramInfo）。</summary>
public sealed record ProgramRequest(string Name);

/// <summary>读变量命令参数。</summary>
public sealed record ReadVariableRequest(string Name);

/// <summary>写变量命令参数。</summary>
public sealed record WriteVariableRequest(string Name, double Value);
