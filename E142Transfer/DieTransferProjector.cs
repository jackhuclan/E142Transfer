using E142Transfer.Models;

namespace E142Transfer;

/// <summary>
/// 把上位机里的 DieResult 集合投影成 E142 TransferMap，
/// 或把 E142 TransferMap 反查回"每颗源 die 的下游去向"。
///
/// 这是把"三映射分析"升级为"追溯链一环"的桥接层。
/// </summary>
public sealed class DieTransferProjector
{
    private readonly MapFrame _sourceFrame;

    public DieTransferProjector(MapFrame sourceFrame)
    {
        ArgumentNullException.ThrowIfNull(sourceFrame);
        _sourceFrame = sourceFrame;
    }

    /// <summary>
    /// 从 die 结果集合生成 TransferMap。
    /// 只处理有 Transfers 的 die；NullBin 空位会被跳过。
    /// </summary>
    public E142TransferMap BuildTransferMap(
        IEnumerable<DieResult> dies,
        string mapName = "TransferMap",
        string mapVersion = "1")
    {
        ArgumentNullException.ThrowIfNull(dies);

        var tm = new E142TransferMap
        {
            MapName = mapName,
            MapVersion = mapVersion,
            FromSubstrateType = _sourceFrame.SubstrateType,
            FromSubstrateId = _sourceFrame.SubstrateId,
            FromLayoutSpecifier = _sourceFrame.LayoutSpecifier,
        };

        foreach (var die in dies)
        {
            if (!die.IsDie || die.Transfers.Count == 0) continue;

            foreach (var r in die.Transfers)
            {
                tm.Entries.Add(new TransferEntry
                {
                    FX = die.X,
                    FY = die.Y,
                    TX = r.TargetX,
                    TY = r.TargetY,
                    FID = die.BinCode,
                    TID = r.TargetDieId,
                });
            }
        }

        return tm;
    }

    /// <summary>
    /// 按源 die 坐标建立"下游去向"索引，方便从上位机界面点一颗 die 就列出去向。
    /// </summary>
    public static Dictionary<(int X, int Y), List<TransferRef>> BuildForwardIndex(
        IEnumerable<DieResult> dies)
    {
        ArgumentNullException.ThrowIfNull(dies);

        var index = new Dictionary<(int, int), List<TransferRef>>();
        foreach (var die in dies)
        {
            if (die.Transfers.Count == 0) continue;
            index[(die.X, die.Y)] = [.. die.Transfers];
        }
        return index;
    }

    /// <summary>
    /// 从解析后的 E142 文档建立反查索引：目标载具位置 → 源 die。
    /// 用于 field failure 反查：知道成品位号，查回晶圆坐标。
    /// </summary>
    public static Dictionary<(string TargetId, int X, int Y), List<DieIdentity>> BuildReverseIndex(
        E142DocumentModel doc)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var index = new Dictionary<(string, int, int), List<DieIdentity>>();
        foreach (var (_, tm) in doc.TransferMaps)
        {
            foreach (var e in tm.Entries)
            {
                if (!e.HasCoordinates) continue;
                var key = (tm.MapName, e.TX!.Value, e.TY!.Value);
                if (!index.TryGetValue(key, out var list))
                    index[key] = list = [];
                list.Add(new DieIdentity(tm.FromSubstrateType, tm.FromSubstrateId,
                    e.FX!.Value, e.FY!.Value) { DieId = e.FID });
            }
        }
        return index;
    }
}
