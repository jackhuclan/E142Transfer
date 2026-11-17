# 14 KLARF 真实样本解析（AOI 缺陷清单 · KC07043-14G1）

[返回索引](./README.md)

> 样本：`E:\wafer-ws\Map图谱\KC07043-14G1\DA2305158B_KC07043-14G1_UBM.klarf`
> 490,933 字节 / 60,786 行 / CRLF / 纯 ASCII 无 BOM / 末行不带换行
> 同目录另有 74 张 `DA2305158B_KC07043-14G1_UBM_<缺陷号>_1.jpg` 复核图

## 结论先行

这份文件是 **Rudolph Technologies XCluster AOI-30** 输出的 KLARF 1.7，
语义上是**「抽样 die 图 + 74 条缺陷清单」**，不是电测结果 map：

| 维度 | 本文件的答案 |
|---|---|
| 谁产的 | `InspectionStationID "RudolphTechnologies" "XCluster" "AOI-30"`（光学外观检测，非探针台） |
| 有多少 die | `SampleDieMap` 声明 **30218** 颗，die 图覆盖 238×159 的标称包围盒，圆形外 7624 格空缺 |
| 有多少缺陷 | **74 条**，落在 **73 颗** die 上（有一颗 die 上两条，所以 NDEFECT ≠ NDEFDIE） |
| 有 die 的 pass/fail 吗 | **没有**。KLARF 的 die 图只声明"哪些格位有 die"，bin 语义完全缺失 |
| 缺陷类别 | 99 个类号定义（`ClassLookup`），实际 74 条缺陷**全是 90 = BF Defect** |

**一句话**：它回答"这颗 die 上哪个位置有什么缺陷、多大、长什么样"，
不回答"这颗 die 是 pass 还是 fail"。要得到 die 级结论，必须自己做聚合
（本工程的做法：有缺陷 → Fail，无缺陷 → Pass，并显式告警这是推导值）。

## 一、整体结构（行号即偏移索引）

```
行 1-18      HEADER：18 条 `KEY value;` 语句（身份 / 时间 / 几何 / 抽样）
行 19        ClassLookup 99            ← 声明 99 条
行 20-118    99 行类表：`<类号> "<类别名>"`
行 119       SampleDieMap 30218        ← 声明 30218 条
行 120-30337 30218 行 `X Y`（最后一行为 `127 158;`）
行 30338     InspectionTest 1;
行 30339     SampleTestPlan 30218      ← 声明 30218 条
行 30340-60557 30218 行 `X Y`（与 SampleDieMap 逐行相同）
行 60558     AreaPerTest 28284047384.012;
行 60559     TiffFileName …_1_1.jpg;
行 60560     DefectRecordSpec 19 DEFECTID X Y … IMAGELIST;   ← 自描述列名表
行 60561     DefectList
行 60562     缺陷 1（21 列，行尾 `;`）
行 60563-60781  ×73 重复块：TiffFileName → DefectList → 1 条缺陷
行 60782     SummarySpec 5 TESTNO NDEFECT DEFDENSITY NDIE NDEFDIE;
行 60783     SummaryList
行 60784     1 74 0.326952 30218 73;
行 60785     WaferStatus P;
行 60786     EndOfFile;（文件到此结束，无换行）
```

层级关系：

```
KLARF
├─ HEADER ─────────────── 晶圆身份 + 决定坐标落点的几何四要素
├─ ClassLookup 99 ────── 缺陷类号 → 名称（缺陷级语义，不是 die bin）
├─ SampleDieMap 30218 ── (XINDEX, YINDEX) 全量 die 格位；仅坐标，无 bin
├─ InspectionTest / SampleTestPlan 30218
│                        └─ 测试定义 + 被检 die 清单（本文件同样只有坐标）
├─ AreaPerTest ───────── 受检面积，用于缺陷密度
├─ DefectRecordSpec 19 ─ 列名表（列的顺序由它声明，不是固定位置）
├─ DefectList（74 块）── TiffFileName + 逐条缺陷（19 字段 + 样本多出 2 列）
└─ SummarySpec + SummaryList + WaferStatus
                         └─ 晶圆级统计与状态
```

**关键点**：`ClassLookup` 的类号是**缺陷类别**，不是 die 的 bin。
把 90 当成 die 的 bin 码去渲染，会把 74 条缺陷画成 74 颗 die 的"bin 90"，语义全错。

## 二、HEADER 字段含义（逐条实测）

| 字段 | 本文件值 | 含义与坑 |
|---|---|---|
| `FileVersion` | `1 7` | KLARF 1.7（主版本 1、次版本 7），不是 "1.7" 浮点 |
| `FileTimestamp` | `05-14-2023 01:55:13` | 文件写出时间（`MM-DD-YYYY`，美式月在前，勿按日在前解析） |
| `ResultTimestamp` | `05-14-2023 01:55:12` | 检测完成时间，比文件时间早 1 秒 |
| `TiffSpec` | `6.0 NA R` | 位图规格声明为 TIFF，**但实际引用的是 JPG**（见异常 8） |
| `InspectionStationID` | `"RudolphTechnologies" "XCluster" "AOI-30"` | 厂商 / 机型 / 站位，三段引号串；AOI 而非 prober |
| `SampleType` | `WAFER` | 样本类型，决定 `SampleDieMap` 的坐标系 |
| `LotID` | `"DA2305158B"` | 批次 |
| `SampleSize` | `1 200` | 声明式抽样规模；与 30218 颗 die、74 条缺陷都无直接换算关系（口径未声明） |
| `SetupID` | `"2AI000IM1-DA-DW9714V" 08-17-2022 09:21:05` | 配方 ID + 配方建立时间（2022-08，比检测早 9 个月） |
| `DeviceID` / `StepID` | `"UBM"` / `"2AI000IM1"` | 器件名 / 工序号（UBM = Under Bump Metallization，凸块底层金属） |
| `SampleOrientationMarkType` | `NOTCH` | 方位标记类型 |
| `OrientationMarkLocation` | `DOWN` | **notch 朝下**——多数工具默认 notch-up，投影前必须统一（见异常 9） |
| `DiePitch` | `800.000 1170.000` | die 间距 µm；本格式没有独立 die 尺寸字段，尺寸 = pitch |
| `DieOrigin` | `-95118.641 -89765.812` | die (0,0) 左下角的绝对坐标 µm（晶圆坐标系，圆心为 0,0） |
| `WaferID` | `"KC07043-14G1"` | 晶圆号（与文件名前缀一致） |
| `Slot` | `1` | 片盒槽位 |
| `SampleCenterLocation` | `0.000 0.000` | 晶圆圆心在机台坐标系中的位置；这里已是 0，说明坐标已归心 |

## 三、坐标三层对应关系（实测 0 偏差）

一条缺陷记录里同时给了三套坐标，它们是**同一事实的三种表达**：

```
X  = DieOrigin.X + XINDEX × DiePitch.X + XREL
Y  = DieOrigin.Y + YINDEX × DiePitch.Y + YREL
```

以 1 号缺陷验证：

```
X  = -95118.641 + 135 × 800  + 638.675 =  13520.034  ✓
Y  =  -89765.812 +   6 × 1170 + 896.383 = -81849.430  ✓
```

- `X / Y`：缺陷中心的**绝对坐标**（µm，原点为晶圆中心）
- `XINDEX / YINDEX`：缺陷所在 die 的**阵列索引**（0 基，自 DieOrigin 起，X 右增、Y 上增）
- `XREL / YREL`：缺陷在 die 内的**相对偏移**（0..DiePitch）

全量 74 条用上式反算：`XINDEX/YINDEX` 不符 **0** 条，`XREL/YREL` 不符 **0** 条，
`XREL` 落在 0..800、`YREL` 落在 0..1170 之外 **0** 条 —— 三条链路完全自洽。

**这条公式是判断文件可信度的第一道闸**：任何一条对不上，说明写入方的坐标基准或
pitch 算错，die 归属就不可信，必须告警而不是静默采信。

## 四、DefectRecordSpec 与缺陷记录：19 个字段 vs 21 列

`DefectRecordSpec` 是**自描述列名表**，列的先后由它声明，解析器必须按名字取：

```
DefectRecordSpec 19 DEFECTID X Y XREL YREL XINDEX YINDEX XSIZE YSIZE DEFECTAREA DSIZE
                    CLASSNUMBER TEST CLUSTERNUMBER ROUGHBINNUMBER FINEBINNUMBER
                    REVIEWSAMPLE IMAGECOUNT IMAGELIST;
```

实际缺陷行（1 号）：

```
1 13520.034 -81849.430 638.675 896.383 135 6 9.321 18.623 59.010 20.207 90 1 0 90 0 1 1 1 1 0;
```

按名字对齐后：

| 序号 | 字段 | 值 | 含义 |
|---|---|---|---|
| 1 | `DEFECTID` | 1 | 缺陷号，与 `TiffFileName` 的 `_1_1` 对应 |
| 2-3 | `X` `Y` | 13520.034 / -81849.430 | 绝对坐标 µm |
| 4-5 | `XREL` `YREL` | 638.675 / 896.383 | die 内偏移 µm |
| 6-7 | `XINDEX` `YINDEX` | 135 / 6 | die 阵列索引 |
| 8-9 | `XSIZE` `YSIZE` | 9.321 / 18.623 | 外接框 µm（量化到 **1.864 µm** 像素格） |
| 10 | `DEFECTAREA` | 59.010 | 面积 µm²（≈ 17 个像素 × 3.4745 µm²） |
| 11 | `DSIZE` | 20.207 | 等效直径 µm，与面积不成几何关系（见异常 6） |
| 12 | `CLASSNUMBER` | 90 | 缺陷类别，查 `ClassLookup` → "BF Defect" |
| 13 | `TEST` | 1 | 测试号，对应 `InspectionTest 1` |
| 14 | `CLUSTERNUMBER` | 0 | 簇号，全 0 表示未做簇分析 |
| 15 | `ROUGHBINNUMBER` | 90 | 粗分类号，本文件恒等于 `CLASSNUMBER` |
| 16 | `FINEBINNUMBER` | 0 | 细分类号，全 0（未细分） |
| 17 | `REVIEWSAMPLE` | 1 | 标记为需人工复核 |
| 18 | `IMAGECOUNT` | 1 | 复核图数量 |
| 19 | `IMAGELIST` | 1 | 图像索引 |
| 20-21 | **规范外多出** | 1 / 0 | 74/74 条恒为 `1 0`，语义未知（见异常 5） |

注意字段 15/16 的真实名字是 `ROUGHBINNUMBER` / `FINEBINNUMBER`（**BIN + NUMBER**）——
手抄时极易漏掉中间的 B，一旦按名索引就会全部取 0，把粗分类悄悄丢掉。

## 五、die 图：只有格位，没有 bin

`SampleDieMap 30218` 后面每行只有两列 `X Y`：

```
84 0
85 0
86 0
…
127 158;
```

- X 范围 0..237（238 列）、Y 范围 0..158（159 行），标称包围盒 238×159 = 37842 格，
  实际只声明 **30218** 格 → 缺的 **7624** 格就是晶圆轮廓外的位置。
- 每行 die 数从 18（上下边缘）变到 238（赤道行），拟合出**半径约 96.4 mm**的圆形
  （die 角点半径最大 96.55 mm）——200 mm 晶圆 + 边缘 exclusion，正常。
- `SampleTestPlan` 声明同样 30218 行，逐行与 `SampleDieMap` **完全相同**，且同样只有两列
  （标准 KLARF 应为 `X Y 缺陷数 测试号`）。

**因此本文件无法直接得到 die 级 bin**。要拿到 die 结论只有一条路：
按 `(XINDEX, YINDEX)` 把 `DefectList` 聚合到 die 上。

## 六、与 SummaryList 的对账

```
SummarySpec 5 TESTNO NDEFECT DEFDENSITY NDIE NDEFDIE;
SummaryList
1 74 0.326952 30218 73;
```

| 字段 | 声明 | 实测 | 结论 |
|---|---|---|---|
| `NDEFECT` | 74 | 74 条缺陷记录 | ✓ |
| `NDEFDIE` | 73 | 73 个不同 die | ✓（有 1 颗 die 上两条缺陷） |
| `NDIE` | 30218 | 30218 行 SampleDieMap | ✓ |
| `DEFDENSITY` | 0.326952 | 74 / (282.84 cm²) = **0.261632** | ✗ 差 1.2497 倍，口径未声明 |

`AreaPerTest 28284047384.012 µm²` 本身是可自证的：

```
30218 颗 × (800 × 1170 µm) = 28284048000 µm²  → 与声明值比值 1.000000
即 28284.05 mm² = 282.84 cm²
```

所以密度差异不在面积，而在分子或口径（疑为按别的采样面积或另一套 NDEFECT 口径算）。
**结论：`DEFDENSITY` 不可直接引用**，要用密度就自己按 `NDEFECT / AreaPerTest` 算。

## 七、异常值、缺失字段与边界情况

| # | 现象 | 实测证据 | 影响与处理 |
|---|---|---|---|
| 1 | **die 图无 bin** | `SampleDieMap` 每行仅 2 列 | 不能把 KLARF 当结果级 map；die 类别必须自聚合，且须明示"非官方 disposition" |
| 2 | 缺陷类别**全同类** | 74 条全为 90 `BF Defect` | 按类别的帕累托/良率损失分析会失真（本文件未做缺陷分类） |
| 3 | `ClassLookup` 有**类号空洞** | 声明 99 条，类号 0..128 中缺 93..122 共 30 个 | 缺陷类号落在空洞里时无类别名，只能显示类号；不能假设类号连续 |
| 4 | 同 die 多条缺陷 | die (228,43) 上有 20、21 两条（X 相差 122 µm、Y 相差 829 µm） | `NDEFECT(74) ≠ NDEFDIE(73)` 属正常；统计时"缺陷数"与"缺陷 die 数"不可混用 |
| 5 | 缺陷记录**多 2 列** | 74/74 条为 21 列，末尾恒 `1 0` | 必须按 `DefectRecordSpec` 的字段名索引取值；按位置硬编码会在末列错位 |
| 6 | **面积与外接框矛盾** | 5 号：`620.8×989.1 µm` 外接框却 `AREA=960000.063`（填充率 156%）；20 号：`140×84` 像素框只 60 像素面积（0.51%） | 面积与框不是同一分割口径；用面积阈值筛片前必须与供应商确认，不能自行换算 |
| 7 | `DEFDENSITY` 不可复现 | 声明 0.326952 vs 反算 0.261632 | 密度只做提示，勿入报表 |
| 8 | `TiffSpec` 与实际格式不符 | 声明 `6.0 NA R`（TIFF），实际 74 个 `.jpg` | 按 `TiffFileName` 原名打开即可，别按声明硬编解码器 |
| 9 | **notch 朝下** | `OrientationMarkType=NOTCH` / `OrientationMarkLocation=DOWN` | 与多数工具默认 notch-up 相反，跨机台对齐/渲染前必须统一方位基准 |
| 10 | **缺 `WaferDiameter`** | 头部无该字段 | 晶圆轮廓只能由 die 分布拟合（反推半径 96.55 mm 含角点，实际略小） |
| 11 | `SampleTestPlan` 缺列 | 每行仅 `X Y`，标准应为 `X Y 缺陷数 测试号` | 逐 die 缺陷数只能由 `DefectList` 聚合，不能直读测试计划 |
| 12 | `SampleSize 1 200` 口径不明 | 30218 die / 74 缺陷都对不上 200 | 不要参与任何计算 |
| 13 | 图片是引用不是内嵌 | 74 个 `TiffFileName`，目录里 74 个 jpg，无缺失 | 迁移文件时必须连图一起搬，否则丢失复核证据 |
| 14 | 边缘 die 缺陷 | 缺陷中心半径 7.07..96.39 mm，最外 5 条都在 96.2 mm 以上 | 边缘 die 的缺陷常被判为 process 噪声，统计时按半径分环考虑 |

补充：`FileTimestamp` 用 `MM-DD-YYYY`（05-14-2023），`ResultTimestamp` 相同格式；
`SetupID` 带配方建立时间 2022-08-17，与检测日 2023-05-14 相隔 9 个月 —— 配方与检测不同期，
做溯因分析时要注意。

## 八、接入实现（KlarfMapReader）

### 字段映射

| KLARF | NeutralWaferMap | 说明 |
|---|---|---|
| `WaferID` / `LotID` / `DeviceID` | `SubstrateId` / `LotId` / `DeviceId` | 去引号 |
| `DiePitch` X/Y | `DieSizeX/Y` + `Layout.StepSizeX/Y` | KLARF 无独立 die 尺寸，尺寸即 pitch |
| `SampleDieMap` 的最大索引 + 1 | `Layout.Columns/Rows` | 238×159（不是 30218 的因数分解） |
| 索引方向 | `OriginLocation.LowerLeft` + `AxisDirection.UpRight` | XINDEX 右增、YINDEX 上增 |
| `SampleDieMap` 全部格位 | `NeutralDie{ BinCode="0", Class=Pass }` | 无缺陷 |
| `DefectList` 聚合后的 die | `NeutralDie{ BinCode="1", Class=Fail }` | 有缺陷 |
| `ClassLookup` | `NeutralDefect.ClassName` | 只用于缺陷命名，**不进 `BinDefinitions`** |
| 逐条缺陷 | `NeutralDefect`（含 X/Y/XREL/YREL/尺寸/类号/复核图） | 保留缺陷级信息，不做有损压缩 |
| `SummaryList` | `Metadata["Summary"]` + 密度两键 | 仅留档 |
| `AreaPerTest` 反推半径 | `Metadata["InferredRadiusMm"]` | 96.55 |

die 级 bin 只有两个合成码：

- `0` = 无缺陷（Pass）
- `1` = 含缺陷（Fail）

### 告警码

| 码 | 级别 | 触发条件 |
|---|---|---|
| `KLARF_NO_DIE_BIN` | 提示 | 恒发：声明 die 类别是推导值，不能当 yield |
| `KLARF_NO_DIEMAP` | 错误级 | 没有 / 空的 `SampleDieMap` |
| `KLARF_DIEMAP_COUNT` | 提示 | 声明条数 ≠ 实际解析条数 |
| `KLARF_DIEMAP_EXTRA` | 提示 | die 图出现第 3 列（本格式未用） |
| `KLARF_INDEX_MISMATCH` | 错误级 | X/Y 反算的索引 ≠ `XINDEX/YINDEX` |
| `KLARF_REL_MISMATCH` | 错误级 | `XREL/YREL` ≠ 反算偏移 |
| `KLARF_ORPHAN_DEFECT` | 错误级 | 缺陷落在 die 图未声明的格位 |
| `KLARF_GEOMETRY_INCONSISTENT` | 提示 | `DEFECTAREA` 与外接框填充率 >105% 或 <1% |
| `KLARF_CLASS_GAP` / `KLARF_CLASS_COUNT` / `KLARF_UNDEFINED_CLASS` | 提示 | 类表空洞 / 条数不符 / 类号未定义 |
| `KLARF_SINGLE_CLASS` | 提示 | 全部缺陷同一类 |
| `KLARF_MULTI_DEFECT_DIE` | 提示 | 存在一 die 多缺陷 |
| `KLARF_MULTI_RECORD_BLOCK` | 提示 | 一个 `DefectList` 块含多条记录（图片归属歧义） |
| `KLARF_EXTRA_COLUMNS` | 提示 | 缺陷列数 > `DefectRecordSpec` 声明 |
| `KLARF_SUMMARY_DEFECT` / `_DEFDIE` / `_NDIE` / `KLARF_DENSITY_MISMATCH` | 提示 | 与 `SummaryList` 对账不符 |
| `KLARF_IMAGE_MISSING` / `KLARF_TIFFSPEC` | 提示 | 复核图缺失 / 位图规格与实际不符 |
| `KLARF_NO_DIAMETER` / `KLARF_ORIENTATION` / `KLARF_NO_PITCH` | 提示 | 缺直径 / 方位为 notch-down / 缺 pitch |
| `KLARF_TESTPLAN_NO_COUNT` | 提示 | 测试计划缺缺陷数列 |

### 解析器必须容忍的写法

1. 块续行数与声明条数不一致（类表 99 条正常，但可能声明多于实际）；
2. 每个缺陷一个 `TiffFileName`+`DefectList` 块，或一块多条记录；
3. `DefectList` 可能出现在 `DefectRecordSpec` 之前（本样本首个 `TiffFileName` 就在 spec 前一行）；
4. 行尾 `;` 可能带尾随空格（`SummarySpec … ; `）;
5. CRLF / 纯 ASCII / 末行无换行。

## 九、验证结果

| 验证项 | 结果 |
|---|---|
| 格式识别 | `Klarf`，置信度 **1.00**（不依赖扩展名，`.klarf` 内容不符会被拒识） |
| die 数 | 30218，与 `SampleDieMap` 声明、`SummaryList.NDIE` 三方一致 |
| 缺陷数 | 74 条，与 `NDEFECT` 一致；73 颗缺陷 die，与 `NDEFDIE` 一致 |
| 坐标自洽 | `XINDEX/YINDEX` 0 条不符、`XREL/YREL` 0 条不符（74/74） |
| 分类结果 | Pass 30145 / Fail 73（= 30218 − 73） |
| 独立解码比对 | 与 Python 手写解析的字段值逐项相同 |
| 自检 | KLARF 相关 **20 项**全通过（覆盖多列、多记录块、孤儿缺陷、类表空洞、摘要不符、尺寸矛盾、误识别） |

## 十、一句话总结

KLARF 是**缺陷级**格式：**几何**（DiePitch/DieOrigin/三层坐标）决定缺陷落哪颗 die，
**类表**决定缺陷叫什么，**die 图**只圈出晶圆形状。它**不携带 die 的 pass/fail**——
接入时必须自己做聚合，并把"这是推导值"写进告警，否则会把 AOI 的缺陷抽查结果
误当成最终良率。相关理论背景见 [11 KLARF 格式详解](./11-KLARF格式详解.md)，
格式对比见 [07 Wafer Map 常见文件格式](./07-Wafer-Map常见文件格式.md)。
