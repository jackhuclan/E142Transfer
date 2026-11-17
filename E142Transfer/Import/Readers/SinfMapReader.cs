using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import.Readers;

/// <summary>
/// SINF 文本格式读取器（示例适配器）。
/// SINF 家族方言差异大，本实现只覆盖 "KEY:VALUE 头 + 空格分隔 bin 网格" 这一常见形态；
/// 接到具体厂内样本时，只需在本类里扩展头部键与数据段规则，不影响其他层。
/// </summary>
public sealed class SinfMapReader : IWaferMapReader
{
    public WaferMapFormat Format => WaferMapFormat.Sinf;

    public IReadOnlyList<string> SupportedExtensions => [".sinf", ".txt", ".map"];

    public double DetectConfidence(in FormatProbe probe)
    {
        var text = probe.HeadText;
        var hasRows = text.Contains("ROWCT:", StringComparison.OrdinalIgnoreCase);
        var hasCols = text.Contains("COLCT:", StringComparison.OrdinalIgnoreCase);
        var hasDevice = text.Contains("DEVICE:", StringComparison.OrdinalIgnoreCase);

        if (!hasRows || !hasCols) return 0;
        var score = hasDevice ? 0.95 : 0.85;
        var ext = probe.Extension?.ToLowerInvariant();
        if (ext == ".sinf") score = Math.Min(1.0, score + 0.05);
        return score;
    }

    public NeutralWaferMap Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var lines = File.ReadAllLines(path);
        var map = new NeutralWaferMap
        {
            Format = Format,
            SourcePath = path,
            SubstrateType = SubstrateType.Wafer,
        };

        var rows = 0;
        var cols = 0;
        var gridRows = new List<string[]>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                var key = line[..colon].Trim().ToUpperInvariant();
                var value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "ROWCT": int.TryParse(value, out rows); break;
                    case "COLCT": int.TryParse(value, out cols); break;
                    case "LOT": map.LotId = value; break;
                    case "WAFER": map.SubstrateId = value; break;
                    case "DEVICE": break;
                }
                continue;
            }

            // 数据段：空格分隔的 bin 码序列，一个序列视为一行
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2 && tokens.All(t => t.Length <= 4))
                gridRows.Add(tokens);
        }

        map.Layout = new NeutralLayout
        {
            Columns = cols,
            Rows = rows,
            OriginLocation = OriginLocation.LowerLeft,
            AxisDirection = AxisDirection.UpRight,
        };

        for (var r = 0; r < gridRows.Count; r++)
        {
            var tokens = gridRows[r];
            for (var c = 0; c < tokens.Length; c++)
            {
                var bin = tokens[c];
                var cls = Classify(bin);
                EnsureBin(map, bin, cls);
                map.AddDie(new NeutralDie(c, r) { BinCode = bin, Class = cls });
            }
        }

        if (cols > 0 && gridRows.Count > 0 && gridRows[0].Length != cols)
            map.AddIssue("SINF_COL_MISMATCH",
                $"首行 token 数 {gridRows[0].Length} 与 COLCT={cols} 不一致，已按实际 token 写入");

        map.AddIssue("SINF_DIALECT",
            "SINF 方言差异较大：行序与原点需按厂内样本确认，必要时用 CoordinateNormalizer 做上下翻转");

        return map;
    }

    private static NeutralBinClass Classify(string bin) => bin.ToUpperInvariant() switch
    {
        "1" or "P" or "PASS" or "GOOD" => NeutralBinClass.Pass,
        "0" or "F" or "FAIL" or "BAD" => NeutralBinClass.Fail,
        "." or "N" or "NULL" or "NONE" => NeutralBinClass.Null,
        "R" or "REF" => NeutralBinClass.Reference,
        "S" or "SKIP" => NeutralBinClass.Skip,
        _ => NeutralBinClass.Unknown,
    };

    private static void EnsureBin(NeutralWaferMap map, string bin, NeutralBinClass cls)
    {
        if (!map.BinDefinitions.ContainsKey(bin))
            map.AddBin(new NeutralBinDefinition { Code = bin, Class = cls, Description = cls.ToString() });
    }
}
