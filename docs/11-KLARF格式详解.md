# 11 KLARF 格式详解

[返回索引](./README.md)

## 结论先行

**KLARF** 是 KLA Tencor 的缺陷报告格式。它**不是 wafer map**——虽然包含 map 的元素，但它还记录了**缺陷在每颗 die 内部的位置**，以及指向缺陷位图文件的指针。Artwork 称它为 "a bit of a hybrid database"（某种混合数据库）。

这是理解 KLARF 的钥匙：

- E142 / SINF / CSV 回答「**这颗 die 是 pass 还是 fail**」；
- KLARF 回答「**这颗 die 上哪个位置有什么缺陷、多大、长什么样**」。

数据粒度不同，决定了它不能直接塞进结果级的中性模型。与 SINF 的对比见 [10 SINF 格式详解](./10-SINF格式详解.md)。

## 五段结构

KLARF 文件分五个主要节段，语句以分号 `;` 结尾：

| 节段 | 作用 |
|---|---|
| **1. HEADER** | 衬底通用信息 + 决定 die 落在哪里的几何参数 |
| **2. CLASS LOOKUP** | 缺陷类别索引 → 文字描述的对照表 |
| **3. INSPECTION TEST** | 一次或多次检测的定义 |
| **4. SAMPLE TEST PLAN** | 被检测 die 的逻辑坐标清单 |
| **5. DEFECT RECORD** | 逐条缺陷记录 + 位图指针 |

## 1. HEADER

常见字段：

| 字段 | 含义 |
|---|---|
| `FileVersion` | 文件格式版本，如 `8.5`、`9.0` |
| `RecordType` | 记录类型 |
| `SampleType` | 样本类型，`Wafer` / `Reticle` 等 |
| `DeviceID` | 产品/器件名 |
| `LotID` | 批次号 |
| `WaferID` | 晶圆 ID |
| `SampleSize` | 文件中的 wafer 数量 |
| `SlotMap` | 晶圆盒槽位映射 |
| `ResultTimestamp` | 结果时间戳 |
| `OrientationMarkLocation` | notch/flat 方位，如 `DOWN` |
| `DiePitch` | die 阵列间距（X, Y） |
| `DieOrigin` | die (0,0) **左下角**的坐标 |
| `SampleCenterLocation` | 晶圆物理圆心在机台坐标系中的位置 |

### 决定坐标落点的四个几何量

Artwork 强调，真正决定「缺陷落在哪颗 die」的是这四个值：

```
OrientationMarkLocation DOWN;
DiePitch 3.5717153320e+003 3.3786257324e+003;
DieOrigin 0.000000 0.000000;
SampleCenterLocation 1.4683145313e+005 1.3981967188e+005;
```

有了它们，给定 die 阵列索引就能反推出物理位置。注意 `SampleCenterLocation` 在某些公开资料里被描述为「0,0 die 的原点」，但 Artwork 定义的是**晶圆圆心在机台坐标系中的偏移**——以机台/厂商的实际定义为准，接入时必须用已知缺陷做一次对齐验证。

## 2. CLASS LOOKUP

类别索引 → 缺陷描述的对照表，DEFECT 段按索引引用：

```
0 "Alignment Failure"
5 "Ink Dot Inspection Defect"
6 "Frontside Defect"
7 "Kerf Defect"
12 "Crack Die"
17 "Top Side Chipping"
22 "Stain/FM On Pad"
24 "Corner Die Chipping"
```

### 两个转换硬约束

**1. 类别数可能超过 256。** Artwork 明确说遇到过 `CLASS LOOKUP` 超过 256 条的 KLARF 文件，而多数 map 格式最多 256 个 bin。转 map 时必须做类别合并映射。

**2. KLARF 没有 Null 码，也没有 Pass 码。** 转成 map 时至少要预留 1 个 bin 给 Null、1 个给 Pass，所以实际可用类别数比 256 还少 2。这与 [03 BinCode 码值到语义的映射](./03-BinCode-码值到语义的映射.md) 里的最少三类码要求正好对应。

## 3. INSPECTION TEST

一份 KLARF 可能包含单次或多次检测：

```
InspectionTest 1;
```

Artwork 的建议是**每个 inspection test 单独生成一张 map**，不要混在一起——否则同一颗 die 上不同测试的结果会互相覆盖。

## 4. SAMPLE TEST PLAN

被检测 die 的逻辑坐标清单（注意是**逻辑坐标**，不是物理坐标）：

```
SampleTestPlan 5411 29 0 30 0 31 0 32 0 45 2 46 2 ...
```

关键语义：**清单里的 die 若没有出现在 DEFECT 段，就视为 PASS**。这是 KLARF 表达良品的唯一方式——它不像 map 那样给每颗 die 显式写 bin 码。

## 5. DEFECT RECORD

### DefectRecordSpec 是自描述的

列结构不固定，**第一行声明列名和列数**，后面的记录按这个顺序写值。这是解析 KLARF 最关键的一点：**必须按列名动态映射，不能按固定下标读**。

```
DefectRecordSpec 17 DEFECTID XREL YREL XINDEX YINDEX XSIZE YSIZE DEFECTAREA
                   DSIZE CLASSNUMBER TEST CLUSTERNUMBER ROUGHBINNUMBER
                   FINEBINNUMBER REVIEWSAMPLE IMAGECOUNT IMAGELIST;
```

| 列 | 含义 |
|---|---|
| `DEFECTID` | 缺陷唯一编号 |
| `XREL` / `YREL` | **从 die 原点到缺陷中心的距离**（die 内部相对坐标） |
| `XINDEX` / `YINDEX` | die 在阵列中的位置 |
| `XSIZE` / `YSIZE` | 缺陷尺寸 |
| `DEFECTAREA` | 缺陷区域面积 |
| `DSIZE` | 缺陷大小 |
| `CLASSNUMBER` | 指向 CLASS LOOKUP 的索引 |
| `TEST` | 属于哪次 inspection test |
| `CLUSTERNUMBER` | 群聚编号（转 map 用不到） |
| `ROUGHBINNUMBER` | 粗分 bin（少见定义） |
| `FINEBINNUMBER` | 细分 bin（少见定义） |
| `REVIEWSAMPLE` | 是否属于复检计划 |
| `IMAGECOUNT` | 关联位图数量 |
| `IMAGELIST` | 关联位图列表 |

### DefectList 样例逐段解读

```
DefectList 327 3.4916440430e+003 3.2954443359e+003 57 12 16.998291 16.990662
           288.812211 1.6990661621e+001 17 1 0 0 0 0 1 1 1 0 ;
TiffFileName TR7U5303.05_Ceres_256993787_1.jpg;
```

| 值 | 对应列 | 解读 |
|---|---|---|
| `327` | DEFECTID | 缺陷编号 |
| `3.4916e+003`, `3.2954e+003` | XREL, YREL | die 内部相对坐标 |
| `57`, `12` | XINDEX, YINDEX | 位于阵列第 57 列、12 行 |
| `16.998`, `16.991` | XSIZE, YSIZE | 缺陷宽高 |
| `288.812` | DEFECTAREA | 面积 |
| `1.699e+001` | DSIZE | 缺陷大小 |
| `17` | CLASSNUMBER | 对应 "Top Side Chipping" |
| `1` | TEST | 属于 inspection test 1 |
| `1` | IMAGECOUNT | 有 1 张位图 |
| `1 0` | IMAGELIST | 引用第 1 张图 |
| `TR7U5303.05_Ceres_...jpg` | TiffFileName | 位图文件名 |

### 两处容易误读的地方

**`XREL/YREL` 是相对 die 原点的偏移，不是相对晶圆中心。** Artwork 的定义是 "distance from die origin to center of the defect"。部分中文资料把它写成「相对 Wafer 中心的坐标」，按那个理解会让缺陷整体偏移一个 die 网格。**`XINDEX/YINDEX` 才是 die 阵列位置**，两者不能混用。

**同一颗 die 可以有多条缺陷记录**，可能是同类也可能不同类。而 map 每颗 die 只能有一个 bin 码——Artwork 的转换策略是**取第一条缺陷，忽略同一 die 上的后续记录**。这个信息损失必须知道。

## 转到 map 时的语义损失

| KLARF 信息 | 转 map 后 |
|---|---|
| 缺陷位图（IMAGELIST / TiffFileName） | **丢失**，标准 map 无图像字段 |
| 缺陷在 die 内的相对位置 | **丢失**（E142 勉强可表达），只能给 die 一个 bin |
| 同一 die 上的多条缺陷 | **只保留第一条** |
| 超过 256 个类别 | 需合并映射 |
| 无 Null / Pass 码 | 需人为补齐 |

结论：**KLARF → 结果级 map 是有损转换**。如果下游要保留缺陷级信息，就不要转成 map，而应直接把缺陷集合存进数据库或中性模型的扩展字段。

## 坐标体系三大坑

1. **单位**：不同机台导出的坐标可能是微米、纳米甚至像素。先读 `Unit` 字段，或从 `DieSize` 的数量级反推。搞错会让所有缺陷偏 10 倍、全部落到晶圆外面。
2. **参考系**：`XREL/YREL` 相对 die 原点，`XINDEX/YINDEX` 是阵列索引，`SampleCenterLocation` 是晶圆圆心在机台坐标系的偏移——三个参考系别混。
3. **方向**：机台用自己的扫描坐标系，工程师看 map 习惯 notch 朝下或朝右，两者常差一次旋转或镜像。投影到画布前必须用已知缺陷做一次校验。

## 落到本工程

KLARF **不能**用现有 `NeutralDie.BinCode` 表达。要接它，建议给中性模型加一层缺陷集合：

```
NeutralDie
 └─ Defects: List<NeutralDefect>
      ├─ RelX / RelY        // die 内偏移（XREL/YREL）
      ├─ ClassNumber        // 查 CLASS LOOKUP
      ├─ SizeX / SizeY
      └─ ImageRefs          // 位图路径
```

转成 E142 时再按 die 聚合成结果级 bin：

- 有缺陷 → Fail（bin 取第一条缺陷的 class）；
- `SampleTestPlan` 里列出但无缺陷 → Pass；
- 不在测试计划内 → Null；
- 超过 256 类时先合并映射，并预留 Null / Pass。

这正好对应 PL 检测上位机的三映射分工：**EL / PL 走 E142 / SINF 结果级链路，AOI 走 KLARF 缺陷级链路**，最后在 `NeutralWaferMap` 汇合。新增 Reader 的步骤见 [09 工程实现](./09-工程实现-E142Transfer适配器.md) 的「新增一种格式」。

## 资料来源

- Artwork Conversion Software — [KLARF 格式页](https://www.artwork.com/package/wmapconvert/map_formats/KLARF/index.htm)（五节段、几何四要素、DefectRecordSpec 全列、260+ 类别问题、转换语义损失与「取第一条缺陷」策略）；
- 美国专利 US6744266 — KLA 结果文件的记录结构与 DefectRecordSpec 字段表；
- 国内 Fab 缺陷分析实践资料（坐标单位与方向校验经验，XREL/YREL 定义以 Artwork 为准）。
