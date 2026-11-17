namespace WaferMapStudio.Lighting;

/// <summary>
/// 底层设备通信接口 —— 整个抽象层最关键的接缝。
///
/// 实现方责任（各机型适配器）：
/// - 把 <see cref="LightChannelCommand"/> 翻译成设备帧（Modbus 寄存器 / 串口 ASCII / 厂商协议）；
/// - 把设备原始码值翻译成 <see cref="LightChannelCommand"/> 与 <see cref="LightChannelStatus"/>；
/// - 负责字节序、校验和、重试、超时等协议细节，并做电平/电流量的单位换算。
///
/// 明确不做的责任（归 <see cref="LightController"/>）：
/// - 参数校验、连接状态机、事件语义、异常归一化、多通道批量意图。
///
/// 因此本接口刻意保持"窄"：一次一个通道命令或一次批量命令，不做业务判断；
/// 换硬件只需新增一个实现，上层 ViewModel/App 完全不动。
/// </summary>
public interface ILightDeviceTransport : IAsyncDisposable
{
    /// <summary>当前链路状态。</summary>
    LightConnectionState ConnectionState { get; }

    /// <summary>链路状态变化时触发。</summary>
    event EventHandler<LightConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>建立连接。方法在连接完成（或失败）后返回。</summary>
    /// <param name="options">连接参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ConnectAsync(LightConnectionOptions options, CancellationToken cancellationToken = default);

    /// <summary>断开连接。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>读取设备身份与能力。连接后由控制器调用一次并缓存。</summary>
    Task<LightDeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>对单个通道写入亮度指令。方法在设备确认后返回。</summary>
    Task SetChannelAsync(int channel, LightChannelCommand command, CancellationToken cancellationToken = default);

    /// <summary>批量写入多个通道。适配器应尽量合并为一帧；不支持时可逐条下发。</summary>
    Task SetChannelsAsync(
        IReadOnlyDictionary<int, LightChannelCommand> commands,
        CancellationToken cancellationToken = default);

    /// <summary>读取单通道状态快照。</summary>
    Task<LightChannelStatus> ReadChannelAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>批量读取全部通道状态。</summary>
    Task<IReadOnlyList<LightChannelStatus>> ReadAllChannelsAsync(CancellationToken cancellationToken = default);
}
