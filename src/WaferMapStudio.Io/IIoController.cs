namespace WaferMapStudio.Io;

/// <summary>
/// 通用 IO 控制器契约 —— 上层业务（ViewModel / 检测流程）只依赖此接口。
///
/// 设计约定：
/// - 强类型点位 <see cref="IoPoint"/>，不允许裸通道号；
/// - 全异步 + 取消令牌，不阻塞 UI；
/// - 事件驱动变化沿，避免上层高频轮询；
/// - 所有设备原始异常归一化为 <see cref="IoControllerException"/> 派生类型；
/// - 初始化与释放都强制把输出置安全态。
/// </summary>
public interface IIoController : IAsyncDisposable
{
    /// <summary>当前链路状态。</summary>
    IoConnectionState ConnectionState { get; }

    /// <summary>已加载的点表；未初始化为 null。</summary>
    IoPointMap? PointMap { get; }

    /// <summary>点位逻辑值变化（去抖后）时触发。</summary>
    event EventHandler<IoChangedEventArgs>? InputChanged;

    /// <summary>控制器故障时触发（轮询异常只上报不退出）。</summary>
    event EventHandler<IoFaultEventArgs>? Faulted;

    /// <summary>链路状态变化时触发。</summary>
    event EventHandler<IoConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// 初始化：建链 → 读身份/校验型号 → 加载点表 → 建立输入镜像 → 输出置安全态 → Ready。
    /// </summary>
    Task InitializeAsync(
        IoConnectionOptions options,
        IoPointMap pointMap,
        CancellationToken cancellationToken = default);

    /// <summary>断开连接。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量刷新输入镜像：读整帧 → 去抖 → 变化沿上报。返回新镜像的拷贝。
    /// </summary>
    Task<byte[]> ReadInputsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 从本地输入镜像读点位逻辑值，零 IO 开销；需在 ReadInputsAsync 或监控循环之后调用。
    /// </summary>
    bool Read(IoPoint point);

    /// <summary>写单个输出点：方向校验 → 极性编码 → 下发 → 回读确认。</summary>
    Task WriteAsync(IoPoint point, bool value, CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量写输出：按 Bank 合并位掩码，尽量减少往返；写后统一回读校验。
    /// </summary>
    Task WriteBatchAsync(
        IReadOnlyDictionary<IoPoint, bool> values,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动后台轮询监控。重复调用会先停止上一次。异常经 Faulted 上报，循环不退出。
    /// </summary>
    Task StartMonitoringAsync(TimeSpan pollInterval, CancellationToken cancellationToken = default);

    /// <summary>停止后台轮询。</summary>
    Task StopMonitoringAsync(CancellationToken cancellationToken = default);
}
