namespace WaferMapStudio.Core.Visuals;

/// <summary>
/// BinCode → 颜色的调色板。
///
/// 两级映射：bin 码有专属配色（同类别不同码 = 同色系不同深浅，用于区分多种失效机理）
/// 时优先按码取色；否则回落到类别基色。亮色主题下 Null 空位用浅灰格呈现晶圆外形。
/// </summary>
public sealed class BinColorPalette
{
    private readonly Dictionary<string, DieColor> _binColors;

    public BinColorPalette(IReadOnlyDictionary<string, DieColor>? binOverrides = null) =>
        _binColors = binOverrides is null
            ? []
            : new Dictionary<string, DieColor>(binOverrides, StringComparer.Ordinal);

    /// <summary>类别基色（亮色主题语义色，与 App 的 Light.xaml 调性一致；白底上清晰可辨）。</summary>
    public static DieColor CategoryColor(DieCategory category) => category switch
    {
        DieCategory.Pass => new DieColor(0x16, 0xA3, 0x4A),
        DieCategory.Fail => new DieColor(0xDC, 0x26, 0x26),
        DieCategory.Marked => new DieColor(0xD9, 0x77, 0x06),
        DieCategory.Reference => new DieColor(0x25, 0x63, 0xEB),
        DieCategory.Skip => new DieColor(0x7C, 0x3A, 0xED),
        DieCategory.Edge => new DieColor(0x0E, 0xA5, 0xE9),
        DieCategory.Unknown => new DieColor(0x64, 0x74, 0x8B),
        _ => new DieColor(0xE2, 0xE8, 0xF0),
    };

    /// <summary>取 die 的显示颜色：bin 码专属色优先，类别基色兜底。</summary>
    public DieColor ColorOf(DieVisual die) =>
        _binColors.TryGetValue(die.BinCode, out var color)
            ? color
            : CategoryColor(die.Category);

    /// <summary>
    /// 从一张视觉 map 生成调色板：同一类别的每个不同 bin 码分配该类别色系下的不同深浅，
    /// 保证"不同 BinCode → 不同颜色"，同时颜色语义仍可读（红系=失效、绿系=良品）。
    /// 分配按码值首次出现的行列顺序，结果确定可复现。
    /// </summary>
    public static BinColorPalette FromMap(WaferMapVisual map)
    {
        ArgumentNullException.ThrowIfNull(map);

        var overrides = new Dictionary<string, DieColor>(StringComparer.Ordinal);
        var counters = new Dictionary<DieCategory, int>();
        foreach (var die in map.Dies)
        {
            if (!die.IsRealDie || overrides.ContainsKey(die.BinCode))
            {
                continue;
            }

            var index = counters.GetValueOrDefault(die.Category);
            counters[die.Category] = index + 1;
            var shades = ShadesOf(die.Category);
            overrides[die.BinCode] = shades[index % shades.Length];
        }

        return new BinColorPalette(overrides);
    }

    /// <summary>
    /// 生成图例：按 bin 码聚合（含 Null 空位），数量降序；同码不同类别取高频类别的颜色。
    /// </summary>
    public IReadOnlyList<LegendEntry> BuildLegend(WaferMapVisual map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return map.Dies
            .GroupBy(d => d.IsRealDie ? d.BinCode : string.Empty)
            .Select(g =>
            {
                var topCategory = g.GroupBy(d => d.Category)
                    .OrderByDescending(c => c.Count())
                    .First().Key;
                var representative = g.First() with { Category = topCategory };
                var color = g.Key.Length == 0
                    ? CategoryColor(DieCategory.Null)
                    : ColorOf(representative);
                var definition = g.First();
                var label = g.Key.Length == 0
                    ? "空位"
                    : string.IsNullOrEmpty(definition.Description) || definition.Description == topCategory.ToString()
                        ? g.Key
                        : $"{g.Key} · {definition.Description}";
                return new LegendEntry(g.Key, label, color, g.Count());
            })
            .OrderByDescending(e => e.Count)
            .ToList();
    }

    /// <summary>类别色系下的深浅序列：同类别多码时逐个取用，循环复用。</summary>
    private static DieColor[] ShadesOf(DieCategory category) => category switch
    {
        DieCategory.Pass => PassShades,
        DieCategory.Fail => FailShades,
        DieCategory.Marked => MarkedShades,
        _ => OtherShades,
    };

    // 亮色主题下的色系深浅：白底可读的中饱和度语义色；各系列首项与 CategoryColor 基色一致。
    private static readonly DieColor[] PassShades =
    [
        new(0x16, 0xA3, 0x4A), new(0x22, 0xC5, 0x5E), new(0x15, 0x80, 0x3D), new(0x0E, 0x9F, 0x6E),
    ];

    private static readonly DieColor[] FailShades =
    [
        new(0xDC, 0x26, 0x26), new(0xEF, 0x44, 0x44), new(0xB9, 0x1C, 0x1C), new(0xE1, 0x1D, 0x48), new(0x99, 0x1B, 0x1B),
    ];

    private static readonly DieColor[] MarkedShades =
    [
        new(0xD9, 0x77, 0x06), new(0xF5, 0x9E, 0x0B), new(0xB4, 0x53, 0x09), new(0xCA, 0x8A, 0x04),
    ];

    private static readonly DieColor[] OtherShades =
    [
        new(0x25, 0x63, 0xEB), new(0x0E, 0xA5, 0xE9), new(0x7C, 0x3A, 0xED), new(0x64, 0x74, 0x8B), new(0x02, 0x84, 0xC7), new(0x93, 0x33, 0xEA),
    ];
}
