using E142Transfer.Import.Model;

namespace E142Transfer.Import.Abstractions;

/// <summary>已知晶圆图格式。新增格式只需在此加一个枚举值 + 一个 Reader。</summary>
public enum WaferMapFormat
{
    Unknown = 0,
    E142,
    CsvXyBin,
    Sinf,
    Klarf,
    Tel,
    Tff,
    Stf,
    Other,
}

/// <summary>
/// 格式探测输入。只读前若干字节 + 扩展名 + 可选厂内提示，
/// 保证识别过程不读整个大文件、不依赖第三方库。
/// </summary>
public readonly record struct FormatProbe(
    string? Extension,
    ReadOnlyMemory<byte> Head,
    string? Hint = null)
{
    /// <summary>把文件头解码为文本（宽松解码，坏字节替换为 U+FFFD）。</summary>
    public string HeadText => System.Text.Encoding.UTF8.GetString(Head.Span);
}

/// <summary>
/// 格式读取适配器。所有格式统一实现此接口，输出 NeutralWaferMap。
/// 这是"多对一"收敛点：分析层永远只见中性模型，不见文件格式。
/// </summary>
public interface IWaferMapReader
{
    WaferMapFormat Format { get; }

    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>0..1 的匹配置信度，0 表示完全不匹配。</summary>
    double DetectConfidence(in FormatProbe probe);

    /// <summary>读取并归一化为中性模型；解析问题记录到 map.Issues，不轻易抛异常。</summary>
    NeutralWaferMap Read(string path);
}

/// <summary>格式写出适配器。目前提供 CSV/XYBin，后续可加 E142/SINF。</summary>
public interface IWaferMapWriter
{
    WaferMapFormat Format { get; }

    void Write(NeutralWaferMap map, string path);
}
