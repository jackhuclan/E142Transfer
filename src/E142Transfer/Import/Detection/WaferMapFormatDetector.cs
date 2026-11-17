using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Readers;

namespace E142Transfer.Import.Detection;

/// <summary>无法识别格式时抛出。</summary>
public sealed class UnsupportedWaferMapFormatException : Exception
{
    public UnsupportedWaferMapFormatException(string message) : base(message) { }
}

/// <summary>
/// 格式自动识别器：扩展名 + 文件头内容嗅探，按置信度选 Reader。
/// 内容优先于扩展名——现场常有 .txt 装着 E142、.csv 装着 SINF 的情况。
/// </summary>
public sealed class WaferMapFormatDetector
{
    /// <summary>文件头嗅探字节数，导入门面的 Probe() 也复用这个值。</summary>
    public const int ProbeSize = 8192;
    private readonly IReadOnlyList<IWaferMapReader> _readers;

    public WaferMapFormatDetector(IEnumerable<IWaferMapReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers = readers.ToArray();
        if (_readers.Count == 0) throw new ArgumentException("至少需要一个 Reader", nameof(readers));
    }

    public WaferMapFormatDetector(params IWaferMapReader[] readers) : this((IEnumerable<IWaferMapReader>)readers) { }

    /// <summary>默认 Reader 集合。新增格式时在这里注册。</summary>
    public static WaferMapFormatDetector CreateDefault() => new(
        new E142MapReader(),
        new XyBinCsvReader(),
        new SinfMapReader(),
        new G85MapReader(),
        new TskDatReader());

    /// <summary>识别文件应使用的 Reader。</summary>
    public IWaferMapReader Detect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) throw new FileNotFoundException("晶圆图文件不存在", path);

        var head = new byte[ProbeSize];
        var read = 0;
        using (var fs = File.OpenRead(path))
            read = fs.Read(head, 0, head.Length);

        var probe = new FormatProbe(
            Extension: Path.GetExtension(path),
            Head: head.AsMemory(0, read));

        return Detect(in probe);
    }

    /// <summary>识别内存中的文件头，便于单元测试或上传流场景。</summary>
    public IWaferMapReader Detect(in FormatProbe probe)
    {
        var best = (Reader: (IWaferMapReader?)null, Score: 0.0);
        foreach (var reader in _readers)
        {
            var score = reader.DetectConfidence(in probe);
            if (score > best.Score) best = (reader, score);
        }

        if (best.Reader is null || best.Score < 0.5)
            throw new UnsupportedWaferMapFormatException(
                $"无法识别晶圆图格式（最高置信度 {best.Score:F2}）");

        return best.Reader;
    }

    /// <summary>同时返回识别结果和得分，供日志与 UI 显示。</summary>
    public (IWaferMapReader Reader, double Score) DetectWithScore(in FormatProbe probe)
    {
        var reader = Detect(in probe);
        return (reader, reader.DetectConfidence(in probe));
    }
}
