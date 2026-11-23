#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""按文件夹批量启用/停用 BepInEx 插件（改名 .dll -> .dll.off，不删文件）"""
import os, sys, shutil

PLUG = r"D:/Steam/steamapps/common/Casualties Unknown/BepInEx/plugins"

def list_mods():
    out = []
    for name in sorted(os.listdir(PLUG)):
        p = os.path.join(PLUG, name)
        if os.path.isdir(p):
            dlls = [f for f in os.listdir(p) if f.lower().endswith((".dll", ".dll.off"))]
            if dlls:
                off = all(f.endswith(".off") for f in dlls)
                out.append((name, "停用" if off else "启用", len(dlls)))
        elif name.lower().endswith((".dll", ".dll.off")):
            out.append((name[:-4] if name.endswith(".off") else name, "停用" if name.endswith(".off") else "启用", 1))
    return out

def set_state(keyword, enable):
    hit = 0
    for name in os.listdir(PLUG):
        if keyword.lower() not in name.lower():
            continue
        p = os.path.join(PLUG, name)
        targets = []
        if os.path.isdir(p):
            targets = [os.path.join(p, f) for f in os.listdir(p) if f.lower().endswith((".dll", ".dll.off"))]
        elif name.lower().endswith((".dll", ".dll.off")):
            targets = [p]
        for t in targets:
            if enable and t.endswith(".off"):
                shutil.move(t, t[:-4]); hit += 1
            elif not enable and t.endswith(".dll"):
                shutil.move(t, t + ".off"); hit += 1
    return hit

if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] == "list":
        print(f"{'插件':42} 状态")
        for n, s, c in list_mods():
            print(f"{n:42} {s}")
        sys.exit()
    cmd = sys.argv[1]
    if cmd in ("offall", "onall"):
        keys = [n for n, _, _ in list_mods()]
        n = 0
        for k in keys:
            n += set_state(k, cmd == "onall")
        print(f"{'全部启用' if cmd=='onall' else '全部停用'}: {n} 个文件")
        sys.exit()
    if cmd in ("off", "on") and len(sys.argv) > 2:
        n = set_state(sys.argv[2], cmd == "on")
        print(f"{'启用' if cmd=='on' else '停用'} {n} 个文件（匹配 {sys.argv[2]}）")
    else:
        print("用法: python mods_toggle.py list | off <关键字> | on <关键字>")
