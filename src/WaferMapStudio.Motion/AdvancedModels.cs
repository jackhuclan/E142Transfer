namespace WaferMapStudio.Motion;

/// <summary>PEG 触发模式。</summary>
public enum PegMode
{
    /// <summary>每走过固定距离触发一次（AOI 行扫最常用）。</summary>
    Distance,

    /// <summary>在给定的绝对位置点序列上各触发一次。</summary>
    PositionList,
}

/// <summary>PEG（位置事件生成）配置。
///
/// PEG 让控制器在轴走到指定位置时由硬件直接输出脉冲触发相机曝光，
/// 触发时刻与位置严格绑定，不受 Windows 上位机定时器抖动影响——这是
/// 高速扫描时"图像不错位"的关键。
/// </summary>
/// <param name="Axis">产生位置事件的轴（通常是扫描主轴）。</param>
/// <param name="OutputChannel">输出脉冲的通道号（映射到控制器 PEG 输出）。</param>
/// <param name="Mode">触发模式。</param>
/// <param name="Distance">Distance 模式下的触发间隔，单位同轴位置单位。</param>
/// <param name="Positions">PositionList 模式下的绝对位置序列；其它模式为 null。</param>
/// <param name="StartPosition">触发起始位置；从该点之后开始计数。</param>
/// <param name="Count">期望触发次数；0 表示持续触发直到显式停止。</param>
public sealed record PegConfig(
    MotionAxis Axis,
    int OutputChannel,
    PegMode Mode,
    double Distance,
    IReadOnlyList<double>? Positions,
    double StartPosition,
    int Count);

/// <summary>PEG 运行时状态。</summary>
/// <param name="Axis">轴。</param>
/// <param name="IsEnabled">是否已启动。</param>
/// <param name="TriggerCount">已触发次数。</param>
/// <param name="Config">当前配置；未配置为 null。</param>
public sealed record PegStatus(
    MotionAxis Axis,
    bool IsEnabled,
    int TriggerCount,
    PegConfig? Config);

/// <summary>PEG 触发事件参数。供观测与测试使用；真机的相机触发由硬件脉冲完成。</summary>
public sealed class PegTriggeredEventArgs : EventArgs
{
    /// <summary>创建 PEG 触发事件参数。</summary>
    public PegTriggeredEventArgs(MotionAxis axis, int triggerIndex, double position, DateTimeOffset timestampUtc)
    {
        Axis = axis;
        TriggerIndex = triggerIndex;
        Position = position;
        TimestampUtc = timestampUtc;
    }

    /// <summary>触发轴。</summary>
    public MotionAxis Axis { get; }

    /// <summary>第几次触发（从 1 开始）。</summary>
    public int TriggerIndex { get; }

    /// <summary>触发时的轴位置。</summary>
    public double Position { get; }

    /// <summary>触发时刻 UTC。</summary>
    public DateTimeOffset TimestampUtc { get; }
}

/// <summary>数据采集源。</summary>
public enum HistorySource
{
    /// <summary>采集指令位置。</summary>
    CommandPosition,

    /// <summary>采集编码器反馈位置。</summary>
    FeedbackPosition,

    /// <summary>指令与反馈都采集。</summary>
    Both,
}

/// <summary> History Buffer 采集配置。</summary>
/// <param name="Axes">要采集的轴集合。</param>
/// <param name="SampleInterval">采样周期。</param>
/// <param name="Source">采集源。</param>
/// <param name="Capacity">缓冲区容量（样本数），超出后覆盖最旧样本。</param>
public sealed record DataCollectionConfig(
    IReadOnlyList<MotionAxis> Axes,
    TimeSpan SampleInterval,
    HistorySource Source,
    int Capacity);

/// <summary>
/// 单个采集样本。指令位置与反馈位置分开存；未启用的源对应字典为空，
/// 便于上层直接比较两者做跟随误差分析。
/// </summary>
/// <param name="TimestampUtc">采样时刻 UTC。</param>
/// <param name="CommandPositions">轴 → 指令位置。</param>
/// <param name="FeedbackPositions">轴 → 反馈位置。</param>
public sealed record HistorySample(
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<MotionAxis, double> CommandPositions,
    IReadOnlyDictionary<MotionAxis, double> FeedbackPositions);

/// <summary>采集结果。</summary>
/// <param name="Samples">有效样本（按时间升序）。</param>
/// <param name="DroppedSamples">因缓冲区溢出被覆盖的样本数。</param>
public sealed record HistoryData(
    IReadOnlyList<HistorySample> Samples,
    int DroppedSamples);

/// <summary>ACSPL+ 程序运行状态。</summary>
public enum AcsplProgramState
{
    /// <summary>控制器中不存在该程序。</summary>
    NotLoaded,

    /// <summary>已加载，未运行。</summary>
    Loaded,

    /// <summary>正在运行。</summary>
    Running,

    /// <summary>已暂停。</summary>
    Paused,

    /// <summary>正常执行完毕。</summary>
    Completed,

    /// <summary>执行出错。</summary>
    Faulted,
}

/// <summary>ACSPL+ 程序状态快照。</summary>
/// <param name="Name">程序名。</param>
/// <param name="State">运行状态。</param>
/// <param name="CurrentLine">当前执行行号；非运行态为 0。</param>
public sealed record ProgramInfo(string Name, AcsplProgramState State, int CurrentLine);
