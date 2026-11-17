namespace WaferMapStudio.Motion;

/// <summary>
/// 高级运动控制契约：AOI 扫描时序所需的三块能力——
/// PEG（位置事件触发相机）、History Buffer（位置轨迹采集）、ACSPL+ 程序与变量访问。
///
/// 独立成接口而非并入 <see cref="IMotionController"/>，原因：
/// 1. 并非所有控制器/机型都具备这些能力，基础实现无需被迫实现；
/// 2. ViewModel 依赖基础接口即可，只有扫描流程依赖本接口，依赖更精确；
/// 3. 模拟器可独立实现，便于对扫描时序做无硬件测试。
///
/// 上位机通过类型检查或 DI 判断控制器是否支持：若拿到的是本接口实例，
/// 说明可做扫描采集；否则只能做普通点位运动。
/// </summary>
public interface IAdvancedMotionController : IMotionController
{
    /// <summary>PEG 触发事件（每次硬件/模拟触发时上报）。</summary>
    event EventHandler<PegTriggeredEventArgs>? PegTriggered;

    // ===== PEG：位置事件生成 =====

    /// <summary>配置指定轴的 PEG。配置会替换该轴原有配置。</summary>
    Task ConfigurePegAsync(PegConfig config, CancellationToken cancellationToken = default);

    /// <summary>启动 PEG，从当前配置的位置开始产生触发。</summary>
    Task StartPegAsync(MotionAxis axis, CancellationToken cancellationToken = default);

    /// <summary>停止 PEG，保留配置。</summary>
    Task StopPegAsync(MotionAxis axis, CancellationToken cancellationToken = default);

    /// <summary>读取 PEG 运行时状态（含已触发次数）。</summary>
    Task<PegStatus> GetPegStatusAsync(MotionAxis axis, CancellationToken cancellationToken = default);

    // ===== History Buffer：数据采集 =====

    /// <summary>启动历史数据采集；若已有采集在进行则先停止。</summary>
    Task StartDataCollectionAsync(DataCollectionConfig config, CancellationToken cancellationToken = default);

    /// <summary>停止数据采集，已采样本保留在缓冲区。</summary>
    Task StopDataCollectionAsync(CancellationToken cancellationToken = default);

    /// <summary>读取当前采集到的数据，不清空缓冲区。</summary>
    Task<HistoryData> ReadHistoryAsync(CancellationToken cancellationToken = default);

    /// <summary>清空采集缓冲区。</summary>
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);

    // ===== ACSPL+ 程序与变量 =====

    /// <summary>把程序源码加载到控制器（同名则覆盖），状态置为 Loaded。</summary>
    Task LoadProgramAsync(string name, string source, CancellationToken cancellationToken = default);

    /// <summary>启动已加载的程序；程序不存在时抛异常。</summary>
    Task RunProgramAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>停止正在运行的程序。</summary>
    Task StopProgramAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>查询程序状态。</summary>
    Task<ProgramInfo> GetProgramInfoAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>读取控制器实型变量（ACSPL+ 变量多为实型）。</summary>
    Task<double> ReadVariableAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>写控制器实型变量。</summary>
    Task WriteVariableAsync(string name, double value, CancellationToken cancellationToken = default);
}
