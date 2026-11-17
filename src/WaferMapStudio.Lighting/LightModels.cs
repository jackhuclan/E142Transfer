namespace WaferMapStudio.Lighting;

/// <summary>
/// 连接参数。只描述"连什么、怎么连"，不含厂商 SDK 类型；
/// 各适配器把自身特有字段放进 <see cref="VendorOptions"/>，实现细节不外泄到调用方。
/// </summary>
/// <param name="Kind">链路类型。</param>
/// <param name="Address">地址（串口为端口名，TCP 为 IP，模拟器为任意标识）。</param>
/// <param name="Port">TCP 端口号；非 TCP 链路忽略。</param>
/// <param name="Timeout">单次指令超时；null 表示使用适配器默认值。</param>
/// <param name="VendorOptions">厂商私有键值，由具体适配器定义与消费。</param>
public sealed record LightConnectionOptions(
    LightTransportKind Kind,
    string Address,
    int Port = 7788,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? VendorOptions = null);

/// <summary>
/// 单个通道的亮度指令。这是控制器与传输层之间的"中性命令"：
/// 只表达目标和过渡方式，不规定如何编码成设备帧。
/// </summary>
/// <param name="IsOn">是否点亮。false 时 <see cref="BrightnessPercent"/> 应被视为 0。</param>
/// <param name="BrightnessPercent">目标亮度，归一化到 0–100。</param>
/// <param name="Ramp">过渡方式。</param>
/// <param name="RampMilliseconds">线性过渡时长；<see cref="LightRampMode.Immediate"/> 时忽略。</param>
public sealed record LightChannelCommand(
    bool IsOn,
    double BrightnessPercent,
    LightRampMode Ramp = LightRampMode.Immediate,
    int RampMilliseconds = 0);

/// <summary>单通道状态快照。一次性读取开关、亮度、故障信息，避免多次往返。</summary>
/// <param name="Channel">通道号（1-based，与面板丝印一致）。</param>
/// <param name="State">开关/故障状态。</param>
/// <param name="BrightnessPercent">当前亮度百分比。</param>
/// <param name="MeasuredCurrentMa">实测电流；设备不支持回读时为 null。</param>
/// <param name="FaultCode">设备故障码；无故障为 null。</param>
/// <param name="TimestampUtc">采样时刻（UTC）。</param>
public sealed record LightChannelStatus(
    int Channel,
    LightChannelState State,
    double BrightnessPercent,
    double? MeasuredCurrentMa,
    string? FaultCode,
    DateTimeOffset TimestampUtc);

/// <summary>设备身份与能力。控制器据此做参数校验，避免把非法值发给硬件。</summary>
/// <param name="Model">型号。</param>
/// <param name="SerialNumber">序列号；无则为空串。</param>
/// <param name="FirmwareVersion">固件版本。</param>
/// <param name="ChannelCount">可用通道数，通道号范围 1..ChannelCount。</param>
/// <param name="MaxCurrentMa">单通道额定最大电流，用于 mA ↔ 百分比换算与上限校验。</param>
/// <param name="SupportsCurrentReadback">是否支持电流回读。</param>
/// <param name="SupportsRamp">是否支持亮度线性过渡。</param>
public sealed record LightDeviceCapabilities(
    string Model,
    string SerialNumber,
    string FirmwareVersion,
    int ChannelCount,
    double MaxCurrentMa,
    bool SupportsCurrentReadback,
    bool SupportsRamp);

/// <summary>通道状态变化事件参数。</summary>
public sealed class LightChannelChangedEventArgs : EventArgs
{
    /// <summary>创建通道状态变化事件参数。</summary>
    public LightChannelChangedEventArgs(int channel, LightChannelStatus status)
    {
        Channel = channel;
        Status = status;
    }

    /// <summary>发生变化的通道。</summary>
    public int Channel { get; }

    /// <summary>变化后的状态快照。</summary>
    public LightChannelStatus Status { get; }
}

/// <summary>光源故障事件参数。区别于"字段里带故障码"，事件用于主动上报。</summary>
public sealed class LightFaultEventArgs : EventArgs
{
    /// <summary>创建故障事件参数。</summary>
    public LightFaultEventArgs(int? channel, string code, string message)
    {
        Channel = channel;
        Code = code;
        Message = message;
    }

    /// <summary>故障通道；整机关联故障为 null。</summary>
    public int? Channel { get; }

    /// <summary>故障码，保留设备原始码便于排障。</summary>
    public string Code { get; }

    /// <summary>可读描述。</summary>
    public string Message { get; }
}

/// <summary>连接状态变化事件参数。</summary>
public sealed class LightConnectionChangedEventArgs : EventArgs
{
    /// <summary>创建连接状态变化事件参数。</summary>
    public LightConnectionChangedEventArgs(LightConnectionState state) => State = state;

    /// <summary>新状态。</summary>
    public LightConnectionState State { get; }
}
