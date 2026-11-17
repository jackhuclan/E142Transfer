namespace WaferMapStudio.Io;

/// <summary>
/// 点位映射表：语义点位 ↔ 物理地址（Bank/Bit/极性）的唯一事实来源。
/// 由 <see cref="IOManagerPointTable.Load(string)"/> 从真实点表构建，构建时完成校验，
/// 运行时只做 O(1) 查表与位运算，不碰硬件。
/// </summary>
public sealed class IoPointMap
{
    private readonly IReadOnlyDictionary<IoPoint, IoPointDef> _byPoint;

    /// <summary>创建映射表。通常用 IOManagerPointTable.Load 构建，而非直接 new。</summary>
    /// <param name="definitions">已校验的点位定义。</param>
    /// <param name="inputBankCount">输入字节组数量。</param>
    /// <param name="outputBankCount">输出字节组数量。</param>
    public IoPointMap(
        IEnumerable<IoPointDef> definitions,
        int inputBankCount,
        int outputBankCount)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (inputBankCount <= 0) throw new ArgumentOutOfRangeException(nameof(inputBankCount));
        if (outputBankCount <= 0) throw new ArgumentOutOfRangeException(nameof(outputBankCount));

        var defs = definitions.ToList();
        _byPoint = defs.ToDictionary(d => d.Point);
        Points = defs.AsReadOnly();
        InputBankCount = inputBankCount;
        OutputBankCount = outputBankCount;

        // 构建期即校验地址合法性：任何点都必须落在声明的字节组范围内。
        foreach (IoPointDef d in defs)
        {
            int bankCount = d.Direction == IoDirection.Input ? inputBankCount : outputBankCount;
            if (d.Bank < 0 || d.Bank >= bankCount)
                throw new IoPointTableException(
                    $"点位 {d.Point}({d.IoName}) 的 Bank={d.Bank} 超出 {d.Direction} 的字节组数 {bankCount}");
            if (d.Bit is < 0 or > 7)
                throw new IoPointTableException($"点位 {d.Point}({d.IoName}) 的 Bit={d.Bit} 不在 0–7");
        }
    }

    /// <summary>全部已定义点位（只读）。</summary>
    public IReadOnlyList<IoPointDef> Points { get; }

    /// <summary>输入字节组数量（真实点表为 4：IN_1…IN_4）。</summary>
    public int InputBankCount { get; }

    /// <summary>输出字节组数量（真实点表为 4：OUT_1…OUT_4）。</summary>
    public int OutputBankCount { get; }

    /// <summary>按点位查询定义；未定义抛 <see cref="IoValidationException"/>。</summary>
    public IoPointDef Require(IoPoint point) =>
        _byPoint.TryGetValue(point, out IoPointDef? d)
            ? d
            : throw new IoValidationException($"点位 {point} 未在点表中定义");

    /// <summary>尝试查询定义。</summary>
    public bool TryGet(IoPoint point, out IoPointDef? def) => _byPoint.TryGetValue(point, out def);

    /// <summary>从输入帧解码某点的逻辑值（含低有效取反）。</summary>
    /// <param name="point">目标点位，必须是输入方向。</param>
    /// <param name="inputFrame">输入字节帧，长度至少 InputBankCount。</param>
    public bool Decode(IoPoint point, ReadOnlySpan<byte> inputFrame)
    {
        IoPointDef d = Require(point);
        if (d.Direction != IoDirection.Input)
            throw new IoValidationException($"点位 {point} 是输出点，不能从输入帧解码");
        return DecodeCore(d, inputFrame);
    }

    /// <summary>从输出回读帧解码某点的逻辑值（含低有效取反），用于写后校验。</summary>
    public bool DecodeOutput(IoPoint point, ReadOnlySpan<byte> outputFrame)
    {
        IoPointDef d = Require(point);
        if (d.Direction != IoDirection.Output)
            throw new IoValidationException($"点位 {point} 是输入点，不能从输出帧解码");
        return DecodeCore(d, outputFrame);
    }

    /// <summary>把逻辑值编码为写入硬件的位值（低有效点取反）。</summary>
    public bool EncodeOutput(IoPoint point, bool logicalValue)
    {
        IoPointDef d = Require(point);
        if (d.Direction != IoDirection.Output)
            throw new IoValidationException($"点位 {point} 是输入点，不能写");
        return d.ActiveLow ? !logicalValue : logicalValue;
    }

    /// <summary>写出位值到输出帧（只改目标位，其他位保持），返回是否需要实际下发。</summary>
    public bool ApplyOutput(IoPoint point, bool physicalValue, Span<byte> outputFrame)
    {
        IoPointDef d = Require(point);
        if (d.Direction != IoDirection.Output)
            throw new IoValidationException($"点位 {point} 是输入点，不能写");
        if (d.Bank >= outputFrame.Length)
            throw new IoValidationException($"输出帧长度 {outputFrame.Length} 不足以访问 Bank={d.Bank}");

        byte mask = (byte)(1 << d.Bit);
        byte old = outputFrame[d.Bank];
        byte updated = physicalValue ? (byte)(old | mask) : (byte)(old & ~mask);
        outputFrame[d.Bank] = updated;
        return old != updated; // 值未变化可跳过硬件写入，减少无谓往返
    }

    /// <summary>所有输出点的安全态位值（逻辑 false 对应的物理值）。</summary>
    public IReadOnlyDictionary<IoPoint, bool> SafeOutputStates() =>
        Points
            .Where(d => d.Direction == IoDirection.Output)
            .ToDictionary(d => d.Point, d => d.ActiveLow); // 逻辑 false：高有效→0，低有效→1

    private static bool DecodeCore(IoPointDef d, ReadOnlySpan<byte> frame)
    {
        if (d.Bank >= frame.Length)
            throw new IoValidationException($"帧长度 {frame.Length} 不足以访问点位 {d.Point} 的 Bank={d.Bank}");
        bool raw = (frame[d.Bank] & (1 << d.Bit)) != 0;
        return d.ActiveLow ? !raw : raw;
    }
}
