namespace WaferMapStudio.Core.Visuals;

/// <summary>
/// die 的视觉类别。是 <see cref="E142Transfer.Import.Model.NeutralBinClass"/>
/// 在渲染侧的投影：渲染只关心"涂什么颜色、算不算良品"，不关心格式方言。
/// </summary>
public enum DieCategory
{
    /// <summary>阵列空位（无 die）：渲染为暗色底格，用于呈现晶圆外形。</summary>
    Null = 0,
    Pass,
    Fail,
    /// <summary>已标记但未判定（如 G85/Camtek Marked Bin）。</summary>
    Marked,
    Reference,
    Skip,
    Edge,
    Unknown,
}

/// <summary>渲染侧颜色（通道 0–255）。Core 不引用 WPF 类型，由 Rendering 负责转 Brush。</summary>
public readonly record struct DieColor(byte R, byte G, byte B)
{
    /// <summary>形如 "#RRGGBB" 的十六进制表示（主题/日志友好）。</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static DieColor FromHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        var s = hex.TrimStart('#');
        if (s.Length != 6 || !s.All(Uri.IsHexDigit))
            throw new FormatException($"颜色须为 #RRGGBB：{hex}");
        return new DieColor(
            Convert.ToByte(s[..2], 16),
            Convert.ToByte(s[2..4], 16),
            Convert.ToByte(s[4..6], 16));
    }
}
