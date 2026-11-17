using System.Text.Json;
using Xunit;

namespace WaferMapStudio.Io.Tests;

/// <summary>点表加载器测试：覆盖真实点表的关键特征与非法输入边界。</summary>
public class IOManagerPointTableTests
{
    /// <summary>构造与真实 IOManager.json 同结构的一条记录。</summary>
    private static string Row(string? name, string module, int pin, string? ioname) =>
        JsonSerializer.Serialize(new
        {
            IOParameterName = name,
            IOModule = module,
            DeviceName = "IO控制",
            ConnectStatus = true,
            IOErrorValue = true,
            IOPin = pin,
            isAlarm = false,
            ErrorMsg = (string?)null,
            IOName = ioname,
        });

    private static string Table(params string[] rows) => "[" + string.Join(",", rows) + "]";

    [Fact]
    public void Load_真实结构_过滤空占位并换算位掩码()
    {
        // 输入 IN_1: DoorLock1(bit0)/DoorLock3(bit2)/一个空占位；IN_2: 总负压(bit0)
        // 输出 OUT_1: RED(bit0)/CameraTrigger(bit5)/DoorLock1(bit6)；OUT_2: Vacuum6(bit2)
        string json = Table(
            Row("DoorLock1", "IN_1", 1, "IN_1.0:1#门锁状态"),
            Row("DoorLock3", "IN_1", 4, "IN_1.2:3#门锁状态"),
            Row(null, "IN_1", 8, "IN_1.3:"),
            Row("TotalNegativePressureDetection", "IN_2", 1, "IN_2.0:总负气压检测"),
            Row("RED", "OUT_1", 1, "OUT_1.0:信号灯柱_红"),
            Row("CameraTrigger", "OUT_1", 32, "OUT_1.5:IO拍照"),
            Row("DoorLock1", "OUT_1", 64, "OUT_1.6:1#门锁控制"),
            Row("Vacuum6", "OUT_2", 4, "OUT_2.2:6寸吸真空"));

        IoPointMap map = IOManagerPointTable.LoadFromJson(json);

        Assert.Equal(2, map.InputBankCount);
        Assert.Equal(2, map.OutputBankCount);
        Assert.Equal(7, map.Points.Count); // 8 条中 1 条空占位被过滤

        IoPointDef lockIn = map.Require(IoPoint.DoorLock1Status);
        Assert.Equal(IoDirection.Input, lockIn.Direction);
        Assert.Equal(0, lockIn.Bank);
        Assert.Equal(0, lockIn.Bit);
        Assert.Equal("1#门锁状态", lockIn.Description);

        IoPointDef lockOut = map.Require(IoPoint.DoorLock1Control);
        Assert.Equal(IoDirection.Output, lockOut.Direction);
        Assert.Equal(0, lockOut.Bank);
        Assert.Equal(6, lockOut.Bit); // IOPin=64 → log2=6
        Assert.Equal("OUT_1.6:1#门锁控制", lockOut.IoName);

        Assert.Equal(5, map.Require(IoPoint.CameraTrigger).Bit); // IOPin=32 → bit5
        Assert.Equal(2, map.Require(IoPoint.Vacuum6On).Bit);
    }

    [Fact]
    public void Load_输入与输出同名点按方向分别绑定()
    {
        string json = Table(
            Row("DoorLock1", "IN_1", 1, "IN_1.0:锁状态"),
            Row("DoorLock1", "OUT_1", 1, "OUT_1.0:锁控制"),
            Row("RED", "OUT_1", 2, "OUT_1.1:红"),
            Row("TotalNegativePressureDetection", "IN_1", 2, "IN_1.1:负压"));

        IoPointMap map = IOManagerPointTable.LoadFromJson(json);

        Assert.Equal(IoDirection.Input, map.Require(IoPoint.DoorLock1Status).Direction);
        Assert.Equal(IoDirection.Output, map.Require(IoPoint.DoorLock1Control).Direction);
        Assert.Equal(0, map.Require(IoPoint.DoorLock1Status).Bit);
        Assert.Equal(0, map.Require(IoPoint.DoorLock1Control).Bit);
    }

    [Theory]
    [InlineData(3)]   // 非 2 的幂
    [InlineData(0)]   // 下界
    [InlineData(256)] // 超出 8 位
    public void Load_IOPin非法_抛点表异常(int badPin)
    {
        string json = Table(
            Row("DoorLock1", "IN_1", badPin, "IN_1.0:x"),
            Row("DoorLock1", "OUT_1", 1, "OUT_1.0:y"),
            Row("RED", "OUT_1", 2, "OUT_1.1:z"),
            Row("TotalNegativePressureDetection", "IN_1", 2, "IN_1.1:w"));
        Assert.Throws<IoPointTableException>(() => IOManagerPointTable.LoadFromJson(json));
    }

    [Fact]
    public void Load_IOName位号与掩码不符_抛点表异常()
    {
        // IOPin=1(bit0) 但 IOName 写 IN_1.3 → 手工改错，应拒绝
        string json = Table(
            Row("DoorLock1", "IN_1", 1, "IN_1.3:错位"),
            Row("DoorLock1", "OUT_1", 1, "OUT_1.0:y"),
            Row("RED", "OUT_1", 2, "OUT_1.1:z"),
            Row("TotalNegativePressureDetection", "IN_1", 2, "IN_1.1:w"));
        Assert.Throws<IoPointTableException>(() => IOManagerPointTable.LoadFromJson(json));
    }

    [Fact]
    public void Load_未绑定枚举的未知点位_抛点表异常()
    {
        string json = Table(
            Row("UnknownSensorXYZ", "IN_1", 1, "IN_1.0:未知"),
            Row("DoorLock1", "OUT_1", 1, "OUT_1.0:y"),
            Row("RED", "OUT_1", 2, "OUT_1.1:z"),
            Row("TotalNegativePressureDetection", "IN_1", 2, "IN_1.1:w"));
        Assert.Throws<IoPointTableException>(() => IOManagerPointTable.LoadFromJson(json));
    }

    [Fact]
    public void Load_物理地址重复_抛点表异常()
    {
        // DoorLock1 与 AcsPowerStatus 都占 IN_1.bit0
        string json = Table(
            Row("DoorLock1", "IN_1", 1, "IN_1.0:a"),
            Row("KMI", "IN_1", 1, "IN_1.0:b"),
            Row("DoorLock1", "OUT_1", 1, "OUT_1.0:c"),
            Row("RED", "OUT_1", 2, "OUT_1.1:d"),
            Row("TotalNegativePressureDetection", "IN_1", 2, "IN_1.1:e"));
        Assert.Throws<IoPointTableException>(() => IOManagerPointTable.LoadFromJson(json));
    }

    [Fact]
    public void Decode与SafeOutputStates_按极性正确()
    {
        string json = Table(
            Row("DoorLock1", "IN_1", 1, "IN_1.0:a"),
            Row("TotalNegativePressureDetection", "IN_1", 2, "IN_1.1:b"),
            Row("RED", "OUT_1", 1, "OUT_1.0:c"),
            Row("CameraTrigger", "OUT_1", 2, "OUT_1.1:d"));
        IoPointMap map = IOManagerPointTable.LoadFromJson(json);

        byte[] frame = [0b0000_0010, 0]; // IN_1 bit1=1
        Assert.False(map.Decode(IoPoint.DoorLock1Status, frame));
        Assert.True(map.Decode(IoPoint.TotalNegativePressureOk, frame));

        // 高有效点安全态为 false（物理 0）
        var safe = map.SafeOutputStates();
        Assert.False(safe[IoPoint.StackLightRed]);
        Assert.False(safe[IoPoint.CameraTrigger]);
    }

    [Fact]
    public void 真实点表文件存在时_验证全部38个有效点()
    {
        const string realPath = @"E:\Data\Confs\IOManager.json";
        if (!File.Exists(realPath)) return; // 测试不依赖外部文件；本机存在时做真实验证

        IoPointMap map = IOManagerPointTable.Load(realPath);
        Assert.Equal(4, map.InputBankCount);
        Assert.Equal(4, map.OutputBankCount);
        Assert.Equal(38, map.Points.Count); // 64 点中 26 个空占位

        // 关键点的物理位置与真实点表一致
        Assert.Equal((0, 0), (map.Require(IoPoint.DoorLock1Status).Bank, map.Require(IoPoint.DoorLock1Status).Bit));
        Assert.Equal((0, 7), (map.Require(IoPoint.TotalPositivePressureOk).Bank, map.Require(IoPoint.TotalPositivePressureOk).Bit));
        Assert.Equal((0, 5), (map.Require(IoPoint.CameraTrigger).Bank, map.Require(IoPoint.CameraTrigger).Bit));
        Assert.Equal((1, 4), (map.Require(IoPoint.Vacuum8On).Bank, map.Require(IoPoint.Vacuum8On).Bit));
    }
}
