# 12. SEMI G85-1101 格式与 Amkor 样本接入

本篇记录一次完整的真实数据接入：把一份 Amkor / Camtek OQA 的 G85 晶圆图接进本工程，并把流程沉淀为可复用技能 `amkor-g85-ingest`。字段全部基于用户提供的真实样本 `IBM_AURA_3.1_OQA_Setup1_4EEEB00000_HW3UREG.xml`，并与 Artwork G85 格式页、Micron TN0021 交叉核对。

## 12.1 格式定位

SEMI G85-1101 是一种**较老的 XML 晶圆图格式**（G81 数据项 + G85 数据格式），由 Micron 推广，至今仍在 Amkor 等封测厂、Camtek 等检测设备的 OQA 数据里流通。它与 E142 是两套独立 schema：

| 维度 | G85-1101 | E142 |
|---|---|---|
| 根节点 | `<Maps><Map>` | `<MapData>` |
| 数据区 | `<Device><Data><Row>` + CDATA | `<SubstrateMaps><Overlay><BinCodeMap>` |
| bin 定义 | `<Bin BinCode BinCount BinQuality>` | `<BinDefinition>` |
| 原点 | `OriginLocation` 数字码 | `OriginLocation` 字符串枚举 |
| bin 类型 | 通常 `Decimal`（3 位码） | Ascii / Decimal / Hex / Integer2 |
| 载体 | 单层 wafer | 多 substrate + TransferMap 跨载体追溯 |

识别特征：`FormatRevision="SEMI G85-1101"` + `<Device>` + `NullBin` + `<Data><Row>`。本工程 `G85MapReader.DetectConfidence` 对真实样本给出 1.00，优先于 E142。

## 12.2 文件结构

```
<Maps>
  <Map WaferId="4EEEB00000-HW3UREG" FormatRevision="SEMI G85-1101">
    <Device ProductId LotId CreateDate Orientation Rows Columns
            DeviceSizeX/Y StepSizeX/Y OriginLocation BinType FrameId NullBin>
      <Bin BinCode="000" BinCount="51"  BinQuality="Fail" BinDescription="Non-pickable site"/>
      <Bin BinCode="001" BinCount="198" BinQuality="Pass" BinDescription="Pickable site"/>
      <Bin BinCode="007" BinCount="4463" BinQuality="None" BinDescription="Marked Bin"/>
      <SupplierData DUTMS="10^-6*m" SLOT="2" REFP1.X="38" REFP1.Y="65" ORGFNLOC="180" .../>
      <Data MapName="Camtek" Version="1">
        <Row><![CDATA[255 255 ... 007 001 ...]]></Row>   <!-- 92 个 token × 66 行 -->
      </Data>
    </Device>
  </Map>
</Maps>
```

## 12.3 三个必须避开的坑

**1. `BinQuality="None"` 是 Marked Bin，不是 Null。**
样本里 `007` 有 4463 颗（占 6072 格的 73%），描述是 "Marked Bin"——Camtek 检测标记，有效但未判定。E142 的 `E142EnumParser.ParseBinQuality("None")` 会把它映射成 `BinQuality.Null`，一旦复用就抹掉大半张图。本工程为此新增 `NeutralBinClass.Marked` / `BinQuality.Marked`，`007` 保留为有效 die、排除出 `GoodDies`，并写 `G85_BIN_MARKED` 告警要求产线确认 disposition。

**2. `OriginLocation` 是数字码。**
样本 `2`，按 Micron TN0021 = upper left（左上角），映射到 `OriginLocation.UpperLeft`。这与 E142 的字符串枚举不是一套。其余数字码各家实现不一，本工程对 `1/3/4` 按对称推断并附告警，对 `2` 才免告警。G85 不显式给轴向，按行序自顶向下、列序自左向右固定 `AxisDirection.DownRight`。

**3. `DUTMS` 决定单位，读取阶段不换算。**
样本 `10^-6*m` = 微米，`StepSizeX=2098`、`DeviceSizeX=2005.755` 都是 µm。中性模型原样保留并写 `G85_UNITS` 告警；与 mm 基准的 E142 Layout 互操作前须由调用方换算，解析器不偷偷改数。

## 12.4 字段映射（要点）

完整映射表见技能参考 `.workbuddy/skills/amkor-g85-ingest/references/g85-field-mapping.md`。

| G85 | 中性模型 | 规则 |
|---|---|---|
| `Map/@WaferId` | `SubstrateId` | 原样 |
| `Device/@ProductId` `/@LotId` | `DeviceId` / `LotId` | 原样 |
| `Device/@Rows` `/@Columns` | `Layout.Rows` / `Columns` | int |
| `Device/@StepSizeX/Y` | `Layout.StepSizeX/Y` | 原样（µm） |
| `Device/@DeviceSizeX/Y` | `DieSizeX/Y` | 原样（µm） |
| `Device/@OriginLocation=2` | `Layout.OriginLocation=UpperLeft` | 数字码映射 |
| `Device/@Orientation=180` | `Layout.Orientation=Degree180` | 数字角 |
| `Bin/@BinQuality` Pass/Fail/None | Pass / Fail / **Marked** | None 特殊处理 |
| `Bin/@BinCount` | 校验用 | 与阵列实际计数交叉核对 |
| `SupplierData/@REFP1.X/Y` | `ReferenceDie` | 参考 die |
| `SupplierData/*` | `Metadata[...]` | 扩展字段原样保留 |
| `Data/Row` CDATA | `NeutralDie(col,row)` | 空白切分，逐 token 建 die |

NullBin（`255`）保留为 `NeutralBinClass.Null` die，使网格完整 = Rows×Columns；投影后 null 位表现为 `IsDie=false`（G85 规范不为 NullBin 定义 `<Bin>`，故无 `Bin.Quality`）。

## 12.5 真实样本校验结果

`dotnet run --project E142Transfer.RealSampleCheck -- <样本路径>` 输出：

```
识别格式: G85 (置信度 1.00)
网格: 92x66, Origin=UpperLeft, Axis=DownRight, Orient=Degree180
Wafer=4EEEB00000-HW3UREG Lot=4EEEB00000 Device=IBM_AURA_3.1_OQA SLOT=2
参考die=(38, 65) DUTMS=10^-6*m
die总数=6072 GoodDies=198
  Pass   : 198
  Fail   : 51
  Null   : 1360
  Marked : 4463
```

四项与文件 `<Bin>` 的 `BinCount` 声明**逐项吻合**；无 `G85_BINCOUNT_MISMATCH`、`G85_ORIGIN` 告警，Marked / 单位 / 参考 die 三条预期告警齐备。Python 预检脚本 `validate_g85.py` 在真实样本上同样全部通过。

## 12.6 与配套 SINF 的转置关系（重要）

同目录的 `4EEEB00000 HW3UREG.txt` 是 3-Digit SINF，两者是同一片晶圆的两种表示，但做了**行列转置**：

| 项 | G85 XML | SINF | 关系 |
|---|---|---|---|
| 网格 | 66×92 | 92×66 | 转置 |
| 参考 die | `(38,65)` | `REFPX=65, REFPY=38` | X/Y 互换 |
| 方位 | 180 / ORGFNLOC=180 | FNLOC=90 | 不一致 |
| bin | `001/000/007/255` | `001/000/@@@/___` | Marked / Null 码不同 |

实测按 `G85[y][x]` 与 `SINF[x][y]` 对照，6072 格中 6021 格一致；51 格差异集中在 G85=`000`(Fail) 对 SINF=`007`(Marked)，是判定口径差异而非坐标错位。**叠加比对前必须转置对齐，并单独处理这 51 格**；直接按相同索引叠加会全盘错位——这正是"map 转置不会报错"的真实案例。

## 12.7 复用流程：amkor-g85-ingest 技能

已把本次接入固化为项目级技能 `.workbuddy/skills/amkor-g85-ingest/`：

- `SKILL.md`：五步工作流（识别→映射→校验→集成回归→提交）+ 异常处理原则；
- `references/g85-field-mapping.md`：完整字段映射表、BinQuality 语义、Origin 数字码、校验清单、G85↔SINF 转置关系；
- `scripts/validate_g85.py`：无 .NET 环境的轻量预检脚本（标准库即可运行）。

以后再拿到新的 Amkor/G85 数据，按同一流程执行：
1. `dotnet run --project E142Transfer.RealSampleCheck -- <新样本>` 出报告；
2. 对照映射表逐字段确认，重点盯 None/Origin/单位；
3. 按校验清单逐条核对 Issues；
4. 若有新变体，扩展 `G85MapReader` + 自检用例，跑全量自检；
5. 更新本文档并提交推送。

## 12.8 本次改动文件清单

| 文件 | 改动 |
|---|---|
| `Import/Abstractions/IWaferMapAdapter.cs` | 新增 `WaferMapFormat.G85` |
| `Import/Readers/G85MapReader.cs` | 新增 G85 读取器（识别/映射/校验全套） |
| `Import/Detection/WaferMapFormatDetector.cs` | 默认注册 G85MapReader |
| `Import/Model/NeutralWaferMap.cs` | 新增 `Marked` 类别、`DieSize*`、`ReferenceDie`、`Metadata` |
| `Models/E142Enums.cs` | 新增 `BinQuality.Marked` |
| `Import/NeutralMapProjector.cs` | Marked 投影映射 |
| `E142Transfer.SelfTest/ImportChecks.cs` | 新增 20 项 G85 自检 |
| `E142Transfer.RealSampleCheck/` | 新增验证工具项目并加入 solution |
| `.workbuddy/skills/amkor-g85-ingest/` | 新增可复用技能 |
| `docs/12-SEMI-G85-格式与Amkor样本接入.md` | 本文档 |
