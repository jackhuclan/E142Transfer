using E142Transfer.Import;

// G85 接入验证工具：读取一个或多个晶圆图文件，输出格式识别、字段映射与校验报告。
// 用法：dotnet run --project E142Transfer.RealSampleCheck -- <file1> [file2 ...]
// 不带参数时使用内置的 Amkor/G85 参考样本路径。
var paths = args.Length > 0
    ? args.ToList()
    : [@"E:\wafer-ws\Map图谱\A-semi G85_Amkor\IBM_AURA_3.1_OQA_Setup1_4EEEB00000_HW3UREG.xml"];

var importer = new WaferMapImporter();
var failed = false;
foreach (var path in paths)
{
    Console.WriteLine($"\n========== {path} ==========");
    if (!File.Exists(path))
    {
        Console.WriteLine("文件不存在，跳过。");
        failed = true;
        continue;
    }

    try
    {
        var probe = importer.Probe(path);
        Console.WriteLine($"识别格式: {probe.Format} (置信度 {probe.Score:F2})");
        var outcome = importer.Import(path);
        var m = outcome.Map;
        Console.WriteLine($"网格: {m.Layout.Columns}x{m.Layout.Rows}, Origin={m.Layout.OriginLocation}, " +
                          $"Axis={m.Layout.AxisDirection}, Orient={m.Layout.Orientation}");
        Console.WriteLine($"Wafer={m.SubstrateId} Lot={m.LotId} Device={m.DeviceId} SLOT={m.Metadata.GetValueOrDefault("SLOT")}");
        Console.WriteLine($"参考die={m.ReferenceDie} DUTMS={m.Metadata.GetValueOrDefault("DUTMS")}");
        Console.WriteLine($"die尺寸={m.DieSizeX:0.##}x{m.DieSizeY:0.##}µm die总数={m.Dies.Count} GoodDies={m.GoodDies.Count()}");
        if (m.Metadata.Count > 0)
            Console.WriteLine("元数据: " + string.Join(", ",
                m.Metadata.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        Console.WriteLine("Bin 分布:");
        foreach (var kv in m.ClassCounts().OrderBy(k => k.Key))
            Console.WriteLine($"  {kv.Key,-10}: {kv.Value}");
        Console.WriteLine($"校验告警 ({m.Issues.Count}):");
        foreach (var i in m.Issues)
            Console.WriteLine($"  [{i.Code}] {i.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"解析失败: {ex.GetType().Name}: {ex.Message}");
        failed = true;
    }
}

if (failed) Environment.ExitCode = 1;
