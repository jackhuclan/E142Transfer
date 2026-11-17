namespace WaferMapStudio.Lighting;

/// <summary>
/// 可复用光源控制器契约 —— 上层业务（ViewModel / 检测流程）只依赖此接口。
///
/// 与 <see cref="ILightDeviceTransport"/> 的分工：
/// 本接口提供"意图级"操作（开灯、设亮度、查状态），并保证参数合法、状态可观测、
/// 异常类型统一；传输接口只负责把命令送达设备。
///
/// 设计约定：
/// - 全异步 + 取消令牌，不阻塞 UI；
/// - 事件驱动状态变化，避免上位机轮询；
/// - 通道号 1-based，与设备面板丝印一致。
/// </summary>
public interface ILightController : IAsyncDisposable
{
    /// <summary>当前链路状态。</summary>
    LightConnectionState ConnectionState { get; }

    /// <summary>已读取的设备能力；未连接时为 null。</summary>
    LightDeviceCapabilities? Capabilities { get; }

    /// <summary>该控制器实际可用的通道号集合。</summary>
    IReadOnlySet<int> AvailableChannels { get; }

    /// <summary>通道开关/亮度发生变化时触发。</summary>
    event EventHandler<LightChannelChangedEventArgs>? ChannelChanged;

    /// <summary>设备上报故障时触发。</summary>
    event EventHandler<LightFaultEventArgs>? Faulted;

    /// <summary>链路状态变化时触发。</summary>
    event EventHandler<LightConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>连接并读取设备能力。重复调用时实现方决定复用或重连。</summary>
    /// <param name="options">连接参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ConnectAsync(LightConnectionOptions options, CancellationToken cancellationToken = default);

    /// <summary>断开连接。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>打开或关闭指定通道（保持当前亮度）。</summary>
    Task SetOnAsync(int channel, bool isOn, CancellationToken cancellationToken = default);

    /// <summary>设置指定通道的亮度（百分比 0–100），不改变开关状态。</summary>
    Task SetBrightnessAsync(
        int channel,
        double brightnessPercent,
        LightRampMode ramp = LightRampMode.Immediate,
        int rampMilliseconds = 0,
        CancellationToken cancellationToken = default);

    /// <summary>按指定单位设置强度：百分比、毫安或原始码值。</summary>
    Task SetIntensityAsync(
        int channel,
        double value,
        LightIntensityUnit unit,
        CancellationToken cancellationToken = default);

    /// <summary>一次性设置多通道亮度（如明场/暗场配方切换），尽量合并为一帧。</summary>
    Task SetBrightnessBatchAsync(
        IReadOnlyDictionary<int, double> brightnessByChannel,
        CancellationToken cancellationToken = default);

    /// <summary>关闭全部通道。用于换片、维护或急停后的安全态。</summary>
    Task TurnOffAllAsync(CancellationToken cancellationToken = default);

    /// <summary>读取单通道状态快照。</summary>
    Task<LightChannelStatus> ReadChannelAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>批量读取所有通道状态。</summary>
    Task<IReadOnlyList<LightChannelStatus>> ReadAllChannelsAsync(CancellationToken cancellationToken = default);
}
