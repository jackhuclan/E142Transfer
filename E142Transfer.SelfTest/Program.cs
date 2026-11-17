using System.Xml.Linq;
using E142Transfer;
using E142Transfer.Models;

Console.WriteLine("=== E142 TransferMap 骨架自检 ===\n");

// ---------- 1. 构造一颗 wafer 上的 die 及下游去向 ----------
var waferFrame = new MapFrame
{
    SubstrateType = SubstrateType.Wafer,
    SubstrateId = "WAFER-001",
    LayoutSpecifier = "WaferMap/LAYOUT",
    OriginLocation = OriginLocation.LowerLeft,
    AxisDirection = AxisDirection.UpRight,
    Orientation = WaferOrientation.Degree0,
};

var dies = new List<DieResult>
{
    new() { X = 12, Y = 8, BinCode = "P", ElClass = "OK", PlClass = "OK", AoiClass = "OK" },
    new() { X = 13, Y = 8, BinCode = "F", ElClass = "NG", PlClass = "OK", AoiClass = "OK" },
    new() { X = 14, Y = 8, BinCode = "P", ElClass = "OK", PlClass = "OK", AoiClass = "NG" },
    new() { X = 99, Y = 99, BinCode = ".", IsDie = false }, // NullBin 空位，应被跳过
};

dies[0].Transfers.Add(new TransferRef(SubstrateType.Strip, "STRIP-A", 3, 1, "U-0301")
{
    StackLayer = 0,
    LotId = "LOT-2401",
    Slot = 7,
});
dies[2].Transfers.Add(new TransferRef(SubstrateType.Strip, "STRIP-A", 3, 2, "U-0302")
{
    StackLayer = 0,
});
// 3D 堆叠：同一封装位第二层，来自另一颗 die
dies[1].Transfers.Add(new TransferRef(SubstrateType.Strip, "STRIP-A", 3, 1, "U-0301")
{
    StackLayer = 1,
});

// ---------- 2. 生成 TransferMap 并写出完整 E142 文档 ----------
var projector = new DieTransferProjector(waferFrame);
var transferMap = projector.BuildTransferMap(dies);
Console.WriteLine($"生成 <T> 记录数: {transferMap.Entries.Count}（期望 3，NullBin 已跳过）");

var layout = new E142Layout
{
    LayoutId = "LAYOUT",
    Columns = 320,
    Rows = 309,
    StepSizeX = 0.63,
    StepSizeY = 0.64,
    ProductId = "TESTDEVICE_00",
};
var writer = new E142TransferMapWriter();
var targetFrame = new MapFrame
{
    SubstrateType = SubstrateType.Strip,
    SubstrateId = "STRIP-A",
    LayoutSpecifier = "StripMap/LAYOUT",
};
var doc = writer.Build(transferMap, targetFrame, layout);
var outPath = Path.Combine(AppContext.BaseDirectory, "sample-transfer.e142.xml");
E142TransferMapWriter.Save(doc, outPath);
Console.WriteLine($"写出: {outPath}");

// ---------- 3. 解析回来，验证闭环 ----------
var parser = new E142TransferMapParser();
var parsed = parser.ParseFile(outPath);
Console.WriteLine($"解析 Namespace: {parsed.NamespaceUri}");
Console.WriteLine($"解析 TransferMap 数: {parsed.TransferMaps.Count}");

var parsedTm = parsed.TransferMaps[0].TransferMap;
Console.WriteLine($"解析 <T> 记录数: {parsedTm.Entries.Count}");
Console.WriteLine($"FromSubstrate: {E142EnumParser.ToE142(parsedTm.FromSubstrateType)} {parsedTm.FromSubstrateId}");

var expected = new[]
{
    (12, 8, 3, 1),
    (13, 8, 3, 1),
    (14, 8, 3, 2),
};
for (var i = 0; i < expected.Length; i++)
{
    var e = parsedTm.Entries[i];
    var ok = e.FX == expected[i].Item1 && e.FY == expected[i].Item2
          && e.TX == expected[i].Item3 && e.TY == expected[i].Item4;
    Console.WriteLine($"  T{i}: {e} -> {(ok ? "OK" : "FAIL")}");
    if (!ok) throw new InvalidOperationException("解析结果与写入不一致");
}

// ---------- 4. 反查索引：由成品位号查回晶圆坐标 ----------
var reverse = DieTransferProjector.BuildReverseIndex(parsed);
var back = reverse[("TransferMap", 3, 1)];
Console.WriteLine($"\n反查 STRIP-A(3,1) 的源 die 数: {back.Count}（期望 2，3D 堆叠）");
foreach (var src in back) Console.WriteLine($"  {src}");
if (back.Count != 2) throw new InvalidOperationException("3D 堆叠反查数量不对");

// ---------- 5. 坐标归一化：默认 LowerLeft / UpRight ----------
var p = CoordinateNormalizer.ToLogical(0, 0, 10, 10,
    OriginLocation.LowerLeft, AxisDirection.UpRight);
Console.WriteLine($"\nLowerLeft/UpRight (0,0) -> 逻辑 (Col={p.Col}, Row={p.Row})（期望 0,9）");
if (p.Col != 0 || p.Row != 9) throw new InvalidOperationException("坐标归一化错误");

var roundTrip = CoordinateNormalizer.ToFile(p.Col, p.Row, 10, 10,
    OriginLocation.LowerLeft, AxisDirection.UpRight);
Console.WriteLine($"逻辑 ({p.Col},{p.Row}) -> 文件 ({roundTrip.X},{roundTrip.Y})（期望 0,0）");
if (roundTrip.X != 0 || roundTrip.Y != 0) throw new InvalidOperationException("坐标往返不一致");

// ---------- 6. 解析一份带 BinCodeMap + TransferMap 的真实结构样例 ----------
const string sampleXml = """
<?xml version="1.0" encoding="UTF-8"?>
<MapData xmlns="urn:semi-org:xsd.E142-1.V1005.SubstrateMap">
  <Layouts>
    <Layout LayoutId="LAYOUT" DefaultUnits="mm">
      <Dimension X="320" Y="309" />
      <StepSize X="0.63" Y="0.64" />
    </Layout>
  </Layouts>
  <Substrates>
    <Substrate SubstrateType="Wafer" SubstrateId="WAFER-X">
      <GoodDevices>68000</GoodDevices>
    </Substrate>
  </Substrates>
  <SubstrateMaps>
    <SubstrateMap SubstrateType="Strip" SubstrateId="STRIP-X"
                  LayoutSpecifier="StripMap/LAYOUT" OriginLocation="UpperRight" AxisDirection="DownLeft">
      <Overlay MapName="SortGrade" MapVersion="1">
        <BinCodeMap BinType="Ascii" NullBin="." MapType="2DArray">
          <BinDefinitions>
            <BinDefinition BinCode="P" BinDescription="PASS" BinQuality="Pass" Pick="true" />
            <BinDefinition BinCode="F" BinDescription="OPEN" BinQuality="Fail" BinCount="12" />
          </BinDefinitions>
          <BinCode>PPFFPP</BinCode>
        </BinCodeMap>
      </Overlay>
      <Overlay MapName="TransferMap" MapVersion="1">
        <TransferMap FromSubstrateType="Wafer" FromSubstrateId="WAFER-X">
          <T FX="10" FY="20" TX="1" TY="1" FID="P" TID="U-0101" />
          <T FX="11" FY="20" TX="1" TY="2" FID="F" TID="U-0102" />
        </TransferMap>
      </Overlay>
    </SubstrateMap>
  </SubstrateMaps>
</MapData>
""";
var parsed2 = parser.Parse(sampleXml);
var frame2 = parsed2.TransferMaps[0].Frame;
Console.WriteLine($"\n样例 SubstrateMap 坐标: {E142EnumParser.ToE142(frame2.OriginLocation)} / {E142EnumParser.ToE142(frame2.AxisDirection)}");
if (frame2.OriginLocation != OriginLocation.UpperRight ||
    frame2.AxisDirection != AxisDirection.DownLeft)
    throw new InvalidOperationException("坐标默认值解析错误");

var binTable = parsed2.BinTables[0].Definitions;
Console.WriteLine($"BinCode 定义数: {binTable.Count}, P.IsPass={binTable["P"].IsPass}, F.Count={binTable["F"].Count}");
if (!binTable["P"].IsPass || binTable["F"].Count != 12)
    throw new InvalidOperationException("BinCode 定义解析错误");

// ---------- 7. 追加 overlay 到已有文档 ----------
var extra = new E142TransferMap
{
    MapName = "TransferMap2",
    FromSubstrateType = SubstrateType.Wafer,
    FromSubstrateId = "WAFER-Y",
};
extra.Entries.Add(new TransferEntry { FX = 5, FY = 5, TX = 9, TY = 9 });
var doc2 = XDocument.Parse(sampleXml);
writer.AppendToDocument(doc2, "STRIP-X", extra);
var parsed3 = parser.Parse(doc2.ToString());
Console.WriteLine($"追加后 TransferMap 数: {parsed3.TransferMaps.Count}（期望 2）");
if (parsed3.TransferMaps.Count != 2) throw new InvalidOperationException("追加 overlay 失败");

ImportChecks.Run();

Console.WriteLine("\n=== 全部自检通过 ===");
