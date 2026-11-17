using WaferMapStudio.Motion;
using Xunit;

namespace WaferMapStudio.Motion.Tests;

/// <summary>
/// 模拟器行为测试。验证接口契约在"无硬件"条件下自洽，
/// 确保 ViewModel 层可以针对 IMotionController 做完整单测。
/// </summary>
public sealed class SimulatedMotionControllerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(5);

    [Fact]
    public async Task Connect_ReportsConnectedState()
    {
        await using IMotionController controller = new SimulatedMotionController(tickInterval: Tick);
        MotionConnectionState? last = null;
        controller.ConnectionStateChanged += (_, e) => last = e.State;

        await controller.ConnectAsync(new MotionConnectionOptions(MotionConnectionKind.Simulator, "local"));

        Assert.Equal(MotionConnectionState.Connected, controller.ConnectionState);
        Assert.Equal(MotionConnectionState.Connected, last);
    }

    [Fact]
    public async Task Move_WhenAxisDisabled_ThrowsInvalidOperation()
    {
        await using IMotionController controller = new SimulatedMotionController(tickInterval: Tick);
        await controller.ConnectAsync(new MotionConnectionOptions(MotionConnectionKind.Simulator, "local"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.MoveAsync(MotionAxis.X, 100.0, MoveMode.Absolute));
    }

    [Fact]
    public async Task Move_AbsoluteAndRelative_ReachExpectedPosition()
    {
        await using IMotionController controller = new SimulatedMotionController(tickInterval: Tick);
        await controller.ConnectAsync(new MotionConnectionOptions(MotionConnectionKind.Simulator, "local"));
        await controller.EnableAxisAsync(MotionAxis.X);
        MotionProfile profile = new(Velocity: 1000.0, Acceleration: 10000.0);

        await controller.MoveAsync(MotionAxis.X, 500.0, MoveMode.Absolute, profile);
        AxisStatus afterAbsolute = await controller.GetStatusAsync(MotionAxis.X);
        Assert.Equal(500.0, afterAbsolute.FeedbackPosition, 3);

        await controller.MoveAsync(MotionAxis.X, -100.0, MoveMode.Relative, profile);
        AxisStatus afterRelative = await controller.GetStatusAsync(MotionAxis.X);
        Assert.Equal(400.0, afterRelative.FeedbackPosition, 3);
    }

    [Fact]
    public async Task Move_RaisesStateChangedToMovingThenIdle()
    {
        await using IMotionController controller = new SimulatedMotionController(tickInterval: Tick);
        await controller.ConnectAsync(new MotionConnectionOptions(MotionConnectionKind.Simulator, "local"));
        await controller.EnableAxisAsync(MotionAxis.X);
        List<AxisMotionState> states = new();
        controller.AxisStateChanged += (_, e) =>
        {
            if (e.Axis == MotionAxis.X)
            {
                states.Add(e.State);
            }
        };

        await controller.MoveAsync(MotionAxis.X, 10.0, MoveMode.Absolute, new MotionProfile(1000.0, 10000.0));

        Assert.Contains(AxisMotionState.Moving, states);
        Assert.Equal(AxisMotionState.Idle, states[^1]);
    }

    [Fact]
    public async Task Stop_Emergency_RaisesFaultEvent()
    {
        await using IMotionController controller = new SimulatedMotionController(tickInterval: Tick);
        await controller.ConnectAsync(new MotionConnectionOptions(MotionConnectionKind.Simulator, "local"));
        await controller.EnableAxisAsync(MotionAxis.X);
        MotionFaultEventArgs? fault = null;
        controller.Faulted += (_, e) => fault = e;

        await controller.StopAsync(MotionAxis.X, StopMode.Emergency);

        Assert.NotNull(fault);
        Assert.Equal(MotionAxis.X, fault!.Axis);
        Assert.Equal("ESTOP", fault.Code);
        AxisStatus status = await controller.GetStatusAsync(MotionAxis.X);
        Assert.Equal(AxisMotionState.Faulted, status.Motion);
    }

    [Fact]
    public async Task Io_ReadWrite_Works()
    {
        SimulatedMotionController controller = new(tickInterval: Tick);
        await using (controller)
        {
            controller.SetSimulatedInput(3, true);
            Assert.True(await controller.ReadInputAsync(3));
            Assert.False(await controller.ReadInputAsync(4));

            await controller.WriteOutputAsync(7, true);
            Assert.True(controller.GetSimulatedOutput(7));
        }
    }

    [Fact]
    public async Task Home_Here_ResetsPositionToZero()
    {
        await using IMotionController controller = new SimulatedMotionController(tickInterval: Tick);
        await controller.ConnectAsync(new MotionConnectionOptions(MotionConnectionKind.Simulator, "local"));
        await controller.EnableAxisAsync(MotionAxis.Z);
        await controller.MoveAsync(MotionAxis.Z, 250.0, MoveMode.Absolute, new MotionProfile(1000.0, 10000.0));

        await controller.HomeAsync(MotionAxis.Z, HomingMode.Here);

        AxisStatus status = await controller.GetStatusAsync(MotionAxis.Z);
        Assert.Equal(0.0, status.FeedbackPosition, 3);
    }

    [Fact]
    public async Task GetStatus_UnknownAxis_Throws()
    {
        await using IMotionController controller = new SimulatedMotionController(new[] { MotionAxis.X }, Tick);
        await Assert.ThrowsAsync<ArgumentException>(() => controller.GetStatusAsync(MotionAxis.Y));
    }
}
