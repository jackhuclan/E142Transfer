namespace WaferMapStudio.Motion.Interpolation;

public enum PidForm { Positional, Incremental }

/// <summary>
/// PID 参数集。严格对应前文公式：u = Kp·e + Ki·∫e·dt + Kd·de/dt + Kvff·v_ff + Kaff·a_ff。
/// Tf 为微分滤波时间常数；IntegralSeparation 为积分分离阈值；
/// AntiWindupGain 为抗饱和反算增益；OutputLimit 为输出限幅。
/// </summary>
public sealed class PidGains
{
    public double Kp, Ki, Kd, Ts;
    public double Tf;
    public double OutputLimit = double.MaxValue;
    public double IntegralSeparation = double.MaxValue;
    public double Kvff, Kaff;
    public double AntiWindupGain = 0.1;
    public PidForm Form = PidForm.Positional;
}

/// <summary>
/// 数字 PID 控制器。支持位置式/增量式、微分先行+一阶滤波、积分分离、
/// 输出限幅与反算抗饱和、速度/加速度前馈。
/// </summary>
public sealed class PidController
{
    private readonly PidGains _g;
    private double _integral;       // 已累积的 Ki·∫e·dt
    private double _prevMeasured;
    private double _prevError, _prevPrevError, _prevOutput, _prevFf;
    private double _filteredDerivative;

    public PidController(PidGains gains) => _g = gains ?? throw new ArgumentNullException(nameof(gains));
    public double Output { get; private set; }

    public double Update(double setpoint, double measured, double velocityFeedforward = 0, double accelFeedforward = 0)
    {
        double e = setpoint - measured;
        double ff = _g.Kvff * velocityFeedforward + _g.Kaff * accelFeedforward;
        double u;

        if (_g.Form == PidForm.Incremental)
        {
            // 增量式：Δu = Kp·(e_k-e_{k-1}) + Ki·Ts·e_k + Kd·(e_k-2e_{k-1}+e_{k-2})/Ts
            double de = e - _prevError;
            double d2e = e - 2 * _prevError + _prevPrevError;
            double du = _g.Kp * de + _g.Ki * _g.Ts * e
                        + (_g.Ts > 0 ? _g.Kd * d2e / _g.Ts : 0)
                        + (ff - _prevFf);
            u = Math.Clamp(_prevOutput + du, -_g.OutputLimit, _g.OutputLimit);
            _prevPrevError = _prevError;
            _prevError = e;
            _prevFf = ff;
            _prevOutput = u;
        }
        else
        {
            // 微分先作用于测量值，避免给定突变造成微分冲击；一阶低通抑制编码器噪声。
            double derivRaw = (measured - _prevMeasured) / Math.Max(_g.Ts, 1e-12);
            double alpha = _g.Tf <= 0 ? 0 : _g.Tf / (_g.Tf + _g.Ts);
            _filteredDerivative = alpha * _filteredDerivative + (1 - alpha) * derivRaw;
            double dTerm = -_g.Kd * _filteredDerivative;

            bool allowIntegral = Math.Abs(e) < _g.IntegralSeparation;
            if (allowIntegral) _integral += _g.Ki * e * _g.Ts;

            double uRaw = _g.Kp * e + _integral + dTerm + ff;
            u = Math.Clamp(uRaw, -_g.OutputLimit, _g.OutputLimit);

            // 条件积分：饱和且误差继续把输出推向同向时撤销本步积分；反算再拉回积分状态。
            if (allowIntegral && Math.Abs(uRaw) > _g.OutputLimit && Math.Sign(e) == Math.Sign(uRaw))
                _integral -= _g.Ki * e * _g.Ts;
            _integral += _g.AntiWindupGain * (u - uRaw) * _g.Ts;

            _prevMeasured = measured;
        }

        Output = u;
        return u;
    }
}

/// <summary>
/// 单轴三环级联伺服：位置环 1 ms → 速度环 0.5 ms → 电流环 0.1 ms，
/// base tick 固定 0.1 ms，与前图文中的周期层级完全一致。
/// 被控对象为简化直线电机：L·di/dt = u - R·i - Ke·v；F=Kf·i；M·dv/dt = F - b·v。
/// </summary>
public sealed class ServoAxis
{
    private const double Mass = 1.0;          // kg（等效直线质量）
    private const double Damping = 0.5;       // N·s/mm
    private const double ForceConst = 20.0;   // N/A
    private const double Resistance = 1.0;    // Ω
    private const double Inductance = 0.001;  // H
    private const double Ke = ForceConst / 1000.0; // V/(mm/s)

    private readonly PidController _position;
    private readonly PidController _velocity;
    private readonly PidController _current;
    private readonly double _dt = 0.0001;     // 0.1 ms 电流环基准周期
    private readonly int _posDivider = 10;    // 1 ms / 0.1 ms
    private readonly int _velDivider = 5;     // 0.5 ms / 0.1 ms
    private long _tick;
    private double _x, _v, _i, _u, _vSet, _iSet;

    public double PositionMm => _x;

    public ServoAxis(double maxFeed)
    {
        _position = new PidController(new PidGains
        {
            Kp = 80, Ki = 200, Kd = 0, Ts = 0.001,
            Kvff = 1.0,                       // 速度前馈直接加到位置环输出
            OutputLimit = maxFeed * 1.5
        });
        _velocity = new PidController(new PidGains
        {
            Kp = 8, Ki = 80, Kd = 0, Ts = 0.0005,
            Kaff = Mass / ForceConst,         // 加速度前馈：M/Kf A/(mm/s²)
            OutputLimit = 35                  // 电流上限 35 A
        });
        _current = new PidController(new PidGains
        {
            Kp = 0.5, Ki = 500, Kd = 0, Ts = 0.0001,
            OutputLimit = 60                  // 母线电压上限 60 V
        });
    }

    /// <summary>每 0.1 ms 调用一次；xCmd/vCmd/aCmd 在插补周期内保持不变。</summary>
    public void Step(double xCmd, double vCmd, double aCmd)
    {
        _tick++;
        if (_tick % _posDivider == 0)
            _vSet = _position.Update(xCmd, _x, vCmd);
        if (_tick % _velDivider == 0)
            _iSet = _velocity.Update(_vSet, _v, accelFeedforward: aCmd);
        _u = _current.Update(_iSet, _i);

        _i = Math.Clamp(_i + ((_u - Resistance * _i - Ke * _v) / Inductance) * _dt, -35, 35);
        double force = ForceConst * _i;
        double acc = (force - Damping * _v) / Mass;
        _v += acc * _dt;
        _x += _v * _dt;
    }
}
