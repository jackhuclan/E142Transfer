using System.Globalization;
using E142Transfer.Import.Model;

namespace E142Transfer.Import.Readers;

/// <summary>
/// TSK .XTR 伴生参数文件中的 bin 表解析结果。
///
/// XTR 是纯文本，bin 表每行形如 <c>NAME ,RGB ,binCode ,QUALITY</c>，
/// QUALITY ∈ {GOOD, BAD, None}；区间行（Unused_Bingroup 的 11-18,24-29,…）只声明
/// 未使用区段，解析时跳过。DAT 自身只有 bin 数字，语义（GOOD/BAD/None）完全来自这里，
/// 所以两者的配对必须按"计数能对上"来确定，而不是按文件名猜测。
/// </summary>
internal sealed class XtrBinTable
{
    private static readonly string[] QualityTokens = ["GOOD", "BAD", "NONE"];

    private readonly Dictionary<int, (string Name, NeutralBinClass Class)> _bins = [];

    private XtrBinTable(string sourcePath) => SourcePath = sourcePath;

    public string SourcePath { get; }

    public int Count => _bins.Count;

    public bool TryGet(int code, out string name, out NeutralBinClass cls)
    {
        if (_bins.TryGetValue(code, out var entry))
        {
            name = entry.Name;
            cls = entry.Class;
            return true;
        }
        name = string.Empty;
        cls = NeutralBinClass.Unknown;
        return false;
    }

    /// <summary>
    /// 在同目录下找与晶圆 ID 同名前缀的 .XTR，取计数能与 DAT 头部对账的那一份。
    /// 一批晶圆常有首测/复测两份 XTR，且同一 bin 码在两次测试里含义不同
    /// （首测 GOOD=bin30、复测 GOOD=bin0），所以只能靠对账选，不能取最新或最旧。
    /// </summary>
    public static XtrMatch? FindBest(
        string datPath,
        string waferId,
        string datEndTime,
        IReadOnlyDictionary<(int Bin, NeutralBinClass Class), int> binCounts,
        int headerPass,
        int headerFail)
    {
        var directory = Path.GetDirectoryName(datPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrWhiteSpace(waferId) || headerPass <= 0)
            return null;

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory);
        }
        catch (IOException)
        {
            return null;
        }

        var datStamp = ParseDatStamp(datEndTime);
        var best = (Table: (XtrBinTable?)null, Score: 0, Delta: long.MaxValue);
        foreach (var file in candidates)
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".XTR", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.StartsWith(waferId, StringComparison.OrdinalIgnoreCase)) continue;

            var table = TryLoad(file);
            if (table is null) continue;

            var score = table.Score(binCounts, headerPass, headerFail);

            // 同一颗晶圆常有首测/复测两份 XTR，对实际出现的 bin 判定完全一致、得分相同。
            // 此时用文件名时间戳与 DAT 结束时间的接近度裁决：DAT 记录的是哪一轮，就取哪一轮。
            var delta = long.MaxValue;
            if (datStamp is { } dt && ParseXtrStamp(name) is { } xt)
                delta = Math.Abs(xt - dt);

            if (score > best.Score || (score == best.Score && delta < best.Delta))
                best = (table, score, delta);
        }

        // 只有 GOOD / 非 GOOD 两项计数全部对上才认为配对成功，避免拿错测试轮次的 bin 表。
        return best.Table is not null && best.Score >= 3 ? new XtrMatch(best.Table) : null;
    }

    /// <summary>XTR 文件名尾部 YYYYMMDDHHMMSS。</summary>
    private static long? ParseXtrStamp(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length < 14) return null;
        var digits = stem[^14..];
        foreach (var c in digits)
            if (!char.IsAsciiDigit(c)) return null;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>DAT 头部时间戳 YYMMDDHHMM → YYYYMMDDHHMMSS（补 20 前缀与秒位）。</summary>
    private static long? ParseDatStamp(string endTime)
    {
        if (endTime.Length != 10) return null;
        foreach (var c in endTime)
            if (!char.IsAsciiDigit(c)) return null;
        return long.TryParse("20" + endTime + "00", NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static XtrBinTable? TryLoad(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return null;
        }

        var table = new XtrBinTable(path);
        foreach (var line in lines)
        {
            var parts = line.Split(',');
            if (parts.Length < 4) continue;

            var quality = parts[^1].Trim().ToUpperInvariant();
            if (!QualityTokens.Contains(quality)) continue;
            if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                continue;
            if (code is < 0 or > 255) continue;

            table._bins[code] = (parts[0].Trim(), quality switch
            {
                "GOOD" => NeutralBinClass.Pass,
                "BAD" => NeutralBinClass.Fail,
                // None：已测但既非良品也非明确不良（EDGE_DIE / PROTECT / WAFER_ID_ZONE / MIRROR_DIE）
                _ => NeutralBinClass.Marked,
            });
        }

        return table._bins.Count > 0 ? table : null;
    }

    /// <summary>用 bin 直方图反算 GOOD / 非 GOOD 数量，与 DAT 头部计数比对。</summary>
    private int Score(
        IReadOnlyDictionary<(int Bin, NeutralBinClass Class), int> binCounts,
        int headerPass,
        int headerFail)
    {
        var good = 0;
        var other = 0;
        foreach (var ((bin, cls), count) in binCounts)
        {
            if (cls == NeutralBinClass.Null) continue; // 空位不进头部计数
            if (_bins.TryGetValue(bin, out var entry) && entry.Class == NeutralBinClass.Pass) good += count;
            else other += count;
        }

        var score = 0;
        if (good == headerPass) score += 2;
        if (other == headerFail) score += 1;
        return score;
    }
}

/// <summary>配对成功的 XTR bin 表。</summary>
internal sealed record XtrMatch(XtrBinTable Table);
