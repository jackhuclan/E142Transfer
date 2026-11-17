# TSK 探针台 DAT 格式详解（含 Q4FR02 样本接入）

> 样本：`E:\wafer-ws\Map图谱\TSK\Q4FR02-02B4.DAT`（69,158 字节）、`Q4FR02-03A7.DAT`
> 结论：TSK（Tokyo Seimitsu）探针台的**逐 die 二进制记录**，与同批次 `*.MAP` / `*.XTR` 三件套配套。
> 本工程的实现见 `src/E142Transfer/Import/Readers/TskDatReader.cs` 与 `XtrBinTable.cs`。

## 1. 文件族中的角色

一个批次（lot）目录里常见这几类文件，职责互不重叠：

| 文件 | 形态 | 承载什么 |
|---|---|---|
| `LOT.ID` / `PRODUCT.ID` | 纯文本 | 批次号、产品号 |
| `XREF.WAF` | 纯文本 | 槽位 ↔ 晶圆 ID 对照 |
| `<wafer>_<时间戳>.MAP` | 二进制 | die 图（bin 阵列，按行段稀疏存储） |
| `<wafer>_<时间戳>.XTR` | 纯文本 | 几何参数 + **bin 表（含 GOOD/BAD/None）** + 时间戳 |
| `<wafer>.DAT` | 二进制 | 逐 die 定长记录（本文对象） |

两个关键事实（用 `cmp` 逐字节验证过）：

1. `TSK\Q4FR02-02B4.DAT` 与 `9-EG_Amkor\Q4FR02-02B4.DAT` **完全相同**——`TSK` 目录里的 `.DAT` 就是同一批探针台输出，只是当时只拷了 DAT。
2. **DAT 自身不含 bin 语义**。它只有 bin 数字（0~255），哪个数字是良品只写在配对的 `.XTR` 里。所以 DAT 必须与 XTR 联合解读。

DAT 头部记录的计数与 **复测**（2024-10-09）一致，而不是首测：它记的是该晶圆最近一轮测试的逐 die 结果。

## 2. 整体布局

```
0x0000 ┌ 头部 230 字节（签名 / 产品 / die 尺寸 / flat / 行列 / ID / 时间戳 / 计数）
0x00E6 ├ 特殊记录 1 条（X=0x0040, Y=0x0100, bin=0）——语义未定，跳过
0x00EC ├ 网格：Rows × Columns 条 × 6 字节，行优先，行内按实际列号升序
0xA20E ├ 9 条全零记录（分隔）
0xA244 ├ 第二统计头：u16 5307 / 4874 / 433 / 433（各带 2 字节填充）
0xA320 └ 稀疏次级表 ≈27 KB（结构未解码）+ 尾部零填充
```

样本固定 69,158 字节；网格区大小恒为 `0xEC + Rows×Columns×6 = 0xA20E`（125×55 时）。

## 3. 头部字段（大端）

| 偏移 | 类型 | 样本值 | 含义 |
|---|---|---|---|
| `0x00` | char[4] | `"OP  "` | 记录类型/站点码，与 XTR 的 `OPID=OP` 一致 |
| `0x14` | char[16] | `AS3616-THE64A-03` | 产品号（不含 `-CP1/-CP2` 段位后缀） |
| `0x28` | u32 | 360000 | die 尺寸 X，单位 **0.01 µm** → 3600 µm |
| `0x2C` | u32 | 158000 | die 尺寸 Y，单位 **0.01 µm** → 1580 µm |
| `0x30` | u16 | 180 | flat/notch 角度 |
| `0x34` | u16 | 55 | Columns |
| `0x36` | u16 | 125 | Rows |
| `0x3C` | char[11] | `Q4FR02-02B4` | 晶圆 ID |
| `0x52` | char[8] | `Q4FR02.1` | 批次 ID |
| `0x94/0xA0/0xAC/0xB8` | char[10] ×4 | `2410092316` 等 | 4 个时间戳（`YYMMDDHHMM`，起测/结束/装卸等，未逐一定名） |
| `0xD2` | u16 | 5307 | 总 die 数（= Good + Bad） |
| `0xD4` | u16 | 4874 | 良品数 |
| `0xD6` | u16 | 433 | 不良数，**口径为 BAD + None 类**（51 + 382） |

> 单位陷阱：`360000 / 100 = 3600 µm`，与 XTR 的 `dieSizeCX=3600` 吻合。若按 0.1 µm 换算会得到 36 mm，比整颗晶圆还大。

## 4. die 记录（6 字节，格式核心）

```
字节0  标志位    0x10=空位 0x80=FAIL 0x40(或0x04)=PASS 0x00=None 类
字节1  v         |col_raw + 51|      到参考 die 的列距幅值
字节2  几何码    bit3=列侧别（1=参考列右侧）、bit2/bit1/bit6 随几何变化
字节3  w         |row_raw + 79|      到参考 die 的行距幅值
字节4  aux       0~63，随位置递增，语义未定
字节5  bin       0~255，复测口径（0 = GOOD）
```

坐标是**相对参考 die 的距离**，不是绝对行列：

- 参考 die 的原始坐标为 `(-51, -79)`，与同批 `.MAP` 头里的 `refdie` 完全呼应。
- **列**：`v = |col_raw + 51|`，侧别位决定符号 →
  1-based 列 = `v + (Columns − 51)`（右侧）或 `(Columns − 51) − v`（左侧），55 列时即 `v+4` / `4−v`。
- **行**：直接由记录序号得到：`row = index / Columns`（0-based，0 = 文件首行）。
  `w` 恒等于 `|79 − row|`，实测 6875/6875 条全部成立——这是最强的格式自检点。
- 行号**不需要**符号位（早期曾以为 `w` 带行符号位，实际 `w` 是幅值，行序完全由记录顺序决定）。

标志位必须**按位判断**：样本里出现过 `0x88`（0x80 | 0x08）这类"FAIL + 附加位"的组合，整字节比对会把它们漏掉或误判。

## 5. 与 `.MAP` 的对应关系

同一颗晶圆的两份文件可以逐 die 互验：

| DAT | MAP | 说明 |
|---|---|---|
| `row = index / Columns` | `<69 06>` 记录的 `B` 取负（`B = -row`） | 行序一致，均自上而下 |
| 1-based 列（v + 侧别位） | `<a5>` 行段的 `A` 起点 + 蛇形展开 | 奇数行 MAP 从 A 向左填，DAT 直接给绝对列 |
| `bin`（字节5） | `<a5>` 的 bin 字节 | 同一测试轮次下逐颗相同 |
| 头部 `0xD2/0xD4/0xD6` | XTR 的 `TouchDown/GoodCount/BadCount` | 仅复测对得上 |

验证结果（Python 独立脚本）：**02B4 与 03A7 两颗晶圆对各自复测 MAP 均 5307/5307 颗 die 全中、bin 100% 一致、0 坐标冲突**。
bin 直方图 `{0:4874, 3:13, 4:25, 5:1, 7:12, 19:10, 20:75, 21:295, 50:2}` 与复测 MAP 逐项相等。

## 6. bin 语义：必须与 XTR 配对

XTR 的 bin 表每行形如 `NAME ,RGB ,binCode ,QUALITY`，`QUALITY ∈ {GOOD, BAD, None}`：

```
GOOD            ,33020,0                                           ,GOOD
FC_FAIL         ,3158204,3                                          ,BAD
EDGE_DIE        ,3952820,21                                         ,None
PROTECT         ,11837540,20                                        ,None
```

对接时必须处理两件事：

1. **同一 bin 码在不同轮次含义不同**：首测 `TEST_ZONE(30)=GOOD`、复测把 `TEST_ZONE` 降为 `None` 并改用 `GOOD(0)`。
   所以不能按文件名"取最新"或"取最旧"来选 XTR。
2. **选表靠对账**：本工程用 DAT 的 bin 直方图反算 `GOOD` 数量与"非 GOOD"数量，与头部 `0xD4/0xD6` 比对，
   只有两项全中才算配对成功；得分相同的（首测/复测的表对实际出现的 bin 判定往往一致）
   再用 `.XTR` 文件名里的 `YYYYMMDDHHMMSS` 与 DAT 时间戳的接近度裁决。

## 7. 异常值、缺失字段与边界情况

1. **`bin = 0` 双义**：既是 GOOD（4874 颗），也是空位填充（1568 格）。**必须靠标志位 `0x10` 区分**，不能只看 bin。
   本工程对空位不赋 bin 码，否则投影时会按 bin 0 的定义被误标成 Pass。
2. **DAT 没有 NullBinCode 概念**：与 SINF/G85 不同，它把 `Rows×Columns` **整矩形逐格存储**，
   晶圆外形由"空位标志"圈出，而不是稀疏行段。
3. **头部不良数口径**：`0xD6 = 433 = 51(BAD) + 382(None)`。当 FAIL 数用会把 EdgeDie/Protect 算成失效。
4. **标志位是位掩码**：出现过 `0x88`。`0x44`（PASS + 0x04）也是位组合，不是常量码。
5. **次级表未解码**：`0xA320` 起约 27 KB 的稀疏表（孤立 `(0x0015,0x0000)`、`(0x0014,0x0000)` 片段），
   疑似逐 touchdown 顺序表，不影响 die 网格。
6. **三处语义未定**：`0xE6` 特殊记录（X=0x40/Y=0x100）、`aux` 字节（0~63）、几何码低 2 位。
   其中 `aux` 与几何码低位在 02B4/03A7 两颗晶圆间**逐格相同**（只随 (行,列) 变化），
   可判定它们**不携带 die 判定信息**，可以安全忽略。
7. **DAT 时间戳 ≠ 测试时刻**：头部时间戳（10-09 23:1x）比复测 XTR 的 `WaferEndTime`（15:19）晚约 8 小时，
   疑为转存时间。做时间追溯应以 XTR 为准。
8. **行 0 方位**：DAT 结构化地自证"首行 = 记录第一行"，但"首行是 notch 端还是对面"仍需 prober 文档或
   已知方位的晶圆确认（与之前 G85/MAP 的待确认项相同）。`FlatAngle=180` 只能当线索。
9. **`ProbeCardID=ERROR`**：配套 XTR 里的字面量错误值，属数据质量问题。
10. **扩展名不可信**：`.DAT` 在别的机台上可能是完全不同的格式，识别必须靠结构特征
    （`OP` 签名 + 网格尺寸合法 + 总数自洽 + `w=|79−row|` 四重），不能只看后缀。

## 8. 本工程接入

`TskDatReader`（`WaferMapFormat.TskDat`）把 DAT 收敛到 `NeutralWaferMap`：

| DAT | NeutralWaferMap |
|---|---|
| 头部产品/晶圆/批次 | `DeviceId` / `SubstrateId` / `LotId` |
| die 尺寸（0.01 µm） | `DieSizeX/Y`（µm），`Layout.StepSizeX/Y`（DAT 无间距字段，暂等于尺寸） |
| `Rows/Columns` | `Layout.Columns/Rows`，`OriginLocation=UpperLeft`、`AxisDirection=DownRight` |
| `FlatAngle=180` | `Orientation=Degree180` |
| 参考 die `(-51,-79)` | `ReferenceDie = (3, 79)`（0-based） |
| 标志位 `0x10/0x80/0x40/0x00` | `Null` / `Fail` / `Pass` / `Marked` |
| 配对 XTR bin 表 | `BinDefinitions` 的 `Code/Description/Class`（分类仍以状态位为准） |
| 头部计数 | `Metadata`：`TotalDieCount/PassDieCount/FailDieCount` + `DAT_*` 告警交叉校验 |

**识别打分**（`DetectConfidence`，阈值 0.5）：
`OP` 签名 0.50 + 计数自洽 0.15 + 产品字段可读 0.05 + `w=|79−row|` 结构校验 0.20 + `.dat` 扩展名 0.10。
真实样本 1.00；内容不是本格式的 `.dat` 文件得分 0，会明确抛 `UnsupportedWaferMapFormatException`。

**告警码**：`DAT_NO_XTR` / `DAT_XTR_PAIRED` / `DAT_BIN_CLASS_CONFLICT` / `DAT_TOTAL_MISMATCH` /
`DAT_PASS_MISMATCH` / `DAT_FAIL_COUNT_SEMANTICS` / `DAT_ROW_ENCODING` / `DAT_COL_OUT_OF_RANGE` /
`DAT_CELL_COLLISION` / `DAT_GRID_TRUNCATED` / `DAT_TRAILING_SECTION` / `DAT_ROW_DIRECTION` /
`DAT_UNDECODED_BYTES` / `DAT_DIE_SIZE_UNITS` / `DAT_COLUMN_OFFSET`。

## 9. 对接时最容易踩的三个坑

1. **拿 bin 数字当语义**：bin 0 不等于"良品"，空位也是 0。先看标志位。
2. **忽略列侧别位**：参考列左侧的 4 列会整体镜像，图看起来仍然"像晶圆"，但左右反了。
3. **拿首测 XTR 解释复测 DAT**：良品码从 30 变成 0，直方图对不上，良率会凭空掉一大截。
