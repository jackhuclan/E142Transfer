namespace WaferMapStudio.Motion;

/// <summary>
/// 默认工厂实现。真机适配器需在此注册：
/// - <see cref="MotionConnectionKind.OutOfProcess"/> → 进程外 ACS 桥接代理；
/// - <see cref="MotionConnectionKind.Simulator"/> → 模拟器。
/// 其它链路的真机实现按机型接入时，在此 switch 中扩展。
/// </summary>
public sealed class MotionControllerFactory : IMotionControllerFactory
{
    /// <inheritdoc />
    public IMotionController Create(MotionConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.Kind switch
        {
            MotionConnectionKind.Simulator => new SimulatedMotionController(),
            // MotionConnectionKind.OutOfProcess => new AcsOutOfProcessController(options),
            // ↑ 由独立的 32 位桥接项目实现后在 App 层注册；抽象层不引用它。
            _ => throw new NotSupportedException(
                $"连接类型 {options.Kind} 尚未注册实现，请在宿主层注入对应适配器"),
        };
    }

    /// <inheritdoc />
    public IMotionController CreateSimulator() => new SimulatedMotionController();

    /// <inheritdoc />
    public IAdvancedMotionController CreateAdvancedSimulator() => new SimulatedMotionController();
}
