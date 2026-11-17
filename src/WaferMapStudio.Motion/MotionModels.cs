namespace WaferMapStudio.Motion;

/// <summary>
/// 连接参数。只描述"连什么、怎么连"，不含任何厂商 SDK 类型；
/// 各适配器把自身特有字段放进 <see cref="VendorOptions"/>，实现细节不外泄到调用方。
/// </summary>
/// <param name="Kind">链路类型（以太网/PCI/串口/模拟器）。</param>
/// <param name="Address">主机地址（TCP 时为 IP，串口时为端口名，PCI 时为板卡号）。</param>
/// <param name="Port">TCP 端口号；非 TCP 链路忽略。</param>
/// <param name="Timeout">单次指令超时。设为 null 表示使用适配器默认值。</param>
/// <param name="VendorOptions">厂商私有键值，由具体适配器定义与消费。</param>
public sealed record MotionConnectionOptions(
    MotionConnectionKind Kind,
    string Address,
    int Port = 7010,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? VendorOptions = null);

/// <summary>通信链路类型。与 SDK 的 OpenCommXxx 一一对应，但名称中立。</summary>
public enum MotionConnectionKind
{
    /// <summary>以太网 TCP。</summary>
    Tcp,

    /// <summary>以太网 UDP。</summary>
    Udp,

    /// <summary>PCI/PCIe 板卡。</summary>
    Pci,

    /// <summary>串口。</summary>
    Serial,

    /// <summary>进程外 IPC（用于连接 32 位 ACS 桥接服务）。</summary>
    OutOfProcess,

    /// <summary>纯软件模拟器，无需硬件。</summary>
    Simulator,
}

/// <summary>轴状态快照。一次性读取该轴的使能、运动、位置信息，避免多次往返。</summary>
/// <param name="Axis">轴标识。</param>
/// <param name="Enable">使能状态。</param>
/// <param name="Motion">运动状态。</param>
/// <param name="CommandPosition">指令位置。</param>
/// <param name="FeedbackPosition">编码器反馈位置。</param>
/// <param name="Unit">位置单位。</param>
public sealed record AxisStatus(
    MotionAxis Axis,
    AxisEnableState Enable,
    AxisMotionState Motion,
    double CommandPosition,
    double FeedbackPosition,
    MotionUnit Unit);

/// <summary>轴状态变化事件参数。</summary>
public sealed class AxisStateChangedEventArgs : EventArgs
{
    /// <summary>创建轴状态变化事件参数。</summary>
    public AxisStateChangedEventArgs(MotionAxis axis, AxisMotionState state, DateTimeOffset timestampUtc)
    {
        Axis = axis;
        State = state;
        TimestampUtc = timestampUtc;
    }

    /// <summary>发生变化的轴。</summary>
    public MotionAxis Axis { get; }

    /// <summary>新的运动状态。</summary>
    public AxisMotionState State { get; }

    /// <summary>事件产生的 UTC 时间。</summary>
    public DateTimeOffset TimestampUtc { get; }
}

/// <summary>控制器故障事件参数。</summary>
public sealed class MotionFaultEventArgs : EventArgs
{
    /// <summary>创建故障事件参数。</summary>
    public MotionFaultEventArgs(MotionAxis? axis, string code, string message)
    {
        Axis = axis;
        Code = code;
        Message = message;
    }

    /// <summary>故障轴；整机关联故障为 null。</summary>
    public MotionAxis? Axis { get; }

    /// <summary>故障码，保留控制器原始码便于排障。</summary>
    public string Code { get; }

    /// <summary>可读描述。</summary>
    public string Message { get; }
}

/// <summary>连接状态变化事件参数。</summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>创建连接状态变化事件参数。</summary>
    public ConnectionStateChangedEventArgs(MotionConnectionState state) => State = state;

    /// <summary>新状态。</summary>
    public MotionConnectionState State { get; }
}

/// <summary>
/// 轴的运动参数（速度/加减速）。用工程单位表达，由适配器换算成控制器单位。
/// </summary>
/// <param name="Velocity">最大速度（单位/秒）。</param>
/// <param name="Acceleration">加速度（单位/秒²）。</param>
/// <param name="Deceleration">减速度（单位/秒²）。如为 null 则与加速度相同。</param>
public sealed record MotionProfile(
    double Velocity,
    double Acceleration,
    double? Deceleration = null);
