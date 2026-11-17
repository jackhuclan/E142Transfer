namespace E142Transfer.Models;

/// <summary>
/// 一颗 die 在某一层载体上的身份：载具类型 + 载具 ID + 阵列坐标。
/// E142 中 TransferMap 的 "源" 与 "目标" 都可用这个结构表达。
/// </summary>
public readonly record struct DieIdentity(
    SubstrateType SubstrateType,
    string SubstrateId,
    int X,
    int Y)
{
    /// <summary>E142 中可选的 die 字符串 ID（对应 FID / TID）。</summary>
    public string? DieId { get; init; }

    public override string ToString() =>
        $"{E142EnumParser.ToE142(SubstrateType)}:{SubstrateId}({X},{Y})" +
        (DieId is null ? string.Empty : $"[{DieId}]");
}
