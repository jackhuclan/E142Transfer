namespace WaferMapStudio.Motion.Interpolation;

/// <summary>时间律采样结果：s 为已走弧长(mm)，v 为速度(mm/s)，a 为加速度(mm/s²)。</summary>
public readonly record struct ProfileSample(double S, double V, double A);

/// <summary>轨迹时间律的公共契约。插补器只依赖它，不关心具体是梯形还是 S 曲线。</summary>
public interface IMotionProfile
{
    double Duration { get; }
    ProfileSample Sample(double t);
}

/// <summary>
/// 梯形速度曲线（两阶段匀加减速）。支持非零入段速度 v0 与出段速度 v1，
/// 用于前瞻模块把上一段的拐角限速作为本段边界条件。
/// 峰值速度：vp² = A·L + (v0²+v1²)/2，当该值小于 F 时退化为三角形曲线。
/// </summary>
public sealed class TrapezoidProfile : IMotionProfile
{
    private readonly double _len, _a, _v0, _v1, _vp;
    private readonly double _ta, _tc, _td, _da;

    public double Duration { get; }

    public TrapezoidProfile(double length, double f, double a, double v0 = 0, double v1 = 0)
    {
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (a <= 0 || f <= 0) throw new ArgumentOutOfRangeException(nameof(f));
        if (v0 > f + 1e-9 || v1 > f + 1e-9)
            throw new ArgumentException("入段/出段速度不得超过进给速度 F");

        _len = length; _a = a; _v0 = v0; _v1 = v1;
        double vp2 = a * length + (v0 * v0 + v1 * v1) / 2.0;
        _vp = Math.Min(f, Math.Sqrt(Math.Max(0, vp2)));
        _da = (_vp * _vp - v0 * v0) / (2 * a);
        double dd = (_vp * _vp - v1 * v1) / (2 * a);
        _ta = (_vp - v0) / a;
        _td = (_vp - v1) / a;
        _tc = Math.Max(0, length - _da - dd) / Math.Max(_vp, 1e-9);
        Duration = _ta + _tc + _td;
    }

    public ProfileSample Sample(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        if (t < _ta)
        {
            return new ProfileSample(_v0 * t + 0.5 * _a * t * t, _v0 + _a * t, _a);
        }
        if (t < _ta + _tc)
        {
            double tau = t - _ta;
            return new ProfileSample(_da + _vp * tau, _vp, 0);
        }
        double td = Math.Min(t - _ta - _tc, _td);
        return new ProfileSample(
            Math.Min(_len, _da + _vp * _tc + _vp * td - 0.5 * _a * td * td),
            Math.Max(0, _vp - _a * td),
            -_a);
    }
}

/// <summary>
/// 七段 S 曲线（限制加加速度 J）静止到静止版本。
/// 阶段： jerk+ / 恒A / jerk- / 匀速 / jerk- / 恒A / jerk+
/// Tj = min(A/J, sqrt(Fp/J))，Ta = Fp/A - Tj，
/// 加速段距离 d_acc = Fp·(Ta + 2Tj)/2；当 2·d_acc(F) &gt; L 时对峰值速度 Fp 二分求解。
/// 物理含义： jerk 受限使加速度连续，消除梯形曲线的加速度突变与机械冲击。
/// </summary>
public sealed class SCurveProfile : IMotionProfile
{
    private readonly double _len;
    private readonly double[] _dur = new double[7];
    private readonly double[] _a0 = new double[7];
    private readonly double[] _a1 = new double[7];
    private readonly double[] _vStart = new double[8];
    private readonly double[] _sStart = new double[8];

    public double Duration { get; }

    public SCurveProfile(double length, double f, double a, double j)
    {
        if (length <= 0 || f <= 0 || a <= 0 || j <= 0)
            throw new ArgumentOutOfRangeException(nameof(length), "L/F/A/J 必须为正数");
        _len = length;

        // 峰值速度不超过 F 时按七段构造；否则二分降低峰值速度（无匀速段的 S 曲线）。
        double fEff = 2 * AccelDistance(f, a, j) <= length
            ? f
            : SolvePeak(length, f, a, j);

        double tj = Math.Min(a / j, Math.Sqrt(fEff / j));
        double ta = Math.Max(0, fEff / a - tj);
        double tc = Math.Max(0, length - 2 * AccelDistance(fEff, a, j)) / Math.Max(fEff, 1e-9);
        double amax = j * tj; // 当 Fp < A²/J 时有效加速度小于 A

        double[] d = [tj, ta, tj, tc, tj, ta, tj];
        double[] c0 = [0, amax, amax, 0, 0, -amax, -amax];
        double[] c1 = [j, 0, -j, 0, -j, 0, j];

        for (int i = 0; i < 7; i++)
        {
            _dur[i] = d[i]; _a0[i] = c0[i]; _a1[i] = c1[i];
            _vStart[i + 1] = _vStart[i] + c0[i] * d[i] + c1[i] * d[i] * d[i] / 2.0;
            _sStart[i + 1] = _sStart[i] + _vStart[i] * d[i] + c0[i] * d[i] * d[i] / 2.0
                             + c1[i] * d[i] * d[i] * d[i] / 6.0;
        }
        Duration = _dur.Sum();
    }

    private static double AccelDistance(double peak, double a, double j)
    {
        double tj = Math.Min(a / j, Math.Sqrt(peak / j));
        double ta = Math.Max(0, peak / a - tj);
        return peak * (ta + 2 * tj) / 2.0;
    }

    private static double SolvePeak(double length, double f, double a, double j)
    {
        double lo = 1e-9, hi = f;
        for (int k = 0; k < 80; k++)
        {
            double mid = (lo + hi) / 2;
            if (2 * AccelDistance(mid, a, j) > length) hi = mid; else lo = mid;
        }
        return lo;
    }

    public ProfileSample Sample(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        double acc = 0;
        for (int i = 0; i < 7; i++)
        {
            double end = acc + _dur[i];
            if (t <= end || i == 6)
            {
                double tau = Math.Clamp(t - acc, 0, _dur[i]);
                double v = _vStart[i] + _a0[i] * tau + _a1[i] * tau * tau / 2.0;
                double s = _sStart[i] + _vStart[i] * tau + _a0[i] * tau * tau / 2.0
                           + _a1[i] * tau * tau * tau / 6.0;
                return new ProfileSample(
                    Math.Clamp(s, 0, _len),
                    Math.Max(0, v),
                    _a0[i] + _a1[i] * tau);
            }
            acc = end;
        }
        return new ProfileSample(_len, 0, 0);
    }
}
