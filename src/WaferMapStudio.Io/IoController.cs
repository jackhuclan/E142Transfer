namespace WaferMapStudio.Io;

/// <summary>
/// 传输无关的通用 IO 控制器。承载与硬件无关的全部逻辑：
/// 连接状态机、型号校验、点位校验、输入镜像与去抖、变化沿事件、批量写与回读确认、
/// 后台轮询、异常归一化、安全态与资源释放。
///
/// 关键设计（对应文档 19 的七项要求）：
/// - 只依赖 <see cref="IIoTransport"/>，不认识任何协议；
/// - 输入镜像 _inputFrame 是唯一读源，业务 Read 零 IO；轮询只刷新它并上报变化；
/// - 去抖按"连续 N 帧一致才认"，N=1 时等价于不去抖，方便测试；
/// - 输出写采用 read-modify-write + 回读确认，关键输出（阀/光源）真实生效才返回；
/// - 轮询循环内异常只经 Faulted 上报，绝不退出（否则设备静默失明）；
/// - 初始化与 DisposeAsync 都把全部输出置安全态（断使能/关灯/关阀）。
/// </summary>
public sealed class IoController : IIoController
{
    private readonly IIoTransport _transport;
    private readonly object _gate = new();
    private readonly int _debounceFrames;

    private IoPointMap? _pointMap;
    private byte[] _inputFrame = [];
    private byte[] _outputFrame = [];
    private readonly Dictionary<IoPoint, bool> _stableValues = new();
    private readonly Dictionary<IoPoint, int> _debounceCounters = new();
    private IoConnectionState _connectionState = IoConnectionState.Disconnected;

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _disposed;

    /// <summary>创建控制器。</summary>
    /// <param name="transport">底层传输；真机适配器或模拟器。</param>
    /// <param name="debounceFrames">连续多少帧一致才认为输入变化；1 表示不去抖。</param>
    public IoController(IIoTransport transport, int debounceFrames = 1)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (debounceFrames < 1) throw new ArgumentOutOfRangeException(nameof(debounceFrames));
        _transport = transport;
        _debounceFrames = debounceFrames;
        _transport.ConnectionStateChanged += OnTransportConnectionStateChanged;
    }

    /// <inheritdoc />
    public IoConnectionState ConnectionState
    {
        get { lock (_gate) return _connectionState; }
    }

    /// <inheritdoc />
    public IoPointMap? PointMap => _pointMap;

    /// <inheritdoc />
    public event EventHandler<IoChangedEventArgs>? InputChanged;

    /// <inheritdoc />
    public event EventHandler<IoFaultEventArgs>? Faulted;

    /// <inheritdoc />
    public event EventHandler<IoConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <inheritdoc />
    public async Task InitializeAsync(
        IoConnectionOptions options,
        IoPointMap pointMap,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pointMap);

        try
        {
            SetConnectionState(IoConnectionState.Connecting);
            IoDeviceIdentity identity = await _transport
                .ConnectAsync(options, cancellationToken)
                .ConfigureAwait(false);

            // 型号校验：期望值非空时才比，且大小写不敏感
            if (!string.IsNullOrWhiteSpace(options.ExpectedModel) &&
                !string.Equals(identity.Model, options.ExpectedModel, StringComparison.OrdinalIgnoreCase))
                throw new IoModelMismatchException(identity.Model, options.ExpectedModel);

            // 帧长度以设备身份为准，必须与点表声明的 Bank 数一致，否则会地址错位
            if (identity.InputBankCount < pointMap.InputBankCount ||
                identity.OutputBankCount < pointMap.OutputBankCount)
                throw new IoConnectionException(
                    $"模块帧长度不足：设备输入/输出 Bank={identity.InputBankCount}/{identity.OutputBankCount}，" +
                    $"点表要求 {pointMap.InputBankCount}/{pointMap.OutputBankCount}");

            _pointMap = pointMap;
            lock (_gate)
            {
                _inputFrame = new byte[identity.InputBankCount];
                _outputFrame = new byte[identity.OutputBankCount];
                _stableValues.Clear();
                _debounceCounters.Clear();
            }

            // 建初始输入镜像（不触发事件：初始化不算"变化"）
            byte[] frame = await _transport.ReadInputsAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) Array.Copy(frame, _inputFrame, Math.Min(frame.Length, _inputFrame.Length));
            SeedStableValues();

            // 输出先归安全态，杜绝上电残留导致误动作
            await ApplySafeOutputsAsync(cancellationToken).ConfigureAwait(false);

            SetConnectionState(IoConnectionState.Ready);
        }
        catch (OperationCanceledException) { throw; }
        catch (IoControllerException)
        {
            SetConnectionState(IoConnectionState.Faulted);
            throw;
        }
        catch (Exception ex)
        {
            SetConnectionState(IoConnectionState.Faulted);
            throw new IoConnectionException($"IO 初始化失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopMonitoringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplySafeOutputsAsync(cancellationToken).ConfigureAwait(false);
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            SetConnectionState(IoConnectionState.Disconnected);
        }
        catch (OperationCanceledException) { throw; }
        catch (IoControllerException) { throw; }
        catch (Exception ex)
        {
            SetConnectionState(IoConnectionState.Faulted);
            throw new IoConnectionException($"断开 IO 失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadInputsAsync(CancellationToken cancellationToken = default)
    {
        IoPointMap map = RequireReady();
        try
        {
            byte[] frame = await _transport.ReadInputsAsync(cancellationToken).ConfigureAwait(false);
            if (frame.Length < map.InputBankCount)
                throw new IoConnectionException($"读到的输入帧长度 {frame.Length} 小于点表要求 {map.InputBankCount}");

            byte[] snapshot;
            List<(IoPoint Point, bool Value)> changes = [];
            lock (_gate)
            {
                Array.Copy(frame, _inputFrame, map.InputBankCount);
                DetectChanges(map, changes); // 去抖 + 变化沿
                snapshot = (byte[])_inputFrame.Clone();
            }

            foreach ((IoPoint point, bool value) in changes)
                InputChanged?.Invoke(this, new IoChangedEventArgs(point, value, DateTimeOffset.UtcNow));

            return snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (IoControllerException) { throw; }
        catch (Exception ex)
        {
            throw new IoConnectionException($"读取输入失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public bool Read(IoPoint point)
    {
        IoPointMap map = RequireReady();
        lock (_gate)
            return _stableValues.TryGetValue(point, out bool value)
                ? value
                : map.Decode(point, _inputFrame);
    }

    /// <inheritdoc />
    public async Task WriteAsync(IoPoint point, bool value, CancellationToken cancellationToken = default)
    {
        IoPointMap map = RequireReady();
        IoPointDef def = map.Require(point);
        if (def.Direction != IoDirection.Output)
            throw new IoValidationException($"点位 {point} 是输入点，不可写");

        bool physical = map.EncodeOutput(point, value);
        byte mask = (byte)(1 << def.Bit);
        byte bitValue = physical ? mask : (byte)0;

        try
        {
            // read-modify-write：传输层也做一次，这里先算出整字节目标值供回读比对
            lock (_gate) _outputFrame[def.Bank] = (byte)((_outputFrame[def.Bank] & ~mask) | bitValue);
            await _transport.WriteBankAsync(def.Bank, mask, bitValue, cancellationToken).ConfigureAwait(false);

            await VerifyOutputAsync(map, point, value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IoControllerException) { throw; }
        catch (Exception ex)
        {
            throw new IoConnectionException($"写点位 {point} 失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task WriteBatchAsync(
        IReadOnlyDictionary<IoPoint, bool> values,
        CancellationToken cancellationToken = default)
    {
        IoPointMap map = RequireReady();
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return;

        // 先做全量校验，避免写了一半才发现有非法点
        var resolved = new List<(IoPointDef Def, bool Physical)>();
        foreach ((IoPoint point, bool logical) in values)
        {
            IoPointDef def = map.Require(point);
            if (def.Direction != IoDirection.Output)
                throw new IoValidationException($"点位 {point} 是输入点，不可批量写");
            resolved.Add((def, map.EncodeOutput(point, logical)));
        }

        // 按 Bank 合并掩码与值，一字节一次下发
        var byBank = new Dictionary<int, (byte Mask, byte Value)>();
        lock (_gate)
        {
            foreach ((IoPointDef def, bool physical) in resolved)
            {
                byte mask = (byte)(1 << def.Bit);
                byte bitValue = physical ? mask : (byte)0;
                _outputFrame[def.Bank] = (byte)((_outputFrame[def.Bank] & ~mask) | bitValue);
                byBank.TryGetValue(def.Bank, out var acc);
                byBank[def.Bank] = ((byte)(acc.Mask | mask), (byte)(acc.Value | bitValue));
            }
        }

        try
        {
            foreach ((int bank, (byte mask, byte value)) in byBank.OrderBy(kv => kv.Key))
                await _transport.WriteBankAsync(bank, mask, value, cancellationToken).ConfigureAwait(false);

            foreach ((IoPoint point, bool logical) in values)
                await VerifyOutputAsync(map, point, logical, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (IoControllerException) { throw; }
        catch (Exception ex)
        {
            throw new IoConnectionException($"批量写 IO 失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public Task StartMonitoringAsync(TimeSpan pollInterval, CancellationToken cancellationToken = default)
    {
        IoPointMap map = RequireReady();
        if (pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 重复调用先停旧循环，避免两个轮询同时刷新镜像
        StopMonitoringAsync().GetAwaiter().GetResult();

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorCts = linked;
        _monitorTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(pollInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
                {
                    try { await ReadInputsAsync(linked.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        // 轮询故障只上报，不退出循环，也不改 Ready 状态
                        Faulted?.Invoke(this, new IoFaultEventArgs("POLL", $"轮询读取失败：{ex.Message}", ex));
                    }
                }
            }
            catch (OperationCanceledException) { /* 正常停止 */ }
        }, linked.Token);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopMonitoringAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_gate)
        {
            cts = _monitorCts;
            task = _monitorTask;
            _monitorCts = null;
            _monitorTask = null;
        }
        if (cts is null) return;

        await cts.CancelAsync().ConfigureAwait(false);
        if (task is not null)
        {
            try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 预期：循环被取消 */ }
        }
        cts.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.ConnectionStateChanged -= OnTransportConnectionStateChanged;

        await StopMonitoringAsync().ConfigureAwait(false);
        try { await ApplySafeOutputsAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* 释放阶段不阻断，安全态失败已由 Faulted 语义覆盖 */ }
        await _transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    // ---- 内部辅助 ----

    private IoPointMap RequireReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ConnectionState != IoConnectionState.Ready || _pointMap is null)
            throw new IoConnectionException($"IO 控制器未就绪（当前状态 {ConnectionState}），请先 InitializeAsync");
        return _pointMap;
    }

    private void SeedStableValues()
    {
        IoPointMap map = _pointMap!;
        _stableValues.Clear();
        _debounceCounters.Clear();
        foreach (IoPointDef d in map.Points.Where(p => p.Direction == IoDirection.Input))
        {
            bool v = map.Decode(d.Point, _inputFrame);
            _stableValues[d.Point] = v;
            _debounceCounters[d.Point] = 0;
        }
    }

    /// <summary>
    /// 去抖并产出变化沿。调用方持有 _gate 锁。
    /// 规则：连续 _debounceFrames 帧原始值一致才更新稳定值；只有稳定值改变才算变化。
    /// </summary>
    private void DetectChanges(IoPointMap map, List<(IoPoint, bool)> changes)
    {
        foreach (IoPointDef d in map.Points.Where(p => p.Direction == IoDirection.Input))
        {
            bool raw = map.Decode(d.Point, _inputFrame);
            bool stable = _stableValues[d.Point];
            if (raw == stable)
            {
                _debounceCounters[d.Point] = 0;
                continue;
            }

            int count = _debounceCounters.GetValueOrDefault(d.Point) + 1;
            _debounceCounters[d.Point] = count;
            if (count >= _debounceFrames)
            {
                _stableValues[d.Point] = raw;
                _debounceCounters[d.Point] = 0;
                changes.Add((d.Point, raw));
            }
        }
    }

    private async Task VerifyOutputAsync(
        IoPointMap map, IoPoint point, bool expectedLogical, CancellationToken ct)
    {
        byte[] echo = await _transport.ReadOutputsAsync(ct).ConfigureAwait(false);
        bool actualLogical = map.DecodeOutput(point, echo);
        if (actualLogical != expectedLogical)
            throw new IoWriteVerifyException(point, expectedLogical, actualLogical);
    }

    private async Task ApplySafeOutputsAsync(CancellationToken ct)
    {
        IoPointMap? map = _pointMap;
        if (map is null) return;

        var byBank = new Dictionary<int, (byte Mask, byte Value)>();
        lock (_gate)
        {
            Array.Clear(_outputFrame);
            foreach ((IoPoint point, bool physical) in map.SafeOutputStates())
            {
                IoPointDef d = map.Require(point);
                byte mask = (byte)(1 << d.Bit);
                byte bitValue = physical ? mask : (byte)0;
                byBank.TryGetValue(d.Bank, out var acc);
                byBank[d.Bank] = ((byte)(acc.Mask | mask), (byte)(acc.Value | bitValue));
            }
        }

        foreach ((int bank, (byte mask, byte value)) in byBank.OrderBy(kv => kv.Key))
            await _transport.WriteBankAsync(bank, mask, value, ct).ConfigureAwait(false);
    }

    private void SetConnectionState(IoConnectionState state)
    {
        IoConnectionState old;
        lock (_gate)
        {
            old = _connectionState;
            _connectionState = state;
        }
        if (old != state) ConnectionStateChanged?.Invoke(this, new IoConnectionChangedEventArgs(state));
    }

    private void OnTransportConnectionStateChanged(object? sender, IoConnectionChangedEventArgs e)
    {
        // 传输层掉线同步到控制器状态；上层订阅同一个事件即可感知
        SetConnectionState(e.State);
    }
}
