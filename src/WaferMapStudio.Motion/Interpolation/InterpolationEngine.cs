namespace WaferMapStudio.Motion.Interpolation;

public enum ProfileKind { Trapezoid, SCurve }

/// <summary>
/// 前瞻与拐角限速规划。核心公式：
/// 1) 弦高误差 e = R(1-cos(Δθ/2)) ≈ v²T²/(8R) ⇒ 单段最大速度 v ≤ sqrt(8·e·κ)/T，κ=1/R；
/// 2) 拐角圆角：由允许弦高 e 与转角 φ 求等效半径 R = e / (1/cos(φ/2) - 1)，
///    再由向心加速度限制得拐角速度 v_corner = sqrt(A·R)；
/// 3) 前瞻 N 段：从第 i 段起点到第 j 段终点的距离 d，进入速度 v ≤ sqrt(v_corner[j]² + 2A·d)。
/// </summary>
public sealed class LookAheadPlanner
{
    private readonly double _chordTol;   // mm
    private readonly double _period;     // s
    private readonly double _feed;       // mm/s
    private readonly double _latAccel;   // mm/s²
    private readonly int _lookahead;     // 向前查看的段数（含当前段）
    private readonly double _startSpeed; // 首段入段速度，默认从静止启动

    public LookAheadPlanner(double chordTolMm, double periodSec, double feed, double latAccel,
        int lookahead, double startSpeed = 0)
    {
        if (chordTolMm <= 0 || periodSec <= 0 || feed <= 0 || latAccel <= 0)
            throw new ArgumentOutOfRangeException(nameof(chordTolMm));
        _chordTol = chordTolMm;
        _period = periodSec;
        _feed = feed;
        _latAccel = latAccel;
        _lookahead = Math.Max(1, lookahead);
        _startSpeed = Math.Max(0, startSpeed);
    }

    /// <summary>返回每段入段速度与出段速度（出段速度即该段终点拐角限速）。</summary>
    public (double[] Entry, double[] Exit) Plan(IReadOnlyList<IPathSegment> paths)
    {
        int n = paths.Count;
        if (n == 0) throw new ArgumentException("路径段不能为空");

        double[] corner = new double[n];
        for (int i = 0; i < n; i++)
        {
            double v = _feed;
            double kMax = 0;
            for (int k = 0; k <= 20; k++)
                kMax = Math.Max(kMax, paths[i].CurvatureAt(paths[i].Length * k / 20.0));
            if (kMax > 1e-9)
                v = Math.Min(v, Math.Sqrt(8.0 * _chordTol * kMax) / _period);
            corner[i] = v;
        }

        for (int i = 0; i < n - 1; i++)
        {
            double phi = paths[i].TangentAt(paths[i].Length).AngleTo(paths[i + 1].TangentAt(0));
            if (phi <= 1e-3) continue;
            double denom = 1.0 / Math.Cos(Math.Min(phi, Math.PI - 1e-6) / 2.0) - 1.0;
            double radius = denom > 1e-9 ? _chordTol / denom : _chordTol * 0.1;
            corner[i] = Math.Min(corner[i], Math.Sqrt(_latAccel * radius));
        }
        corner[n - 1] = 0; // 终点停止

        double[] entry = new double[n];
        double[] exit = new double[n];
        for (int i = 0; i < n; i++)
        {
            double limit = _feed;
            double distance = 0;
            for (int j = i; j < Math.Min(n, i + _lookahead); j++)
            {
                distance += paths[j].Length;
                limit = Math.Min(limit, Math.Sqrt(corner[j] * corner[j] + 2 * _latAccel * distance));
            }
            if (i > 0) limit = Math.Min(limit, exit[i - 1]);
            else limit = Math.Min(limit, _startSpeed); // 起点静止：首段从 0 开始加速
            entry[i] = Math.Max(0, limit);
            exit[i] = corner[i];
        }
        return (entry, exit);
    }
}

/// <summary>
/// 时间分割插补引擎：每个插补周期输出一个 InterpolatedCommand。
/// 直线/圆弧/螺旋用解析点，样条用弧长 LUT；速度由时间律给出，
/// 加速度 = 切向分量 a + 法向分量 v²κ（向心加速度）。
/// </summary>
public sealed class InterpolationEngine
{
    private static double Comp(Vec3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    public static List<InterpolatedCommand> Run(
        IReadOnlyList<IPathSegment> paths,
        FeedProfile motion,
        IReadOnlyList<AxisConfig> axes,
        ProfileKind kind,
        double periodMs,
        double chordTolMm,
        int lookahead)
    {
        motion.Validate();
        double periodSec = periodMs / 1000.0;
        var planner = new LookAheadPlanner(chordTolMm, periodSec, motion.FeedRate, motion.Accel, lookahead);
        var (entry, exit) = planner.Plan(paths);

        var profiles = new IMotionProfile[paths.Count];
        var starts = new double[paths.Count];
        double total = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            bool restToRest = entry[i] < 1e-9 && exit[i] < 1e-9;
            profiles[i] = kind == ProfileKind.SCurve && restToRest
                ? new SCurveProfile(paths[i].Length, motion.FeedRate, motion.Accel, motion.Jerk)
                : new TrapezoidProfile(paths[i].Length, motion.FeedRate, motion.Accel, entry[i], exit[i]);
            starts[i] = total;
            total += profiles[i].Duration;
        }

        var result = new List<InterpolatedCommand>();
        for (double t = 0; t <= total + 1e-9; t += periodSec)
        {
            int seg = paths.Count - 1;
            for (int i = 0; i < paths.Count; i++)
                if (t < starts[i] + profiles[i].Duration) { seg = i; break; }

            var sm = profiles[seg].Sample(t - starts[seg]);
            var path = paths[seg];
            double s = Math.Clamp(sm.S, 0, path.Length);
            Vec3 point = path.PointAt(s);
            Vec3 tangent = path.TangentAt(s);
            double kappa = path.CurvatureAt(s);

            // 由切向差分求主法向，供向心加速度 v²κ 使用。
            double eps = Math.Max(1e-3, path.Length / 2000.0);
            Vec3 t0 = path.TangentAt(Math.Max(0, s - eps));
            Vec3 t1 = path.TangentAt(Math.Min(path.Length, s + eps));
            Vec3 dT = t1 - t0;
            Vec3 normal = dT.Norm() > 1e-9 ? dT.Unit() : Vec3.Zero;

            Vec3 vel = tangent * sm.V;
            Vec3 acc = tangent * sm.A + normal * (sm.V * sm.V * kappa);
            var pulses = new long[axes.Count];
            for (int a = 0; a < axes.Count; a++) pulses[a] = axes[a].ToPulses(Comp(point, a));

            result.Add(new InterpolatedCommand(t * 1000.0, point, vel, acc, pulses));
        }
        return result;
    }
}
