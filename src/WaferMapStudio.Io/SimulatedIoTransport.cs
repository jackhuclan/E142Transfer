namespace WaferMapStudio.Io;

/// <summary>
/// 纯软件 IO 传输模拟器。用字节帧模拟现场模块，可注入型号、帧长度与故障，便于无硬件单测。
/// 不依赖任何厂商 SDK，跨平台可用。
/// </summary>
public sealed class SimulatedIoTransport : IIoTransport
{
    private readonly object _gate = new();
    private byte[] _inputs;
    private byte[] _outputs;
    private bool _disposed;

    /// <summary>模拟的模块型号。</summary>
    public string Model { get; set; } = "SIM-IO-64";

    /// <summary>模拟的固件版本。</summary>
    public string Firmware { get; set; } = "1.0.0";

    /// <summary>模拟的输入字节组数。</summary>
    public int InputBankCount { get; }

    /// <summary>模拟的输出字节组数。</summary>
    public int OutputBankCount { get; }

    /// <summary>注入：读输入时抛出的异常；null 表示正常。</summary>
    public Exception? ReadInputFault { get; set; }

    /// <summary>注入：写字节组时抛出的异常；null 表示正常。</summary>
    public Exception? WriteFault { get; set; }

    /// <inheritdoc />
    public IoConnectionState ConnectionState { get; private set; } = IoConnectionState.Disconnected;

    /// <inheritdoc />
    public event EventHandler<IoConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>用指定帧长度创建模拟器。</summary>
    public SimulatedIoTransport(int inputBankCount = 4, int outputBankCount = 4)
    {
        if (inputBankCount <= 0) throw new ArgumentOutOfRangeException(nameof(inputBankCount));
        if (outputBankCount <= 0) throw new ArgumentOutOfRangeException(nameof(outputBankCount));
        InputBankCount = inputBankCount;
        OutputBankCount = outputBankCount;
        _inputs = new byte[inputBankCount];
        _outputs = new byte[outputBankCount];
    }

    /// <inheritdoc />
    public Task<IoDeviceIdentity> ConnectAsync(
        IoConnectionOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        SetState(IoConnectionState.Connecting);
        SetState(IoConnectionState.Ready);
        return Task.FromResult(new IoDeviceIdentity(Model, Firmware, InputBankCount, OutputBankCount));
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(IoConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<byte[]> ReadInputsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadInputFault is not null) throw ReadInputFault;
        lock (_gate) return Task.FromResult((byte[])_inputs.Clone());
    }

    /// <inheritdoc />
    public Task<byte[]> ReadOutputsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult((byte[])_outputs.Clone());
    }

    /// <inheritdoc />
    public Task WriteBankAsync(int bank, byte mask, byte value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteFault is not null) throw WriteFault;
        lock (_gate)
        {
            if (bank < 0 || bank >= _outputs.Length)
                throw new ArgumentOutOfRangeException(nameof(bank), $"输出 Bank={bank} 越界");
            // read-modify-write：只改 mask 指定的位
            _outputs[bank] = (byte)((_outputs[bank] & ~mask) | (value & mask));
        }
        return Task.CompletedTask;
    }

    /// <summary>测试辅助：直接置位某个输入 Bank 的原始字节。</summary>
    public void SetInputBank(int bank, byte value)
    {
        lock (_gate)
        {
            if (bank < 0 || bank >= _inputs.Length)
                throw new ArgumentOutOfRangeException(nameof(bank));
            _inputs[bank] = value;
        }
    }

    /// <summary>测试辅助：读取某个输出 Bank 的原始字节。</summary>
    public byte GetOutputBank(int bank)
    {
        lock (_gate)
        {
            if (bank < 0 || bank >= _outputs.Length)
                throw new ArgumentOutOfRangeException(nameof(bank));
            return _outputs[bank];
        }
    }

    private void SetState(IoConnectionState state)
    {
        IoConnectionState old = ConnectionState;
        ConnectionState = state;
        if (old != state) ConnectionStateChanged?.Invoke(this, new IoConnectionChangedEventArgs(state));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        ConnectionState = IoConnectionState.Disconnected;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
