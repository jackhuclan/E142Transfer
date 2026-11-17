#!/usr/bin/env python3
"""SEMI G85-1101 XML 晶圆图轻量预检（仅标准库，无需 .NET）。

用法: python validate_g85.py <file.xml> [more.xml ...]

对每个文件输出: 格式识别、Device 字段、Bin 定义、阵列实际计数与 BinCount 交叉校验、
Null/Marked 统计、参考 die。发现不一致时退出码为 1。
"""
from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def children(node, name: str):
    return [c for c in node if local(c.tag) == name]


def find(node, name: str):
    for c in node:
        if local(c.tag) == name:
            return c
    return None


def validate(path: str) -> bool:
    print(f"\n========== {path} ==========")
    try:
        tree = ET.parse(path)
    except ET.ParseError as exc:
        print(f"  [FAIL] XML 解析失败: {exc}")
        return False
    root = tree.getroot()
    maps = find(root, "Map") if local(root.tag) == "Map" else find(root, "Map")
    if maps is None:
        print("  [FAIL] 缺少 <Map> 节点")
        return False
    revision = maps.get("FormatRevision", "")
    if "G85" not in revision.upper():
        print(f"  [FAIL] FormatRevision='{revision}' 不是 G85")
        return False
    print(f"  格式: SEMI G85 ({revision})  WaferId={maps.get('WaferId')}")

    dev = find(maps, "Device")
    if dev is None:
        print("  [FAIL] 缺少 <Device> 节点")
        return False
    rows = int(dev.get("Rows", "0"))
    cols = int(dev.get("Columns", "0"))
    null_bin = dev.get("NullBin", "")
    print(f"  网格: Rows={rows} Columns={cols} BinType={dev.get('BinType')} "
          f"NullBin={null_bin} OriginLocation={dev.get('OriginLocation')} "
          f"Orientation={dev.get('Orientation')}")

    defined: dict[str, tuple[str, int | None]] = {}
    for b in children(dev, "Bin"):
        code = b.get("BinCode", "")
        cnt = b.get("BinCount")
        defined[code] = (b.get("BinQuality", ""), int(cnt) if cnt and cnt.isdigit() else None)
        print(f"  Bin {code}: count={cnt} quality={b.get('BinQuality')} desc={b.get('BinDescription')}")

    sup = find(dev, "SupplierData")
    if sup is not None:
        ref = (sup.get("REFP1.X"), sup.get("REFP1.Y"))
        print(f"  SupplierData: SLOT={sup.get('SLOT')} DUTMS={sup.get('DUTMS')} "
              f"REFP1=({ref[0]},{ref[1]}) ORGFNLOC={sup.get('ORGFNLOC')}")

    data = find(dev, "Data")
    if data is None:
        print("  [FAIL] 缺少 <Data> 节点")
        return False
    row_nodes = children(data, "Row")
    actual: Counter[str] = Counter()
    null_count = 0
    row_bad = 0
    for r in row_nodes:
        toks = (r.text or "").split()
        if cols and len(toks) != cols:
            row_bad += 1
        for t in toks:
            if t == null_bin:
                null_count += 1
            else:
                actual[t] += 1
    total = sum(len((r.text or "").split()) for r in row_nodes)
    print(f"  数据: Row数={len(row_nodes)} 总格数={total} (期望 {rows * cols}) "
          f"空位={null_count} 行宽不符={row_bad}")

    ok = True
    if len(row_nodes) != rows:
        print(f"  [FAIL] Row 数 {len(row_nodes)} != Rows {rows}")
        ok = False
    if row_bad:
        print(f"  [FAIL] {row_bad} 行 token 数 != Columns {cols}")
        ok = False
    if total != rows * cols:
        print(f"  [WARN] 总格数 {total} != Rows×Columns {rows * cols}")

    for code, (quality, declared) in defined.items():
        got = actual.get(code, 0)
        mark = "OK" if declared == got else "MISMATCH"
        if declared != got:
            ok = False
        print(f"  校验 Bin {code}: 声明 {declared} 实际 {got} [{mark}] quality={quality}")
        if quality.upper() == "NONE":
            print(f"    提示: {code} 为 Marked Bin（None），必须按有效未判定 die 处理，不可当 Null")

    for code in sorted(set(actual) - set(defined)):
        print(f"  [WARN] 数据出现未定义 bin {code}（{actual[code]} 次）")
        ok = False
    for code in set(defined) - set(actual):
        print(f"  [WARN] Bin {code} 已声明但未在数据中出现")
    print(f"  结果: {'通过' if ok else '有问题需处理'}")
    return ok


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    results = [validate(p) for p in sys.argv[1:]]
    return 0 if all(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
