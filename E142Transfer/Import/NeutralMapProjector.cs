using E142Transfer.Import.Model;
using E142Transfer.Models;

namespace E142Transfer.Import;

/// <summary>
/// 把中性 map 投影成上位机现有的 DieResult，
/// 使"多格式导入"与"三映射分析 + E142 追溯"两张链路接起来。
/// </summary>
public static class NeutralMapProjector
{
    public static List<DieResult> ToDieResults(NeutralWaferMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var transfersBySource = map.Transfers
            .GroupBy(t => (t.FX, t.FY))
            .ToDictionary(g => g.Key, g => g.ToArray());

        var result = new List<DieResult>(map.Dies.Count);
        foreach (var (x, y) in map.Dies.Keys.OrderBy(k => k.Y).ThenBy(k => k.X))
        {
            var die = map.Dies[(x, y)];
            var d = new DieResult
            {
                X = x,
                Y = y,
                BinCode = die.BinCode,
                IsDie = die.IsDie,
            };

            if (map.BinDefinitions.TryGetValue(die.BinCode, out var bin))
            {
                d.Bin = new BinDefinition
                {
                    Code = bin.Code,
                    Description = bin.Description,
                    Quality = ToQuality(bin.Class),
                };
            }

            if (transfersBySource.TryGetValue((x, y), out var list))
            {
                foreach (var t in list)
                {
                    d.Transfers.Add(new TransferRef(
                        t.TargetSubstrateType, t.TargetSubstrateId, t.TX, t.TY, t.Tid));
                }
            }

            result.Add(d);
        }
        return result;
    }

    private static BinQuality ToQuality(NeutralBinClass cls) => cls switch
    {
        NeutralBinClass.Pass => BinQuality.Pass,
        NeutralBinClass.Fail => BinQuality.Fail,
        NeutralBinClass.Null => BinQuality.Null,
        NeutralBinClass.Reference => BinQuality.Reference,
        NeutralBinClass.Skip => BinQuality.Skip,
        NeutralBinClass.Edge => BinQuality.Edge,
        NeutralBinClass.Marked => BinQuality.Marked,
        _ => BinQuality.Unknown,
    };
}
