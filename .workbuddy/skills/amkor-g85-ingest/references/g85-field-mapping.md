# SEMI G85-1101 字段映射与校验参考

来源：用户提供的真实样本 `IBM_AURA_3.1_OQA_Setup1_4EEEB00000_HW3UREG.xml`（Amkor / Camtek OQA，FormatRevision=`SEMI G85-1101`），
并与 Artwork G85 格式页、Micron TN0021 规范交叉核对。

## 1. 文件结构

```
<Maps>
  <Map WaferId FormatRevision>            ← 载体标识
    <Device ProductId LotId CreateDate Orientation Rows Columns
            DeviceSizeX/Y StepSizeX/Y OriginLocation BinType FrameId NullBin>
      <Bin BinCode BinCount BinQuality BinDescription/>   ← 每个非 Null bin 一条
      <SupplierData .../>                 ← 厂商扩展字段（SLOT、REFP1、DUTMS 等）
      <Data MapName Version>
        <Row><![CDATA[空格分隔的 bin 码]]></Row> × Rows
      </Data>
    </Device>
  </Map>
</Maps>
```

## 2. 字段映射表（G85 → NeutralWaferMap）

| G85 字段 | 样本值 | 中性模型字段 | 转换规则 |
|---|---|---|---|
| `Map/@WaferId` | `4EEEB00000-HW3UREG` | `SubstrateId` | 原样 |
| `Map/@FormatRevision` | `SEMI G85-1101` | `Metadata["Revision"]` | 校验含 G85 |
| `Device/@ProductId` | `IBM_AURA_3.1_OQA` | `DeviceId` | 原样 |
| `Device/@LotId` | `4EEEB00000` | `LotId` | 原样 |
| `Device/@Rows` | `66` | `Layout.Rows` | int |
| `Device/@Columns` | `92` | `Layout.Columns` | int |
| `Device/@StepSizeX/Y` | `2098 / 2897.9` | `Layout.StepSizeX/Y` | 原样（单位见 DUTMS，不换算） |
| `Device/@DeviceSizeX/Y` | `2005.755 / 2806.044` | `DieSizeX/Y` | 原样（µm） |
| `Device/@OriginLocation` | `2` | `Layout.OriginLocation` | **数字码 2 → UpperLeft**（Micron TN0021）；其余码见下 |
| `Device/@Orientation` | `180` | `Layout.Orientation` | 数字角 → Degree180 |
| `Device/@BinType` | `Decimal` | `Metadata["BinType"]` | 决定 token 解析为十进制字符串 |
| `Device/@NullBin` | `255` | `Metadata["NullBin"]` | 匹配的 token → Null die |
| `Device/@FrameId` | `HW3UREG` | `Metadata["FrameId"]` | 原样 |
| `Device/@CreateDate` | `20140930165234` | `Metadata["CreateDate"]` | 原样（YYYYMMDDhhmmss） |
| `Bin/@BinCode` | `000/001/007` | `NeutralBinDefinition.Code` | 原样字符串（保留前导零） |
| `Bin/@BinCount` | `51/198/4463` | （校验用） | 与阵列实际计数交叉核对 |
| `Bin/@BinQuality` | `Fail/Pass/None` | `NeutralBinDefinition.Class` | **None → Marked**，其余见下 |
| `Bin/@BinDescription` | `Non-pickable site` 等 | `Description` | 原样 |
| `SupplierData/@REFP1.X/Y` | `38 / 65` | `ReferenceDie` | 参考 die 阵列坐标 |
| `SupplierData/@SLOT` | `2` | `Metadata["SLOT"]` | 原样 |
| `SupplierData/@DUTMS` | `10^-6*m` | `Metadata["DUTMS"]` | 单位，`10^-6*m` = µm |
| `SupplierData/@ORGFNLOC` | `180` | （与 Orientation 交叉校验） | 不一致写告警 |
| `SupplierData/*` | 全部 | `Metadata[...]` | 原样保留，含 FileName、WAFERID、PRAXI 等 |
| `Data/@MapName` | `Camtek` | `Metadata["MapName"]` | 原样 |
| `Data/@Version` | `1` | `Metadata["MapVersion"]` | 原样 |
| `Data/Row` CDATA | `255 001 007 ...` | `NeutralDie(col,row)` | 按空白切分，逐 token 建 die |

## 3. BinQuality 语义映射（关键）

| G85 BinQuality | 样本 BinCode | 中性类别 | 说明 |
|---|---|---|---|
| `Pass` / `Good` | `001` | `Pass` | 可拾取良品，样本 198 颗 |
| `Fail` / `Bad` | `000` | `Fail` | 不可拾取，样本 51 颗 |
| `None` / `Marked` / 空 | `007` | **`Marked`** | Camtek "Marked Bin"，有效但未判定，样本 4463 颗。**禁止映射为 Null**（`E142EnumParser.ParseBinQuality("None")` 会返回 Null，是陷阱）。不进 `GoodDies`。 |
| `Null` | — | `Null` | 若出现；通常 NullBin 无 `<Bin>` 定义 |
| 未知 | — | `Unknown` | 不可拾取 + 告警 |

## 4. OriginLocation 数字码

| 码 | 映射 | 置信度 |
|---|---|---|
| `2` | `UpperLeft` | **权威**（Micron TN0021：2 = upper left / top side），无告警 |
| `1` | `UpperRight` | 推断，附告警 |
| `3` | `LowerLeft` | 推断，附告警 |
| `4` | `LowerRight` | 推断，附告警 |
| 缺失/未知 | `LowerLeft` | 附 `G85_ORIGIN` 告警 |

G85 未显式给 AxisDirection；按行序自顶向下、列序自左向右，固定用 `AxisDirection.DownRight`（与上左原点配合，文件坐标 = 逻辑坐标）。

## 5. 校验清单

打开工具报告后逐条核对：

- [ ] 识别格式为 G85，置信度 ≥ 0.9
- [ ] `die总数 = Rows × Columns`（样本 6072 = 92×66），Null 位计入
- [ ] `Pass + Fail + Null + Marked + Unknown = die总数`
- [ ] 每个 `<Bin BinCount>` = 阵列实际计数，**无** `G85_BINCOUNT_MISMATCH`
- [ ] `GoodDies` 只含 Pass；Marked 不被当良品拾取
- [ ] Origin=UpperLeft、Axis=DownRight、Orient=Degree180（`G85_ORIGIN` 应无告警）
- [ ] 参考 die 已解析（样本 `(38,65)`），有 `G85_REFERENCE` 告警提醒上线前对齐
- [ ] `G85_UNITS` 告警转述给下游：µm 单位，与 mm 基准工具互操作前需换算
- [ ] 若同时有配套 SINF：已做转置一致性核对（见下）

## 6. G85 ↔ 配套 SINF 转置关系（实测）

同一片晶圆常同时存在 G85 XML 与 3-Digit SINF `.txt`。实测两者是**转置**关系：

| 项 | G85 XML | 配套 SINF | 关系 |
|---|---|---|---|
| 网格 | Rows=66, Columns=92 | ROWCT=92, COLCT=66 | 行列互换 |
| 参考 die | REFP1=(38, 65) | REFPX=65, REFPY=38 | X/Y 互换 |
| 方位 | Orientation=180 / ORGFNLOC=180 | FNLOC=90 | **不一致**，须产线确认 |
| bin 码 | `001/000/007/255` | `001/000/@@@/___` | `007`↔`@@@`（Marked/Skip），`255`↔`___`（Null），`001`↔`001`，`000`↔`000` |

实测按 `G85[y][x]` 与 `SINF[x][y]` 对照，6072 格中 6021 格一致；不一致的 51 格集中在 G85=`/000`/（Fail）对 SINF=`/007`/（Marked）的位置，属两文件判定口径差异，不是坐标错位。

**结论**：两份文件叠加/比对前必须转置对齐，并单独处理 51 格语义差异；直接按相同索引叠加会全盘错位。这是"map 转置不会报错"的真实案例。
