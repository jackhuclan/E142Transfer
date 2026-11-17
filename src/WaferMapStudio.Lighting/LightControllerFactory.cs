namespace WaferMapStudio.Lighting;

/// <summary>
/// 控制器工厂契约。宿主通过 DI 注入此接口，避免直接 new 具体实现。
/// </summary>
public interface ILightControllerFactory
{
    /// <summary>基于给定传输实现创建控制器。</summary>
    ILightController Create(ILightDeviceTransport transport);

    /// <summary>创建带模拟设备的控制器，供无硬件开发/单测使用。</summary>
    ILightController CreateSimulator(int channelCount = 4, double maxCurrentMa = 500.0);
}

/// <summary>
/// 默认工厂。真机适配器在宿主层按 <see cref="LightTransportKind"/> 注册：
/// 串口 / 网口 / USB 适配器实现 <see cref="ILightDeviceTransport"/> 后，
/// 由 App 的 DI 容器选择并传入 <see cref="Create"/>，本抽象层不引用它们。
/// </summary>
public sealed class LightControllerFactory : ILightControllerFactory
{
    /// <inheritdoc />
    public ILightController Create(ILightDeviceTransport transport) => new LightController(transport);

    /// <inheritdoc />
    public ILightController CreateSimulator(int channelCount = 4, double maxCurrentMa = 500.0) =>
        new LightController(new SimulatedLightDevice(channelCount, maxCurrentMa));
}
