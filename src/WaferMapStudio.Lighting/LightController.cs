namespace WaferMapStudio.Lighting;

/// <summary>
/// 可复用光源控制器。承载与硬件无关的全部逻辑：
/// 参数校验、连接状态机、亮度缓存、批量意图、事件上报、异常归一化。
///
/// 关键设计：
/// - 只依赖 <see cref="ILightDeviceTransport"/>，不认识任何具体协议；
/// - _lastCommand 缓存每个通道最近一次"意图亮度"，使 SetOnAsync 能在不回读设备的情况下保持亮度；
/// - 所有设备侧原始异常统一翻译成 <see cref="LightControllerException"/> 派生类型，
///   且取消操作原样抛出 OperationCanceledException，便于上层区分"用户取消"与"真故障"。
/// </summary>
public sealed class LightController : ILightController
{
    private readonly ILightDeviceTransport _transport;
    private readonly Dictionary<int, LightChannelCommand> _lastCommand = new();
    // 单独记住"亮度设定值"而非最后一次命令：关灯命令亮度为 0，
    // 若只缓存命令，重新开灯时会把亮度错误地恢复成 0。
    private readonly Dictionary<int, double> _brightnessSetting = new();
    private LightDeviceCapabilities? _capabilities;
    private bool _disposed;

    /// <summary>创建控制器。</summary>
    /// <param name="transport">底层传输实现；真机适配器或模拟器。</param>
    public LightController(ILightDeviceTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _transport.ConnectionStateChanged += OnTransportConnectionStateChanged;
    }

    /// <inheritdoc />
    public LightConnectionState ConnectionState => _transport.ConnectionState;

    /// <inheritdoc />
    public LightDeviceCapabilities? Capabilities => _capabilities;

    /// <inheritdoc />
    public IReadOnlySet<int> AvailableChannels =>
        _capabilities is null
            ? new HashSet<int>()
            : Enumerable.Range(1, _capabilities.ChannelCount).ToHashSet();

    /// <inheritdoc />
    public event EventHandler<LightChannelChangedEventArgs>? ChannelChanged;

    /// <inheritdoc />
    public event EventHandler<LightFaultEventArgs>? Faulted;

    /// <inheritdoc />
    public event EventHandler<LightConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <inheritdoc />
    public async Task ConnectAsync(
        LightConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            await _transport.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            _capabilities = await _transport.ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LightControllerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LightConnectionException($"连接光源控制器 {options.Address} 失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LightConnectionException($"断开光源控制器失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task SetOnAsync(int channel, bool isOn, CancellationToken cancellationToken = default)
    {
        LightDeviceCapabilities caps = RequireCapabilities();
        LightParameterValidator.ValidateChannel(channel, caps);

        // 从亮度设定缓存恢复；从未设置过时默认 50%，避免上电满亮度冲击。
        double brightness = _brightnessSetting.GetValueOrDefault(channel, 50.0);
        var command = new LightChannelCommand(isOn, isOn ? brightness : 0.0);
        await WriteChannelCoreAsync(channel, command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetBrightnessAsync(
        int channel,
        double brightnessPercent,
        LightRampMode ramp = LightRampMode.Immediate,
        int rampMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        // 校验统一在 WriteChannelCoreAsync 内完成，此处只构造命令。
        var command = new LightChannelCommand(
            IsOn: brightnessPercent > 0,
            BrightnessPercent: brightnessPercent,
            Ramp: ramp,
            RampMilliseconds: rampMilliseconds);
        await WriteChannelCoreAsync(channel, command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetIntensityAsync(
        int channel,
        double value,
        LightIntensityUnit unit,
        CancellationToken cancellationToken = default)
    {
        LightDeviceCapabilities caps = RequireCapabilities();
        LightParameterValidator.ValidateChannel(channel, caps);

        double percent = unit switch
        {
            LightIntensityUnit.PercentOfNominal => value,
            LightIntensityUnit.Milliampere => LightParameterValidator.CurrentMaToPercent(value, caps),
            LightIntensityUnit.RawCode => value, // 原始码由适配器按自身 DAC 位宽换算，这里不二次加工
            _ => throw new LightValidationException(nameof(unit), $"不支持的强度单位：{unit}"),
        };

        LightParameterValidator.ValidateBrightnessPercent(percent);
        var command = new LightChannelCommand(IsOn: percent > 0, BrightnessPercent: percent);
        await WriteChannelCoreAsync(channel, command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetBrightnessBatchAsync(
        IReadOnlyDictionary<int, double> brightnessByChannel,
        CancellationToken cancellationToken = default)
    {
        LightDeviceCapabilities caps = RequireCapabilities();
        ArgumentNullException.ThrowIfNull(brightnessByChannel);

        var commands = new Dictionary<int, LightChannelCommand>();
        foreach ((int channel, double percent) in brightnessByChannel)
        {
            var command = new LightChannelCommand(IsOn: percent > 0, BrightnessPercent: percent);
            LightParameterValidator.ValidateCommand(channel, command, caps);
            commands[channel] = command;
        }

        try
        {
            await _transport.SetChannelsAsync(commands, cancellationToken).ConfigureAwait(false);
            foreach ((int channel, LightChannelCommand command) in commands)
            {
                RememberCommand(channel, command);
                RaiseChannelChanged(channel, command);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LightControllerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LightDeviceException(string.Empty, $"批量设置亮度失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task TurnOffAllAsync(CancellationToken cancellationToken = default)
    {
        LightDeviceCapabilities caps = RequireCapabilities();
        try
        {
            await _transport.SetChannelsAsync(
                Enumerable.Range(1, caps.ChannelCount).ToDictionary(
                    channel => channel,
                    channel => new LightChannelCommand(false, 0.0)),
                cancellationToken).ConfigureAwait(false);

            foreach (int channel in Enumerable.Range(1, caps.ChannelCount))
            {
                RememberCommand(channel, new LightChannelCommand(false, 0.0));
                RaiseChannelChanged(channel, _lastCommand[channel]);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LightControllerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LightDeviceException(string.Empty, $"关闭全部光源失败：{ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<LightChannelStatus> ReadChannelAsync(
        int channel,
        CancellationToken cancellationToken = default)
    {
        LightDeviceCapabilities caps = RequireCapabilities();
        LightParameterValidator.ValidateChannel(channel, caps);
        LightChannelStatus status;
        try
        {
            status = await _transport.ReadChannelAsync(channel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LightControllerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LightDeviceException(string.Empty, $"读取通道 {channel} 状态失败：{ex.Message}", ex);
        }

        InspectFault(channel, status);
        RememberCommand(
            channel,
            new LightChannelCommand(status.State == LightChannelState.On, status.BrightnessPercent));
        return status;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LightChannelStatus>> ReadAllChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        _ = RequireCapabilities();
        IReadOnlyList<LightChannelStatus> statuses;
        try
        {
            statuses = await _transport.ReadAllChannelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LightControllerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LightDeviceException(string.Empty, $"批量读取光源状态失败：{ex.Message}", ex);
        }

        foreach (LightChannelStatus status in statuses)
        {
            InspectFault(status.Channel, status);
        }

        return statuses;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _transport.ConnectionStateChanged -= OnTransportConnectionStateChanged;
        await _transport.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task WriteChannelCoreAsync(
        int channel,
        LightChannelCommand command,
        CancellationToken cancellationToken)
    {
        // 校验（ValidateCommand 内部已含通道检查）必须在任何设备访问之前完成。
        LightParameterValidator.ValidateCommand(channel, command, RequireCapabilities());
        try
        {
            await _transport.SetChannelAsync(channel, command, cancellationToken).ConfigureAwait(false);
            RememberCommand(channel, command);
            RaiseChannelChanged(channel, command);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LightControllerException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LightDeviceException(string.Empty, $"设置通道 {channel} 失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 记录命令并同步亮度设定：开灯命令携带的亮度是"用户意图"，会被记住供下次开灯恢复；
    /// 关灯命令（亮度 0）不覆盖设定值，因此开关切换不会丢失亮度。
    /// </summary>
    private void RememberCommand(int channel, LightChannelCommand command)
    {
        _lastCommand[channel] = command;
        if (command.IsOn)
        {
            _brightnessSetting[channel] = command.BrightnessPercent;
        }
    }

    private LightDeviceCapabilities RequireCapabilities()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _capabilities
            ?? throw new LightConnectionException("尚未连接光源控制器，请先调用 ConnectAsync");
    }

    private void InspectFault(int channel, LightChannelStatus status)
    {
        if (status.State == LightChannelState.Fault || !string.IsNullOrEmpty(status.FaultCode))
        {
            Faulted?.Invoke(this, new LightFaultEventArgs(
                channel,
                status.FaultCode ?? "UNKNOWN",
                $"通道 {channel} 故障：{status.FaultCode ?? "未知"}"));
        }
    }

    private void RaiseChannelChanged(int channel, LightChannelCommand command)
    {
        var status = new LightChannelStatus(
            channel,
            command.IsOn ? LightChannelState.On : LightChannelState.Off,
            command.IsOn ? command.BrightnessPercent : 0.0,
            MeasuredCurrentMa: null,
            FaultCode: null,
            TimestampUtc: DateTimeOffset.UtcNow);
        ChannelChanged?.Invoke(this, new LightChannelChangedEventArgs(channel, status));
    }

    private void OnTransportConnectionStateChanged(object? sender, LightConnectionChangedEventArgs e)
    {
        if (e.State != LightConnectionState.Connected)
        {
            _capabilities = null; // 断链后能力信息失效，下次操作必须重新连接
        }

        ConnectionStateChanged?.Invoke(this, e);
    }
}
