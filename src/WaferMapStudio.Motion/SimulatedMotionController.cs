namespace WaferMapStudio.Motion;

/// <summary>
/// 纯软件运动控制器模拟器。用于无硬件开发与单元测试：
/// - 位置按"运动时长 = 距离 / 速度"模拟推进，可被取消令牌打断；
/// - 状态变化通过接口事件上报，与真机行为一致；
/// - I/O 用位数组模拟。
///
/// 本类刻意不依赖任何厂商 SDK，可跨平台直接单测。
/// </summary>
public sealed partial class SimulatedMotionController : IAdvancedMotionController
{
    private const double DefaultVelocity = 100.0; // 单位/秒

    private readonly object _gate = new();
    private readonly Dictionary<MotionAxis, SimAxis> _axes = new();
    private readonly bool[] _inputs = new bool[64];
    private readonly bool[] _outputs = new bool[64];
    private readonly TimeSpan _tickInterval;

    private MotionConnectionState _connectionState = MotionConnectionState.Disconnected;
    private bool _disposed;

    /// <summary>用默认轴集合（X/Y/Z/θ）创建模拟器。</summary>
    /// <param name="tickInterval">状态轮询间隔，默认 20ms。</param>
    public SimulatedMotionController(TimeSpan? tickInterval = null)
        : this(new[] { MotionAxis.X, MotionAxis.Y, MotionAxis.Z, MotionAxis.Theta }, tickInterval)
    {
    }

    /// <summary>用指定轴集合创建模拟器。</summary>
    public SimulatedMotionController(IEnumerable<MotionAxis> axes, TimeSpan? tickInterval = null)
    {
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(20);
        foreach (MotionAxis axis in axes)
        {
            _axes[axis] = new SimAxis(axis);
        }
    }

    /// <inheritdoc />
    public MotionConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    /// <inheritdoc />
    public IReadOnlySet<MotionAxis> AvailableAxes => _axes.Keys.ToHashSet();

    /// <inheritdoc />
    public event EventHandler<AxisStateChangedEventArgs>? AxisStateChanged;

    /// <inheritdoc />
    public event EventHandler<MotionFaultEventArgs>? Faulted;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <inheritdoc />
    public Task ConnectAsync(MotionConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SetConnectionState(MotionConnectionState.Connecting);
        SetConnectionState(MotionConnectionState.Connected);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetConnectionState(MotionConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EnableAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        SimAxis sim = GetAxis(axis);
        lock (_gate)
        {
            sim.Enable = AxisEnableState.Enabled;
            sim.Motion = AxisMotionState.Idle;
        }
        RaiseAxisState(axis);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisableAxisAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        SimAxis sim = GetAxis(axis);
        lock (_gate)
        {
            sim.Enable = AxisEnableState.Disabled;
            sim.Motion = AxisMotionState.Idle;
        }
        RaiseAxisState(axis);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task HomeAsync(MotionAxis axis, HomingMode mode, CancellationToken cancellationToken = default)
    {
        SimAxis sim = GetAxis(axis);
        EnsureEnabled(sim);
        SetMotionState(axis, AxisMotionState.Homing);

        try
        {
            if (mode != HomingMode.Here)
            {
                await SimulateTravelAsync(sim, 0.0, DefaultVelocity, cancellationToken).ConfigureAwait(false);
            }
            lock (_gate)
            {
                sim.CommandPosition = 0.0;
                sim.FeedbackPosition = 0.0;
            }
        }
        finally
        {
            if (sim.Motion != AxisMotionState.Faulted)
            {
                SetMotionState(axis, AxisMotionState.Idle);
            }
        }
    }

    /// <inheritdoc />
    public async Task MoveAsync(
        MotionAxis axis,
        double position,
        MoveMode mode,
        MotionProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        SimAxis sim = GetAxis(axis);
        EnsureEnabled(sim);
        double target = mode == MoveMode.Relative
            ? sim.FeedbackPosition + position
            : position;

        await MoveCoreAsync(sim, target, profile, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveMultiAsync(
        IReadOnlyDictionary<MotionAxis, double> targets,
        MoveMode mode,
        MotionProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        // 模拟器按最长行程并发推进，模拟"同步启动、各自到位"。
        List<Task> moves = new();
        foreach ((MotionAxis axis, double value) in targets)
        {
            SimAxis sim = GetAxis(axis);
            EnsureEnabled(sim);
            double target = mode == MoveMode.Relative ? sim.FeedbackPosition + value : value;
            moves.Add(MoveCoreAsync(sim, target, profile, cancellationToken));
        }
        await Task.WhenAll(moves).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task JogAsync(
        MotionAxis axis,
        double velocity,
        CancellationToken cancellationToken = default)
    {
        SimAxis sim = GetAxis(axis);
        EnsureEnabled(sim);
        SetMotionState(axis, AxisMotionState.Jogging);
        try
        {
            while (!cancellationToken.IsCancellationRequested && sim.Motion == AxisMotionState.Jogging)
            {
                await Task.Delay(_tickInterval, cancellationToken).ConfigureAwait(false);
                double previous = sim.FeedbackPosition;
                double current;
                lock (_gate)
                {
                    sim.CommandPosition += velocity * _tickInterval.TotalSeconds;
                    sim.FeedbackPosition = sim.CommandPosition;
                    current = sim.FeedbackPosition;
                }
                EvaluatePeg(sim, previous, current);
            }
        }
        catch (OperationCanceledException)
        {
            SetMotionState(axis, AxisMotionState.Idle);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(
        MotionAxis? axis,
        StopMode mode = StopMode.Decelerated,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<MotionAxis> targets = axis.HasValue ? new[] { axis.Value } : _axes.Keys;
        foreach (MotionAxis item in targets)
        {
            SimAxis sim = GetAxis(item);
            if (mode == StopMode.Emergency)
            {
                lock (_gate)
                {
                    sim.Motion = AxisMotionState.Faulted;
                }
                Faulted?.Invoke(this, new MotionFaultEventArgs(item, "ESTOP", "紧急停止"));
            }
            else
            {
                SetMotionState(item, AxisMotionState.Idle);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<AxisStatus> GetStatusAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        SimAxis sim = GetAxis(axis);
        lock (_gate)
        {
            return Task.FromResult(new AxisStatus(
                sim.Axis, sim.Enable, sim.Motion,
                sim.CommandPosition, sim.FeedbackPosition, MotionUnit.Micrometer));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AxisStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default)
    {
        List<AxisStatus> result = new();
        foreach (MotionAxis axis in _axes.Keys)
        {
            result.Add(await GetStatusAsync(axis, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    /// <inheritdoc />
    public Task<bool> ReadInputAsync(int channel, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        lock (_gate)
        {
            return Task.FromResult(_inputs[channel]);
        }
    }

    /// <inheritdoc />
    public Task WriteOutputAsync(int channel, bool value, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        lock (_gate)
        {
            _outputs[channel] = value;
        }
        return Task.CompletedTask;
    }

    /// <summary>测试辅助：设置模拟输入点状态。</summary>
    public void SetSimulatedInput(int channel, bool value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        lock (_gate)
        {
            _inputs[channel] = value;
        }
    }

    /// <summary>测试辅助：读取已写出的输出点状态。</summary>
    public bool GetSimulatedOutput(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        lock (_gate)
        {
            return _outputs[channel];
        }
    }

    private async Task MoveCoreAsync(
        SimAxis sim,
        double target,
        MotionProfile? profile,
        CancellationToken cancellationToken)
    {
        SetMotionState(sim.Axis, AxisMotionState.Moving);
        double velocity = profile?.Velocity ?? DefaultVelocity;
        if (velocity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "速度必须大于 0");
        }

        try
        {
            await SimulateTravelAsync(sim, target, velocity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (sim.Motion != AxisMotionState.Faulted)
            {
                SetMotionState(sim.Axis, AxisMotionState.Idle);
            }
        }
    }

    private async Task SimulateTravelAsync(
        SimAxis sim,
        double target,
        double velocity,
        CancellationToken cancellationToken)
    {
        double start;
        lock (_gate)
        {
            start = sim.FeedbackPosition;
            sim.CommandPosition = target;
        }

        double distance = Math.Abs(target - start);
        double totalSeconds = distance / velocity;
        int steps = Math.Max(1, (int)Math.Ceiling(totalSeconds / _tickInterval.TotalSeconds));

        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(_tickInterval, cancellationToken).ConfigureAwait(false);
            double previous = sim.FeedbackPosition;
            double current;
            lock (_gate)
            {
                sim.FeedbackPosition = start + (target - start) * i / steps;
                current = sim.FeedbackPosition;
            }
            EvaluatePeg(sim, previous, current);
        }

        lock (_gate)
        {
            sim.FeedbackPosition = target;
        }
    }

    private SimAxis GetAxis(MotionAxis axis)
    {
        if (!_axes.TryGetValue(axis, out SimAxis? sim))
        {
            throw new ArgumentException($"轴 {axis} 不在可用轴集合中", nameof(axis));
        }
        return sim;
    }

    private static void EnsureEnabled(SimAxis sim)
    {
        if (sim.Enable != AxisEnableState.Enabled)
        {
            throw new InvalidOperationException($"轴 {sim.Axis} 未使能，无法运动");
        }
    }

    private void SetMotionState(MotionAxis axis, AxisMotionState state)
    {
        lock (_gate)
        {
            _axes[axis].Motion = state;
        }
        RaiseAxisState(axis);
    }

    private void RaiseAxisState(MotionAxis axis)
    {
        AxisMotionState state;
        lock (_gate)
        {
            state = _axes[axis].Motion;
        }
        AxisStateChanged?.Invoke(this, new AxisStateChangedEventArgs(axis, state, DateTimeOffset.UtcNow));
    }

    private void SetConnectionState(MotionConnectionState state)
    {
        lock (_gate)
        {
            _connectionState = state;
        }
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(state));
    }

    private sealed class SimAxis(MotionAxis axis)
    {
        public MotionAxis Axis { get; } = axis;

        public AxisEnableState Enable { get; set; } = AxisEnableState.Disabled;

        public AxisMotionState Motion { get; set; } = AxisMotionState.Idle;

        public double CommandPosition { get; set; }

        public double FeedbackPosition { get; set; }
    }
}
