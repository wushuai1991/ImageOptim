#!/usr/bin/env python3
"""校验双端工具链契约 shared/toolchain.json 与两端实现是否一致。

做的事情：
1. 结构校验：类型/字段/条件词汇是否合法；
2. 条件里的偏好项名必须真实存在于 Windows 的 Preferences（契约以 Windows 为基准）；
3. 契约里的每个工具 id，必须在 Windows 的 Toolchain.Create() 里登记；
4. 契约里的每个工具 id，必须在 macOS 有一一对应的 Worker 类（已知缺口会以 WARN 报出）；
5. 报告实现里多出来的工具（说明契约漏登记）。

用法：python3 scripts/check-toolchain.py [仓库根目录]
退出码：有 ERROR 时非 0。
"""

import json
import re
import sys
from pathlib import Path

VALID_TYPES = {"png", "jpeg", "gif", "svg", "bmp", "webp"}
VALID_FLAGS = {"lossyEnabled", "noLossyYet"}
NUMERIC_OPS = {"gt", "lt", "gte", "lte"}

# 契约里的工具 id -> macOS 侧对应的 Worker 类（None 表示 macOS 尚未实现）
MACOS_WORKER = {
    "pngquant": "PngquantWorker",
    "pngcrush": "PngCrushWorker",
    "oxipng": "OxiPngWorker",
    "pngout": "PngoutWorker",
    "advpng": "AdvCompWorker",
    "zopfli": "ZopfliWorker",
    "jpegli": None,  # macOS 没有 jpegli
    "jpegtran": "JpegtranWorker",
    "jpegoptim": "JpegoptimWorker",
    "guetzli": "GuetzliWorker",
    "gifsicle": "GifsicleWorker",
    "gifsicle_interlaced": "GifsicleWorker",
    "gifsicle_lossy": "GifsicleWorker",
    "svgo": "SvgoWorker",
    "svgcleaner": "SvgcleanerWorker",
    "jpegconvert": "JpegConvertWorker",
}

errors: list[str] = []
warnings: list[str] = []


def err(msg: str) -> None:
    errors.append(msg)


def warn(msg: str) -> None:
    warnings.append(msg)


def load_pref_names(prefs_cs: Path) -> tuple[set[str], set[str]]:
    """从 Preferences.cs 提取属性名与 JpegToolMode 枚举值。"""
    if not prefs_cs.exists():
        err(f"找不到 {prefs_cs}")
        return set(), set()
    text = prefs_cs.read_text(encoding="utf-8")
    props = set(re.findall(r"public\s+(?:bool|int|JpegToolMode)\s+(\w+)\s*\{", text))
    enum_body = re.search(r"enum\s+JpegToolMode\s*\{(.*?)\}", text, re.S)
    modes = set(re.findall(r"(\w+)\s*=\s*\d+", enum_body.group(1))) if enum_body else set()
    return props, modes


def load_csharp_factory_ids(toolchain_cs: Path) -> set[str]:
    """从 Toolchain.cs 的 switch 表达式里提取已登记的工具 id。"""
    if not toolchain_cs.exists():
        err(f"找不到 {toolchain_cs}")
        return set()
    text = toolchain_cs.read_text(encoding="utf-8")
    body = re.search(r"return id switch\s*\{(.*?)\n\s*\};", text, re.S)
    if not body:
        err("Toolchain.cs 里没找到 `return id switch { ... };`，无法校验工厂登记")
        return set()
    return set(re.findall(r'"([a-z_]+)"\s*=>', body.group(1)))


def load_macos_workers(workers_dir: Path) -> set[str]:
    if not workers_dir.is_dir():
        err(f"找不到目录 {workers_dir}")
        return set()
    return {p.stem for p in workers_dir.glob("*Worker.m")}


def check_condition(cond: dict, where: str, props: set[str], modes: set[str]) -> None:
    kinds = [k for k in ("pref", "flag", "mode") if k in cond]
    if len(kinds) != 1:
        err(f"{where}: 条件必须且只能有 pref/flag/mode 之一，实际 {sorted(cond)}")
        return

    if kinds[0] == "flag":
        if cond["flag"] not in VALID_FLAGS:
            err(f"{where}: 未知 flag '{cond['flag']}'，允许 {sorted(VALID_FLAGS)}")
        return

    if kinds[0] == "mode":
        if cond["mode"] not in modes:
            err(f"{where}: 未知 JpegToolMode '{cond['mode']}'，允许 {sorted(modes)}")
        return

    name = cond["pref"]
    if name not in props:
        err(f"{where}: 偏好项 '{name}' 在 Preferences.cs 里不存在")
    for op in cond:
        if op in ("pref", "value"):
            continue
        if op not in NUMERIC_OPS:
            err(f"{where}: 未知比较符 '{op}'")
        elif not isinstance(cond[op], (int, float)):
            err(f"{where}: '{op}' 的值必须是数字")


def main() -> int:
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()
    spec_path = root / "shared" / "toolchain.json"

    if not spec_path.exists():
        print(f"ERROR 契约文件不存在: {spec_path}")
        return 1

    try:
        spec = json.loads(spec_path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        print(f"ERROR 契约文件 JSON 解析失败: {e}")
        return 1

    props, modes = load_pref_names(root / "win/ImageOptim/Preferences.cs")
    factory_ids = load_csharp_factory_ids(root / "win/ImageOptim/Toolchain.cs")
    mac_workers = load_macos_workers(root / "imageoptim/Backend/Workers")

    types = spec.get("types") or {}
    if not types:
        err("types 为空")

    spec_ids: set[str] = set()
    for tname, tools in types.items():
        if tname not in VALID_TYPES:
            err(f"未知文件类型 '{tname}'，允许 {sorted(VALID_TYPES)}")
        if not isinstance(tools, list) or not tools:
            err(f"类型 '{tname}' 的工具列表为空")
            continue

        for idx, tool in enumerate(tools):
            where = f"{tname}[{idx}]"
            tid = tool.get("id")
            if not tid:
                err(f"{where}: 缺少 id")
                continue
            where = f"{tname}[{idx}] {tid}"
            spec_ids.add(tid)

            if not tool.get("name"):
                err(f"{where}: 缺少 name")

            when = tool.get("when") or []
            unless = tool.get("unless") or []
            if not when and not unless:
                warn(f"{where}: when/unless 都为空，该工具会无条件加入")

            for c in when + unless:
                check_condition(c, where, props, modes)

            for flag in tool.get("setFlags") or []:
                if flag != "lossyConverted":
                    err(f"{where}: 未知 setFlags '{flag}'（目前只支持 lossyConverted）")

            # Windows 工厂登记
            if tid not in factory_ids:
                err(f"{where}: Windows Toolchain.Create() 里没有登记该 id")

            # macOS 对应实现
            if tid not in MACOS_WORKER:
                warn(f"{where}: 未在脚本的 MACOS_WORKER 映射里登记，跳过 macOS 覆盖检查")
            else:
                cls = MACOS_WORKER[tid]
                if cls is None:
                    warn(f"{where}: macOS 侧尚未实现该工具（属已知平台差异）")
                elif f"{cls}.m" not in {p.name for p in (root / 'imageoptim/Backend/Workers').glob('*.m')}:
                    err(f"{where}: macOS 缺少 {cls}.m")

    # 实现里多出来的 id（契约漏登记）
    for tid in sorted(factory_ids - spec_ids):
        warn(f"Windows Toolchain.Create() 实现了 '{tid}' 但契约里没有，考虑补登记或删除")

    if not spec.get("platformDeviations"):
        warn("契约缺少 platformDeviations，两端的已知差异会重新变成隐性漂移")

    for w in warnings:
        print(f"WARN  {w}")
    for e in errors:
        print(f"ERROR {e}")

    print(
        f"\n契约工具 id: {len(spec_ids)} 个 | Windows 已实现: {len(factory_ids)} 个 | "
        f"macOS Worker 类: {len(mac_workers)} 个 | 错误 {len(errors)} | 警告 {len(warnings)}"
    )
    if errors:
        print("检查未通过")
        return 1
    print("检查通过")
    return 0


if __name__ == "__main__":
    sys.exit(main())
