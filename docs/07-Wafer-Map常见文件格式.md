# 07 Wafer Map 常见文件格式

[返回索引](./README.md)

## 结论先行

**wafer map 从来没有真正统一过格式**。大厂各搞各的、SEMI 出了多个标准、探针台和检测设备厂商又各有私有格式。

按 Artwork 转换工具统计，它支持 **24 种**具名格式，另外还见过 5 种未支持的；实际工厂里流通的大致是 **20–30 种**。但日常高频使用的，收敛在 **7 种左右**。

## 四大家族

| 家族 | 代表格式 | 说明 |
|---|---|---|
| 标准与通用交换 | SEMI E142 (XML)、E5-1296、G85、CSV、XYBin | 标准组织定义或轻量通用交换 |
| 测试机与分选机 | STDF、TEL P-8/P-12、SECS-EG、SINF、STIF | 产线设备侧常见 |
| 检测与缺陷数据 | KLARF（KLA）、TFF（KLA/TOPCON）、Royce XML、IBIS | 记录缺陷级信息 |
| 厂内与设备私有 | ASE、Unisem、Viking、WWF (TI)、BEIJING CSV、MAP001、ATT (.A) | 特定厂或设备专有 |

## 高频的 7 种

| 格式 | 形态 | 典型来源 | 数据粒度 | 说明 |
|---|---|---|---|---|
| **SEMI E142** | XML | SEMI 标准（2005） | die 结果 + transfer map | 最新、最全面，支持 wafer/frame/strip/tray 多载体 |
| **KLARF** | 文本分节 | KLA 检测设备 | 缺陷级（含 die 内 XREL/YREL 坐标） | AOI 缺陷数据最接近这一类 |
| **SINF** | 纯文本 | 老牌通用格式 | die 结果 | 遗留系统覆盖面极广 |
| **TEL P-8 / P-12** | 厂商格式 | 东京电子探针台/分选机 | die 结果 | 产线设备侧常见 |
| **TFF** | 二进制 | KLA / TOPCON 检测系统 | 缺陷级 | 紧凑、需专用解析 |
| **XYBin / CSV** | 纯文本 | 通用数据交换 | 坐标 + bin | 最轻量，`x,y,bin` 三列，适合自研上位机 |
| **STDF** | 二进制 | ATE 测试数据记录 | 测试原始 datalog | 严格说是测试记录而非 map，但常作为 map 的数据源 |

## 其余常见名字

Artwork 列出的 24 种支持格式还包括：`1 DIGIT`、`ASY`、`ATT (.A)`、`BEIJING CSV`、`E5-1296`、`EM(MAP)`、`E-MAP`、`G85`、`IBIS`、`MAP001`、`Royce XML`、`SECS-EG`（二进制与文本两版）、`ASE`、`STIF`、`UF3000`、`Unisem`、`Viking`、`WWF (TI)`。

另外见过但未支持的有：Cascade Microtech、UMC、ALP Realtime Wafermap、TEL Text、Wentworth Map。

## 三个工程要点

### 1. 格式差异不只是语法，而是元数据完整度不同

Artwork 明确提到：某些格式缺少另一些格式里必填的参数（units、die pitch、wafer flat 朝向、reference die 等）。所以转换器不能做 1:1 直转——正确做法是先全部读进一个**中性内部 map 结构**，再从该结构写出目标格式。详见 [08 Artwork 工具与中性模型架构](./08-Artwork转换工具与中性模型架构.md)。

### 2. "结果级"和"缺陷级"是两条不同的数据线

- E142 / SINF / TEL / XYBin 回答"这颗 die 是 pass 还是 fail"；
- KLARF / TFF 回答"这颗 die 上哪个位置有什么缺陷"。

两者粒度不同，对应 EL/PL 三映射（逐 die 判定）和 AOI（缺陷位置）的区分。这也是本工程中性模型目前偏"结果级"、接 KLARF 时需扩展缺陷内部坐标字段的原因。

### 3. 实际支持范围由客户 fab 决定

没有"全支持"的解析器，但 **E142 + KLARF + SINF + CSV/XYBin + TEL** 这几种基本能覆盖绝大多数场景；其余私有格式通常是接到具体项目后再加适配器。

## 资料来源

- Artwork Conversion Software 的 Supported Formats 页面（24 种支持格式清单）；
- MAPx 的 Format Reference（7 种主流格式）；
- SEMI E142 标准说明。
