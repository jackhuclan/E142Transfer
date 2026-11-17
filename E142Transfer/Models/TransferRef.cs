namespace E142Transfer.Models;

/// <summary>
/// 一颗 die 的下游去向引用。挂在源 die（如 wafer 上的 die）上，
/// 表达"这颗 die 后来去了哪个载具的哪个位置"。
///
/// 对应 E142 中一个 &lt;T&gt; 元素：
///   FX/FY = 源 die 的阵列坐标（即本 die 的 X/Y）
///   TX/TY = 目标 die 的阵列坐标
///   FID   = 源 die 的字符串 ID
///   TID   = 目标 die 的字符串 ID
/// 而 FromSubstrateType / FromSubstrateId 记录在 TransferMap 节点属性上。
/// </summary>
public sealed class TransferRef
{
    /// <summary>目标载具类型（Strip / Tray / Frame / Wafer ...）。</summary>
    public SubstrateType TargetSubstrateType { get; set; } = SubstrateType.Strip;

    /// <summary>目标载具 ID，如 Strip ID（SEMI T9）。</summary>
    public string TargetSubstrateId { get; set; } = string.Empty;

    /// <summary>目标阵列 X 坐标（对应 E142 的 TX）。</summary>
    public int TargetX { get; set; }

    /// <summary>目标阵列 Y 坐标（对应 E142 的 TY）。</summary>
    public int TargetY { get; set; }

    /// <summary>目标 die 字符串 ID（对应 E142 的 TID），可空。</summary>
    public string? TargetDieId { get; set; }

    /// <summary>
    /// 堆叠序号。2.5D/3D 封装中同一封装位可能堆多颗 die，
    /// 用此字段区分第几层；单层封装填 0。
    /// </summary>
    public int StackLayer { get; set; }

    /// <summary>批次号，便于从成品反查 lot。</summary>
    public string? LotId { get; set; }

    /// <summary>晶圆在 lot 中的槽位号。</summary>
    public int? Slot { get; set; }

    /// <summary>生成时间。</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>自定义扩展位：可放 PL 判定、AOI 缺陷码等工艺侧信息。</summary>
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造一个指向目标载具位置的引用。</summary>
    public TransferRef(
        SubstrateType targetType,
        string targetId,
        int targetX,
        int targetY,
        string? targetDieId = null)
    {
        TargetSubstrateType = targetType;
        TargetSubstrateId = targetId ?? string.Empty;
        TargetX = targetX;
        TargetY = targetY;
        TargetDieId = targetDieId;
    }

    /// <summary>供序列化器使用的无参构造。</summary>
    public TransferRef() { }

    public DieIdentity ToTargetIdentity() =>
        new(TargetSubstrateType, TargetSubstrateId, TargetX, TargetY) { DieId = TargetDieId };

    public override string ToString() =>
        $"{E142EnumParser.ToE142(TargetSubstrateType)}:{TargetSubstrateId}" +
        $"({TargetX},{TargetY})" +
        (StackLayer > 0 ? $" L{StackLayer}" : string.Empty);
}
