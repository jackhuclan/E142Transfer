using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import.Readers;

/// <summary>
/// CSV / XYBin 读取器：最轻量的 "x,y,bin" 三列格式。
/// 也兼容四列 "x,y,bin,class" 的厂内扩展。
/// </summary>
public sealed class XyBinCsvReader : IWaferMapReader
{
    public WaferMapFormat Format => WaferMapFormat.CsvXyBin;

    public IReadOnlyList<string> SupportedExtensions => [".csv", ".xybin", ".txt"];

    public double DetectConfidence(in FormatProbe probe)
    {
        var text = probe.HeadText;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().TrimEnd('\r'))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Take(6)
            .ToArray();

        if (lines.Length == 0) return 0;

        var score = 0.0;
        foreach (var line in lines)
        {
            var normalized = new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (normalized.StartsWith("x,y,bin", StringComparison.OrdinalIgnoreCase))
            {
                score = Math.Max(score, 0.95);
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            if (TryNumber(parts[0], out _) && TryNumber(parts[1], out _))
                score = Math.Max(score, 0.88);
        }

        var ext = probe.Extension?.ToLowerInvariant();
        if (ext is ".csv" or ".xybin" && score > 0) score = Math.Min(1.0, score + 0.05);
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
            SubstrateId = Path.GetFileNameWithoutExtension(path),
        };

        var coords = new List<(int X, int Y, string Bin, string? ClassHint)>();
        var lineNo = 0;
        foreach (var raw in lines)
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length < 3) continue;

            var header = new string(line.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (header.StartsWith("x,y,bin", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TryNumber(parts[0], out var xd) || !TryNumber(parts[1], out var yd))
            {
                map.AddIssue("CSV_SKIP_LINE", $"第 {lineNo} 行坐标无法解析，已跳过");
                continue;
            }

            coords.Add(((int)Math.Round(xd), (int)Math.Round(yd), parts[2],
                parts.Length >= 4 ? parts[3] : null));
        }

        if (coords.Count == 0)
        {
            map.AddIssue("CSV_EMPTY", "未解析到任何 die 记录", isWarning: false);
            return map;
        }

        var minX = coords.Min(c => c.X);
        var minY = coords.Min(c => c.Y);
        var maxX = coords.Max(c => c.X);
        var maxY = coords.Max(c => c.Y);
        var needsShift = minX < 0 || minY < 0;

        foreach (var (x, y, bin, hint) in coords)
        {
            var nx = x - minX;
            var ny = y - minY;
            var cls = Classify(bin, hint);
            EnsureBin(map, bin, cls, hint);
            map.AddDie(new NeutralDie(nx, ny) { BinCode = bin, Class = cls });
        }

        if (needsShift)
            map.AddIssue("CSV_COORD_SHIFT",
                $"原始坐标含负值，已平移到 0 基准：offset=({minX},{minY})");

        // CSV 缺少 pitch / origin 等元数据，按规范默认补齐，并留告警。
        map.Layout = new NeutralLayout
        {
            Columns = maxX - minX + 1,
            Rows = maxY - minY + 1,
            OriginLocation = OriginLocation.LowerLeft,
            AxisDirection = AxisDirection.UpRight,
        };
        map.AddIssue("CSV_NO_METADATA",
            "CSV/XYBin 不含 pitch/orientation/reference die，已按 E142 默认值补齐；上线前须用 reference die 实物核对");

        return map;
    }

    private static bool TryNumber(string s, out double value) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private static NeutralBinClass Classify(string bin, string? hint)
    {
        var b = Normalize(bin);
        var h = Normalize(hint);
        if (h is "PASS" or "GOOD" or "OK") return NeutralBinClass.Pass;
        if (h is "FAIL" or "BAD" or "NG") return NeutralBinClass.Fail;
        return b switch
        {
            "1" or "P" or "PASS" or "GOOD" => NeutralBinClass.Pass,
            "0" or "F" or "FAIL" or "BAD" => NeutralBinClass.Fail,
            "." or "N" or "NULL" or "NONE" or "" => NeutralBinClass.Null,
            "R" or "REF" => NeutralBinClass.Reference,
            "S" or "SKIP" => NeutralBinClass.Skip,
            _ => NeutralBinClass.Unknown,
        };
    }

    private static string Normalize(string? s) =>
        new string((s ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static void EnsureBin(NeutralWaferMap map, string bin, NeutralBinClass cls, string? hint)
    {
        if (map.BinDefinitions.ContainsKey(bin)) return;
        map.AddBin(new NeutralBinDefinition
        {
            Code = bin,
            Class = cls,
            Description = string.IsNullOrWhiteSpace(hint) ? cls.ToString() : hint!,
        });
    }
}
