using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import.Readers;

/// <summary>
/// SINF（Simplified Integrator Nested Format）读取器。
/// 覆盖标准头部字段与 RowData 段：BCEQU 良品列表、3 Digit 变体、
/// FF/FD/FE/@@/__ 特殊码，以及 REFPX/REFPY 的 1-based → 0-based 修正。
/// SINF 方言差异大，不确定的信息写入 Issues 而非静默猜测。
/// </summary>
public sealed class SinfMapReader : IWaferMapReader
{
    private static readonly char[] ValueSeparators = [' ', '\t', ',', ';'];

    private static readonly HashSet<string> HeaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "DEVICE", "LOT", "WAFER", "FNLOC", "ROWCT", "COLCT", "BCEQU",
        "REFPX", "REFPY", "DUTMS", "DIECT", "XDIES", "YDIES", "COMMENT",
    };

    public WaferMapFormat Format => WaferMapFormat.Sinf;

    public IReadOnlyList<string> SupportedExtensions => [".sinf", ".txt", ".map"];

    public double DetectConfidence(in FormatProbe probe)
    {
        var text = probe.HeadText;
        var hasRows = text.Contains("ROWCT:", StringComparison.OrdinalIgnoreCase);
        var hasCols = text.Contains("COLCT:", StringComparison.OrdinalIgnoreCase);
        if (!hasRows || !hasCols) return 0;

        var score = 0.80;
        if (text.Contains("RowData:", StringComparison.OrdinalIgnoreCase)) score += 0.10;
        if (text.Contains("BCEQU:", StringComparison.OrdinalIgnoreCase)) score += 0.05;
        if (text.Contains("FNLOC:", StringComparison.OrdinalIgnoreCase)) score += 0.03;
        if (text.Contains("DEVICE:", StringComparison.OrdinalIgnoreCase)) score += 0.02;
        var ext = probe.Extension?.ToLowerInvariant();
        if (ext is ".sinf" or ".map") score += 0.02;
        return Math.Min(1.0, score);
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
        var dieCount = -1;
        double stepX = 0, stepY = 0;
        string? fnloc = null, dutms = null;
        int? refPx = null, refPy = null;
        var hasBcequ = false;
        var goodBins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gridRows = new List<string[]>();
        var has3Digit = false;
        var hasLegacyTokens = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var colon = line.IndexOf(':');
            if (colon > 0 && HeaderKeys.Contains(line[..colon].Trim()))
            {
                var key = line[..colon].Trim().ToUpperInvariant();
                var value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "ROWCT": int.TryParse(value, out rows); break;
                    case "COLCT": int.TryParse(value, out cols); break;
                    case "DIECT": int.TryParse(value, out dieCount); break;
                    case "DEVICE": map.DeviceId = value; break;
                    case "LOT": map.LotId = value; break;
                    case "WAFER": map.SubstrateId = value; break;
                    case "FNLOC": fnloc = value; break;
                    case "DUTMS": dutms = value; break;
                    case "XDIES": double.TryParse(value, out stepX); break;
                    case "YDIES": double.TryParse(value, out stepY); break;
                    case "BCEQU":
                        hasBcequ = true;
                        foreach (var t in value.Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries))
                            goodBins.Add(t.Trim().ToUpperInvariant());
                        break;
                    case "REFPX": int.TryParse(value, out var px); refPx = px; break;
                    case "REFPY": int.TryParse(value, out var py); refPy = py; break;
                    // COMMENT: 注释字段，忽略
                }
                continue;
            }

            var payload = line;
            if (payload.StartsWith("RowData", StringComparison.OrdinalIgnoreCase))
            {
                var c = payload.IndexOf(':');
                if (c >= 0) payload = payload[(c + 1)..];
            }

            var tokens = payload.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            if (!tokens.All(IsRecognizedToken))
            {
                hasLegacyTokens = true;
                if (!tokens.All(IsLegacyToken)) continue;
            }
            if (tokens.Any(t => t.Length == 3 && t.All(char.IsDigit))) has3Digit = true;
            gridRows.Add(tokens);
        }

        if (!hasBcequ || goodBins.Count == 0)
        {
            goodBins.Add("01");
            map.AddIssue("SINF_BCEQU_DEFAULT",
                "BCEQU 缺失或为空，按规范默认 01 为 Pass；若厂内另有良品码请在头部显式声明");
        }

        map.Layout = new NeutralLayout
        {
            Columns = cols,
            Rows = rows,
            StepSizeX = stepX,
            StepSizeY = stepY,
            OriginLocation = OriginLocation.LowerLeft,
            AxisDirection = AxisDirection.UpRight,
        };

        var written = 0;
        for (var r = 0; r < gridRows.Count; r++)
        {
            var tokens = gridRows[r];
            for (var c = 0; c < tokens.Length; c++)
            {
                var bin = tokens[c];
                var cls = Classify(bin, goodBins);
                EnsureBin(map, bin, cls);
                map.AddDie(new NeutralDie(c, r) { BinCode = bin, Class = cls });
                written++;
            }
        }

        if (dieCount > 0 && written != dieCount)
            map.AddIssue("SINF_DIECT_MISMATCH",
                $"DIECT={dieCount} 与实际写入 die 数 {written} 不一致，已按实际数据写入");

        if (cols > 0 && gridRows.Count > 0 && gridRows[0].Length != cols)
            map.AddIssue("SINF_COL_MISMATCH",
                $"首行 token 数 {gridRows[0].Length} 与 COLCT={cols} 不一致，已按实际 token 写入");

        if (rows > 0 && gridRows.Count != rows)
            map.AddIssue("SINF_ROW_MISMATCH",
                $"数据行数 {gridRows.Count} 与 ROWCT={rows} 不一致，已按实际数据写入");

        if (refPx is not null || refPy is not null)
        {
            var rx = refPx is null ? -1 : Math.Max(0, refPx.Value - 1);
            var ry = refPy is null ? -1 : Math.Max(0, refPy.Value - 1);
            map.AddIssue("SINF_REFERENCE_1BASED",
                $"REFPX/REFPY 为 1-based，已转换为 0-based ({rx},{ry})；请用参考 die 复核 map 与物理 die 的对齐");
        }

        if (fnloc is not null)
            map.AddIssue("SINF_FNLOC",
                $"FNLOC={fnloc} 已记录但未参与坐标变换；投影前请按机台定义校验 flat/notch 方向");

        if (dutms is not null)
            map.AddIssue("SINF_DUTMS",
                $"DUTMS={dutms} 已记录；StepSize 单位需与目标 Layout 保持一致");

        if (has3Digit)
            map.AddIssue("SINF_3DIGIT",
                "检测到 3 Digit SINF（三位 bin / ___ 空位），各家约定不一致，需按厂内样本校准");

        if (hasLegacyTokens)
            map.AddIssue("SINF_LEGACY_TOKENS",
                "数据行含非规范 bin 码（如 P/F/.），已按旧式示例语义解析，建议改用标准 hex 码");

        map.AddIssue("SINF_DIALECT",
            "SINF 方言差异较大：行序与原点需按厂内样本确认，必要时用 CoordinateNormalizer 做上下翻转");

        return map;
    }

    private static bool IsRecognizedToken(string token)
    {
        var t = token.Trim();
        return t is "__" or "___" or "@@" or "@@@" or "FF" or "FD" or "FE"
            || (t.Length == 2 && t.All(Uri.IsHexDigit))
            || (t.Length == 3 && t.All(char.IsDigit));
    }

    private static bool IsLegacyToken(string token) => token.Trim().ToUpperInvariant() is
        "P" or "PASS" or "GOOD" or "1" or "0" or "F" or "FAIL" or "BAD" or
        "." or "N" or "NULL" or "NONE" or "R" or "REF" or "S" or "SKIP";

    private static NeutralBinClass Classify(string token, IReadOnlySet<string> goodBins)
    {
        var t = token.Trim().ToUpperInvariant();
        if (t is "__" or "___") return NeutralBinClass.Null;
        if (t is "@@" or "@@@") return NeutralBinClass.Skip;
        if (t == "FF") return NeutralBinClass.Reference;
        if (t is "FD" or "FE") return NeutralBinClass.Edge;
        if (goodBins.Contains(t)) return NeutralBinClass.Pass;

        // 兼容旧示例用的宽松写法；未声明为 good 的规范 bin 一律不拾取。
        return t switch
        {
            "P" or "PASS" or "GOOD" or "1" => NeutralBinClass.Pass,
            "0" or "F" or "FAIL" or "BAD" => NeutralBinClass.Fail,
            "." or "N" or "NULL" or "NONE" => NeutralBinClass.Null,
            "R" or "REF" => NeutralBinClass.Reference,
            "S" or "SKIP" => NeutralBinClass.Skip,
            _ => NeutralBinClass.Fail,
        };
    }

    private static void EnsureBin(NeutralWaferMap map, string bin, NeutralBinClass cls)
    {
        if (!map.BinDefinitions.ContainsKey(bin))
            map.AddBin(new NeutralBinDefinition { Code = bin, Class = cls, Description = cls.ToString() });
    }
}
