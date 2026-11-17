using WaferMapStudio.Motion;
using Xunit;

namespace WaferMapStudio.Motion.Tests;

/// <summary>
/// 高级运动能力测试：PEG 位置触发、History Buffer 采集、ACSPL+ 程序与变量。
/// 这些用例让 AOI 扫描时序在没有真机的情况下也能被验证。
/// </summary>
public sealed class AdvancedMotionControllerTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(5);
    private static MotionConnectionOptions SimOptions =>
        new(MotionConnectionKind.Simulator, "local");

    [Fact]
    public async Task Peg_DistanceMode_TriggersAtIntervalsDuringMove()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await controller.ConnectAsync(SimOptions);
        await controller.EnableAxisAsync(MotionAxis.X);

        PegConfig config = new(
            Axis: MotionAxis.X, OutputChannel: 0, Mode: PegMode.Distance,
            Distance: 20.0, Positions: null, StartPosition: 0.0, Count: 0);
        await controller.ConfigurePegAsync(config);
        await controller.StartPegAsync(MotionAxis.X);

        List<double> triggerPositions = new();
        controller.PegTriggered += (_, e) => triggerPositions.Add(e.Position);

        // 从 0 走到 100，间隔 20 → 期望在 20/40/60/80/100 触发。
        await controller.MoveAsync(MotionAxis.X, 100.0, MoveMode.Absolute, new MotionProfile(1000.0, 10000.0));

        Assert.Equal(5, triggerPositions.Count);
        Assert.Equal(20.0, triggerPositions[0], 6);
        Assert.Equal(100.0, triggerPositions[^1], 6);
        PegStatus status = await controller.GetPegStatusAsync(MotionAxis.X);
        Assert.True(status.IsEnabled);
        Assert.Equal(5, status.TriggerCount);
    }

    [Fact]
    public async Task Peg_CountLimit_StopsTriggering()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await controller.ConnectAsync(SimOptions);
        await controller.EnableAxisAsync(MotionAxis.X);
        PegConfig config = new(MotionAxis.X, 0, PegMode.Distance, 10.0, null, 0.0, Count: 3);
        await controller.ConfigurePegAsync(config);
        await controller.StartPegAsync(MotionAxis.X);
        int triggers = 0;
        controller.PegTriggered += (_, _) => triggers++;

        await controller.MoveAsync(MotionAxis.X, 100.0, MoveMode.Absolute, new MotionProfile(1000.0, 10000.0));

        Assert.Equal(3, triggers);
    }

    [Fact]
    public async Task Peg_PositionListMode_TriggersOnlyAtListedPositions()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await controller.ConnectAsync(SimOptions);
        await controller.EnableAxisAsync(MotionAxis.X);
        PegConfig config = new(
            Axis: MotionAxis.X, OutputChannel: 1, Mode: PegMode.PositionList,
            Distance: 0.0, Positions: new[] { 25.0, 50.0, 75.0 },
            StartPosition: 0.0, Count: 0);
        await controller.ConfigurePegAsync(config);
        await controller.StartPegAsync(MotionAxis.X);
        List<double> positions = new();
        controller.PegTriggered += (_, e) => positions.Add(e.Position);

        await controller.MoveAsync(MotionAxis.X, 90.0, MoveMode.Absolute, new MotionProfile(1000.0, 10000.0));

        Assert.Equal(new[] { 25.0, 50.0, 75.0 }, positions);
    }

    [Fact]
    public async Task Peg_WhenNotConfigured_Throws()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartPegAsync(MotionAxis.X));
    }

    [Fact]
    public async Task History_CollectsSamplesAndRespectsCapacity()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await controller.ConnectAsync(SimOptions);
        await controller.EnableAxisAsync(MotionAxis.X);
        DataCollectionConfig config = new(
            Axes: new[] { MotionAxis.X },
            SampleInterval: TimeSpan.FromMilliseconds(5),
            Source: HistorySource.Both,
            Capacity: 4);

        await controller.StartDataCollectionAsync(config);
        await controller.MoveAsync(MotionAxis.X, 500.0, MoveMode.Absolute, new MotionProfile(200.0, 2000.0));
        await Task.Delay(60);
        await controller.StopDataCollectionAsync();

        HistoryData data = await controller.ReadHistoryAsync();
        Assert.Equal(4, data.Samples.Count);
        Assert.True(data.DroppedSamples > 0);
        Assert.All(data.Samples, s =>
        {
            Assert.True(s.CommandPositions.ContainsKey(MotionAxis.X));
            Assert.True(s.FeedbackPositions.ContainsKey(MotionAxis.X));
        });
    }

    [Fact]
    public async Task History_Clear_EmptiesBuffer()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await controller.ConnectAsync(SimOptions);
        DataCollectionConfig config = new(
            new[] { MotionAxis.Y }, TimeSpan.FromMilliseconds(5), HistorySource.FeedbackPosition, 16);
        await controller.StartDataCollectionAsync(config);
        // 轮询等待首个采样出现，而非固定延时：并行负载下后台采集任务
        // （Task.Run + PeriodicTimer）可能被延迟调度，固定 30ms 不足以稳定产出样本。
        await WaitUntilAsync(async () => (await controller.ReadHistoryAsync()).Samples.Count > 0);
        await controller.StopDataCollectionAsync();
        Assert.NotEmpty((await controller.ReadHistoryAsync()).Samples);

        await controller.ClearHistoryAsync();

        HistoryData cleared = await controller.ReadHistoryAsync();
        Assert.Empty(cleared.Samples);
        Assert.Equal(0, cleared.DroppedSamples);
    }

    [Fact]
    public async Task Program_LoadRunComplete_StateTransition()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await controller.LoadProgramAsync("SCAN", "LINE X 100\nWAIT 0\nENDS");

        ProgramInfo loaded = await controller.GetProgramInfoAsync("SCAN");
        Assert.Equal(AcsplProgramState.Loaded, loaded.State);

        await controller.RunProgramAsync("SCAN");
        ProgramInfo running = await controller.GetProgramInfoAsync("SCAN");
        Assert.Equal(AcsplProgramState.Running, running.State);

        await WaitUntilAsync(async () =>
            (await controller.GetProgramInfoAsync("SCAN")).State == AcsplProgramState.Completed);
        ProgramInfo done = await controller.GetProgramInfoAsync("SCAN");
        Assert.Equal(AcsplProgramState.Completed, done.State);
    }

    [Fact]
    public async Task Program_Stop_ResetsToLoaded()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await controller.LoadProgramAsync("JOGPROG", "JOG X\nENDS");
        await controller.RunProgramAsync("JOGPROG");

        await controller.StopProgramAsync("JOGPROG");

        ProgramInfo info = await controller.GetProgramInfoAsync("JOGPROG");
        Assert.Equal(AcsplProgramState.Loaded, info.State);
        Assert.Equal(0, info.CurrentLine);
    }

    [Fact]
    public async Task Program_Unknown_Throws()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.RunProgramAsync("MISSING"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.GetProgramInfoAsync("MISSING"));
    }

    [Fact]
    public async Task Variables_ReadWriteRoundTrip()
    {
        await using SimulatedMotionController controller = new(tickInterval: Tick);
        Assert.Equal(0.0, await controller.ReadVariableAsync("pVelocity"));

        await controller.WriteVariableAsync("pVelocity", 123.45);

        Assert.Equal(123.45, await controller.ReadVariableAsync("pVelocity"));
        // 变量名大小写不敏感，与 ACSPL+ 一致。
        Assert.Equal(123.45, await controller.ReadVariableAsync("PVELOCITY"));
    }

    [Fact]
    public async Task Factory_CreatesAdvancedSimulator()
    {
        IMotionControllerFactory factory = new MotionControllerFactory();
        await using IAdvancedMotionController controller = factory.CreateAdvancedSimulator();

        await controller.ConfigurePegAsync(new PegConfig(MotionAxis.X, 0, PegMode.Distance, 1.0, null, 0.0, 0));
        Assert.NotNull(await controller.GetPegStatusAsync(MotionAxis.X));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 2000)
    {
        using CancellationTokenSource cts = new(timeoutMs);
        while (!await condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
