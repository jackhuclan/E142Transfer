using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaferMapStudio.Io;

/// <summary>
/// IOManager.json 点表加载器。把设备导出的 IO 配置数组解析为强类型 <see cref="IoPointMap"/>。
///
/// 真实点表字段：IOParameterName / IOModule(IN_1…OUT_4) / IOPin(1,2,4…128 掩码) / IOName(模块.位:描述)
/// 及运行态字段 ConnectStatus/IOErrorValue/isAlarm/ErrorMsg（本层忽略运行态，只取静态定义）。
///
/// 解析规则：
/// 1. IOParameterName 为 null 的是空占位点（26 个），直接跳过；
/// 2. 用 (SourceName, Direction) 二元键绑定枚举，解决 DoorLock1 等输入/输出同名冲突；
/// 3. IOPin 必须是 2 的幂（1/2/4…128），换算为 Bit=log2(IOPin)；
/// 4. Bank 由 IOModule 尾号 -1 得到；输入/输出各自独立编 Bank；
/// 5. 校验 IOName 的前缀与 IOModule 一致、位号与 IOPin 一致，防止点表被手工改错。
/// </summary>
public static class IOManagerPointTable
{
    private static readonly Lazy<IReadOnlyDictionary<(string, IoDirection), IoPoint>> BindingIndex =
        new(BuildBindingIndex);

    /// <summary>从文件路径加载点表。</summary>
    public static IoPointMap Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using FileStream stream = File.OpenRead(filePath);
        return Load(stream);
    }

    /// <summary>从流加载点表，便于测试与嵌入资源。</summary>
    public static IoPointMap Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        List<IoManagerEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<IoManagerEntry>>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new IoPointTableException($"点表 JSON 解析失败：{ex.Message}", innerException: ex);
        }

        if (entries is null)
            throw new IoPointTableException("点表 JSON 为空（反序列化结果为 null）");
        if (entries.Count == 0)
            throw new IoPointTableException("点表 JSON 不含任何点位");

        return Build(entries);
    }

    /// <summary>从 JSON 字符串加载点表，供单测直接构造。</summary>
    public static IoPointMap LoadFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return Load(stream);
    }

    private static IoPointMap Build(IReadOnlyList<IoManagerEntry> entries)
    {
        var defs = new List<IoPointDef>(capacity: entries.Count);
        var seenPoints = new HashSet<IoPoint>();
        var seenAddresses = new HashSet<(IoDirection, int, int)>();
        int maxInputBank = -1;
        int maxOutputBank = -1;

        foreach (IoManagerEntry e in entries)
        {
            // 规则 1：空占位点跳过
            if (string.IsNullOrWhiteSpace(e.IOParameterName))
                continue;

            // 规则 2：解析方向（IOModule 前缀）。ParseDirection 已保证非空。
            IoDirection dir = ParseDirection(e.IOModule);
            int moduleNumber = ParseModuleNumber(e.IOModule!);
            int bank = moduleNumber - 1;

            // 规则 3：IOPin 掩码 → Bit
            int bit = ParseBitMask(e.IOPin, e.IOModule!);

            // 规则 4：绑定枚举
            var key = (e.IOParameterName, dir);
            if (!BindingIndex.Value.TryGetValue(key, out IoPoint point))
                throw new IoPointTableException(
                    $"点表条目 {e.IOName}({e.IOParameterName}) 无法绑定到 IoPoint 枚举：" +
                    "请检查枚举的 IoPointBinding 是否遗漏该名称或方向");

            // 规则 5：校验 IOName 一致性（仅在描述非空时校验后半段位号）
            ValidateIoName(e, moduleNumber, bit);

            if (!seenPoints.Add(point))
                throw new IoPointTableException($"点位 {point}({e.IOParameterName}) 在点表中重复定义");

            var address = (dir, bank, bit);
            if (!seenAddresses.Add(address))
                throw new IoPointTableException(
                    $"物理地址 {e.IOModule}.{bit} 被多个点位占用（重复掩码或重复模块号）");

            if (dir == IoDirection.Input) maxInputBank = Math.Max(maxInputBank, bank);
            else maxOutputBank = Math.Max(maxOutputBank, bank);

            defs.Add(new IoPointDef(
                Point: point,
                Direction: dir,
                Bank: bank,
                Bit: bit,
                ActiveLow: false, // 真实点表无有效电平字段，默认高有效；低有效点可在构建后按需替换定义
                SourceName: e.IOParameterName,
                IoModule: e.IOModule!,
                IoPin: e.IOPin,
                IoName: e.IOName ?? string.Empty,
                Description: ExtractDescription(e.IOName)));
        }

        if (defs.Count == 0)
            throw new IoPointTableException("点表中没有有效命名点（全部为空占位）");
        if (maxInputBank < 0 || maxOutputBank < 0)
            throw new IoPointTableException("点表必须同时包含输入模块与输出模块");

        // 连续 Bank 校验：出现空洞说明点表缺模块，宁可失败也不静默错位
        ValidateContiguousBanks(defs, IoDirection.Input, maxInputBank + 1);
        ValidateContiguousBanks(defs, IoDirection.Output, maxOutputBank + 1);

        return new IoPointMap(defs, maxInputBank + 1, maxOutputBank + 1);
    }

    private static void ValidateContiguousBanks(IEnumerable<IoPointDef> defs, IoDirection dir, int expectedCount)
    {
        var banks = defs.Where(d => d.Direction == dir).Select(d => d.Bank).Distinct().OrderBy(b => b).ToList();
        for (int i = 0; i < expectedCount; i++)
            if (!banks.Contains(i))
                throw new IoPointTableException($"{dir} 的字节组 Bank={i} 缺失，点表不连续");
    }

    private static IoDirection ParseDirection(string? ioModule)
    {
        if (string.IsNullOrWhiteSpace(ioModule))
            throw new IoPointTableException("存在 IOModule 为空的点位条目");

        if (ioModule.StartsWith("IN_", StringComparison.OrdinalIgnoreCase)) return IoDirection.Input;
        if (ioModule.StartsWith("OUT_", StringComparison.OrdinalIgnoreCase)) return IoDirection.Output;
        throw new IoPointTableException($"IOModule={ioModule} 前缀既不是 IN_ 也不是 OUT_");
    }

    private static int ParseModuleNumber(string ioModule)
    {
        string[] parts = ioModule.Split('_');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int n) || n < 1)
            throw new IoPointTableException($"IOModule={ioModule} 尾号非法，应为正整数");
        return n;
    }

    private static int ParseBitMask(int ioPin, string ioModule)
    {
        // 2 的幂且落在 1..128：用 (x & (x-1)) == 0 判定
        if (ioPin <= 0 || ioPin > 128 || (ioPin & (ioPin - 1)) != 0)
            throw new IoPointTableException($"IOModule={ioModule} 的 IOPin={ioPin} 不是合法位掩码(1/2/4…128)");
        return BitOperations.TrailingZeroCount(ioPin);
    }

    private static void ValidateIoName(IoManagerEntry e, int moduleNumber, int bit)
    {
        if (string.IsNullOrWhiteSpace(e.IOName)) return;
        string prefix = e.IOName.Split(':')[0];
        string[] seg = prefix.Split('.');
        if (seg.Length != 2) return; // 描述里含冒号等特殊情况，只做宽松校验
        if (!string.Equals(seg[0], e.IOModule, StringComparison.OrdinalIgnoreCase))
            throw new IoPointTableException($"IOName={e.IOName} 的模块前缀与 IOModule={e.IOModule} 不一致");
        if (int.TryParse(seg[1], out int nameBit) && nameBit != bit)
            throw new IoPointTableException($"IOName={e.IOName} 的位号 {nameBit} 与 IOPin 换算位号 {bit} 不一致");
    }

    private static string ExtractDescription(string? ioName)
    {
        if (string.IsNullOrWhiteSpace(ioName)) return string.Empty;
        int idx = ioName.IndexOf(':');
        return idx >= 0 && idx < ioName.Length - 1 ? ioName[(idx + 1)..] : string.Empty;
    }

    private static IReadOnlyDictionary<(string, IoDirection), IoPoint> BuildBindingIndex()
    {
        var index = new Dictionary<(string, IoDirection), IoPoint>();
        foreach (IoPoint point in Enum.GetValues<IoPoint>())
        {
            FieldInfo? field = typeof(IoPoint).GetField(point.ToString());
            var attr = field?.GetCustomAttribute<IoPointBindingAttribute>();
            if (attr is null) continue;
            if (!index.TryAdd((attr.SourceName, attr.Direction), point))
                throw new IoPointTableException($"IoPoint 枚举绑定重复：{attr.SourceName}/{attr.Direction}");
        }
        return index;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>点表 JSON 条目的 DTO。只声明需要的字段，运行态字段由序列化器忽略。</summary>
    private sealed class IoManagerEntry
    {
        public string? IOParameterName { get; set; }
        public string? IOModule { get; set; }
        public int IOPin { get; set; }
        public string? IOName { get; set; }
    }
}
