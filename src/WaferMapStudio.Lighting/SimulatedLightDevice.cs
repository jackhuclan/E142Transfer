namespace WaferMapStudio.Lighting;

/// <summary>
/// 软件模拟的光源设备，实现 <see cref="ILightDeviceTransport"/>。
///
/// 用途：
/// 1. 无硬件单测 —— 验证 LightController 的全部行为；
/// 2. 适配器编写范本 —— 展示"中性命令 → 设备状态 → 状态快照"的映射方式；
/// 3. 故障注入测试 —— <see cref="FailNextWrite"/> / <see cref="InjectFault"/>。
///
/// 注意：模拟器内部也做基本边界检查，但真正的安全校验在 LightController，
/// 这正是分层设计的目的——即便某个适配器疏于校验，上层仍有一道防线。
/// </summary>
public sealed class SimulatedLightDevice : ILightDeviceTransport
{
    private readonly Dictionary<int, LightChannelCommand> _channels = new();
    private readonly Dictionary<int, string?> _faults = new();
    private readonly LightDeviceCapabilities _capabilities;
    private LightConnectionState _connectionState = LightConnectionState.Disconnected;
    private bool _disposed;

    /// <summary>创建模拟器。</summary>
    /// <param name="channelCount">通道数。</param>
    /// <param name="maxCurrentMa">单通道额定电流。</param>
    /// <param name="supportsRamp">是否模拟支持亮度过渡。</param>
    public SimulatedLightDevice(
        int channelCount = 4,
        double maxCurrentMa = 500.0,
        bool supportsRamp = false)
    {
        _capabilities = new LightDeviceCapabilities(
            Model: "SIM-LED",
            SerialNumber: "SIM-0001",
            FirmwareVersion: "1.0.0",
            ChannelCount: channelCount,
            MaxCurrentMa: maxCurrentMa,
            SupportsCurrentReadback: true,
            SupportsRamp: supportsRamp);

        for (int channel = 1; channel <= channelCount; channel++)
        {
            _channels[channel] = new LightChannelCommand(false, 0.0);
        }
    }

    /// <summary>令下一次写操作抛出指定异常，用于测试控制器的异常归一化。</summary>
    public Exception? FailNextWrite { get; set; }

    /// <inheritdoc />
    public LightConnectionState ConnectionState => _connectionState;

    /// <inheritdoc />
    public event EventHandler<LightConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <inheritdoc />
    public Task ConnectAsync(LightConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetState(LightConnectionState.Connecting);
        SetState(LightConnectionState.Connected);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(LightConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LightDeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return Task.FromResult(_capabilities);
    }

    /// <inheritdoc />
    public Task SetChannelAsync(
        int channel,
        LightChannelCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();
        ThrowIfFailInjected();
        EnsureChannelExists(channel);
        _channels[channel] = command;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetChannelsAsync(
        IReadOnlyDictionary<int, LightChannelCommand> commands,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        ThrowIfFailInjected();
        foreach ((int channel, LightChannelCommand command) in commands)
        {
            EnsureChannelExists(channel);
            _channels[channel] = command;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<LightChannelStatus> ReadChannelAsync(
        int channel,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        EnsureChannelExists(channel);
        return Task.FromResult(BuildStatus(channel));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LightChannelStatus>> ReadAllChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        IReadOnlyList<LightChannelStatus> statuses = _channels.Keys
            .OrderBy(channel => channel)
            .Select(BuildStatus)
            .ToList();
        return Task.FromResult(statuses);
    }

    /// <summary>注入通道故障，随后读取该通道会得到 Fault 状态与故障码。</summary>
    public void InjectFault(int channel, string? code)
    {
        EnsureChannelExists(channel);
        _faults[channel] = code;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            SetState(LightConnectionState.Disconnected);
        }

        return ValueTask.CompletedTask;
    }

    private LightChannelStatus BuildStatus(int channel)
    {
        LightChannelCommand command = _channels[channel];
        string? faultCode = _faults.GetValueOrDefault(channel);
        LightChannelState state = faultCode is not null
            ? LightChannelState.Fault
            : command.IsOn ? LightChannelState.On : LightChannelState.Off;
        double? currentMa = _capabilities.SupportsCurrentReadback
            ? command.BrightnessPercent / 100.0 * _capabilities.MaxCurrentMa
            : null;
        return new LightChannelStatus(
            channel,
            state,
            command.IsOn ? command.BrightnessPercent : 0.0,
            currentMa,
            faultCode,
            DateTimeOffset.UtcNow);
    }

    private void EnsureConnected()
    {
        if (_connectionState != LightConnectionState.Connected)
        {
            throw new InvalidOperationException("模拟设备尚未连接");
        }
    }

    private void EnsureChannelExists(int channel)
    {
        if (!_channels.ContainsKey(channel))
        {
            throw new LightValidationException(nameof(channel), $"通道 {channel} 不存在");
        }
    }

    private void ThrowIfFailInjected()
    {
        if (FailNextWrite is not null)
        {
            Exception failure = FailNextWrite;
            FailNextWrite = null;
            throw failure;
        }
    }

    private void SetState(LightConnectionState state)
    {
        _connectionState = state;
        ConnectionStateChanged?.Invoke(this, new LightConnectionChangedEventArgs(state));
    }
}
