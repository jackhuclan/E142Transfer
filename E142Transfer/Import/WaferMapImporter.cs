using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Detection;
using E142Transfer.Import.Model;

namespace E142Transfer.Import;

/// <summary>一次导入的结果：识别出的格式、使用的 Reader、中性 map。</summary>
public sealed record ImportOutcome(
    WaferMapFormat Format,
    IWaferMapReader Reader,
    NeutralWaferMap Map);

/// <summary>
/// 导入门面。上位机只调用 Import()，格式细节全在适配器层内。
/// </summary>
public sealed class WaferMapImporter
{
    private readonly WaferMapFormatDetector _detector;

    public WaferMapImporter(WaferMapFormatDetector? detector = null)
    {
        _detector = detector ?? WaferMapFormatDetector.CreateDefault();
    }

    public ImportOutcome Import(string path)
    {
        var reader = _detector.Detect(path);
        var map = reader.Read(path);
        return new ImportOutcome(reader.Format, reader, map);
    }

    public NeutralWaferMap ImportMap(string path) => Import(path).Map;

    /// <summary>只识别不读取，用于 UI 预检或批量分流。</summary>
    public (WaferMapFormat Format, double Score) Probe(string path)
    {
        var (reader, score) = _detector.DetectWithScore(
            ReadProbe(path, WaferMapFormatDetector.ProbeSize));
        return (reader.Format, score);
    }

    private static FormatProbe ReadProbe(string path, int size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var head = new byte[size];
        var read = 0;
        using (var fs = File.OpenRead(path))
            read = fs.Read(head, 0, size);
        return new FormatProbe(Path.GetExtension(path), head.AsMemory(0, read));
    }
}
