# 06 TransferMap 与 KGD 追溯链路

[返回索引](./README.md)

## 直觉：die 会不断"换载体"

先进封装里的 die 会经历多次换载体——从晶圆被切下来、分选到 film frame、再贴到 strip/tray、最终进到封装里堆叠或并排。**每换一次载体，坐标就被重排一次**。transfer map 就是把这一连串重排串成一条可反查的链。

## KGD：链条成立的前提

整条链能成立的前提，是每颗 die 在每一站都保持"已知良好"——也就是 **KGD（Known Good Die）**。

逻辑是：先进封装里一颗坏 die 会连带报废整个昂贵封装，所以必须在封装前把筛选做到极致，而每一层筛选的结果都必须绑定回 die 的原始位置。

典型筛选序列：

1. **晶圆探针测试**：每颗 die 做电性测试并分配 bin code，fail 的从此排除；
2. **进一步测试或老化**：burn-in 抓早期失效、全速测试等；
3. **切割**：晶圆切成单颗 die 上 film frame，切割可能引入崩边和裂纹；
4. **外观检查**：电性测不到的崩边、裂纹、划伤、污染、焊盘损伤；
5. **按 map 拾取**：die sorter 读 wafer map，只拾取通过全部筛选的 die。

关键结论：**电性测试看不到切割和搬运造成的机械损伤**，所以高可靠器件把外观检查也纳入 KGD 定义。

## transfer map 的数据机制

本质是一张**坐标对照表**：把目标载体（下游）上的每个位置，指向源载体（上游）上的位置。E142 里以 `TransferMap` 段承载，配合 substrate ID 工作。

核心结构（E142.1 XSD）：

```xml
<TransferMap FromSubstrateType="Wafer" FromSubstrateId="WAFER-X">
  <T FX="12" FY="8" TX="3" TY="1" FID="P" TID="U-0301" />
  ...
</TransferMap>
```

- `FX/FY` = 源 die 阵列坐标；`TX/TY` = 目标 die 阵列坐标；
- `FID/TID` = 可选的字符串 die ID；
- 目标载具由父 `SubstrateMap/@SubstrateId` 决定，源载具由 `TransferMap/@FromSubstrateId` 决定；
- 同一封装位可被多颗 die 指向（3D 堆叠），反查会返回多条源 die。

## 四大用途

这套机制不只是为追溯而追溯：

1. **提升封装与测试的良率**：把后道失效反馈给前道，定位是哪一站引入的问题；
2. **把 back-end 测试结果绑定回 fab 工艺**：哪一炉、哪一步的异常导致这批 die 后道失效；
3. **隔离 field failure**：产品到了客户手里出问题，反查回晶圆位置，看相邻 die 有没有同类问题，判断是设计、工艺还是偶发；
4. **满足高可靠行业的强制追溯**：车规、医疗这类器件，从晶圆 die 到终端产品里单颗 die 的完整追溯几乎是硬要求。

## ID + Map 的双要素

整套追溯建立在两个东西上：**ID**（身份证号）和 **Map**（地址表）。

| 要素 | 标准 | 说明 |
|---|---|---|
| Wafer ID | SEMI T2 | 二维码，晶圆出 fab 时带着，探针测试时与 wafer map 绑定 |
| Strip ID | SEMI T9 | 2D data matrix，22 个字符，激光刻在 strip 表面 |
| Map | E142 | 存在工厂数据库，设备读到 ID 后向 host 请求对应 map 下载（E142.2 的 S14F1） |

每一步设备处理完，生成新的 map 并上传——**strip map 就是这样和 wafer ID 建立链接的**，链条由此一环扣一环。

## E142 里的载体覆盖

E142 支持的载体类型很全：wafer、frame、strip、tray、tape & reel、panel、PCB——所以跨站点、跨厂、甚至跨 OSAT 的追溯都能用同一套 schema。

## 必须验证的环节：Map 与物理 die 对齐

这是整条链最脆弱的一环。map 是探针测试时写的（常常在另一栋楼），die 是切割贴膜后才被拾取的。如果 map 被旋转 90° 或偏移一个 die，**拾取机会拿走坏片、留下良品，而 map 文件本身不会有任何报错**。

防线是 **reference die**：每份 map 至少标注一个参考 die，所有坐标最终以 notch 为基准。上线前必须用参考 die 和几个已知失效位置做目视/视觉比对，确认对齐。

## 对 PL 检测上位机的借鉴

EL/PL/AOI 三映射如果只用于"这一片晶圆内部的分析"，那是单站 map；但一旦要支撑**失效回溯到具体工艺站**或**客户投诉反查**，就需要给每张光学 map 加一层 transfer data：把每个 die 的下游去向（哪个 strip、哪个封装位）记录下来。

建议的数据结构：die 记录里除三映射判定结果，增加 `TransferRef { SubstrateType, SubstrateId, TargetX, TargetY, StackLayer }`，多个下游去向存成列表。本工程已实现，见 [09 工程实现](./09-工程实现-E142Transfer适配器.md)。
