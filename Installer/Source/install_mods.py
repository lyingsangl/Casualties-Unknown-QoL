#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Casualties: Unknown 模组半自动安装器
------------------------------------
用途：把从 Nexus Mods 手动下载的 .zip 自动分类、解压、放到正确位置。

用法：
    python install_mods.py                 # 预演（只看会做什么，不动文件）
    python install_mods.py --apply         # 真正安装
    python install_mods.py --src "D:\\下载" --apply

规则（按压缩包内的目录结构判断）：
    含 BepInEx/...        -> 解到游戏根目录，覆盖 BepInEx 下的对应路径
    含 ModLoader/...      -> 解到游戏根目录
    根目录/plugins 下有 DLL -> 放到 BepInEx\\plugins\\<包名>\\
    只有 .cfg            -> 放到 BepInEx\\config\\
    其他（png/json/语言）  -> 不处理，打印出来让你手动放
覆盖已有文件前会先备份到 _backup_<时间戳>\\
"""
import argparse
import os
import shutil
import sys
import time
import zipfile

DEFAULT_GAME = r"D:\Steam\steamapps\common\Casualties Unknown"


def human(n):
    return f"{n/1024:.0f} KB" if n < 1024 * 1024 else f"{n/1048576:.1f} MB"


def classify(zf):
    """返回 (类型, 说明)"""
    names = [n.replace("\\", "/") for n in zf.namelist() if not n.endswith("/")]
    low = [n.lower() for n in names]

    def has_seg(seg):
        return any(n == seg or n.startswith(seg + "/") or ("/" + seg + "/") in n for n in low)

    root_dll = [n for n in names if n.lower().endswith(".dll") and "/" not in n.strip("/")]
    any_dll = [n for n in names if n.lower().endswith(".dll")]
    cfgs = [n for n in names if n.lower().endswith(".cfg")]

    if has_seg("bepinex"):
        return "root", "含 BepInEx 结构，整体解到游戏根目录"
    if has_seg("modloader"):
        return "root", "含 ModLoader 结构，整体解到游戏根目录"
    if root_dll:
        return "plugins", f"{len(root_dll)} 个 DLL 在根目录，放进 BepInEx/plugins"
    if any_dll:
        return "plugins", f"{len(any_dll)} 个 DLL（带子目录），放进 BepInEx/plugins"
    if cfgs and len(cfgs) == len(names):
        return "config", "只有配置文件，放进 BepInEx/config"
    return "manual", "没有 DLL，可能是贴图/服装/语言包，需要手动放"


def plan_entries(zf, kind, stem, game):
    """返回 [(zip内路径, 目标绝对路径)]"""
    out = []
    names = [n.replace("\\", "/") for n in zf.namelist() if not n.endswith("/")]
    if kind == "root":
        # 剥掉多套一层的顶层目录（例如 "MyMod/BepInEx/..." -> "BepInEx/..."）
        tops = {n.split("/")[0] for n in names}
        strip = ""
        if len(tops) == 1:
            top = tops.pop()
            if top.lower() not in ("bepinex", "modloader"):
                strip = top + "/"
        for n in names:
            rel = n[len(strip):] if strip and n.startswith(strip) else n
            out.append((n, os.path.join(game, *rel.split("/"))))
    elif kind == "plugins":
        dlls = [n for n in names if n.lower().endswith(".dll")]
        d0 = dlls[0] if dlls else ""
        if "/" in d0.strip("/"):
            # DLL 本身带目录（如 Tailor/Tailor.dll）→ 整体按原结构解到 plugins
            dest = os.path.join(game, "BepInEx", "plugins")
            for n in names:
                out.append((n, os.path.join(dest, *n.split("/"))))
            return out
        folder = os.path.splitext(os.path.basename(d0))[0] if d0 else stem
        folder = "".join(ch for ch in folder if ch not in '\\/:*?"<>|').strip() or stem
        dest = os.path.join(game, "BepInEx", "plugins", folder)
        tops = {n.split("/")[0] for n in names}
        strip = ""
        if len(tops) == 1:
            strip = tops.pop() + "/"
        for n in names:
            rel = n[len(strip):] if strip and n.startswith(strip) else n
            out.append((n, os.path.join(dest, *rel.split("/"))))
    elif kind == "config":
        dest = os.path.join(game, "BepInEx", "config")
        for n in names:
            out.append((n, os.path.join(dest, os.path.basename(n))))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "downloads"))
    ap.add_argument("--game", default=DEFAULT_GAME)
    ap.add_argument("--apply", action="store_true", help="真正写入文件（默认只预演）")
    ap.add_argument("--force", action="store_true", help="覆盖已存在的文件（默认跳过）")
    args = ap.parse_args()

    if not os.path.isdir(args.game):
        sys.exit(f"找不到游戏目录：{args.game}\n用 --game 指定。")
    if not os.path.isdir(args.src):
        os.makedirs(args.src, exist_ok=True)
        sys.exit(f"下载文件夹是空的，已创建：{args.src}\n把 .zip 放进去再跑一次。")

    zips = sorted(f for f in os.listdir(args.src) if f.lower().endswith(".zip"))
    others = sorted(f for f in os.listdir(args.src)
                    if not f.lower().endswith((".zip", ".txt", ".md")))
    if not zips:
        sys.exit(f"{args.src} 里没有 .zip。"
                 + (f"\n发现其他格式（需手动解压）：{', '.join(others[:8])}" if others else ""))

    stamp = time.strftime("%Y%m%d_%H%M%S")
    backup = os.path.join(args.game, f"_backup_{stamp}")
    log = [f"# 安装记录 {stamp}", f"游戏：{args.game}", f"下载：{args.src}", ""]

    print(f"游戏目录：{args.game}")
    print(f"下载目录：{args.src}")
    print(f"模式：{'安装' if args.apply else '预演（加 --apply 才真的装）'}\n")

    total_ok = total_manual = 0
    for z in zips:
        path = os.path.join(args.src, z)
        stem = os.path.splitext(z)[0]
        try:
            with zipfile.ZipFile(path) as zf:
                kind, why = classify(zf)
                entries = plan_entries(zf, kind, stem, args.game) if kind != "manual" else []
                size = sum(i.file_size for i in zf.infolist())
                print(f"[{kind:7}] {stem}  ({human(size)})")
                print(f"          {why}")
                log.append(f"[{kind}] {stem} - {why}")

                if kind == "manual":
                    names = [n for n in zf.namelist() if not n.endswith("/")]
                    print(f"          内容示例：{', '.join(names[:4])}")
                    total_manual += 1
                    continue

                for inner, dest in entries:
                    if os.path.exists(dest) and not args.force:
                        continue          # 已存在就跳过，避免覆盖手动改过的状态
                    mark = "覆盖" if os.path.exists(dest) else "新增"
                    print(f"          - {mark} {os.path.relpath(dest, args.game)}")
                    log.append(f"    {mark} {os.path.relpath(dest, args.game)}")
                    if args.apply:
                        os.makedirs(os.path.dirname(dest), exist_ok=True)
                        if os.path.exists(dest) and not os.path.exists(
                                os.path.join(backup, os.path.relpath(dest, args.game))):
                            b = os.path.join(backup, os.path.relpath(dest, args.game))
                            os.makedirs(os.path.dirname(b), exist_ok=True)
                            shutil.copy2(dest, b)
                        with zf.open(inner) as src, open(dest, "wb") as out:
                            shutil.copyfileobj(src, out)
                total_ok += 1
        except zipfile.BadZipFile:
            print(f"[坏包   ] {stem} —— 下载不完整，重新下一次")
            log.append(f"[坏包] {stem}")
        print()

    log.append("")
    log.append(f"自动处理 {total_ok} 个，需手动 {total_manual} 个")
    if args.apply:
        log_path = os.path.join(args.game, "install_log.txt")
        with open(log_path, "w", encoding="utf-8") as f:
            f.write("\n".join(log))
        print(f"完成。记录：{log_path}")
        if os.path.isdir(backup):
            print(f"被覆盖的旧文件已备份到：{backup}")
        print("\n下一步：启动游戏，看 BepInEx\\LogOutput.log 有没有红色报错。")
    else:
        print("这是预演。确认无误后加 --apply 重新运行。")


if __name__ == "__main__":
    main()
