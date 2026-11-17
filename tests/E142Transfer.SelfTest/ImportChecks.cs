using System.Globalization;
using System.Text;
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
            File.WriteAllLines(sinf3,
                ["ROWCT:1", "COLCT:4", "BCEQU:001", "RowData:___ 001 000 @@@"]);
            var threeOutcome = importer.Import(sinf3);
            Check(threeOutcome.Map.Issues.Any(i => i.Code == "SINF_3DIGIT"), "3 Digit SINF 变体告警");
            Check(threeOutcome.Map.GetDie(1, 0)?.Class == NeutralBinClass.Pass, "3 Digit 001 ∈ BCEQU → Pass");
            Check(threeOutcome.Map.GetDie(0, 0)?.Class == NeutralBinClass.Null, "3 Digit ___ → Null");
            Check(threeOutcome.Map.GetDie(2, 0)?.Class == NeutralBinClass.Fail, "3 Digit 000 → Fail");
            Check(threeOutcome.Map.GetDie(3, 0)?.Class == NeutralBinClass.Skip,
                "3 Digit @@@ → Skip（真实样本 Amkor/G85 用三位未检测码）");
            Check(threeOutcome.Map.Dies.Count == 4, "3 Digit 含 @@@ 的行不再被整行跳过");

            // ---------- 8. G85（SEMI G85-1101 XML，真实 Amkor/Camtek 结构） ----------
            var g85 = Path.Combine(dir, "amkor.e142.g85.xml");
            File.WriteAllText(g85, """
            <?xml version="1.0"?>
            <Maps><Map xmlns:semi="http://www.semi.org" WaferId="4EEEB00000-HW3UREG" FormatRevision="SEMI G85-1101">
              <Device ProductId="IBM_AURA_3.1_OQA" LotId="4EEEB00000" CreateDate="20140930165234"
                      Orientation="180" Rows="3" Columns="4" DeviceSizeX="2005.755" DeviceSizeY="2806.044"
                      StepSizeX="2098" StepSizeY="2897.9" OriginLocation="2" BinType="Decimal"
                      FrameId="HW3UREG" NullBin="255">
                <Bin BinCode="000" BinCount="1" BinQuality="Fail" BinDescription="Non-pickable site"/>
                <Bin BinCode="001" BinCount="2" BinQuality="Pass" BinDescription="Pickable site"/>
                <Bin BinCode="007" BinCount="4" BinQuality="None" BinDescription="Marked Bin"/>
                <SupplierData DUTMS="10^-6*m" FileName="WAFHW3UREGa.map" ORGFNLOC="180"
                              WAFER="IBM_AURA_3.1_OQA" WAFERID="HW3UREG" SLOT="2"
                              REFP1.X="1" REFP1.Y="2" REFP2.X="0" REFP2.Y="0"/>
                <Data MapName="Camtek" Version="1">
                  <Row><![CDATA[255 001 007 007]]></Row>
                  <Row><![CDATA[001 007 000 007]]></Row>
                  <Row><![CDATA[255 255 255 255]]></Row>
                </Data>
              </Device>
            </Map></Maps>
            """);
            var g85Probe = importer.Probe(g85);
            Check(g85Probe.Format == WaferMapFormat.G85, $"G85 自动识别（置信度 {g85Probe.Score:F2}，优先于 E142）");
            var g85Outcome = importer.Import(g85);
            var gm = g85Outcome.Map;
            Check(gm.SubstrateId == "4EEEB00000-HW3UREG" && gm.LotId == "4EEEB00000" &&
                  gm.DeviceId == "IBM_AURA_3.1_OQA", "G85 WaferId/LotId/ProductId 解析");
            Check(gm.Layout is { Columns: 4, Rows: 3, OriginLocation: OriginLocation.UpperLeft,
                   AxisDirection: AxisDirection.DownRight, Orientation: WaferOrientation.Degree180 },
                "G85 网格 4x3 + OriginLocation=2→UpperLeft + Orientation=180");
            Check(Math.Abs(gm.Layout.StepSizeX - 2098) < 1e-9 && Math.Abs(gm.DieSizeX - 2005.755) < 1e-3,
                "G85 StepSize/DieSize 原样保留（µm 未换算）");
            Check(gm.Dies.Count == 12, "G85 NullBin 保留为 Null die，网格完整 4x3=12");
            Check(gm.GetDie(1, 0)?.Class == NeutralBinClass.Pass, "G85 bin 001 Pass");
            Check(gm.GetDie(2, 0)?.Class == NeutralBinClass.Marked, "G85 bin 007 None → Marked（不是 Null/Fail）");
            Check(gm.GetDie(2, 1)?.Class == NeutralBinClass.Fail, "G85 bin 000 Fail");
            Check(gm.GetDie(0, 0)?.Class == NeutralBinClass.Null, "G85 bin 255 NullBin → Null");
            Check(gm.GoodDies.Count() == 2, "G85 Marked 不进 GoodDies，只拾取 2 颗 Pass");
            Check(gm.ReferenceDie == (1, 2), "G85 REFP1 参考 die 解析");
            Check(gm.Metadata.TryGetValue("SLOT", out var slot) && slot == "2" &&
                  gm.Metadata.TryGetValue("DUTMS", out var dutms) && dutms == "10^-6*m",
                "G85 SupplierData 扩展字段保留");
            Check(gm.Issues.Any(i => i.Code == "G85_BIN_MARKED"), "G85 Marked Bin 告警");
            Check(gm.Issues.Any(i => i.Code == "G85_UNITS"), "G85 单位告警（不静默换算）");
            Check(gm.Issues.Any(i => i.Code == "G85_REFERENCE"), "G85 参考 die 对齐告警");
            Check(!gm.Issues.Any(i => i.Code == "G85_BINCOUNT_MISMATCH"), "G85 BinCount 声明与实际一致，无告警");

            var g85Projected = NeutralMapProjector.ToDieResults(gm);
            Check(g85Projected.Count == 12 && g85Projected.Count(d => !d.IsDie) == 5,
                "G85 投影为 DieResult，5 个 Null 位以 IsDie=false 标记（NullBin 无 Bin 定义，符合 G85 规范）");
            Check(g85Projected.Any(d => d.Bin?.Quality == BinQuality.Marked),
                "G85 Marked 类别经投影保留为 BinQuality.Marked");

            // BinCount 不一致时应报非致命告警
            var g85Bad = Path.Combine(dir, "amkor.bad.xml");
            File.WriteAllText(g85Bad, File.ReadAllText(g85).Replace("BinCode=\"001\" BinCount=\"2\"", "BinCode=\"001\" BinCount=\"9\""));
            var g85BadOutcome = importer.Import(g85Bad);
            Check(g85BadOutcome.Map.Issues.Any(i => i.Code == "G85_BINCOUNT_MISMATCH"),
                "G85 BinCount 不符时交叉校验告警");

            // ---------- 9. TSK DAT（探针台二进制逐 die 记录）+ 配对 XTR ----------
            const int datRows = 3;
            const int datCols = 55;
            // 行 0：全良品；行 1：col 1..3 为 None 类(bin 21)、col 4..13 为 FAIL(bin 4，含 0x88 变体)、其余良品；
            // 行 2：col 1..5 空位、其余良品。
            static (byte Flag, byte Bin) DatCell(int row, int col) => row switch
            {
                0 => (0x44, 0),
                1 => col switch
                {
                    <= 3 => (0x00, 21),                               // 0x00 = None 类 → Marked
                    <= 13 => ((byte)(col % 2 == 0 ? 0x88 : 0x80), 4), // 0x88/0x80 都应判 FAIL
                    _ => (0x44, 0),
                },
                _ => col <= 5 ? ((byte)0x10, (byte)0) : ((byte)0x44, (byte)0),
            };
            // pass 147 / fail 10 / marked 3 / null 5，有 die 格位 160
            var dat = Path.Combine(dir, "synth.dat");
            File.WriteAllBytes(dat, BuildTskDat(datRows, datCols, "SYNTH-0001", "SYNTH.1", "SYNTH-DEV",
                total: 160, pass: 147, fail: 13, DatCell));

            var datProbe = importer.Probe(dat);
            Check(datProbe.Format == WaferMapFormat.TskDat && datProbe.Score >= 0.9,
                $"TSK DAT 自动识别（置信度 {datProbe.Score:F2}）");
            var datOutcome = importer.Import(dat);
            var dm = datOutcome.Map;
            Check(dm.SubstrateId == "SYNTH-0001" && dm.LotId == "SYNTH.1" && dm.DeviceId == "SYNTH-DEV",
                "DAT 头部晶圆/批次/产品字段解析");
            Check(dm.Layout is { Columns: 55, Rows: 3, OriginLocation: OriginLocation.UpperLeft,
                   AxisDirection: AxisDirection.DownRight, Orientation: WaferOrientation.Degree180 },
                "DAT 网格 55x3 + UpperLeft/DownRight + FlatAngle=180");
            Check(Math.Abs(dm.DieSizeX - 3600) < 1e-9 && Math.Abs(dm.DieSizeY - 1580) < 1e-9,
                "DAT die 尺寸按 0.01µm 单位换算（360000/158000 → 3600x1580µm）");
            Check(dm.ReferenceDie == (3, 79), "DAT 参考 die = 第 4 列 / 第 80 行");
            Check(dm.Dies.Count == datRows * datCols, "DAT 网格按全矩形写入（空位保留为 Null，共 165 格）");
            Check(dm.GoodDies.Count() == 147, "DAT 状态位 0x40/0x04 → Pass，只拾取 147 颗良品");
            Check(dm.Dies.Values.Count(d => d.Class == NeutralBinClass.Fail) == 10,
                "DAT 状态位 0x80 与 0x88 都判 Fail（按位判断，不比对整字节）");
            Check(dm.Dies.Values.Count(d => d.Class == NeutralBinClass.Marked) == 3,
                "DAT 状态位 0x00 → Marked（None 类有效 die，不进 GoodDies）");
            Check(dm.Dies.Values.Count(d => d.Class == NeutralBinClass.Null) == 5, "DAT 状态位 0x10 → Null 空位");
            Check(dm.GetDie(0, 1)?.Class == NeutralBinClass.Marked &&
                  dm.GetDie(2, 1)?.Class == NeutralBinClass.Marked &&
                  dm.GetDie(6, 1)?.Class == NeutralBinClass.Fail,
                "DAT 参考列左侧的列换算正确（col1=1..3 → 0-based 0..2；若忽略列侧别会镜像到 6..8）");
            Check(dm.GetDie(54, 0)?.Class == NeutralBinClass.Pass && dm.GetDie(0, 2)?.Class == NeutralBinClass.Null,
                "DAT 最右列与第 3 行首个空位解析正确");
            Check(!dm.Issues.Any(i => i.Code is "DAT_TOTAL_MISMATCH" or "DAT_PASS_MISMATCH" or
                      "DAT_ROW_ENCODING" or "DAT_CELL_COLLISION" or "DAT_COL_OUT_OF_RANGE"),
                "DAT 计数/行距/格位校验全部通过，无异常告警");
            Check(dm.Issues.Any(i => i.Code == "DAT_FAIL_COUNT_SEMANTICS"),
                "DAT 头部不良数含 None 类时给出统计口径告警");
            Check(dm.Issues.Any(i => i.Code == "DAT_NO_XTR"),
                "DAT 未配对 XTR 时明确说明 bin 语义来自状态位推断");

            var datProjected = NeutralMapProjector.ToDieResults(dm);
            Check(datProjected.Count == 165 && datProjected.Count(d => !d.IsDie) == 5,
                "DAT 投影为 DieResult，空位以 IsDie=false 标记");
            Check(datProjected.Count(d => d.Bin?.Quality == BinQuality.Pass) == 147,
                "DAT 投影保留 Pass 语义");

            // 配对 XTR：bin 名称与 GOOD/BAD/None 声明；计数能对账才配对
            var xtr = Path.Combine(dir, "SYNTH-0001_20241009151958.XTR");
            File.WriteAllLines(xtr,
            [
                "4",
                "diameter=200000,refDieOffsetX=86089,refDieOffsetY=-26405,dieSizeCX=3600,dieSizeCY=1580",
                "NullBinCode=255,MCSVectorX=0,numBinGroup=4",
                "GOOD            ,33020,0                                           ,GOOD",
                "FC_FAIL         ,3158204,4                                          ,BAD",
                "EDGE_DIE        ,3952820,21                                         ,None",
                "PROTECT         ,11837540,20                                        ,None",
            ]);
            var datPaired = importer.ImportMap(dat);
            Check(datPaired.Metadata.TryGetValue("XtrSource", out var xtrSource) &&
                  xtrSource.EndsWith("_20241009151958.XTR", StringComparison.Ordinal),
                "DAT 按计数对账 + 时间接近度配对到对应轮次的 XTR");
            Check(datPaired.BinDefinitions["0"].Description.Contains("GOOD") &&
                  datPaired.BinDefinitions["4"].Description.Contains("FC_FAIL"),
                "DAT bin 名称取自配对 XTR");
            Check(!datPaired.Issues.Any(i => i.Code == "DAT_BIN_CLASS_CONFLICT"),
                "XTR 声明与状态位一致时不报 bin 口径冲突");

            // XTR 声明与状态位冲突时告警（bin 21：XTR 说 BAD，状态位判 None/Marked）
            var conflictDir = Path.Combine(dir, "conflict");
            Directory.CreateDirectory(conflictDir);
            var datConflict = Path.Combine(conflictDir, "synth.dat");
            File.Copy(dat, datConflict);
            File.WriteAllLines(Path.Combine(conflictDir, "SYNTH-0001_20241009151958.XTR"),
            [
                "4",
                "GOOD            ,33020,0   ,GOOD",
                "FC_FAIL         ,3158204,4  ,BAD",
                "EDGE_DIE        ,3952820,21 ,BAD",
            ]);
            var conflictOutcome = importer.ImportMap(datConflict);
            Check(conflictOutcome.Issues.Any(i => i.Code == "DAT_BIN_CLASS_CONFLICT"),
                "DAT bin 类别与 XTR 声明冲突时告警，并保持状态位判定");

            // 截断文件：不抛异常，尾部 die 缺失必须报出来
            var truncated = Path.Combine(dir, "truncated.dat");
            var full = File.ReadAllBytes(dat);
            File.WriteAllBytes(truncated, full[..(0xEC + 55 * 6 * 2)]);
            var truncatedMap = importer.ImportMap(truncated);
            Check(truncatedMap.Issues.Any(i => i.Code == "DAT_GRID_TRUNCATED") &&
                  truncatedMap.Dies.Count == 110,
                "DAT 截断时只写入可用记录并告警（2 行 = 110 格）");

            // 行距编码被破坏：必须报 DAT_ROW_ENCODING，而不是静默错位
            var broken = Path.Combine(dir, "broken.dat");
            var brokenBytes = (byte[])full.Clone();
            brokenBytes[0xEC + 55 * 6 + 3] ^= 0xFF; // 第 2 行首条记录的 w 字节
            File.WriteAllBytes(broken, brokenBytes);
            var brokenMap = importer.ImportMap(broken);
            Check(brokenMap.Issues.Any(i => i.Code == "DAT_ROW_ENCODING"), "DAT 行距编码异常时告警");

            // 扩展名是 .dat 但内容不是本格式：必须拒绝识别
            var notDat = Path.Combine(dir, "plain.dat");
            File.WriteAllText(notDat, "this is not a prober dat file");
            try
            {
                importer.Import(notDat);
                Check(false, "非 TSK DAT 的 .dat 文件应抛异常");
            }
            catch (UnsupportedWaferMapFormatException)
            {
                Check(true, "非 TSK DAT 内容不会被误识别（不靠扩展名猜）");
            }

            // ---------- 10. KLARF（AOI 缺陷清单：抽样 die 图 + 逐条缺陷） ----------
            var dies43 = new List<(int X, int Y)>();
            for (var y = 0; y < 3; y++)
                for (var x = 0; x < 4; x++)
                    dies43.Add((x, y));

            var classes3 = new List<(int Id, string Name)> { (0, "Accept"), (1, "Reject"), (2, "BF Defect") };
            var defects3 = new List<KDef>
            {
                new(1, 1, 1, 30, 40, 10, 20, 100, 2),
                new(2, 2, 0, 50, 60, 8, 8, 40, 1),
                new(3, 3, 2, 20, 80, 12, 12, 90, 2),
            };

            foreach (var id in new[] { 1, 2, 3 })
                File.WriteAllBytes(Path.Combine(dir, $"img_{id}_1.jpg"), []);

            var klarfGood = Path.Combine(dir, "aoi.klarf");
            File.WriteAllText(klarfGood, RenderKlarf(dies43, defects3, classes3, 12, 3, 3, 1250));

            var klarfProbe = importer.Probe(klarfGood);
            Check(klarfProbe.Format == WaferMapFormat.Klarf && klarfProbe.Score >= 0.9,
                $"KLARF 自动识别（置信度 {klarfProbe.Score:F2}）");

            var km = importer.ImportMap(klarfGood);
            Check(km.SubstrateId == "WAFER-01" && km.LotId == "LOT-KLARF" && km.DeviceId == "UBM",
                "KLARF 头部晶圆/批次/产品字段解析");
            Check(km.Layout is { Columns: 4, Rows: 3, OriginLocation: OriginLocation.LowerLeft,
                   AxisDirection: AxisDirection.UpRight },
                "KLARF 网格 4x3 + LowerLeft/UpRight（XINDEX/YINDEX 自 DieOrigin 递增）");
            Check(Math.Abs(km.DieSizeX - 100) < 1e-9 && Math.Abs(km.DieSizeY - 200) < 1e-9,
                "KLARF 无独立 die 尺寸字段，按 DiePitch 填入 DieSize");
            Check(km.Dies.Count == 12, "KLARF die 图 12 颗全部写入");
            Check(km.GoodDies.Count() == 9 && km.Dies.Values.Count(d => d.Class == NeutralBinClass.Fail) == 3,
                "KLARF 有缺陷 die → Fail、无缺陷 → Pass（9 良 3 败）");
            Check(km.Defects.Count == 3, "KLARF 缺陷明细全部保留（不只留 die 级 pass/fail）");

            var kd1 = km.Defects.First(d => d.DefectId == 1);
            Check(kd1.DieX == 1 && kd1.DieY == 1 &&
                  Math.Abs(kd1.X - 30) < 1e-6 && Math.Abs(kd1.Y - 140) < 1e-6,
                "KLARF 缺陷绝对坐标 = DieOrigin + 索引×pitch + XREL/YREL");
            Check(kd1.ClassName == "BF Defect" && km.Defects.First(d => d.DefectId == 2).ClassName == "Reject",
                "KLARF 缺陷类号按 ClassLookup 取名");
            Check(kd1.ImageFile == "img_1_1.jpg", "KLARF 缺陷关联到 TiffFileName 复核图");
            Check(km.Issues.Any(i => i.Code == "KLARF_NO_DIE_BIN") &&
                  km.Issues.Any(i => i.Code == "KLARF_EXTRA_COLUMNS"),
                "KLARF 明示 die bin 推导口径 + 多余列按字段名忽略");
            Check(!km.Issues.Any(i => i.Code is "KLARF_INDEX_MISMATCH" or "KLARF_REL_MISMATCH" or
                      "KLARF_GEOMETRY_INCONSISTENT" or "KLARF_SUMMARY_DEFECT" or "KLARF_SUMMARY_DEFDIE" or
                      "KLARF_SUMMARY_NDIE" or "KLARF_DENSITY_MISMATCH" or "KLARF_CLASS_GAP" or
                      "KLARF_ORPHAN_DEFECT" or "KLARF_IMAGE_MISSING" or "KLARF_MULTI_DEFECT_DIE"),
                "KLARF 自洽文件不产生结构性告警");

            var klarfProjected = NeutralMapProjector.ToDieResults(km);
            Check(klarfProjected.Count == 12 && klarfProjected.Count(d => d.Bin?.Quality == BinQuality.Fail) == 3,
                "KLARF 投影为 DieResult，3 颗缺陷 die 为 Fail");

            // 坐标与 die 索引不自洽：die 归属不可信，必须告警
            var klarfBadIndex = Path.Combine(dir, "bad-index.klarf");
            File.WriteAllText(klarfBadIndex, RenderKlarf(dies43,
                [new KDef(1, 1, 1, 30, 40, 10, 20, 100, 2, XShift: 1)], classes3, 12, 1, 1, 1250));
            var badIndexMap = importer.ImportMap(klarfBadIndex);
            Check(badIndexMap.Issues.Any(i => i.Code == "KLARF_INDEX_MISMATCH") &&
                  badIndexMap.Issues.Any(i => i.Code == "KLARF_REL_MISMATCH"),
                "KLARF X/Y 与 XINDEX/YINDEX 不自洽时告警（不静默采信）");

            // 缺陷落在 die 图未声明的格位：归属错位
            var klarfOrphan = Path.Combine(dir, "orphan.klarf");
            File.WriteAllText(klarfOrphan, RenderKlarf(dies43,
                [new KDef(1, 9, 9, 10, 10, 10, 10, 50, 1)], classes3, 12, 1, 1, 1250));
            var orphanMap = importer.ImportMap(klarfOrphan);
            Check(orphanMap.Issues.Any(i => i.Code == "KLARF_ORPHAN_DEFECT") &&
                  orphanMap.Dies.Count == 13,
                "KLARF 缺陷落在未声明 die 上时告警，并按缺陷位补一颗 Fail die");

            // SummaryList 三大计数 + 密度对账
            var klarfBadSummary = Path.Combine(dir, "bad-summary.klarf");
            File.WriteAllText(klarfBadSummary, RenderKlarf(dies43, defects3, classes3,
                declaredDie: 12, declaredDefect: 9, declaredDefDie: 7, density: 999));
            var badSummaryMap = importer.ImportMap(klarfBadSummary);
            Check(badSummaryMap.Issues.Any(i => i.Code == "KLARF_SUMMARY_DEFECT") &&
                  badSummaryMap.Issues.Any(i => i.Code == "KLARF_SUMMARY_DEFDIE") &&
                  badSummaryMap.Issues.Any(i => i.Code == "KLARF_DENSITY_MISMATCH"),
                "KLARF SummaryList 计数与密度口径不符时逐项告警");

            // 类表有空洞 + 缺陷类号未定义
            var klarfClassGap = Path.Combine(dir, "class-gap.klarf");
            File.WriteAllText(klarfClassGap, RenderKlarf(dies43, defects3,
                [(0, "Accept"), (1, "Reject"), (3, "BF Defect")], 12, 3, 3, 1250, declaredClasses: 5));
            var classGapMap = importer.ImportMap(klarfClassGap);
            Check(classGapMap.Issues.Any(i => i.Code == "KLARF_CLASS_GAP") &&
                  classGapMap.Issues.Any(i => i.Code == "KLARF_UNDEFINED_CLASS"),
                "KLARF ClassLookup 有空洞且缺陷类号落在空洞内时告警");

            // 外接框与面积矛盾（填充率 > 100%）
            var klarfGeom = Path.Combine(dir, "geometry.klarf");
            File.WriteAllText(klarfGeom, RenderKlarf(dies43,
                [new KDef(1, 1, 1, 10, 20, 10, 10, 500, 1)], classes3, 12, 1, 1, 1250));
            Check(importer.ImportMap(klarfGeom).Issues.Any(i => i.Code == "KLARF_GEOMETRY_INCONSISTENT"),
                "KLARF DEFECTAREA 与外接框矛盾时告警（面积阈值筛片前须确认口径）");

            // 一块多记录：同一 TiffFileName 下两条缺陷
            var klarfMulti = Path.Combine(dir, "multi.klarf");
            File.WriteAllText(klarfMulti, RenderKlarf(dies43,
                [new KDef(1, 1, 1, 10, 20, 10, 10, 50, 1, Group: 0),
                 new KDef(2, 1, 1, 40, 20, 10, 10, 50, 1, Group: 0)], classes3, 12, 2, 1, 1250));
            var multiMap = importer.ImportMap(klarfMulti);
            Check(multiMap.Issues.Any(i => i.Code == "KLARF_MULTI_RECORD_BLOCK") &&
                  multiMap.Issues.Any(i => i.Code == "KLARF_MULTI_DEFECT_DIE") &&
                  multiMap.Defects.Count == 2,
                "KLARF 一个 DefectList 块含多条缺陷时告警，且两条都保留");

            // 扩展名 .klarf 但内容不是 KLARF：必须拒绝识别
            var notKlarf = Path.Combine(dir, "plain.klarf");
            File.WriteAllText(notKlarf, "this is not a klarf file");
            try
            {
                importer.Import(notKlarf);
                Check(false, "非 KLARF 的 .klarf 文件应抛异常");
            }
            catch (UnsupportedWaferMapFormatException)
            {
                Check(true, "非 KLARF 内容不会被误识别（不靠扩展名猜）");
            }
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

    /// <summary>合成 KLARF 的一条缺陷：X/Y 由 DieOrigin + 索引×DiePitch + XREL/YREL 推出。</summary>
    private sealed record KDef(
        int Id, int Xi, int Yi, double XRel, double YRel,
        double SizeX, double SizeY, double Area, int Class,
        int Group = -1, int XShift = 0);

    /// <summary>
    /// 构造一份最小 KLARF 样本：头部 + ClassLookup + SampleDieMap + SampleTestPlan
    /// + 每缺陷一个 TiffFileName/DefectList 块 + SummaryList。
    /// 缺陷记录刻意写成 21 列（DefectRecordSpec 声明 19 个字段 + 2 列尾巴），
    /// 与真实 XCluster 样本一致，用来验证"按字段名取值"而非按列位置。
    /// </summary>
    private static string RenderKlarf(
        IReadOnlyList<(int X, int Y)> dies,
        IReadOnlyList<KDef> defects,
        IReadOnlyList<(int Id, string Name)> classes,
        int declaredDie,
        int declaredDefect,
        int declaredDefDie,
        double density,
        int? declaredClasses = null)
    {
        const double originX = -100, originY = -100, pitchX = 100, pitchY = 200;
        var sb = new StringBuilder();
        sb.AppendLine("FileVersion 1 7;");
        sb.AppendLine("FileTimestamp 05-14-2023 01:55:13;");
        sb.AppendLine("TiffSpec 6.0 NA R;");
        sb.AppendLine("InspectionStationID \"RudolphTechnologies\" \"XCluster\" \"AOI-30\";");
        sb.AppendLine("SampleType WAFER;");
        sb.AppendLine("ResultTimestamp 05-14-2023 01:55:12;");
        sb.AppendLine("LotID \"LOT-KLARF\";");
        sb.AppendLine("SampleSize 1 200;");
        sb.AppendLine("SetupID \"SETUP-1\" 08-17-2022 09:21:05;");
        sb.AppendLine("DeviceID \"UBM\";");
        sb.AppendLine("StepID \"STEP-1\";");
        sb.AppendLine("SampleOrientationMarkType NOTCH;");
        sb.AppendLine("OrientationMarkLocation DOWN;");
        sb.AppendLine($"DiePitch {F(pitchX)} {F(pitchY)};");
        sb.AppendLine($"DieOrigin {F(originX)} {F(originY)};");
        sb.AppendLine("WaferID \"WAFER-01\";");
        sb.AppendLine("Slot 1;");
        sb.AppendLine("SampleCenterLocation 0.000 0.000;");

        sb.AppendLine($"ClassLookup {declaredClasses ?? classes.Count}");
        foreach (var (id, name) in classes) sb.AppendLine($"{id} \"{name}\"");

        sb.AppendLine($"SampleDieMap {dies.Count}");
        for (var i = 0; i < dies.Count; i++)
            sb.AppendLine(i == dies.Count - 1 ? $"{dies[i].X} {dies[i].Y};" : $"{dies[i].X} {dies[i].Y}");

        sb.AppendLine("InspectionTest 1;");
        sb.AppendLine($"SampleTestPlan {dies.Count}");
        for (var i = 0; i < dies.Count; i++)
            sb.AppendLine(i == dies.Count - 1 ? $"{dies[i].X} {dies[i].Y};" : $"{dies[i].X} {dies[i].Y}");

        sb.AppendLine($"AreaPerTest {F(dies.Count * pitchX * pitchY)};");
        sb.AppendLine("DefectRecordSpec 19 DEFECTID X Y XREL YREL XINDEX YINDEX XSIZE YSIZE DEFECTAREA " +
                      "DSIZE CLASSNUMBER TEST CLUSTERNUMBER ROUGHBINNUMBER FINEBINNUMBER REVIEWSAMPLE " +
                      "IMAGECOUNT IMAGELIST;");

        for (var i = 0; i < defects.Count;)
        {
            var group = defects[i].Group;
            var block = new List<KDef> { defects[i] };
            i++;
            while (group >= 0 && i < defects.Count && defects[i].Group == group)
            {
                block.Add(defects[i]);
                i++;
            }

            sb.AppendLine($"TiffFileName img_{block[0].Id}_1.jpg;");
            sb.AppendLine("DefectList");
            for (var k = 0; k < block.Count; k++)
            {
                var d = block[k];
                var x = originX + (d.Xi + d.XShift) * pitchX + d.XRel;
                var y = originY + d.Yi * pitchY + d.YRel;
                // DefectRecordSpec 19 个字段（…ROUGHINNUMBER FINEBINNUMBER REVIEWSAMPLE IMAGECOUNT IMAGELIST）
                // + 真实样本的 2 列尾巴 "1 0"
                var row = string.Join(' ',
                    d.Id, F(x), F(y), F(d.XRel), F(d.YRel), d.Xi, d.Yi,
                    F(d.SizeX), F(d.SizeY), F(d.Area), F(Math.Sqrt(d.Area)),
                    d.Class, 1, 0, d.Class, 0, 1, 1, 1, 1, 0);
                sb.AppendLine(k == block.Count - 1 ? row + ";" : row);
            }
        }

        sb.AppendLine("SummarySpec 5 TESTNO NDEFECT DEFDENSITY NDIE NDEFDIE; ");
        sb.AppendLine("SummaryList");
        sb.AppendLine($"1 {declaredDefect} {density} {declaredDie} {declaredDefDie};");
        sb.AppendLine("WaferStatus P;");
        sb.Append("EndOfFile;");
        return sb.ToString();

        static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 构造一份最小可用的 TSK DAT 样本（头部 + 特殊记录 + 网格），字段偏移与真实样本一致。
    /// 网格记录按真实约定编码：v=|col+51| 幅值、几何码 bit3 表示参考列右/左、w=|79-行号|。
    /// </summary>
    private static byte[] BuildTskDat(
        int rows,
        int columns,
        string waferId,
        string lotId,
        string product,
        int total,
        int pass,
        int fail,
        Func<int, int, (byte Flag, byte Bin)> cell)
    {
        var buffer = new byte[0xEC + rows * columns * 6 + 0x40];
        WriteAscii(buffer, 0x00, "OP  ");
        WriteAscii(buffer, 0x14, product, 16);
        WriteUInt32(buffer, 0x28, 360000);
        WriteUInt32(buffer, 0x2C, 158000);
        WriteUInt16(buffer, 0x30, 180);
        WriteUInt16(buffer, 0x34, columns);
        WriteUInt16(buffer, 0x36, rows);
        WriteAscii(buffer, 0x3C, waferId, 11);
        WriteAscii(buffer, 0x52, lotId, 8);
        foreach (var offset in (int[])[0x94, 0xA0, 0xAC, 0xB8])
            WriteAscii(buffer, offset, "2410092316", 10);
        WriteUInt16(buffer, 0xD2, total);
        WriteUInt16(buffer, 0xD4, pass);
        WriteUInt16(buffer, 0xD6, fail);

        // 与真实样本一致的特殊记录：X=0x0040 / Y=0x0100 / bin=0
        buffer[0xE6 + 1] = 0x40;
        buffer[0xE6 + 3] = 0x01;

        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 1; column <= columns; column++)
            {
                var (flag, bin) = cell(row, column);
                var (v, rightSide) = column >= 4 ? (column - 4, true) : (4 - column, false);
                var o = 0xEC + index * 6;
                buffer[o] = flag;
                buffer[o + 1] = (byte)v;
                buffer[o + 2] = (byte)((flag == 0x10 ? 0x02 : 0x40) | (rightSide ? 0x08 : 0x00) | 0x04);
                buffer[o + 3] = (byte)Math.Abs(79 - row);
                buffer[o + 5] = bin;
                index++;
            }
        }

        return buffer;
    }

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteAscii(byte[] buffer, int offset, string text, int length = -1)
    {
        var count = length < 0 ? text.Length : length;
        for (var i = 0; i < count; i++)
            buffer[offset + i] = i < text.Length ? (byte)text[i] : (byte)' ';
    }
}
