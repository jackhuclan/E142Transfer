namespace WaferMapStudio.Io;

/// <summary>
/// IO 控制器工厂，集中装配传输与控制器，供 DI 容器单点注册（对应 AppServices.cs 的职责）。
/// 真机适配器尚未实现（型号/协议待确认），Simulator 之外的链路明确抛出，
/// 避免误把未验证的适配器用到产线。
/// </summary>
public static class IoControllerFactory
{
    /// <summary>创建模拟器控制器（无硬件、可单测）。</summary>
    /// <param name="inputBankCount">输入字节组数。</param>
    /// <param name="outputBankCount">输出字节组数。</param>
    /// <param name="debounceFrames">去抖帧数。</param>
    public static IIoController CreateSimulator(
        int inputBankCount = 4,
        int outputBankCount = 4,
        int debounceFrames = 1)
    {
        var transport = new SimulatedIoTransport(inputBankCount, outputBankCount);
        return new IoController(transport, debounceFrames);
    }

    /// <summary>用自定义传输创建控制器（真机适配器注入点）。</summary>
    public static IIoController Create(IIoTransport transport, int debounceFrames = 1) =>
        new IoController(transport, debounceFrames);

    /// <summary>
    /// 按连接参数创建真机控制器。当前仅 Simulator 可用；
    /// Tcp/Fieldbus/OutOfProcess 待具体型号确认后补适配器，此处显式失败而非静默降级。
    /// </summary>
    public static IIoController Create(IoConnectionOptions options, int debounceFrames = 1)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Kind switch
        {
            IoConnectionKind.Simulator => CreateSimulator(debounceFrames: debounceFrames),
            IoConnectionKind.Tcp or IoConnectionKind.Fieldbus or IoConnectionKind.OutOfProcess =>
                throw new IoConnectionException(
                    $"链路 {options.Kind} 的真机适配器尚未实现，请先确认 IO 模块型号与协议"),
            _ => throw new IoConnectionException($"未知链路类型 {options.Kind}"),
        };
    }
}
