namespace WaferMapStudio.Io;

/// <summary>
/// 单个强类型点位的完整定义：逻辑点位 + 物理位置 + 点表原始字段。
/// 物理位置采用"字节组(Bank) + 位号(Bit)"：真实点表每个 IOModule 恰好 8 点、占一个字节，
/// IOPin 是 2 的幂掩码（1/2/4…128），加载器把掩码换算成 0–7 的 Bit。
/// </summary>
/// <param name="Point">规范逻辑点位。</param>
/// <param name="Direction">输入/输出。</param>
/// <param name="Bank">字节组序号（IN_1/OUT_1 → 0，IN_2/OUT_2 → 1 …）。</param>
/// <param name="Bit">字节内位号 0–7。</param>
/// <param name="ActiveLow">是否低有效（常闭/故障安全点）。真实点表未给出有效电平，默认高有效。</param>
/// <param name="SourceName">点表原始 IOParameterName。</param>
/// <param name="IoModule">点表原始 IOModule，如 IN_1。</param>
/// <param name="IoPin">点表原始 IOPin 掩码。</param>
/// <param name="IoName">点表原始 IOName，如 IN_1.0:1#门锁状态。</param>
/// <param name="Description">从 IOName 拆出的中文描述。</param>
public sealed record IoPointDef(
    IoPoint Point,
    IoDirection Direction,
    int Bank,
    int Bit,
    bool ActiveLow,
    string SourceName,
    string IoModule,
    int IoPin,
    string IoName,
    string Description);

/// <summary>连接参数，不含任何厂商 SDK 类型。</summary>
/// <param name="Kind">链路类型。</param>
/// <param name="Address">主机地址/IP/端口名。</param>
/// <param name="Port">TCP 端口；非 TCP 忽略。</param>
/// <param name="Timeout">单次指令超时；null 用实现默认值。</param>
/// <param name="ExpectedModel">期望的模块型号；非空时初始化会校验，不符抛异常。</param>
public sealed record IoConnectionOptions(
    IoConnectionKind Kind,
    string Address,
    int Port = 7010,
    TimeSpan? Timeout = null,
    string? ExpectedModel = null);

/// <summary>链路类型。与运动层的 MotionConnectionKind 语义一致，独立定义避免跨层耦合。</summary>
public enum IoConnectionKind
{
    /// <summary>以太网 TCP（Modbus TCP 等）。</summary>
    Tcp,

    /// <summary>进程外 IPC（用于连接 32 位厂商桥接服务）。</summary>
    OutOfProcess,

    /// <summary>独立主站库（EtherCAT/Profinet 等）。</summary>
    Fieldbus,

    /// <summary>纯软件模拟器。</summary>
    Simulator,
}

/// <summary>读到的模块身份，用于型号校验与日志。</summary>
/// <param name="Model">型号字符串。</param>
/// <param name="FirmwareVersion">固件版本。</param>
/// <param name="InputBankCount">输入字节组数量。</param>
/// <param name="OutputBankCount">输出字节组数量。</param>
public sealed record IoDeviceIdentity(
    string Model,
    string FirmwareVersion,
    int InputBankCount,
    int OutputBankCount);

/// <summary>点位变化事件参数。变化沿由轮询 diff 或硬件中断归一化后给出。</summary>
public sealed class IoChangedEventArgs : EventArgs
{
    /// <summary>创建点位变化事件参数。</summary>
    public IoChangedEventArgs(IoPoint point, bool value, DateTimeOffset timestampUtc)
    {
        Point = point;
        Value = value;
        TimestampUtc = timestampUtc;
    }

    /// <summary>发生变化的点位。</summary>
    public IoPoint Point { get; }

    /// <summary>变化后的逻辑值（已按 ActiveLow 归一）。</summary>
    public bool Value { get; }

    /// <summary>UTC 时间戳。</summary>
    public DateTimeOffset TimestampUtc { get; }
}

/// <summary>IO 故障事件参数。</summary>
public sealed class IoFaultEventArgs : EventArgs
{
    /// <summary>创建故障事件参数。</summary>
    public IoFaultEventArgs(string code, string message, Exception? exception = null)
    {
        Code = code;
        Message = message;
        Exception = exception;
    }

    /// <summary>故障码。</summary>
    public string Code { get; }

    /// <summary>可读描述。</summary>
    public string Message { get; }

    /// <summary>原始异常，便于排障。</summary>
    public Exception? Exception { get; }
}

/// <summary>链路状态变化事件参数。</summary>
public sealed class IoConnectionChangedEventArgs : EventArgs
{
    /// <summary>创建链路状态变化事件参数。</summary>
    public IoConnectionChangedEventArgs(IoConnectionState state) => State = state;

    /// <summary>新状态。</summary>
    public IoConnectionState State { get; }
}
