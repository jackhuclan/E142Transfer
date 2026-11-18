namespace WaferMapStudio.Motion.Interpolation;

/// <summary>机床坐标系中的三维点/向量，单位 mm。</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 Zero => new(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    public double Norm() => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;

    /// <summary>零向量返回零向量，避免轨迹切向计算出现 NaN。</summary>
    public Vec3 Unit()
    {
        double n = Norm();
        return n < 1e-12 ? Zero : this * (1.0 / n);
    }

    /// <summary>两向量夹角（弧度），用于前瞻模块的拐角识别。</summary>
    public double AngleTo(Vec3 o)
    {
        double c = Unit().Dot(o.Unit());
        return Math.Acos(Math.Clamp(c, -1.0, 1.0));
    }

    public override string ToString() => $"({X,8:F3}, {Y,8:F3}, {Z,8:F3})";
}

/// <summary>前文给出的进给时间律参数：进给速度 F、加速度 A、加加速度 J。
/// 命名避开 WaferMapStudio.Motion 根层的硬件 <c>MotionProfile</c>（同库不同语义）。</summary>
public sealed record FeedProfile(double FeedRate, double Accel, double Jerk)
{
    public static FeedProfile Demo => new(FeedRate: 100.0, Accel: 500.0, Jerk: 5000.0);

    public void Validate()
    {
        if (FeedRate <= 0 || Accel <= 0 || Jerk <= 0)
            throw new ArgumentOutOfRangeException(nameof(FeedRate), "F/A/J 必须为正数");
    }
}

/// <summary>轴配置：脉冲当量 = mm/pulse，脉冲数 = round(mm / pulseEquivalent)。</summary>
public sealed record AxisConfig(string Name, double PulseEquivalent, double MinMm, double MaxMm)
{
    public long ToPulses(double mm) => (long)Math.Round(mm / PulseEquivalent, MidpointRounding.AwayFromZero);

    public static IReadOnlyList<AxisConfig> ThreeAxes => new[]
    {
        new AxisConfig("X", 0.001, -500, 500),
        new AxisConfig("Y", 0.001, -500, 500),
        new AxisConfig("Z", 0.005, -200, 200)
    };
}

/// <summary>路径段的公共契约：按弧长 s 求位置、单位切向、曲率 1/R。</summary>
public interface IPathSegment
{
    string Name { get; }
    double Length { get; }
    Vec3 PointAt(double s);
    Vec3 TangentAt(double s);
    double CurvatureAt(double s);
}

/// <summary>空间直线插补的几何段；曲率为 0，法向加速度恒为 0。</summary>
public sealed class LineSegment : IPathSegment
{
    private readonly Vec3 _start;
    private readonly Vec3 _dir;

    public string Name { get; }
    public double Length { get; }
    public Vec3 End => _start + _dir * Length;

    public LineSegment(Vec3 start, Vec3 end, string? name = null)
    {
        _start = start;
        Vec3 delta = end - start;
        Length = delta.Norm();
        if (Length < 1e-9) throw new ArgumentException("直线段长度不能为 0");
        _dir = delta.Unit();
        Name = name ?? "直线";
    }

    public Vec3 PointAt(double s) => _start + _dir * Math.Clamp(s, 0, Length);
    public Vec3 TangentAt(double s) => _dir;
    public double CurvatureAt(double s) => 0.0;
}

/// <summary>
/// XY 圆弧 / 螺旋段。sweep 为 XY 平面扫过的角度（弧度），dz 为整段 Z 轴进给。
/// 弦高误差公式 e = R(1-cos(Δθ/2)) ≈ v²T²/(8R) 在 LookAheadPlanner 中使用。
/// 曲率（含螺旋）为 R·sweep²/L²，纯圆弧时退化为 1/R。
/// </summary>
public sealed class ArcSegment : IPathSegment
{
    private readonly Vec3 _center;
    private readonly double _radius;
    private readonly double _startAngle;
    private readonly double _sweep;
    private readonly double _dz;

    public string Name { get; }
    public double Length { get; }
    public Vec3 Start => PointAt(0);
    public Vec3 End => PointAt(Length);

    public ArcSegment(Vec3 center, double radius, double startAngle, double sweep, double dz = 0, string? name = null)
    {
        if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius));
        _center = center;
        _radius = radius;
        _startAngle = startAngle;
        _sweep = sweep;
        _dz = dz;
        double xyLen = Math.Abs(sweep) * radius;
        Length = Math.Sqrt(xyLen * xyLen + dz * dz);
        Name = name ?? (Math.Abs(dz) < 1e-9 ? "圆弧" : "螺旋");
    }

    public Vec3 PointAt(double s)
    {
        double u = Math.Clamp(s, 0, Length) / Length;
        double a = _startAngle + _sweep * u;
        return new Vec3(
            _center.X + _radius * Math.Cos(a),
            _center.Y + _radius * Math.Sin(a),
            _center.Z + _dz * u);
    }

    public Vec3 TangentAt(double s)
    {
        double u = Math.Clamp(s, 0, Length) / Length;
        double a = _startAngle + _sweep * u;
        double k = Math.Sign(_sweep);
        return new Vec3(
            -_radius * Math.Sin(a) * k / Length,
             _radius * Math.Cos(a) * k / Length,
             _dz / Length).Unit();
    }

    public double CurvatureAt(double s) => _radius * _sweep * _sweep / (Length * Length);
}

/// <summary>
/// 三次 Bezier 样条段。通过弧长查找表（LUT）把时间律给出的 s 映射回参数 u，
/// 这是样条插补"按弧长匀速"的常用做法（NURBS 工程实现同理，只是基函数不同）。
/// </summary>
public sealed class CubicBezierSegment : IPathSegment
{
    private readonly Vec3 _p0, _p1, _p2, _p3;
    private readonly double[] _uTable;
    private readonly double[] _sTable;

    public string Name { get; }
    public double Length => _sTable[^1];

    public CubicBezierSegment(Vec3 p0, Vec3 p1, Vec3 p2, Vec3 p3, int samples = 600, string? name = null)
    {
        _p0 = p0; _p1 = p1; _p2 = p2; _p3 = p3;
        Name = name ?? "样条";
        _uTable = new double[samples + 1];
        _sTable = new double[samples + 1];
        Vec3 prev = Eval(0);
        for (int i = 1; i <= samples; i++)
        {
            double u = (double)i / samples;
            Vec3 cur = Eval(u);
            _uTable[i] = u;
            _sTable[i] = _sTable[i - 1] + (cur - prev).Norm();
            prev = cur;
        }
    }

    private Vec3 Eval(double u)
    {
        double v = 1 - u;
        return _p0 * (v * v * v) + _p1 * (3 * v * v * u) + _p2 * (3 * v * u * u) + _p3 * (u * u * u);
    }

    private Vec3 EvalDerivative(double u)
    {
        double v = 1 - u;
        return (_p1 - _p0) * (3 * v * v) + (_p2 - _p1) * (6 * v * u) + (_p3 - _p2) * (3 * u * u);
    }

    private double ParamAt(double s)
    {
        int lo = 0, hi = _sTable.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_sTable[mid] < s) lo = mid + 1; else hi = mid;
        }
        if (lo == 0) return 0;
        double s0 = _sTable[lo - 1], s1 = _sTable[lo];
        double w = s1 - s0 < 1e-12 ? 0 : (s - s0) / (s1 - s0);
        return _uTable[lo - 1] + w * (_uTable[lo] - _uTable[lo - 1]);
    }

    public Vec3 PointAt(double s) => Eval(ParamAt(Math.Clamp(s, 0, Length)));

    public Vec3 TangentAt(double s) => EvalDerivative(ParamAt(Math.Clamp(s, 0, Length))).Unit();

    public double CurvatureAt(double s)
    {
        double ds = Math.Max(0.05, Length / 1000.0);
        Vec3 t0 = TangentAt(Math.Max(0, s - ds));
        Vec3 t1 = TangentAt(Math.Min(Length, s + ds));
        double span = Math.Min(Length, s + ds) - Math.Max(0, s - ds);
        return span < 1e-9 ? 0 : (t1 - t0).Norm() / span;
    }
}

/// <summary>一个插补周期输出：各轴位置给定及其一阶、二阶量，附脉冲换算结果。</summary>
public sealed record InterpolatedCommand(
    double TimeMs,
    Vec3 PositionMm,
    Vec3 VelocityMmPerSec,
    Vec3 AccelMmPerSec2,
    long[] Pulses);
