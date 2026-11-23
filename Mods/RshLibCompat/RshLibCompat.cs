// RshLib 兼容层（替换原 RshLib.dll）
//
// 原版问题：它的 Utils.Create 前缀补丁**无条件 return false**，把游戏里「按名字创建对象」
// 这条通用路径整个顶替掉，只保留了最朴素的 Resources.Load + Instantiate。
// 结果是所有依赖 Utils.Create 建 UI 的 mod（CuSaveManager、CuHotbar…）全变成黑块。
//
// 本兼容层只解决一件事：**注册表里有这个 id 才接管，没有就 return true 交还原版**。
// API 与原 RshLib 保持一致，GUID 也相同，因此依赖它的枪械加载器可以正常识别。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace RshLib
{
    /// <summary>与原作者 API 完全一致的物品描述</summary>
    public class RshItem
    {
        public Sprite sprite;
        public ItemInfo info;
        public string baseItem;
        public Action<GameObject, string> onSpawn;
    }

    [BepInPlugin("com.rushellxyz.rshlib", "RshLib (compat)", "3.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>保持原名，供其它 mod 直接读取</summary>
        public static readonly Dictionary<string, RshItem> itemRegistry =
            new Dictionary<string, RshItem>(StringComparer.Ordinal);

        /// <summary>联机（KrokoshaMP）标记，原库由它自己探测；这里默认单人</summary>
        public static bool krokMpEnabled;

        internal static BepInEx.Logging.ManualLogSource Log;

        /// <summary>自定义物品在掉落池里的权重倍数（1 = 与其他物品相同）</summary>
        internal static int PoolWeight = 4;

        public static void RegisterItem(string itemId, RshItem rshItem)
        {
            if (string.IsNullOrEmpty(itemId) || rshItem == null) return;
            itemRegistry[itemId] = rshItem;

            // 直接写进游戏的物品表，供 UI/合成表使用
            try
            {
                var dict = GlobalItems();
                if (dict != null && rshItem.info != null) dict[itemId] = rshItem.info;
            }
            catch (Exception e) { Log?.LogWarning("注入物品失败 " + itemId + ": " + e.Message); }

            SchedulePoolRebuild();
            Log?.LogInfo("[RshLib] 注册物品: " + itemId);
        }

        private void Awake()
        {
            Log = Logger;
            try
            {
                var h = new Harmony("com.local.rshlib.compat");
                h.PatchAll(typeof(UtilsCreatePatch));
                h.PatchAll(typeof(ItemSetupPatch));
                h.PatchAll(typeof(SpawnAutofillPatch));
                Logger.LogInfo("RshLib 兼容版已加载（仅接管自定义物品，其余交还原版）");
            }
            catch (Exception e)
            {
                Logger.LogError("安装补丁失败: " + e.Message);
            }
        }

        private static bool _poolDirty;

        /// <summary>
        /// ItemLootPool 只在游戏初始化时扫一次 Item.GlobalItems，
        /// 之后注册的物品（枪械、粉末等）永远进不了掉落/商人货池。
        /// 这里在注册后重建一次，让自定义物品能被商人和箱子刷出来。
        /// </summary>
        private static void SchedulePoolRebuild() => _poolDirty = true;

        private void LateUpdate()
        {
            if (!_poolDirty) return;
            _poolDirty = false;
            try
            {
                var t = AccessTools.TypeByName("ItemLootPool");
                var m = t?.GetMethod("InitializePool",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) return;
                m.Invoke(null, null);

                // 让自定义物品在池子里多出现几次：池子按权重随机，
                // 重复加入 id 就等于提高权重，否则枪在 utility 池里几乎抽不到
                int weight = Math.Max(1, PoolWeight);
                if (weight > 1)
                {
                    var fp = t.GetField("pool", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    var pool = fp?.GetValue(null) as System.Collections.IDictionary;
                    if (pool != null)
                    {
                        foreach (var kv in itemRegistry)
                        {
                            var cat = kv.Value?.info?.category;
                            if (string.IsNullOrEmpty(cat)) continue;
                            if (!pool.Contains(cat)) continue;
                            var list = pool[cat] as System.Collections.IList;
                            if (list == null) continue;
                            for (int i = 1; i < weight; i++) list.Add(kv.Key);
                        }
                        Log?.LogInfo("[RshLib] 自定义物品掉落权重 ×" + weight);
                    }
                }

                Log?.LogInfo("[RshLib] 已重建物品掉落池（自定义物品现在可被商人/箱子刷出）");
            }
            catch (Exception e) { Log?.LogWarning("重建掉落池失败: " + e.Message); }
        }

        /// <summary>反射拿游戏的 Item.GlobalItems</summary>
        internal static IDictionary GlobalItems()
        {
            var f = typeof(Item).GetField("GlobalItems",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return f?.GetValue(null) as IDictionary;
        }
    }

    /// <summary>只拦截「注册表里有的 id」，其余一律放行</summary>
    [HarmonyPatch(typeof(Utils), "Create", new[] { typeof(string), typeof(Vector2), typeof(float) })]
    internal static class UtilsCreatePatch
    {
        private static bool Prefix(string id, Vector2 pos, float rot, ref GameObject __result)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return true;

                var parts = id.Split('$');
                var key = parts[0];

                // 不是我们的物品 → 交还原版，避免破坏 UI/其它 mod
                if (!Plugin.itemRegistry.TryGetValue(key, out var item) || item == null) return true;

                var prefab = Resources.Load(string.IsNullOrEmpty(item.baseItem) ? "geofruit" : item.baseItem);
                if (prefab == null) return true;

                var go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.Euler(0f, 0f, rot)) as GameObject;
                if (go == null) return true;

                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null && item.sprite != null) sr.sprite = item.sprite;

                var it = go.GetComponent<Item>();
                if (it != null) it.id = key;

                go.name = key;

                if (item.onSpawn != null)
                    item.onSpawn(go, parts.Length > 1 ? parts[1] : "");

                __result = go;
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("创建自定义物品失败 " + id + ": " + e.Message);
                return true;   // 出了任何问题都退回复原版行为
            }
        }
    }

    /// <summary>把自定义物品 id 加进控制台 spawn 命令的自动补全列表</summary>
    [HarmonyPatch("ConsoleScript", "RegisterSpawnEntities")]
    internal static class SpawnAutofillPatch
    {
        private static void Postfix()
        {
            try
            {
                var t = AccessTools.TypeByName("ConsoleScript");
                if (t == null) return;

                var search = t.GetMethod("SearchExact",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(string) }, null);
                if (search == null) return;

                var cmd = search.Invoke(null, new object[] { "spawn" });
                if (cmd == null) return;

                var f = cmd.GetType().GetField("argAutofill",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var autofill = f?.GetValue(cmd) as IList;
                if (autofill == null || autofill.Count == 0) return;

                var first = autofill[0] as IList;
                if (first == null) return;

                foreach (var id in Plugin.itemRegistry.Keys)
                {
                    bool has = false;
                    foreach (var x in first) if (x as string == id) { has = true; break; }
                    if (!has) first.Add(id);
                }
                Plugin.Log?.LogInfo("[RshLib] 已把 " + Plugin.itemRegistry.Count + " 个物品 id 加入 spawn 自动补全");
            }
            catch (Exception e) { Plugin.Log?.LogWarning("注册 spawn 补全失败: " + e.Message); }
        }
    }

    /// <summary>游戏重建物品表后，把自定义物品补回去</summary>
    [HarmonyPatch(typeof(Item), "SetupItems")]
    internal static class ItemSetupPatch
    {
        private static void Postfix()
        {
            try
            {
                var dict = Plugin.GlobalItems();
                if (dict == null) return;
                foreach (var kv in Plugin.itemRegistry)
                    if (kv.Value?.info != null) dict[kv.Key] = kv.Value.info;
            }
            catch { }
        }
    }
}
