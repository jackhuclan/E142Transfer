namespace WaferMapStudio.Motion;

/// <summary>
/// 运动控制器统一契约。
///
/// 设计目标：
/// 1. 完全屏蔽厂商 SDK —— 上层（ViewModel/App）只依赖本接口，换控制器只换实现；
/// 2. 全异步 + 取消令牌 —— 运动指令耗时且不阻塞 UI 线程，UI 可随时取消；
/// 3. 事件驱动状态 —— 运动完成/故障用事件上报，避免上位机轮询；
/// 4. 可测 —— 提供 <see cref="SimulatedMotionController"/>，无硬件即可单测。
///
/// 约定：所有位置参数的单位由实现方的连接配置决定；调用方通过
/// <see cref="AxisStatus.Unit"/> 读取实际单位。多轴方法按参数顺序执行或由实现
/// 做同步插补，具体语义在实现文档中说明。
/// </summary>
public interface IMotionController : IAsyncDisposable
{
    /// <summary>当前通信链路状态。</summary>
    MotionConnectionState ConnectionState { get; }

    /// <summary>该控制器实际可用的轴集合（不同机型轴数不同）。</summary>
    IReadOnlySet<MotionAxis> AvailableAxes { get; }

    /// <summary>轴运动状态变化（完成、开始、故障）时触发。</summary>
    event EventHandler<AxisStateChangedEventArgs>? AxisStateChanged;

    /// <summary>控制器上报故障时触发。</summary>
    event EventHandler<MotionFaultEventArgs>? Faulted;

    /// <summary>链路状态变化时触发。</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>建立通信连接。重复调用时实现方可选择复用或先断开。</summary>
    /// <param name="options">连接参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task ConnectAsync(MotionConnectionOptions options, CancellationToken cancellationToken = default);

    /// <summary>断开通信连接。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>使能指定轴（电机上使能）。</summary>
    Task EnableAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default);

    /// <summary>关闭指定轴的使能。</summary>
    Task DisableAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default);

    /// <summary>回零。方法在回零完成后返回。</summary>
    Task HomeAsync(MotionAxis axis, HomingMode mode, CancellationToken cancellationToken = default);

    /// <summary>点到点运动。方法在运动完成（或取消/故障）后返回。</summary>
    /// <param name="axis">目标轴。</param>
    /// <param name="position">目标位置或增量，取决于 <paramref name="mode"/>。</param>
    /// <param name="mode">绝对/相对。</param>
    /// <param name="profile">速度参数；为 null 时使用控制器当前参数。</param>
    /// <param name="cancellationToken">取消令牌；取消会触发轴停止。</param>
    Task MoveAsync(
        MotionAxis axis,
        double position,
        MoveMode mode,
        MotionProfile? profile = null,
        CancellationToken cancellationToken = default);

    /// <summary>多轴同步启动的点到点运动（如 XY 平台走直线）。</summary>
    Task MoveMultiAsync(
        IReadOnlyDictionary<MotionAxis, double> targets,
        MoveMode mode,
        MotionProfile? profile = null,
        CancellationToken cancellationToken = default);

    /// <summary>连续点动，直到 <see cref="StopAsync"/> 或取消令牌触发。</summary>
    Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default);

    /// <summary>按指定方式停止单轴；不传轴时停止全部轴。</summary>
    Task StopAsync(
        MotionAxis? axis,
        StopMode mode = StopMode.Decelerated,
        CancellationToken cancellationToken = default);

    /// <summary>读取单轴状态快照。</summary>
    Task<AxisStatus> GetStatusAsync(MotionAxis axis, CancellationToken cancellationToken = default);

    /// <summary>批量读取所有可用轴的状态快照。</summary>
    Task<IReadOnlyList<AxisStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>读取通用数字输入点（如光电传感器、真空确认）。</summary>
    Task<bool> ReadInputAsync(int channel, CancellationToken cancellationToken = default);

    /// <summary>写通用数字输出点（如光源触发、电磁阀）。</summary>
    Task WriteOutputAsync(int channel, bool value, CancellationToken cancellationToken = default);
}
