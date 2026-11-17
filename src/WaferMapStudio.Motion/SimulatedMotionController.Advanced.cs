namespace WaferMapStudio.Motion;

/// <summary>
/// 模拟器的高级能力实现（PEG / History Buffer / ACSPL+ 程序与变量）。
/// 拆到独立 partial 文件，保持基础运动逻辑与扫描时序逻辑各自清晰。
///
/// 实现要点：
/// - PEG：维护每轴的"下一个触发位置"，在每次位置推进后判断区间是否跨越，
///   覆盖 Distance 与 PositionList 两种模式，并尊重 Count 上限；
/// - History：后台按采样周期把轴位置写入定容环形缓冲，溢出计数；
/// - 程序：轻量状态机（加载→运行→完成），不真正解释 ACSPL+ 源码。
/// </summary>
public sealed partial class SimulatedMotionController
{
    private readonly Dictionary<MotionAxis, PegRuntime> _pegRuntimes = new();
    private readonly Dictionary<string, ProgramRuntime> _programs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _variables = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _historyGate = new();
    private Queue<HistorySample> _history = new();
    private int _historyCapacity;
    private int _droppedSamples;
    private DataCollectionConfig? _collectionConfig;
    private CancellationTokenSource? _collectionCts;
    private Task? _collectionTask;

    /// <inheritdoc />
    public event EventHandler<PegTriggeredEventArgs>? PegTriggered;

    // ===== PEG =====

    /// <inheritdoc />
    public Task ConfigurePegAsync(PegConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        SimAxis sim = GetAxis(config.Axis);
        if (config.Mode == PegMode.Distance && config.Distance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "Distance 模式的间隔必须大于 0");
        }
        if (config.Count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "触发次数不能为负");
        }

        lock (_gate)
        {
            _pegRuntimes[config.Axis] = new PegRuntime(config, sim.FeedbackPosition);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StartPegAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        PegRuntime runtime = GetPegRuntime(axis);
        // 从当前位置重新锚定，避免停止期间的位置漂移计入触发间隔。
        double current;
        lock (_gate)
        {
            current = _axes[axis].FeedbackPosition;
        }
        runtime.Start(current);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopPegAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        GetPegRuntime(axis).IsEnabled = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PegStatus> GetPegStatusAsync(MotionAxis axis, CancellationToken cancellationToken = default)
    {
        PegRuntime runtime = GetPegRuntime(axis);
        lock (_gate)
        {
            return Task.FromResult(new PegStatus(
                axis, runtime.IsEnabled, runtime.TriggerCount, runtime.Config));
        }
    }

    // ===== History Buffer =====

    /// <inheritdoc />
    public Task StartDataCollectionAsync(DataCollectionConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "缓冲区容量必须大于 0");
        }
        foreach (MotionAxis axis in config.Axes)
        {
            GetAxis(axis);
        }

        StopCollectionUnsafe();

        lock (_historyGate)
        {
            _history = new Queue<HistorySample>();
            _historyCapacity = config.Capacity;
            _droppedSamples = 0;
        }

        _collectionConfig = config;
        _collectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _collectionTask = Task.Run(() => CollectAsync(_collectionCts.Token), _collectionCts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopDataCollectionAsync(CancellationToken cancellationToken = default)
    {
        StopCollectionUnsafe();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<HistoryData> ReadHistoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_historyGate)
        {
            return Task.FromResult(new HistoryData(_history.ToArray(), _droppedSamples));
        }
    }

    /// <inheritdoc />
    public Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_historyGate)
        {
            _history.Clear();
            _droppedSamples = 0;
        }
        return Task.CompletedTask;
    }

    // ===== ACSPL+ 程序与变量 =====

    /// <inheritdoc />
    public Task LoadProgramAsync(string name, string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            _programs[name] = new ProgramRuntime(name, source);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RunProgramAsync(string name, CancellationToken cancellationToken = default)
    {
        ProgramRuntime program = GetProgram(name);
        lock (_gate)
        {
            if (program.State is AcsplProgramState.Running or AcsplProgramState.Paused)
            {
                throw new InvalidOperationException($"程序 {name} 已在运行");
            }
            program.State = AcsplProgramState.Running;
            program.CurrentLine = 0;
        }
        // 模拟器按程序行数估算一个很短的执行时间，随后标记完成。
        _ = Task.Run(async () =>
        {
            int lines = program.Source.Split('\n').Length;
            for (int i = 1; i <= lines; i++)
            {
                await Task.Delay(_tickInterval, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    program.CurrentLine = i;
                }
            }
            lock (_gate)
            {
                if (program.State == AcsplProgramState.Running)
                {
                    program.State = AcsplProgramState.Completed;
                }
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopProgramAsync(string name, CancellationToken cancellationToken = default)
    {
        ProgramRuntime program = GetProgram(name);
        lock (_gate)
        {
            program.State = AcsplProgramState.Loaded;
            program.CurrentLine = 0;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ProgramInfo> GetProgramInfoAsync(string name, CancellationToken cancellationToken = default)
    {
        ProgramRuntime program = GetProgram(name);
        lock (_gate)
        {
            return Task.FromResult(new ProgramInfo(program.Name, program.State, program.CurrentLine));
        }
    }

    /// <inheritdoc />
    public Task<double> ReadVariableAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            return Task.FromResult(_variables.TryGetValue(name, out double value) ? value : 0.0);
        }
    }

    /// <inheritdoc />
    public Task WriteVariableAsync(string name, double value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            _variables[name] = value;
        }
        return Task.CompletedTask;
    }

    // ===== 内部辅助 =====

    /// <summary>在位置从 previous 推进到 current 后，判断 PEG 是否跨越触发点。</summary>
    private void EvaluatePeg(SimAxis sim, double previous, double current)
    {
        PegRuntime? runtime;
        lock (_gate)
        {
            if (!_pegRuntimes.TryGetValue(sim.Axis, out runtime) || !runtime.IsEnabled)
            {
                return;
            }
        }

        if (runtime.Config.Count > 0 && runtime.TriggerCount >= runtime.Config.Count)
        {
            return;
        }

        bool triggered = false;
        double triggerPosition = current;
        lock (_gate)
        {
            while (runtime.HasPendingTrigger() && Crossed(runtime.NextTriggerPosition, previous, current))
            {
                triggerPosition = runtime.NextTriggerPosition;
                runtime.AdvanceTrigger();
                runtime.TriggerCount++;
                triggered = true;
                if (runtime.Config.Count > 0 && runtime.TriggerCount >= runtime.Config.Count)
                {
                    break;
                }
            }
        }

        if (triggered)
        {
            PegTriggered?.Invoke(this, new PegTriggeredEventArgs(
                sim.Axis, runtime.TriggerCount, triggerPosition, DateTimeOffset.UtcNow));
        }
    }

    private static bool Crossed(double threshold, double previous, double current)
    {
        // 同时覆盖正向与反向跨越；相等时只触发一次由 AdvanceTrigger 推进避免重复。
        return (previous < threshold && current >= threshold)
            || (previous > threshold && current <= threshold);
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        DataCollectionConfig config = _collectionConfig!;
        using PeriodicTimer timer = new(config.SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Dictionary<MotionAxis, double> command = new();
                Dictionary<MotionAxis, double> feedback = new();
                lock (_gate)
                {
                    foreach (MotionAxis axis in config.Axes)
                    {
                        if (!_axes.TryGetValue(axis, out SimAxis? sim))
                        {
                            continue;
                        }
                        if (config.Source is HistorySource.CommandPosition or HistorySource.Both)
                        {
                            command[axis] = sim.CommandPosition;
                        }
                        if (config.Source is HistorySource.FeedbackPosition or HistorySource.Both)
                        {
                            feedback[axis] = sim.FeedbackPosition;
                        }
                    }
                }

                HistorySample sample = new(DateTimeOffset.UtcNow, command, feedback);
                lock (_historyGate)
                {
                    if (_history.Count >= _historyCapacity)
                    {
                        _history.Dequeue();
                        _droppedSamples++;
                    }
                    _history.Enqueue(sample);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止，无需处理。
        }
    }

    private void StopCollectionUnsafe()
    {
        _collectionCts?.Cancel();
        _collectionCts?.Dispose();
        _collectionCts = null;
        _collectionTask = null;
        _collectionConfig = null;
    }

    private PegRuntime GetPegRuntime(MotionAxis axis)
    {
        lock (_gate)
        {
            if (!_pegRuntimes.TryGetValue(axis, out PegRuntime? runtime))
            {
                throw new InvalidOperationException($"轴 {axis} 尚未配置 PEG");
            }
            return runtime;
        }
    }

    private ProgramRuntime GetProgram(string name)
    {
        lock (_gate)
        {
            if (!_programs.TryGetValue(name, out ProgramRuntime? program))
            {
                throw new InvalidOperationException($"程序 {name} 未加载");
            }
            return program;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        StopCollectionUnsafe();
        _disposed = true;
        SetConnectionState(MotionConnectionState.Disconnected);
        return ValueTask.CompletedTask;
    }

    private sealed class PegRuntime(PegConfig config, double anchor)
    {
        public PegConfig Config { get; } = config;

        public bool IsEnabled { get; set; }

        public int TriggerCount { get; set; }

        private double Anchor { get; set; } = anchor;

        private int PositionIndex { get; set; }

        /// <summary>下一个待触发的绝对位置。</summary>
        public double NextTriggerPosition { get; private set; }

        public void Start(double currentPosition)
        {
            Anchor = currentPosition;
            PositionIndex = 0;
            IsEnabled = true;
            RefreshNextTrigger();
        }

        public bool HasPendingTrigger() => Config.Mode == PegMode.Distance
            || (Config.Positions is not null && PositionIndex < Config.Positions.Count);

        public void AdvanceTrigger()
        {
            if (Config.Mode == PegMode.Distance)
            {
                Anchor = NextTriggerPosition;
                RefreshNextTrigger();
            }
            else
            {
                PositionIndex++;
                RefreshNextTrigger();
            }
        }

        private void RefreshNextTrigger()
        {
            NextTriggerPosition = Config.Mode == PegMode.Distance
                ? Anchor + Config.Distance
                : Config.Positions is not null && PositionIndex < Config.Positions.Count
                    ? Config.Positions[PositionIndex]
                    : double.PositiveInfinity;
        }
    }

    private sealed class ProgramRuntime(string name, string source)
    {
        public string Name { get; } = name;

        public string Source { get; } = source;

        public AcsplProgramState State { get; set; } = AcsplProgramState.Loaded;

        public int CurrentLine { get; set; }
    }
}
