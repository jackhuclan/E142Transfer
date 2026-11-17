using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Model;
using WaferMapStudio.Core.Visuals;
using Xunit;

namespace WaferMapStudio.Core.Tests;

/// <summary>中性模型 → 视觉模型映射：网格完整性、类别投影、防御性坐标归一化。</summary>
public sealed class WaferMapVisualMapperTests
{
    private static NeutralWaferMap BuildSampleMap()
    {
        var map = new NeutralWaferMap
        {
            Format = WaferMapFormat.CsvXyBin,
            SourcePath = @"C:\tmp\demo.csv",
            SubstrateId = "W01",
            DeviceId = "DEV1",
            LotId = "LOT1",
        };
        map.Layout = new NeutralLayout { Columns = 3, Rows = 2 };
        map.AddBin(new NeutralBinDefinition { Code = "1", Class = NeutralBinClass.Pass, Description = "Continuity Pass" });
        map.AddBin(new NeutralBinDefinition { Code = "7", Class = NeutralBinClass.Fail, Description = "Short" });

        map.AddDie(new NeutralDie(0, 0) { BinCode = "1", Class = NeutralBinClass.Pass });
        map.AddDie(new NeutralDie(1, 0) { BinCode = "7", Class = NeutralBinClass.Fail });
        map.AddDie(new NeutralDie(2, 0) { BinCode = "1", Class = NeutralBinClass.Pass });
        // (0,1) 缺失 → 应填 Null 空位
        map.AddDie(new NeutralDie(1, 1) { BinCode = "9", Class = NeutralBinClass.Marked });
        map.AddDie(new NeutralDie(2, 1) { BinCode = "1", Class = NeutralBinClass.Pass });
        map.ReferenceDie = (2, 0);
        return map;
    }

    [Fact]
    public void Map_FillsFullGridIncludingNullHoles()
    {
        var visual = WaferMapVisualMapper.Map(BuildSampleMap());

        Assert.Equal(3, visual.Columns);
        Assert.Equal(2, visual.Rows);
        Assert.Equal(6, visual.Dies.Count);

        var hole = visual.Dies.Single(d => d.Col == 0 && d.Row == 1);
        Assert.Equal(DieCategory.Null, hole.Category);
        Assert.False(hole.IsRealDie);
    }

    [Fact]
    public void Map_ProjectsCategoriesAndCounts()
    {
        var visual = WaferMapVisualMapper.Map(BuildSampleMap());

        Assert.Equal(3, visual.CategoryCounts[DieCategory.Pass]);
        Assert.Equal(1, visual.CategoryCounts[DieCategory.Fail]);
        Assert.Equal(1, visual.CategoryCounts[DieCategory.Marked]);
        Assert.Equal(5, visual.DieCount);
        Assert.Equal(60.0, visual.Yield!.Value, precision: 6);
    }

    [Fact]
    public void Map_CarriesDescriptionsAndReferenceDie()
    {
        var visual = WaferMapVisualMapper.Map(BuildSampleMap());

        var fail = visual.Dies.Single(d => d.Col == 1 && d.Row == 0);
        Assert.Equal("7", fail.BinCode);
        Assert.Equal("Short", fail.Description);

        Assert.Equal((2, 0), visual.ReferenceDie);
    }

    [Fact]
    public void Map_NormalizesForeignCoordinates()
    {
        // 防御性归一化：上游若漏发 1 基坐标，映射器按实际最小值平移，图永不越界
        var map = new NeutralWaferMap();
        map.Layout = new NeutralLayout { Columns = 2, Rows = 2 };
        map.AddDie(new NeutralDie(1, 1) { BinCode = "1", Class = NeutralBinClass.Pass });
        map.AddDie(new NeutralDie(2, 1) { BinCode = "0", Class = NeutralBinClass.Fail });
        map.AddDie(new NeutralDie(1, 2) { BinCode = "1", Class = NeutralBinClass.Pass });
        map.AddDie(new NeutralDie(2, 2) { BinCode = "1", Class = NeutralBinClass.Pass });
        map.ReferenceDie = (1, 1);

        var visual = WaferMapVisualMapper.Map(map);

        Assert.Equal(2, visual.Columns);
        Assert.Equal(2, visual.Rows);
        Assert.Equal((0, 0), visual.ReferenceDie);
        Assert.All(visual.Dies, d => Assert.InRange(d.Col, 0, 1));
        Assert.All(visual.Dies, d => Assert.InRange(d.Row, 0, 1));
    }

    [Fact]
    public void Map_EmptyMapStillYieldsValidGrid()
    {
        var map = new NeutralWaferMap
        {
            Format = WaferMapFormat.E142,
            SourcePath = "empty",
        };

        var visual = WaferMapVisualMapper.Map(map);

        Assert.Equal(1, visual.Columns);
        Assert.Equal(1, visual.Rows);
        Assert.Null(visual.Yield);
        Assert.Equal(0, visual.DieCount);
    }
}

/// <summary>BinCode → 颜色：不同码不同色、类别兜底、图例聚合。</summary>
public sealed class BinColorPaletteTests
{
    private static WaferMapVisual BuildVisual()
    {
        var dies = new List<DieVisual>
        {
            new(0, 0, "1", DieCategory.Pass, "Pass"),
            new(1, 0, "1", DieCategory.Pass, "Pass"),
            new(2, 0, "2", DieCategory.Pass, "Pass"),
            new(0, 1, "7", DieCategory.Fail, "Short"),
            new(1, 1, "8", DieCategory.Fail, "Open"),
            new(2, 1, "", DieCategory.Null, null),
        };
        var counts = dies.Where(d => d.IsRealDie)
            .GroupBy(d => d.Category)
            .ToDictionary(g => g.Key, g => g.Count());
        return new WaferMapVisual
        {
            Format = WaferMapFormat.CsvXyBin,
            SourcePath = "demo",
            Columns = 3,
            Rows = 2,
            Dies = dies,
            CategoryCounts = counts,
        };
    }

    [Fact]
    public void FromMap_AssignsDistinctColorsPerBinCode()
    {
        var palette = BinColorPalette.FromMap(BuildVisual());

        var bin1 = palette.ColorOf(new DieVisual(0, 0, "1", DieCategory.Pass, null));
        var bin2 = palette.ColorOf(new DieVisual(2, 0, "2", DieCategory.Pass, null));
        var bin7 = palette.ColorOf(new DieVisual(0, 1, "7", DieCategory.Fail, null));
        var bin8 = palette.ColorOf(new DieVisual(1, 1, "8", DieCategory.Fail, null));

        Assert.NotEqual(bin1, bin2);
        Assert.NotEqual(bin7, bin8);
        // 同类别的码保持同一色系语义（绿=良品、红=失效）
        Assert.Equal(
            BinColorPalette.CategoryColor(DieCategory.Pass).R,
            bin1.R);
        Assert.Equal(
            BinColorPalette.CategoryColor(DieCategory.Fail).R,
            bin7.R);
    }

    [Fact]
    public void ColorOf_FallsBackToCategoryColorForUnknownCode()
    {
        var palette = new BinColorPalette();
        var die = new DieVisual(0, 0, "ZZ", DieCategory.Skip, null);

        Assert.Equal(BinColorPalette.CategoryColor(DieCategory.Skip), palette.ColorOf(die));
    }

    [Fact]
    public void BuildLegend_AggregatesByBinCodeWithCounts()
    {
        var palette = BinColorPalette.FromMap(BuildVisual());
        var legend = palette.BuildLegend(BuildVisual());

        // bin1=2、bin7=1、bin8=1、bin2=1、空位=1 → 5 条，第一条必须是计数最多的 bin1
        Assert.Equal(5, legend.Count);
        Assert.Equal("1", legend[0].Key);
        Assert.Equal(2, legend[0].Count);
        Assert.Contains(legend, e => e.Key == string.Empty && e.Count == 1);
    }

    [Fact]
    public void DieColor_HexRoundTrip()
    {
        var color = DieColor.FromHex("#3E8BFF");

        Assert.Equal(0x3E, color.R);
        Assert.Equal("#3E8BFF", color.ToHex());
        Assert.Throws<FormatException>(() => DieColor.FromHex("XYZ"));
    }
}
