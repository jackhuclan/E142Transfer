using E142Transfer.Import;
using E142Transfer.Import.Abstractions;
using E142Transfer.Import.Detection;
using E142Transfer.Import.Model;
using E142Transfer.Import.Writers;
using E142Transfer.Models;

/// <summary>多格式导入适配器的端到端自检：识别 → 读取 → 中性模型 → 投影 → 写回。</summary>
public static class ImportChecks
{
    public static void Run()
    {
        Console.WriteLine("\n=== 多格式导入适配器自检 ===\n");
        var dir = Path.Combine(Path.GetTempPath(), "E142Transfer-Import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var importer = new WaferMapImporter();

            // ---------- 1. CSV/XYBin：识别 + 负坐标平移 + 投影 ----------
            var csv = Path.Combine(dir, "map.csv");
            File.WriteAllLines(csv, ["x,y,bin", "-1,0,P", "0,0,1", "1,0,0", "2,0,."]);

            var probed = importer.Probe(csv);
            Check(probed.Format == WaferMapFormat.CsvXyBin, $"CSV 自动识别（置信度 {probed.Score:F2}）");

            var outcome = importer.Import(csv);
            Check(outcome.Map.Dies.Count == 4, "CSV die 数 = 4");
            Check(outcome.Map.Layout is { Columns: 4, Rows: 1 }, "CSV 网格 4x1");
            Check(outcome.Map.Issues.Any(i => i.Code == "CSV_COORD_SHIFT"), "CSV 负坐标平移告警");

            var projected = NeutralMapProjector.ToDieResults(outcome.Map);
            Check(projected.Count == 4 && projected[0].X == 0, "投影为 DieResult 且坐标已归一");
            Check(projected[0].Bin?.Quality == BinQuality.Pass, "bin 码 P → Pass");

            // ---------- 2. CSV 写回 → 再导入，闭环 ----------
            var roundTrip = Path.Combine(dir, "round.csv");
            new CsvXyBinWriter().Write(outcome.Map, roundTrip);
            var again = importer.ImportMap(roundTrip);
            Check(again.Dies.Count == 4, "CSV 写回→再导入闭环");

            // ---------- 3. SINF：头字段 + 网格 ----------
            var sinf = Path.Combine(dir, "map.sinf");
            File.WriteAllLines(sinf,
            [
                "SINFLOT-1.sinf", "DEVICE:DEMO", "LOT:LOT-9", "WAFER:07",
                "ROWCT:2", "COLCT:3", "P P F", "1 0 .",
            ]);
            var sinfOutcome = importer.Import(sinf);
            Check(sinfOutcome.Format == WaferMapFormat.Sinf, "SINF 自动识别");
            Check(sinfOutcome.Map.Dies.Count == 6, "SINF die 数 = 6");
            Check(sinfOutcome.Map.LotId == "LOT-9" && sinfOutcome.Map.SubstrateId == "07",
                "SINF 头字段 LOT / WAFER 解析");
            Check(sinfOutcome.Map.Layout is { Columns: 3, Rows: 2 }, "SINF ROWCT/COLCT 解析");

            // ---------- 4. E142：Layout + BinCode 阵列 + TransferMap ----------
            var e142 = Path.Combine(dir, "map.e142.xml");
            File.WriteAllText(e142, """
            <?xml version="1.0" encoding="UTF-8"?>
            <MapData xmlns="urn:semi-org:xsd.E142-1.V1005.SubstrateMap">
              <Layouts>
                <Layout LayoutId="LAYOUT" DefaultUnits="mm">
                  <Dimension X="320" Y="309" />
                  <StepSize X="0.63" Y="0.64" />
                </Layout>
              </Layouts>
              <Substrates>
                <Substrate SubstrateType="Wafer" SubstrateId="WAFER-Z" />
              </Substrates>
              <SubstrateMaps>
                <SubstrateMap SubstrateType="Strip" SubstrateId="STRIP-Z" LayoutSpecifier="StripMap/LAYOUT">
                  <Overlay MapName="SortGrade" MapVersion="1">
                    <BinCodeMap BinType="Ascii" NullBin="." MapType="2DArray">
                      <BinDefinitions>
                        <BinDefinition BinCode="P" BinDescription="PASS" BinQuality="Pass" Pick="true" />
                        <BinDefinition BinCode="F" BinDescription="OPEN" BinQuality="Fail" />
                      </BinDefinitions>
                      <BinCode>PPF</BinCode>
                    </BinCodeMap>
                  </Overlay>
                  <Overlay MapName="TransferMap" MapVersion="1">
                    <TransferMap FromSubstrateType="Wafer" FromSubstrateId="WAFER-Z">
                      <T FX="0" FY="0" TX="1" TY="1" TID="U-0101" />
                      <T FX="1" FY="0" TX="1" TY="2" TID="U-0102" />
                    </TransferMap>
                  </Overlay>
                </SubstrateMap>
              </SubstrateMaps>
            </MapData>
            """);
            var e142Outcome = importer.Import(e142);
            Check(e142Outcome.Format == WaferMapFormat.E142, "E142 自动识别（含 .xml 扩展名）");
            Check(e142Outcome.Map.Dies.Count == 3, "E142 bin 阵列解析（NullBin 已跳过）");
            Check(e142Outcome.Map.BinDefinitions.Count == 2, "E142 BinCode 定义表解析");
            Check(e142Outcome.Map.Transfers.Count == 2, "E142 TransferMap 解析");

            var e142Dies = NeutralMapProjector.ToDieResults(e142Outcome.Map);
            Check(e142Dies[0].Transfers.Count == 1 &&
                  e142Dies[0].Transfers[0].TargetSubstrateId == "STRIP-Z",
                "TransferMap 投影为 DieResult.Transfers，带目标载具 ID");

            // ---------- 5. 无法识别时明确报错，不静默猜测 ----------
            var junk = Path.Combine(dir, "junk.bin");
            File.WriteAllText(junk, "not a wafer map at all");
            try
            {
                importer.Import(junk);
                Check(false, "未知格式应抛异常");
            }
            catch (UnsupportedWaferMapFormatException)
            {
                Check(true, "未知格式明确抛异常");
            }

            // ---------- 6. SINF 标准 hex：BCEQU + 特殊码 + 1-based 参考坐标 ----------
            var sinfStd = Path.Combine(dir, "standard.sinf");
            File.WriteAllLines(sinfStd,
            [
                "DEVICE:DEMO2", "LOT:LOT-X", "WAFER:11", "FNLOC:180",
                "ROWCT:2", "COLCT:4", "BCEQU:01,02", "REFPX:2", "REFPY:1",
                "DUTMS:mm", "XDIES:2.945", "YDIES:2.945", "DIECT:8",
                "RowData:__ 01 0B FF",
                "RowData:02 @@ FD 03",
            ]);
            var stdOutcome = importer.Import(sinfStd);
            Check(stdOutcome.Format == WaferMapFormat.Sinf, "SINF 标准 hex 自动识别");
            Check(stdOutcome.Map.DeviceId == "DEMO2", "SINF DEVICE 字段解析");
            Check(stdOutcome.Map.Layout is { Columns: 4, Rows: 2, StepSizeX: 2.945 },
                "SINF pitch 与网格解析");
            Check(stdOutcome.Map.Dies.Count == 8, "SINF 标准 die 数 = 8");
            Check(stdOutcome.Map.GetDie(1, 0)?.Class == NeutralBinClass.Pass, "SINF 01 ∈ BCEQU → Pass");
            Check(stdOutcome.Map.GetDie(3, 0)?.Class == NeutralBinClass.Reference, "SINF FF → Reference");
            Check(stdOutcome.Map.GetDie(0, 0)?.Class == NeutralBinClass.Null, "SINF __ → Null");
            Check(stdOutcome.Map.GetDie(1, 1)?.Class == NeutralBinClass.Skip, "SINF @@ → Skip");
            Check(stdOutcome.Map.GetDie(2, 1)?.Class == NeutralBinClass.Edge, "SINF FD → Edge");
            Check(stdOutcome.Map.GetDie(3, 1)?.Class == NeutralBinClass.Fail, "SINF 03 未在 BCEQU → Fail");
            Check(stdOutcome.Map.GoodDies.Count() == 2, "SINF 只拾取 BCEQU 声明的 2 颗 Pass");
            Check(stdOutcome.Map.Issues.Any(i => i.Code == "SINF_REFERENCE_1BASED"),
                "SINF 1-based 参考坐标修正告警");
            Check(stdOutcome.Map.Issues.Any(i => i.Code == "SINF_FNLOC"), "SINF FNLOC 告警");

            // ---------- 7. 3 Digit SINF 变体 ----------
            var sinf3 = Path.Combine(dir, "three.sinf");
            File.WriteAllLines(sinf3, ["ROWCT:1", "COLCT:3", "BCEQU:000", "RowData:___ 000 001"]);
            var threeOutcome = importer.Import(sinf3);
            Check(threeOutcome.Map.Issues.Any(i => i.Code == "SINF_3DIGIT"), "3 Digit SINF 变体告警");
            Check(threeOutcome.Map.GetDie(1, 0)?.Class == NeutralBinClass.Pass, "3 Digit 000 ∈ BCEQU → Pass");
            Check(threeOutcome.Map.GetDie(0, 0)?.Class == NeutralBinClass.Null, "3 Digit ___ → Null");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("自检失败: " + message);
        Console.WriteLine($"  OK  {message}");
    }
}
