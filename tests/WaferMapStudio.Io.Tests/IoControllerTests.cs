using System.Text.Json;
using Xunit;

namespace WaferMapStudio.Io.Tests;

/// <summary>控制器测试：覆盖七项要求的运行时行为，全部基于模拟器无硬件。</summary>
public class IoControllerTests
{
    private const string MiniTable =
        "[{\"IOParameterName\":\"DoorLock1\",\"IOModule\":\"IN_1\",\"IOPin\":1,\"IOName\":\"IN_1.0:锁1\"}," +
        "{\"IOParameterName\":\"TotalNegativePressureDetection\",\"IOModule\":\"IN_1\",\"IOPin\":2,\"IOName\":\"IN_1.1:负压\"}," +
        "{\"IOParameterName\":\"RED\",\"IOModule\":\"OUT_1\",\"IOPin\":1,\"IOName\":\"OUT_1.0:红\"}," +
        "{\"IOParameterName\":\"CameraTrigger\",\"IOModule\":\"OUT_1\",\"IOPin\":32,\"IOName\":\"OUT_1.5:拍照\"}]";

    private static IoPointMap Map() => IOManagerPointTable.LoadFromJson(MiniTable);

    private static IoConnectionOptions Options(string model = "SIM-IO-64") =>
        new(IoConnectionKind.Simulator, "sim", ExpectedModel: model);

    private static async Task<(IoController Controller, SimulatedIoTransport Transport)> InitAsync(
        int debounce = 1, string model = "SIM-IO-64")
    {
        var transport = new SimulatedIoTransport(1, 1);
        var controller = new IoController(transport, debounceFrames: debounce);
        await controller.InitializeAsync(Options(model), Map());
        return (controller, transport);
    }

    [Fact]
    public async Task 初始化_状态为Ready且输出归安全态()
    {
        var (controller, transport) = await InitAsync();
        Assert.Equal(IoConnectionState.Ready, controller.ConnectionState);
        Assert.NotNull(controller.PointMap);
        Assert.Equal(0, transport.GetOutputBank(0)); // 无输出残留
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 初始化_型号不符抛异常且状态Faulted()
    {
        var transport = new SimulatedIoTransport(1, 1);
        var controller = new IoController(transport);
        await Assert.ThrowsAsync<IoModelMismatchException>(
            () => controller.InitializeAsync(Options("EXPECTED-X"), Map()));
        Assert.Equal(IoConnectionState.Faulted, controller.ConnectionState);
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 未初始化时读写_抛未就绪异常()
    {
        var transport = new SimulatedIoTransport(1, 1);
        var controller = new IoController(transport);
        Assert.Throws<IoConnectionException>(() => controller.Read(IoPoint.DoorLock1Status));
        await Assert.ThrowsAsync<IoConnectionException>(() => controller.WriteAsync(IoPoint.StackLightRed, true));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 读输入_原始字节按位掩码解码()
    {
        var (controller, transport) = await InitAsync();
        transport.SetInputBank(0, 0b0000_0010); // bit1=1 → 负压点有效

        await controller.ReadInputsAsync();

        Assert.False(controller.Read(IoPoint.DoorLock1Status));
        Assert.True(controller.Read(IoPoint.TotalNegativePressureOk));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 写输出_回读确认且只改目标位()
    {
        var (controller, transport) = await InitAsync();
        transport.SetInputBank(0, 0);

        await controller.WriteAsync(IoPoint.StackLightRed, true); // OUT bit0

        Assert.Equal(0b0000_0001, transport.GetOutputBank(0));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 写输入点_抛校验异常()
    {
        var (controller, _) = await InitAsync();
        await Assert.ThrowsAsync<IoValidationException>(
            () => controller.WriteAsync(IoPoint.DoorLock1Status, true));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 批量写_按Bank合并掩码并回读通过()
    {
        var (controller, transport) = await InitAsync();
        await controller.WriteBatchAsync(new Dictionary<IoPoint, bool>
        {
            [IoPoint.StackLightRed] = true,   // bit0
            [IoPoint.CameraTrigger] = true,   // bit5
        });
        Assert.Equal(0b0010_0001, transport.GetOutputBank(0));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 写后回读不一致_抛写校验异常()
    {
        var transport = new FaultyWriteTransport(1, 1);
        var controller = new IoController(transport);
        await controller.InitializeAsync(Options(), Map());

        // 仿真设备写后回读始终为 0
        await Assert.ThrowsAsync<IoWriteVerifyException>(
            () => controller.WriteAsync(IoPoint.StackLightRed, true));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 去抖_连续N帧一致才上报变化沿()
    {
        var (controller, transport) = await InitAsync(debounce: 3);
        int changes = 0;
        controller.InputChanged += (_, e) =>
        {
            if (e.Point == IoPoint.TotalNegativePressureOk) changes++;
        };

        transport.SetInputBank(0, 0b0000_0010);
        await controller.ReadInputsAsync(); // 第 1 帧：未确认
        await controller.ReadInputsAsync(); // 第 2 帧：未确认
        Assert.Equal(0, changes);
        Assert.False(controller.Read(IoPoint.TotalNegativePressureOk));

        await controller.ReadInputsAsync(); // 第 3 帧：确认变化
        Assert.Equal(1, changes);
        Assert.True(controller.Read(IoPoint.TotalNegativePressureOk));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 轮询中读异常_经Faulted上报但循环不退出()
    {
        var (controller, transport) = await InitAsync();
        var faults = new List<IoFaultEventArgs>();
        controller.Faulted += (_, e) => faults.Add(e);

        await controller.StartMonitoringAsync(TimeSpan.FromMilliseconds(5));
        transport.ReadInputFault = new IOException("模拟读失败");
        await Task.Delay(80);
        Assert.NotEmpty(faults); // 异常被上报

        // 故障清除后循环仍在跑：再次改输入能被读到
        transport.ReadInputFault = null;
        transport.SetInputBank(0, 0b0000_0001);
        await Task.Delay(60);
        Assert.True(controller.Read(IoPoint.DoorLock1Status));
        await controller.DisposeAsync();
    }

    [Fact]
    public async Task 释放_停止轮询并把输出归安全态()
    {
        var (controller, transport) = await InitAsync();
        await controller.WriteAsync(IoPoint.StackLightRed, true);
        await controller.StartMonitoringAsync(TimeSpan.FromMilliseconds(5));
        Assert.NotEqual(0, transport.GetOutputBank(0));

        await controller.DisposeAsync();

        Assert.Equal(0, transport.GetOutputBank(0)); // 安全态：输出清零
        Assert.Equal(IoConnectionState.Disconnected, transport.ConnectionState);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => controller.WriteAsync(IoPoint.StackLightRed, true));
    }

    /// <summary>写成功但回读始终为 0 的传输，专门验证写校验分支。</summary>
    private sealed class FaultyWriteTransport : IIoTransport
    {
        private readonly byte[] _outputs;
        public FaultyWriteTransport(int inBanks, int outBanks)
        {
            _outputs = new byte[outBanks];
        }

        public IoConnectionState ConnectionState { get; private set; } = IoConnectionState.Disconnected;
        public event EventHandler<IoConnectionChangedEventArgs>? ConnectionStateChanged;

        public Task<IoDeviceIdentity> ConnectAsync(IoConnectionOptions o, CancellationToken ct = default)
        {
            ConnectionState = IoConnectionState.Ready;
            ConnectionStateChanged?.Invoke(this, new IoConnectionChangedEventArgs(ConnectionState));
            return Task.FromResult(new IoDeviceIdentity("SIM-IO-64", "1.0", 1, _outputs.Length));
        }

        public Task DisconnectAsync(CancellationToken ct = default)
        {
            ConnectionState = IoConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task<byte[]> ReadInputsAsync(CancellationToken ct = default) => Task.FromResult(new byte[1]);
        public Task<byte[]> ReadOutputsAsync(CancellationToken ct = default) => Task.FromResult(new byte[_outputs.Length]);
        public Task WriteBankAsync(int bank, byte mask, byte value, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
