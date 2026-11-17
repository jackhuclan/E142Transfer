using WaferMapStudio.Lighting;
using Xunit;

namespace WaferMapStudio.Lighting.Tests;

/// <summary>
/// LightController 行为测试。用 SimulatedLightDevice 验证"无硬件"条件下，
/// 开关、亮度、状态、校验、异常、故障上报是否符合契约。
/// </summary>
public sealed class LightControllerTests
{
    private static LightConnectionOptions SimOptions =>
        new(LightTransportKind.Simulator, "local");

    private static async Task<ILightController> ConnectedControllerAsync(
        SimulatedLightDevice? device = null)
    {
        ILightController controller = new LightController(device ?? new SimulatedLightDevice());
        await controller.ConnectAsync(SimOptions);
        return controller;
    }

    [Fact]
    public async Task Connect_ReadsCapabilitiesAndRaisesStateChanged()
    {
        await using ILightController controller = new LightController(new SimulatedLightDevice(channelCount: 4));
        LightConnectionState? observed = null;
        controller.ConnectionStateChanged += (_, e) => observed = e.State;

        await controller.ConnectAsync(SimOptions);

        Assert.Equal(LightConnectionState.Connected, controller.ConnectionState);
        Assert.Equal(LightConnectionState.Connected, observed);
        Assert.NotNull(controller.Capabilities);
        Assert.Equal(4, controller.Capabilities!.ChannelCount);
        Assert.Equal(4, controller.AvailableChannels.Count);
    }

    [Fact]
    public async Task SetOn_ThenSetBrightness_PreservesIntensityOnToggle()
    {
        await using ILightController controller = await ConnectedControllerAsync();
        List<LightChannelStatus> changes = new();
        controller.ChannelChanged += (_, e) => changes.Add(e.Status);

        await controller.SetBrightnessAsync(1, 60.0);
        await controller.SetOnAsync(1, false);
        LightChannelStatus off = await controller.ReadChannelAsync(1);
        Assert.Equal(LightChannelState.Off, off.State);

        await controller.SetOnAsync(1, true);
        LightChannelStatus on = await controller.ReadChannelAsync(1);

        Assert.Equal(LightChannelState.On, on.State);
        Assert.Equal(60.0, on.BrightnessPercent, 3);
        Assert.Contains(changes, s => s.Channel == 1 && s.State == LightChannelState.On);
    }

    [Fact]
    public async Task SetIntensity_Milliampere_ConvertsToPercent()
    {
        await using ILightController controller = await ConnectedControllerAsync(
            new SimulatedLightDevice(maxCurrentMa: 500.0));

        await controller.SetIntensityAsync(2, 125.0, LightIntensityUnit.Milliampere);

        LightChannelStatus status = await controller.ReadChannelAsync(2);
        Assert.Equal(25.0, status.BrightnessPercent, 3);
        Assert.Equal(125.0, status.MeasuredCurrentMa!.Value, 3);
    }

    [Fact]
    public async Task SetBrightnessBatch_AppliesAllChannels()
    {
        await using ILightController controller = await ConnectedControllerAsync();

        await controller.SetBrightnessBatchAsync(new Dictionary<int, double> { [1] = 30, [3] = 70 });

        IReadOnlyList<LightChannelStatus> all = await controller.ReadAllChannelsAsync();
        Assert.Equal(4, all.Count);
        Assert.Equal(30.0, all.Single(s => s.Channel == 1).BrightnessPercent, 3);
        Assert.Equal(70.0, all.Single(s => s.Channel == 3).BrightnessPercent, 3);
        Assert.Equal(LightChannelState.Off, all.Single(s => s.Channel == 2).State);
    }

    [Fact]
    public async Task TurnOffAll_AllChannelsOff()
    {
        await using ILightController controller = await ConnectedControllerAsync();
        await controller.SetBrightnessBatchAsync(new Dictionary<int, double> { [1] = 40, [2] = 80 });

        await controller.TurnOffAllAsync();

        IReadOnlyList<LightChannelStatus> all = await controller.ReadAllChannelsAsync();
        Assert.All(all, s => Assert.Equal(LightChannelState.Off, s.State));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task InvalidChannel_ThrowsValidationException(int channel)
    {
        await using ILightController controller = await ConnectedControllerAsync();

        await Assert.ThrowsAsync<LightValidationException>(() => controller.SetBrightnessAsync(channel, 50));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    [InlineData(double.NaN)]
    public async Task InvalidBrightness_ThrowsValidationException(double brightness)
    {
        await using ILightController controller = await ConnectedControllerAsync();

        await Assert.ThrowsAsync<LightValidationException>(() => controller.SetBrightnessAsync(1, brightness));
    }

    [Fact]
    public async Task OperationBeforeConnect_ThrowsConnectionException()
    {
        await using ILightController controller = new LightController(new SimulatedLightDevice());

        await Assert.ThrowsAsync<LightConnectionException>(() => controller.SetBrightnessAsync(1, 50));
    }

    [Fact]
    public async Task TransportFailure_IsNormalizedToDeviceException()
    {
        SimulatedLightDevice device = new();
        await using ILightController controller = new LightController(device);
        await controller.ConnectAsync(SimOptions);
        device.FailNextWrite = new TimeoutException("device no reply");

        LightDeviceException ex = await Assert.ThrowsAsync<LightDeviceException>(
            () => controller.SetBrightnessAsync(1, 50));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        await using ILightController controller = await ConnectedControllerAsync();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => controller.SetBrightnessAsync(1, 50, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task FaultyChannel_RaisesFaultedEvent()
    {
        SimulatedLightDevice device = new();
        await using ILightController controller = new LightController(device);
        await controller.ConnectAsync(SimOptions);
        device.InjectFault(2, "OVER_CURRENT");
        List<LightFaultEventArgs> faults = new();
        controller.Faulted += (_, e) => faults.Add(e);

        LightChannelStatus status = await controller.ReadChannelAsync(2);

        Assert.Equal(LightChannelState.Fault, status.State);
        Assert.Single(faults);
        Assert.Equal(2, faults[0].Channel);
        Assert.Equal("OVER_CURRENT", faults[0].Code);
    }

    [Fact]
    public async Task LinearRamp_OnUnsupportedDevice_ThrowsValidation()
    {
        await using ILightController controller = await ConnectedControllerAsync(
            new SimulatedLightDevice(supportsRamp: false));

        await Assert.ThrowsAsync<LightValidationException>(() =>
            controller.SetBrightnessAsync(1, 50, LightRampMode.Linear, 200));
    }
}
