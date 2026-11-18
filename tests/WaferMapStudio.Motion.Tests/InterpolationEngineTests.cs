using Xunit;
using WaferMapStudio.Motion.Interpolation;

namespace WaferMapStudio.Motion.Tests;

/// <summary>
/// 插补算法（时间律 / 前瞻 / 引擎 / PID）核心行为的回归测试。
/// 参数沿用仓库工具 E142Transfer.MotionControlDemo 与 UI 演示页的默认值。
/// </summary>
public class InterpolationEngineTests
{
    private static readonly IPathSegment[] LineArcLine =
    [
        new LineSegment(new Vec3(0, 0, 0), new Vec3(60, 0, 0), "L1"),
        new ArcSegment(new Vec3(60, 40, 0), 40, -Math.PI / 2, Math.PI / 2, 0, "C1"),
        new LineSegment(new Vec3(100, 40, 0), new Vec3(100, 80, 0), "L2")
    ];

    [Fact]
    public void SCurveProfile_RestToRest_MatchesLengthAndSymmetry()
    {
        // F=100, A=500, J=5000：Tj=0.1s, Ta=0.1s, Tc=0.7s，总时长 1.3s
        var profile = new SCurveProfile(100, 100, 500, 5000);

        Assert.Equal(1.3, profile.Duration, 3);
        Assert.Equal(0, profile.Sample(0).V, 6);
        Assert.Equal(0, profile.Sample(0).S, 6);
        Assert.Equal(100, profile.Sample(profile.Duration).S, 6);
        Assert.Equal(0, profile.Sample(profile.Duration).V, 6);
        // 对称曲线中点位移恰为一半
        Assert.Equal(50, profile.Sample(profile.Duration / 2).S, 1);
    }

    [Fact]
    public void TrapezoidProfile_WithEntryExitSpeeds_RespectsBoundaries()
    {
        // 入段 100 mm/s、出段 22.36 mm/s：起点速度等于入段速度，终点收敛到出段速度
        var profile = new TrapezoidProfile(60, 100, 500, 100, 22.36);

        Assert.Equal(100, profile.Sample(0).V, 3);
        Assert.Equal(60, profile.Sample(profile.Duration).S, 3);
        Assert.Equal(22.36, profile.Sample(profile.Duration).V, 2);
    }

    [Fact]
    public void LookAheadPlanner_StartsFromRestAndLimitsCornerSpeed()
    {
        // 拐角 C1→L2 处转角 90°，弦高误差约束 v ≤ sqrt(8·e·κ)/T = sqrt(8·0.01·0.025)/0.002 ≈ 22.36
        var planner = new LookAheadPlanner(0.01, 0.002, feed: 100, latAccel: 500, lookahead: 4);
        var (entry, exit) = planner.Plan(LineArcLine);

        Assert.Equal(0, entry[0], 6);     // 起点静止
        Assert.Equal(0, exit[^1], 6);     // 终点停止
        Assert.Equal(22.36, exit[1], 2);  // 圆弧出口受弦高误差约束
        Assert.True(entry[2] <= exit[1] + 1e-9); // 前瞻不允许越过拐角限速
    }

    [Fact]
    public void InterpolationEngine_ProducesMonotonicTimeAndExactEndPoint()
    {
        var cmds = InterpolationEngine.Run(
            LineArcLine, new FeedProfile(100, 500, 5000),
            AxisConfig.ThreeAxes, ProfileKind.SCurve, periodMs: 2.0, chordTolMm: 0.01, lookahead: 4);

        Assert.True(cmds.Count > 10);
        for (int i = 1; i < cmds.Count; i++)
            Assert.True(cmds[i].TimeMs >= cmds[i - 1].TimeMs);

        Assert.Equal(0, cmds[0].PositionMm.X, 6);
        Assert.Equal(0, cmds[0].PositionMm.Y, 6);
        Assert.Equal(100, cmds[^1].PositionMm.X, 3);
        Assert.Equal(80, cmds[^1].PositionMm.Y, 3);
    }

    [Fact]
    public void PidController_PositionalForm_DrivesSimplePlantToSetpoint()
    {
        // 一阶积分对象 x += u·Ts，位置式 PID 应收敛到给定值
        var pid = new PidController(new PidGains { Kp = 5, Ki = 10, Ts = 0.001 });
        double x = 0;
        for (int i = 0; i < 5000; i++)
        {
            double u = pid.Update(setpoint: 1, measured: x);
            x += u * 0.001;
        }

        Assert.Equal(1, x, 3);
    }
}
