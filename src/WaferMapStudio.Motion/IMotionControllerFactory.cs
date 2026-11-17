namespace WaferMapStudio.Motion;

/// <summary>
/// 运动控制器工厂。宿主通过 DI 注入工厂，按连接配置创建对应实现：
/// - 开发/单测 → 模拟器实现；
/// - 现场真机 → 进程外 ACS 桥接代理（32 位服务）。
/// 上层 ViewModel 只依赖 <see cref="IMotionController"/>，不感知具体实现。
/// </summary>
public interface IMotionControllerFactory
{
    /// <summary>按连接配置创建一个控制器实例。</summary>
    IMotionController Create(MotionConnectionOptions options);

    /// <summary>创建一个纯软件模拟器实例，用于无硬件开发。</summary>
    IMotionController CreateSimulator();

    /// <summary>创建具备 PEG / History / ACSPL+ 能力的高级模拟器，用于扫描流程单测。</summary>
    IAdvancedMotionController CreateAdvancedSimulator();
}
