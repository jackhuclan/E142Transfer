namespace WaferMapStudio.Io;

/// <summary>
/// IO 传输契约 —— 只负责"把字节帧读上来、把位写下去"，不含点位语义、不含去抖与事件。
/// 与 <see cref="IIoController"/> 分工：控制器做意图级操作并保证安全，传输层只搬运数据。
/// 真机适配器（Modbus TCP / 进程外桥接 / 主站库）与模拟器都实现本接口。
/// </summary>
public interface IIoTransport : IAsyncDisposable
{
    /// <summary>链路状态。</summary>
    IoConnectionState ConnectionState { get; }

    /// <summary>链路状态变化时触发。</summary>
    event EventHandler<IoConnectionChangedEventArgs>? ConnectionStateChanged;

    /// <summary>建立链路并读取模块身份。</summary>
    Task<IoDeviceIdentity> ConnectAsync(IoConnectionOptions options, CancellationToken cancellationToken = default);

    /// <summary>断开链路。</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>批量读输入帧，返回长度等于模块身份中 InputBankCount 的字节数组。</summary>
    Task<byte[]> ReadInputsAsync(CancellationToken cancellationToken = default);

    /// <summary>读输出回读帧，长度等于 OutputBankCount，用于写后校验。</summary>
    Task<byte[]> ReadOutputsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 把单个字节组的位掩码写出：mask 指出要改哪些位，value 的对应位给出新值。
    /// 传输实现应做 read-modify-write，避免覆盖同字节其他位。
    /// </summary>
    Task WriteBankAsync(int bank, byte mask, byte value, CancellationToken cancellationToken = default);
}
