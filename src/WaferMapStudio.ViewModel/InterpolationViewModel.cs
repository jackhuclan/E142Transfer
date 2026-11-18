using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WaferMapStudio.Motion.Interpolation;

namespace WaferMapStudio.ViewModel;

/// <summary>轨迹预设（顺序即下拉框顺序）。</summary>
public enum InterpPreset
{
    /// <summary>单轴直线：静止→静止，走七段 S 曲线。</summary>
    SingleLine,

    /// <summary>直线→圆弧→直线：演示前瞻与拐角限速。</summary>
    LineArcLine,

    /// <summary>螺旋 + 三次 Bezier 样条：演示弧长 LUT 插补。</summary>
    HelixSpline
}

/// <summary>典型工业场景：一个场景对应一套完整参数配置（顺序对应下拉框选项 1..N）。</summary>
public sealed record IndustryScenario(
    string Name,
    int PresetIndex,
    int CurveIndex,
    double Feed,
    double Accel,
    double Jerk,
    double PeriodMs,
    double ChordTolMm,
    int Lookahead);

/// <summary>分段限速表的一行。</summary>
public sealed record SegmentSpeedRow(string Name, double LengthMm, double EntrySpeed, double ExitSpeed);

/// <summary>采样明细表的一行（从插补指令中均匀抽取）。</summary>
public sealed record SampleRow(double TimeMs, double Xm, double Ym, double Zm, double Speed, double Accel, long XPulse);

/// <summary>轨迹预览点（mm）。</summary>
public sealed record TrajectoryPoint(double X, double Y, double Z);

/// <summary>
/// 插补算法演示视图模型：参数输入 → 时间分割插补 → 三环 PID 闭环跟随统计。
/// 算法全部来自 WaferMapStudio.Motion.Interpolation，本类只做参数解析、编排与展示数据组装。
/// </summary>
public sealed partial class InterpolationViewModel : ObservableObject
{
    public IReadOnlyList<string> PresetOptions { get; } =
    [
        "单轴直线（S 曲线起停）",
        "直线→圆弧→直线（前瞻拐角限速）",
        "螺旋 + Bezier 样条（弧长 LUT）"
    ];

    public IReadOnlyList<string> CurveOptions { get; } = ["梯形曲线", "七段 S 曲线"];

    /// <summary>典型工业场景下拉框选项；索引 0 为自定义（不套用预设），其余与 <see cref="Scenarios"/> 一一对应。</summary>
    public IReadOnlyList<string> ScenarioOptions { get; } =
    [
        "自定义（手动输入参数）",
        "CNC 模具曲面高速铣削",
        "半导体贴片机取放",
        "激光切割轮廓跟踪",
        "晶圆检测线扫平台",
        "多轴点胶 / 焊线"
    ];

    /// <summary>场景参数表（与前文工业场景说明一一对应，下标 = ScenarioOptions 索引 − 1）。</summary>
    private static readonly IndustryScenario[] Scenarios =
    [
        // 预设轨迹 / 曲线 / F / A / J / 周期ms / 弦高mm / 前瞻
        new("CNC 模具曲面高速铣削", (int)InterpPreset.HelixSpline, 1,
            Feed: 200, Accel: 300, Jerk: 2000, PeriodMs: 2, ChordTolMm: 0.01, Lookahead: 4),
        new("半导体贴片机取放", (int)InterpPreset.SingleLine, 1,
            Feed: 300, Accel: 1500, Jerk: 20000, PeriodMs: 1, ChordTolMm: 0.02, Lookahead: 2),
        new("激光切割轮廓跟踪", (int)InterpPreset.LineArcLine, 1,
            Feed: 80, Accel: 400, Jerk: 4000, PeriodMs: 2, ChordTolMm: 0.005, Lookahead: 6),
        new("晶圆检测线扫平台", (int)InterpPreset.HelixSpline, 1,
            Feed: 50, Accel: 200, Jerk: 1000, PeriodMs: 2, ChordTolMm: 0.01, Lookahead: 3),
        new("多轴点胶 / 焊线", (int)InterpPreset.LineArcLine, 1,
            Feed: 120, Accel: 600, Jerk: 6000, PeriodMs: 1, ChordTolMm: 0.02, Lookahead: 4)
    ];

    // ---------- 参数输入（文本框直接绑定字符串，执行时统一解析校验） ----------
    [ObservableProperty] private int selectedScenarioIndex;
    [ObservableProperty] private int selectedPresetIndex;
    [ObservableProperty] private int selectedCurveIndex = 1;
    [ObservableProperty] private string feedRateText = "100";
    [ObservableProperty] private string accelText = "500";
    [ObservableProperty] private string jerkText = "5000";
    [ObservableProperty] private string periodText = "2";
    [ObservableProperty] private string chordTolText = "0.01";
    [ObservableProperty] private string lookaheadText = "4";

    // ---------- 状态提示 ----------
    [ObservableProperty] private string statusText = "就绪：选择预设轨迹与参数后点击「执行插补」。";
    [ObservableProperty] private bool statusIsError;
    [ObservableProperty] private string summaryText = "尚未执行";
    [ObservableProperty] private string followErrorText = "—";

    // ---------- 结果 ----------
    public ObservableCollection<SegmentSpeedRow> SegmentRows { get; } = [];
    public ObservableCollection<SampleRow> SampleRows { get; } = [];

    public IReadOnlyList<TrajectoryPoint> Trajectory { get; private set; } = [];

    /// <summary>轨迹数据更新后通知视图重绘预览（App 层订阅）。</summary>
    public event EventHandler? TrajectoryUpdated;

    public RelayCommand ExecuteCommand { get; }

    public InterpolationViewModel() => ExecuteCommand = new RelayCommand(Execute);

    /// <summary>
    /// 选择典型工业场景：自动套用该场景的轨迹预设、时间律曲线与全部参数，
    /// 并立即执行插补，同步刷新分段限速表、轨迹预览与闭环跟随误差。
    /// 索引 0 为「自定义」，不套用任何预设（保留用户手动输入）。
    /// </summary>
    partial void OnSelectedScenarioIndexChanged(int value)
    {
        if (value <= 0 || value > Scenarios.Length)
            return;

        IndustryScenario s = Scenarios[value - 1];
        SelectedPresetIndex = s.PresetIndex;
        SelectedCurveIndex = s.CurveIndex;
        FeedRateText = s.Feed.ToString(CultureInfo.InvariantCulture);
        AccelText = s.Accel.ToString(CultureInfo.InvariantCulture);
        JerkText = s.Jerk.ToString(CultureInfo.InvariantCulture);
        PeriodText = s.PeriodMs.ToString(CultureInfo.InvariantCulture);
        ChordTolText = s.ChordTolMm.ToString(CultureInfo.InvariantCulture);
        LookaheadText = s.Lookahead.ToString(CultureInfo.InvariantCulture);

        Execute();
        if (!StatusIsError)
            StatusText = $"已套用场景「{s.Name}」（F={s.Feed} A={s.Accel} J={s.Jerk} T={s.PeriodMs}ms 弦高={s.ChordTolMm}mm 前瞻={s.Lookahead}）；{StatusText}";
    }

    private void Execute()
    {
        StatusIsError = false;
        StatusText = "正在执行插补…";

        try
        {
            double feed = ParsePositive(FeedRateText, "进给速度 F");
            double accel = ParsePositive(AccelText, "加速度 A");
            double jerk = ParsePositive(JerkText, "加加速度 J");
            double periodMs = ParseRange(PeriodText, "插补周期 (ms)", 0.1, 10.0);
            double chord = ParsePositive(ChordTolText, "弦高误差 (mm)");
            int lookahead = (int)ParseRange(LookaheadText, "前瞻段数", 1, 50);

            var motion = new FeedProfile(feed, accel, jerk);
            var paths = BuildPaths((InterpPreset)SelectedPresetIndex);
            var cmds = InterpolationEngine.Run(paths, motion, AxisConfig.ThreeAxes,
                (ProfileKind)SelectedCurveIndex, periodMs, chord, lookahead);

            // 分段限速表（与插补引擎同一规划器，保证口径一致）
            SegmentRows.Clear();
            var planner = new LookAheadPlanner(chord, periodMs / 1000.0, feed, accel, lookahead);
            var (entry, exit) = planner.Plan(paths);
            for (int i = 0; i < paths.Count; i++)
                SegmentRows.Add(new SegmentSpeedRow(paths[i].Name, paths[i].Length, entry[i], exit[i]));

            // 采样明细：均匀抽取至多 40 行 + 强制保留末行
            SampleRows.Clear();
            int step = Math.Max(1, cmds.Count / 40);
            for (int i = 0; i < cmds.Count; i += step)
                SampleRows.Add(ToRow(cmds[i]));
            if (SampleRows[^1].TimeMs < cmds[^1].TimeMs)
                SampleRows.Add(ToRow(cmds[^1]));

            // 预览轨迹
            Trajectory = cmds.Select(c => new TrajectoryPoint(
                c.PositionMm.X, c.PositionMm.Y, c.PositionMm.Z)).ToArray();
            TrajectoryUpdated?.Invoke(this, EventArgs.Empty);

            // 三环 PID 闭环跟随统计（与前文参数一致：1ms 位置环 / 0.5ms 速度环 / 0.1ms 电流环）
            double maxErr = 0, sumSq = 0;
            var servos = new[] { new ServoAxis(feed), new ServoAxis(feed), new ServoAxis(feed) };
            int innerSteps = Math.Max(1, (int)Math.Round(periodMs / 0.1));
            foreach (var cmd in cmds)
            {
                for (int k = 0; k < innerSteps; k++)
                    for (int a = 0; a < 3; a++)
                        servos[a].Step(Comp(cmd.PositionMm, a), Comp(cmd.VelocityMmPerSec, a),
                            Comp(cmd.AccelMmPerSec2, a));

                double ex = Comp(cmd.PositionMm, 0) - servos[0].PositionMm;
                double ey = Comp(cmd.PositionMm, 1) - servos[1].PositionMm;
                double ez = Comp(cmd.PositionMm, 2) - servos[2].PositionMm;
                double err = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                maxErr = Math.Max(maxErr, err);
                sumSq += err * err;
            }
            double rms = Math.Sqrt(sumSq / cmds.Count);
            FollowErrorText = $"最大 {maxErr:F4} mm ｜ RMS {rms:F4} mm";

            double maxAccel = cmds.Max(c => c.AccelMmPerSec2.Norm());
            var end = cmds[^1].PositionMm;
            SummaryText = $"段数 {paths.Count} ｜ 周期数 {cmds.Count} ｜ 总时长 {cmds[^1].TimeMs:F1} ms ｜ " +
                          $"最大加速度 {maxAccel:F0} mm/s² ｜ 末点 ({end.X:F3}, {end.Y:F3}, {end.Z:F3})";

            StatusText = $"完成：插补周期 {periodMs} ms，曲线 {CurveOptions[SelectedCurveIndex]}，" +
                         $"已生成 {cmds.Count} 条位置给定并完成闭环仿真。";
        }
        catch (Exception ex) when (ex is FormatException
                                 or OverflowException
                                 or ArgumentOutOfRangeException
                                 or ArgumentException)
        {
            StatusIsError = true;
            StatusText = $"参数错误：{ex.Message}";
        }
    }

    private static SampleRow ToRow(InterpolatedCommand c) => new(
        c.TimeMs, c.PositionMm.X, c.PositionMm.Y, c.PositionMm.Z,
        c.VelocityMmPerSec.Norm(), c.AccelMmPerSec2.Norm(), c.Pulses[0]);

    private static double Comp(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static List<IPathSegment> BuildPaths(InterpPreset preset) => preset switch
    {
        InterpPreset.SingleLine =>
        [
            new LineSegment(new Vec3(0, 0, 0), new Vec3(100, 0, 0), "L")
        ],
        InterpPreset.LineArcLine =>
        [
            new LineSegment(new Vec3(0, 0, 0), new Vec3(60, 0, 0), "L1"),
            new ArcSegment(new Vec3(60, 40, 0), 40, -Math.PI / 2, Math.PI / 2, 0, "C1"),
            new LineSegment(new Vec3(100, 40, 0), new Vec3(100, 80, 0), "L2")
        ],
        _ =>
        [
            new ArcSegment(new Vec3(0, 0, 0), 20, 0, 2 * Math.PI, 10, "螺旋"),
            new CubicBezierSegment(
                new Vec3(20, 0, 10), new Vec3(40, 0, 10),
                new Vec3(60, 40, 10), new Vec3(80, 40, 10), name: "样条")
        ]
    };

    private static double ParsePositive(string text, string name)
    {
        if (!double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0)
            throw new FormatException($"{name} 必须是大于 0 的数字（当前输入：{text}）");
        return v;
    }

    private static double ParseRange(string text, string name, double min, double max)
    {
        if (!double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            || v < min || v > max)
            throw new FormatException($"{name} 必须在 {min}–{max} 之间（当前输入：{text}）");
        return v;
    }
}
