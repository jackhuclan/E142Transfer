using WaferMapStudio.Motion.Interpolation;

// 全部参数严格沿用前文：
// 周期：插补 2 ms / 位置环 1 ms / 速度环 0.5 ms / 电流环 0.1 ms
// 时间律：F=100 mm/s，A=500 mm/s²，J=5000 mm/s³；弦高误差 0.01 mm；前瞻 4 段
// PID：u = Kp·e + Ki·∫e·dt + Kd·de/dt + Kvff·v_ff + Kaff·a_ff（三环级联 + 抗饱和 + 微分滤波）

Console.WriteLine("==========================================================");
Console.WriteLine(" 运动控制插补 + PID 实现（参数沿用前文）");
Console.WriteLine("==========================================================");
Console.WriteLine(" T插补=2ms  T位置=1ms  T速度=0.5ms  T电流=0.1ms");
Console.WriteLine(" F=100mm/s  A=500mm/s²  J=5000mm/s³  弦高=0.01mm  前瞻=4段");
Console.WriteLine();

var motion = FeedProfile.Demo;
var axes = AxisConfig.ThreeAxes;
const double periodMs = 2.0;
const double chordTol = 0.01;
const int lookahead = 4;

Demo1SingleAxisSCurve();
Demo2CornerLookAhead();
Demo3HelixAndSpline();
Demo4ClosedLoopTracking();

void Demo1SingleAxisSCurve()
{
    Console.WriteLine("【示例1】单轴直线 + 七段 S 曲线（静止→静止）");
    var line = new LineSegment(new Vec3(0, 0, 0), new Vec3(100, 0, 0));
    var cmds = InterpolationEngine.Run([line], motion, axes, ProfileKind.SCurve, periodMs, chordTol, lookahead);

    Console.WriteLine($" 段长={line.Length:F2}mm  周期数={cmds.Count}  总时长={cmds[^1].TimeMs:F1}ms");
    PrintSamples(cmds, [0.0, 0.1, 0.5, 0.9, 1.0]);
    double maxA = cmds.Max(c => Math.Abs(c.AccelMmPerSec2.X));
    Console.WriteLine($" 最大加速度={maxA:F1}mm/s²（A={motion.Accel:F0}）  末点={cmds[^1].PositionMm}");
    Console.WriteLine();
}

void Demo2CornerLookAhead()
{
    Console.WriteLine("【示例2】直线→圆弧→直线，前瞻 4 段 + 拐角限速");
    var paths = new IPathSegment[]
    {
        new LineSegment(new Vec3(0, 0, 0), new Vec3(60, 0, 0), "L1"),
        new ArcSegment(new Vec3(60, 40, 0), 40, -Math.PI / 2, Math.PI / 2, 0, "C1"),
        new LineSegment(new Vec3(100, 40, 0), new Vec3(100, 80, 0), "L2")
    };

    var planner = new LookAheadPlanner(chordTol, periodMs / 1000.0, motion.FeedRate, motion.Accel, lookahead);
    var (entry, exit) = planner.Plan(paths);
    for (int i = 0; i < paths.Length; i++)
        Console.WriteLine($" {paths[i].Name}: 长={paths[i].Length,7:F2}mm  入段={entry[i],6:F2}mm/s  出段={exit[i],6:F2}mm/s");

    var cmds = InterpolationEngine.Run(paths, motion, axes, ProfileKind.SCurve, periodMs, chordTol, lookahead);
    Console.WriteLine($" 周期数={cmds.Count}  总时长={cmds[^1].TimeMs:F1}ms  末点={cmds[^1].PositionMm}");
    PrintSamples(cmds, [0.0, 0.25, 0.5, 0.75, 1.0]);
    Console.WriteLine();
}

void Demo3HelixAndSpline()
{
    Console.WriteLine("【示例3】螺旋段 + 三次 Bezier 样条（弧长 LUT 插补）");
    var helix = new ArcSegment(new Vec3(0, 0, 0), 20, 0, 2 * Math.PI, 10, "螺旋");
    var bezier = new CubicBezierSegment(
        new Vec3(20, 0, 10), new Vec3(40, 0, 10),
        new Vec3(60, 40, 10), new Vec3(80, 40, 10), name: "样条");
    var paths = new IPathSegment[] { helix, bezier };

    var cmds = InterpolationEngine.Run(paths, motion, axes, ProfileKind.Trapezoid, periodMs, chordTol, lookahead);
    Console.WriteLine($" 螺旋长={helix.Length:F2}mm  样条长={bezier.Length:F2}mm");
    Console.WriteLine($" 周期数={cmds.Count}  总时长={cmds[^1].TimeMs:F1}ms  理论末点={bezier.PointAt(bezier.Length)}");
    Console.WriteLine($" 实际末点={cmds[^1].PositionMm}  末点脉冲=[{string.Join(", ", cmds[^1].Pulses)}]");
    PrintSamples(cmds, [0.0, 0.3, 0.6, 0.85, 1.0]);
    Console.WriteLine();
}

void Demo4ClosedLoopTracking()
{
    Console.WriteLine("【示例4】三环 PID 闭环跟随示例2轨迹（统计轮廓误差）");
    var paths = new IPathSegment[]
    {
        new LineSegment(new Vec3(0, 0, 0), new Vec3(60, 0, 0), "L1"),
        new ArcSegment(new Vec3(60, 40, 0), 40, -Math.PI / 2, Math.PI / 2, 0, "C1"),
        new LineSegment(new Vec3(100, 40, 0), new Vec3(100, 80, 0), "L2")
    };
    var cmds = InterpolationEngine.Run(paths, motion, axes, ProfileKind.SCurve, periodMs, chordTol, lookahead);
    var servos = new[] { new ServoAxis(motion.FeedRate), new ServoAxis(motion.FeedRate), new ServoAxis(motion.FeedRate) };
    const int innerSteps = 20; // 2ms / 0.1ms

    double maxErr = 0, sumSq = 0;
    foreach (var cmd in cmds)
    {
        for (int k = 0; k < innerSteps; k++)
            for (int a = 0; a < 3; a++)
                servos[a].Step(Comp(cmd.PositionMm, a), Comp(cmd.VelocityMmPerSec, a), Comp(cmd.AccelMmPerSec2, a));

        var err = new Vec3(
            Comp(cmd.PositionMm, 0) - servos[0].PositionMm,
            Comp(cmd.PositionMm, 1) - servos[1].PositionMm,
            Comp(cmd.PositionMm, 2) - servos[2].PositionMm).Norm();
        maxErr = Math.Max(maxErr, err);
        sumSq += err * err;
    }
    double rms = Math.Sqrt(sumSq / cmds.Count);
    Console.WriteLine($" 最大跟随误差={maxErr:F4}mm   RMS={rms:F4}mm");
    Console.WriteLine($" 终点给定={cmds[^1].PositionMm}");
    Console.WriteLine($" 终点实际=({servos[0].PositionMm:F3}, {servos[1].PositionMm:F3}, {servos[2].PositionMm:F3})");
    Console.WriteLine();
}

static double Comp(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

static void PrintSamples(List<InterpolatedCommand> cmds, double[] fractions)
{
    Console.WriteLine("   t(ms)     X(mm)     Y(mm)     V(mm/s)   A(mm/s²)   X脉冲");
    foreach (double f in fractions)
    {
        var c = cmds[(int)Math.Round(f * (cmds.Count - 1))];
        Console.WriteLine($"  {c.TimeMs,7:F1}  {c.PositionMm.X,9:F3}  {c.PositionMm.Y,9:F3}" +
                          $"  {c.VelocityMmPerSec.Norm(),9:F2}  {c.AccelMmPerSec2.Norm(),9:F1}  {c.Pulses[0],8}");
    }
}
