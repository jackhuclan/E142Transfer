using System.Globalization;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;

namespace E142Transfer.Import.Writers;

/// <summary>把中性 map 写回 x,y,bin CSV，便于与其他工具交换或做回归测试。</summary>
public sealed class CsvXyBinWriter : IWaferMapWriter
{
    public WaferMapFormat Format => WaferMapFormat.CsvXyBin;

    public void Write(NeutralWaferMap map, string path)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        writer.WriteLine("x,y,bin,class");
        foreach (var d in map.Dies.Values.OrderBy(d => d.Y).ThenBy(d => d.X))
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{d.X},{d.Y},{d.BinCode},{d.Class}"));
        }
    }
}
