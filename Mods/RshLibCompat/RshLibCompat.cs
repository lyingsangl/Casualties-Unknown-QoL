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

    [BepInPlugin("com.rushellxyz.rshlib", "RshLib (compat)", "6.2.0")]
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
                if (dict != null && rshItem.info != null)
                {
                    dict[itemId] = rshItem.info;
                    EnsureGunTag(rshItem.info);      // 补上原版枪械的 "gun" 标签
                    RegisterViaCucore(itemId, rshItem);   // 同时向 CUCoreLib 登记，获得掉落池注入
                }
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
                GunStockChance = Config.Bind("商人", "GunStockChance", 0.3f,
                    "每个商人上架 mod 枪的概率（0~1）").Value;
                GunStockMax = Config.Bind("商人", "GunStockMax", 1,
                    "单个商人最多上架几把 mod 枪").Value;
            }
            catch { }
            // MuzzleSync 已停用：barrel 字段直接指向火光节点后不再需要每帧遍历（性能）
            try
            {
                var h = new Harmony("com.local.rshlib.compat");
                // 逐个应用，某个失败不影响其他
                var patches = new[]
                {
                    typeof(UtilsCreatePatch), typeof(ItemSetupPatch), typeof(SpawnAutofillPatch),
                    typeof(LootPoolRebuildPatch), typeof(TraderStockPatch), typeof(GunSetupPatch), typeof(GunFirePatch)
                };
                foreach (var pt in patches)
                {
                    try { h.PatchAll(pt); Log?.LogInfo("[RshLib] 补丁应用成功: " + pt.Name); }
                    catch (Exception pe) { Log?.LogWarning("[RshLib] 补丁应用失败 " + pt.Name + ": " + pe.Message); }
                }
                Logger.LogInfo("RshLib 兼容版已加载（仅接管自定义物品，其余交还原版）");
            }
            catch (Exception e)
            {
                Logger.LogError("安装补丁失败: " + e.Message);
            }
        }

        private static bool _poolDirty;
        private static float _retryAt;
        private static bool _warnedEmpty;

        /// <summary>
        /// ItemLootPool 只在游戏初始化时扫一次 Item.GlobalItems，
        /// 之后注册的物品（枪械、粉末等）永远进不了掉落/商人货池。
        /// 这里在注册后重建一次，让自定义物品能被商人和箱子刷出来。
        /// </summary>
        private static void SchedulePoolRebuild() { _poolDirty = true; Log?.LogInfo("[RshLib] 掉落池重建已排队"); }

        private void LateUpdate()
        {
            if (!_poolDirty) return;
            if (Time.unscaledTime < _retryAt) return;   // 失败后隔 1 秒再试

            Log?.LogInfo("[RshLib] 开始重建掉落池…");

            try
            {
                var t = AccessTools.TypeByName("ItemLootPool");
                var fp = t?.GetField("pool", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var fg = typeof(Item).GetField("GlobalItems",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (fp == null || fg == null)
                {
                    _retryAt = Time.unscaledTime + 1f;
                    Log?.LogWarning("[RshLib] 找不到 pool 或 GlobalItems 字段，1 秒后重试");
                    return;
                }

                var globals = fg.GetValue(null) as IDictionary;
                if (globals == null || globals.Count == 0)
                {
                    // 物品表还没建好：保持 dirty，稍后重试（之前在这里直接 return，导致永远不重建）
                    _retryAt = Time.unscaledTime + 1f;
                    if (!_warnedEmpty) { _warnedEmpty = true; Log?.LogWarning("[RshLib] 物品表尚未就绪，等待后重试"); }
                    return;
                }
                _poolDirty = false;
                _warnedEmpty = false;

                // 不复用 InitializePool：它内部任何一个物品取 category 抛异常，
                // 整个重建都会失败（实测被自定义枪械打断）。这里逐项 try，坏一个跳过。
                var pool = Activator.CreateInstance(fp.FieldType) as IDictionary;
                if (pool == null) return;

                int skipped = 0;
                var skippedIds = new List<string>();
                foreach (DictionaryEntry e in globals)
                {
                    var id = e.Key as string;
                    if (string.IsNullOrEmpty(id)) { skipped++; continue; }
                    string cat = null;
                    try { cat = ReadCategory(e.Value); }
                    catch (Exception ex)
                    {
                        skipped++; if (skippedIds.Count < 5) skippedIds.Add(id + "(异常:" + ex.GetType().Name + ")");
                        continue;
                    }
                    if (string.IsNullOrEmpty(cat))
                    {
                        skipped++; if (skippedIds.Count < 5) skippedIds.Add(id + "(category 为空)");
                        continue;
                    }

                    if (!pool.Contains(cat)) pool[cat] = NewStringList(fp.FieldType);
                    var list = pool[cat] as IList;
                    if (list == null) { skipped++; continue; }
                    list.Add(id);
                }

                // 自定义物品加权：重复加入 id 等于提高抽取概率
                int weight = Math.Max(1, PoolWeight);
                foreach (var kv in itemRegistry)
                {
                    string cat = null;
                    try { cat = ReadCategory(kv.Value?.info); } catch { continue; }
                    if (string.IsNullOrEmpty(cat) || !pool.Contains(cat)) continue;
                    var list = pool[cat] as IList;
                    if (list == null) continue;
                    for (int i = 1; i < weight; i++) list.Add(kv.Key);
                }

                fp.SetValue(null, pool);
                Log?.LogInfo("[RshLib] 掉落池已重建：" + pool.Count + " 个分类"
                    + (skipped > 0 ? "（跳过 " + skipped + " 个：" + string.Join("、", skippedIds.ToArray()) + "）" : "")
                    + (weight > 1 ? "，自定义物品权重 ×" + weight : ""));
            }
            catch (Exception e)
            {
                _poolDirty = true;                        // 保留标记，下一轮再试
                _retryAt = Time.unscaledTime + 1f;
                Log?.LogWarning("重建掉落池失败，稍后重试: " + (e.InnerException?.Message ?? e.Message));
            }
        }

        /// <summary>
        /// 绕过掉落池，直接把自定义枪追加进商人货架。
        /// 池子那条路一直没有证据表明枪真的进去了（pool 读出来是 0 项），
        /// 这里直接改商人自己的 items 列表，最可靠。
        /// </summary>
        internal static void InjectGunsIntoTrader(object trader)
        {
            if (trader == null || itemRegistry.Count == 0) return;
            if (UnityEngine.Random.value > GunStockChance) return;

            var t = trader.GetType();
            var fi = t.GetField("items", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var list = fi?.GetValue(trader) as IList;
            if (list == null) return;

            // 收集枪械 id
            var guns = new List<KeyValuePair<string, ItemInfo>>();
            foreach (var kv in itemRegistry)
            {
                string cat = null;
                try { cat = ReadCategory(kv.Value?.info); } catch { }
                if (!string.IsNullOrEmpty(cat) && cat == "utility")
                    guns.Add(new KeyValuePair<string, ItemInfo>(kv.Key, kv.Value?.info));
            }
            if (guns.Count == 0) return;

            // 找一个模板 TraderItem 实例的类型
            if (list.Count == 0) return;
            var sample = list[0];
            if (sample == null) return;
            var st = sample.GetType();

            // 用「替换」而不是「追加」：交易面板行数有限，追加到末尾的条目看不到
            int add = Mathf.Clamp(GunStockMax, 1, guns.Count);
            var used = new List<int>();
            int replaced = 0;
            for (int i = 0; i < add && i < guns.Count; i++)
            {
                int idx;
                int guard = 0;
                do { idx = UnityEngine.Random.Range(0, list.Count); guard++; } while (used.Contains(idx) && guard < 20);
                if (used.Contains(idx)) continue;
                used.Add(idx);

                var pick = guns[UnityEngine.Random.Range(0, guns.Count)];
                var entry = list[idx];
                if (entry == null) continue;

                var et = entry.GetType();
                var idf = et.GetField("id");
                var vf = et.GetField("value");
                if (idf == null) continue;

                string old = idf.GetValue(entry) as string;
                idf.SetValue(entry, pick.Key);

                int val = 45;
                try
                {
                    if (pick.Value != null)
                    {
                        float raw = pick.Value.value;
                        val = raw > 0f ? Mathf.RoundToInt(raw) : GunFallbackValue;
                    }
                }
                catch { val = GunFallbackValue; }
                if (vf != null)
                {
                    // TraderItem.value 是 int，塞 float 会抛异常被外层吞掉
                    if (vf.FieldType == typeof(int)) vf.SetValue(entry, val);
                    else vf.SetValue(entry, (float)val);
                }
                et.GetField("bought")?.SetValue(entry, false);

                // 保留原 preference：分组标题是按相邻 item 的 preference 变化插入的，
                // 乱改会造成界面出现大量「乐于出售/不舍出售」标题。
                replaced++;
                Log?.LogInfo("[RshLib] 货架替换: " + old + " → " + pick.Key + "（价值 " + val + "）");
            }
            Log?.LogInfo("[RshLib] 本次替换 " + replaced + " 件为 Mod 枪");
        }

        private static MethodInfo _cucoreRegister;
        private static bool _cucoreRegisterTried;

        /// <summary>
        /// 药瓶能被商人/容器刷出，是因为它们走 CUCoreLib 的 ItemRegistry.Register——
        /// 那套注册会自动把物品注入掉落池。枪只在 Item.GlobalItems 里，池子里没有。
        /// 这里补一次 CUCoreLib 注册，让枪获得同样的待遇。
        /// </summary>
        private static void RegisterViaCucore(string id, RshItem item)
        {
            try
            {
                if (!_cucoreRegisterTried)
                {
                    _cucoreRegisterTried = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = null;
                        try { t = asm.GetType("CUCoreLib.Registries.ItemRegistry"); } catch { }
                        if (t == null) continue;
                        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                        {
                            if (m.Name != "Register") continue;
                            var ps = m.GetParameters();
                            // 实际的签名：Register(string id, ItemInfo info, Sprite icon, int spawnFrequency = 1)
                            if (ps.Length >= 3 && ps[0].ParameterType == typeof(string)
                                && ps[1].ParameterType.Name == "ItemInfo")
                            { _cucoreRegister = m; break; }
                        }
                        if (_cucoreRegister != null)
                        {
                            Log?.LogInfo("[RshLib] 已接入 CUCoreLib 物品注册: " + t.FullName);
                            break;
                        }
                    }
                    if (_cucoreRegister == null) Log?.LogWarning("[RshLib] 没找到 CUCoreLib ItemRegistry.Register");
                }
                if (_cucoreRegister == null || item?.info == null) return;

                var icon = item.sprite != null ? item.sprite : ResolveSprite(id);
                var ps2 = _cucoreRegister.GetParameters();
                var args = new object[ps2.Length];
                args[0] = id;
                args[1] = item.info;
                for (int i = 2; i < ps2.Length; i++)
                {
                    var pt = ps2[i].ParameterType;
                    if (pt == typeof(Sprite)) args[i] = icon;
                    else if (pt == typeof(int)) args[i] = 1;   // 与原版枪械相同：池内 1 份
                    else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                }

                _cucoreRegister.Invoke(null, args);
                Log?.LogInfo("[RshLib] 已向 CUCoreLib 注册: " + id
                    + " ｜ 图标=" + (icon != null ? icon.name : "无（UI 可能不显示）"));
                Log?.LogInfo("[RshLib] 已向 CUCoreLib 注册: " + id);
            }
            catch (Exception e) { Log?.LogWarning("[RshLib] CUCoreLib 注册失败 " + id + ": " + e.Message); }
        }

        /// <summary>
        /// 枪包只给了 Sprite 文件路径，ItemInfo 上没有图标。
        /// 商人 UI 依赖 CUCoreLib 注册时传入的 Sprite，缺了就整行不渲染。
        /// 这里现造一个物品实例，把它的 SpriteRenderer.sprite 取出来。
        /// </summary>
        private static Sprite ResolveSprite(string id)
        {
            try
            {
                var go = Utils.Create(id, Vector3.zero, 0f);
                if (go == null) return null;
                var sr = go.GetComponentInChildren<SpriteRenderer>();
                var sp = sr != null ? sr.sprite : null;
                UnityEngine.Object.Destroy(go);
                return sp;
            }
            catch { return null; }
        }

        /// <summary>
        /// 原版枪械带 tags "cangetwet,gun"，而枪包只设置了 category/value。
        /// 商人/箱子里那条「随机枪」通道按标签筛选，缺标签就永远进不去。
        /// </summary>
        private static void EnsureGunTag(object info)
        {
            try
            {
                var fi = info.GetType().GetField("tags",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi == null) { Log?.LogWarning("[RshLib] 没找到 tags 字段"); return; }

                string tags = (fi.GetValue(info) as string) ?? "";
                if (tags.IndexOf("gun", StringComparison.OrdinalIgnoreCase) >= 0) return;

                fi.SetValue(info, string.IsNullOrEmpty(tags) ? "gun" : tags + ",gun");

                // 有些实现会缓存 SetTags 的结果，重新算一次
                var m = info.GetType().GetMethod("SetTags",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                try { m?.Invoke(info, null); } catch { }

                Log?.LogInfo("[RshLib] 已补 gun 标签: " + tags + " → " + fi.GetValue(info));
            }
            catch (Exception e) { Log?.LogWarning("[RshLib] 补标签失败: " + e.Message); }
        }

        /// <summary>把 RshLib 注册过的物品补进掉落池（可重复调用，不会重复加）</summary>
        internal static float GunStockChance = 0.3f;   // 每个商人上架 mod 枪的概率
        internal static int GunStockMax = 1;           // 单个商人最多几把
        internal static int GunFallbackValue = 45;      // 枪没有价值数据时用的价格   // 每个商人追加枪械的概率

        private static MethodInfo _cucoreEnsure;
        private static bool _cucoreTried;

        /// <summary>
        /// 优先调用 CUCoreLib 的 ItemLootPoolPatch.EnsureItemInLootPool(id, info)。
        /// 我们的药能正常出现在商人和容器里，靠的就是这条通道；枪走同一条就一致了。
        /// </summary>
        private static bool TryCucoreInject(string id, object info)
        {
            try
            {
                if (!_cucoreTried)
                {
                    _cucoreTried = true;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = null;
                        try { t = asm.GetType("ItemLootPoolPatch"); } catch { }
                        if (t == null) continue;
                        _cucoreEnsure = t.GetMethod("EnsureItemInLootPool",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (_cucoreEnsure != null)
                        {
                            Log?.LogInfo("[RshLib] 已接入 CUCoreLib 补池通道");
                            break;
                        }
                    }
                }
                if (_cucoreEnsure == null || info == null) return false;

                var ps = _cucoreEnsure.GetParameters();
                _cucoreEnsure.Invoke(null, ps.Length == 2 ? new object[] { id, info } : new object[] { id, info });
                return true;
            }
            catch { return false; }
        }

        private static bool _poolSkippedLogged;

        internal static void EnsureCustomInPool()
        {
            // 掉落池由 CUCoreLib 按 spawnFrequency 统一注入，这里不再重复塞，
            // 否则同一把枪会出现多份（曾导致尸体旁满地是枪）。
            if (!_poolSkippedLogged)
            {
                _poolSkippedLogged = true;
                Log?.LogInfo("[RshLib] 掉落权重由 CUCoreLib 接管（每把 1 份，与原版枪械一致）");
            }
            if (true) return;
            try
            {
                // 先走 CUCoreLib 通道（与药瓶完全一致的逻辑）
                int viaCore = 0, viaSelf = 0;
                foreach (var kv in itemRegistry)
                {
                    if (TryCucoreInject(kv.Key, kv.Value?.info)) viaCore++;
                    else viaSelf++;
                }
                if (viaCore > 0)
                    Log?.LogInfo("[RshLib] 补池：CUCoreLib 通道 " + viaCore + " 个，自建通道 " + viaSelf + " 个");

                var t = AccessTools.TypeByName("ItemLootPool");
                var fp = t?.GetField("pool", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var pool = fp?.GetValue(null) as IDictionary;
                if (pool == null) return;

                int weight = Math.Max(1, PoolWeight);
                int added = 0, total = 0;
                foreach (var kv in itemRegistry)
                {
                    total++;
                    string cat = null;
                    try { cat = ReadCategory(kv.Value?.info); } catch { continue; }
                    if (string.IsNullOrEmpty(cat)) continue;

                    if (!pool.Contains(cat)) pool[cat] = NewStringList(fp.FieldType);
                    var list = pool[cat] as IList;
                    if (list == null) continue;

                    // 已经在池子里就不重复加主体，只补权重
                    bool present = false;
                    foreach (var o in list) if (o as string == kv.Key) { present = true; break; }
                    if (!present) { list.Add(kv.Key); added++; }
                    for (int i = 1; i < weight; i++) list.Add(kv.Key);
                }
                Log?.LogInfo("[RshLib] 补池完成：" + total + " 个自定义物品，新加入 " + added
                    + " 个，权重 ×" + weight);
            }
            catch (Exception e)
            {
                Log?.LogWarning("[RshLib] 补池异常: " + e.Message);
            }
        }

        /// <summary>安全读取 ItemInfo.category（自定义物品可能为 null 或抛异常）</summary>
        private static string ReadCategory(object info)
        {
            if (info == null) return null;
            var pi = info.GetType().GetProperty("category",
                BindingFlags.Public | BindingFlags.Instance);
            if (pi != null) return pi.GetValue(info) as string;
            var fi = info.GetType().GetField("category",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return fi?.GetValue(info) as string;
        }

        /// <summary>按 pool 字段的实际类型创建 List&lt;string&gt;</summary>
        private static object NewStringList(Type poolType)
        {
            var valType = poolType.GetGenericArguments()[1];   // List<string>
            return Activator.CreateInstance(valType);
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
    /// <summary>
    /// 游戏在世界加载时会调用 ItemLootPool.InitializePool() 重建整个池子，
    /// 把我们在注册阶段塞进去的自定义物品全部冲掉。挂在它后面补一次。
    /// </summary>
    [HarmonyPatch]
    internal static class LootPoolRebuildPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("ItemLootPool");
            return t?.GetMethod("InitializePool",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            try { Plugin.EnsureCustomInPool(); }
            catch (Exception e) { Plugin.Log?.LogWarning("补池失败: " + e.Message); }
        }
    }

    /// <summary>
    /// 商人库存是在世界生成时抽的，那时池子可能还没补上自定义物品。
    /// 在每次生成库存前再补一次（幂等，开销极小）。
    /// </summary>
    [HarmonyPatch]
    internal static class TraderStockPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("TraderScript");
            return t?.GetMethod("GenerateInventory",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            try { Plugin.EnsureCustomInPool(); }
            catch { }
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            try { Plugin.InjectGunsIntoTrader(__instance); }
            catch (Exception e) { Plugin.Log?.LogWarning("商人塞枪失败: " + e.Message); }
        }
    }

    [HarmonyPatch]
    /// <summary>
    /// 商店购买、容器取出等路径不一定走 Utils.Create，因此拿到的枪可能没有 GunScript 配置
    /// （装不了弹、开不了火）。这里在 Item 启动时兜底补一次 onSpawn。
    /// </summary>
    /// <summary>
    /// 枪口对齐（持续）：barrel 与 muzzleParticle 可能挂在不同的父节点上，
    /// 只设置 localPosition 无法保证世界坐标重合。这里每帧把 barrel 的世界坐标
    /// 同步到火光位置——射线起点与火光中心就必然重合。
    /// </summary>
    internal class MuzzleSync : MonoBehaviour
    {
        private Type _gunType;
        private float _nextLog;

        private void Update()
        {
            try
            {
                if (_gunType == null)
                {
                    _gunType = AccessTools.TypeByName("GunScript");
                    if (_gunType == null) return;
                }

                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;

                // 角色下所有 GunScript 实例（手持的、背包里的都处理，避免拿错对象）
                var guns = body.GetComponentsInChildren(_gunType, true);
                if (guns == null || guns.Length == 0) return;

                foreach (var g in guns)
                {
                    var gun = g as Component;
                    if (gun == null) continue;

                    var barrel = _gunType.GetField("barrel")?.GetValue(gun) as Transform;
                    var mp = _gunType.GetField("muzzleParticle")?.GetValue(gun) as Component;
                    if (barrel == null || mp == null) continue;

                    // 诊断数据表明：mod 枪的 barrel 挂在 InvSlot（背包槽）下，不在枪上，
                    // 所以射线起点跟枪口差一个 muzzleOffset。
                    // 在枪身下建一个基准点，把 barrel 字段指过去。
                    if (!barrel.IsChildOf(gun.transform))
                    {
                        var holder = gun.transform.Find("__muzzlepoint");
                        if (holder == null)
                        {
                            var go2 = new GameObject("__muzzlepoint");
                            holder = go2.transform;
                            holder.SetParent(gun.transform, false);
                            holder.localPosition = mp.transform.localPosition;
                            holder.localRotation = mp.transform.localRotation;
                            Plugin.Log?.LogInfo("[RshLib] 已为 " + gun.name + " 创建枪口基准点，挂载到枪身下");
                        }
                        var bf = _gunType.GetField("barrel");
                        bf?.SetValue(gun, holder);
                        barrel = holder;
                    }

                    Vector3 aim = mp.transform.position;
                    barrel.position = aim;                    // 射线起点对齐火光
                }

                if (Time.unscaledTime > _nextLog)
                {
                    _nextLog = Time.unscaledTime + 10f;
                    Plugin.Log?.LogInfo("[RshLib] 光柱/射线已对齐火光（" + guns.Length + " 把枪）");
                }
            }
            catch { }
        }
    }

    internal class RshInitMark : MonoBehaviour { }

    internal static class BarrelFix
    {
        /// <summary>
        /// New Gun Loader 把 barrel 设成了枪根（val.barrel = go.transform），
        /// 而火光是有 muzzleOffset 的子节点，导致射线起点与火光永远差一个偏移。
        /// 直接把 barrel 指成火光节点，两者成为同一个对象，偏移恒为 0。
        /// </summary>
        internal static void Apply(GameObject go, Type gunType)
        {
            try
            {
                if (go == null || gunType == null) return;
                var gun = go.GetComponent(gunType);
                if (gun == null) return;

                var mp = gunType.GetField("muzzleParticle")?.GetValue(gun) as Component;
                if (mp == null) return;

                var bf = gunType.GetField("barrel");
                if (bf == null) return;

                var cur = bf.GetValue(gun) as Transform;
                if (cur == mp.transform) return;          // 已对齐

                bf.SetValue(gun, mp.transform);
                Plugin.Log?.LogInfo("[RshLib] barrel 已指向火光节点: " + go.name);
            }
            catch { }
        }
    }

    internal static class MuzzleTip
    {
        /// <summary>取枪械 sprite 的最右点作为枪口（世界坐标）</summary>
        internal static Vector3? Tip(object gun, Type gunType)
        {
            try
            {
                var render = gunType.GetField("render",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(gun) as SpriteRenderer;
                if (render == null || render.sprite == null) return null;

                var b = render.bounds;                       // 世界空间的包围盒
                var barrel = gunType.GetField("barrel")?.GetValue(gun) as Transform;
                bool faceRight = barrel == null || barrel.right.x >= 0f;

                Vector3 p = b.center;
                p.x = faceRight ? b.max.x : b.min.x;
                return p;
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// 开火瞬间修正射线起点：直接作用在「正在开火的那把枪」上，
    /// 不依赖遍历/缓存，避免拿到背包里的另一个实例。
    /// </summary>
    [HarmonyPatch]
    internal static class GunFirePatch
    {
        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> Targets()
        {
            var t = AccessTools.TypeByName("GunScript");
            if (t == null) yield break;
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (m.Name == "Fire") yield return m;
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                var t = __instance.GetType();

                var barrel = t.GetField("barrel")?.GetValue(__instance) as Transform;
                var mp = t.GetField("muzzleParticle")?.GetValue(__instance) as Component;
                if (barrel == null || mp == null) return;

                Vector3 aim = mp.transform.position;
                barrel.position = aim;
                barrel.rotation = mp.transform.rotation;

                var laser = t.GetField("laserLine")?.GetValue(__instance) as Component;
                if (laser != null) laser.transform.position = aim;
            }
            catch { }
        }
    }

    [HarmonyPatch]
    internal static class GunSetupPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Item");
            return t?.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            try
            {
                var item = __instance as Component;
                if (item == null) return;

                var gunType = AccessTools.TypeByName("GunScript");
                if (gunType == null) return;

                // —— 对所有枪械统一修基准点（含原版枪）——
                // 只要 barrel 与火光不是同一个对象就纠正；原版枪两者本来就一致，不受影响。
                if (item.GetComponent(gunType) != null && item.gameObject.GetComponent<RshInitMark>() == null)
                {
                    item.gameObject.AddComponent<RshInitMark>();
                    BarrelFix.Apply(item.gameObject, gunType);
                }

                var idf = item.GetType().GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var id = idf?.GetValue(item) as string;
                if (string.IsNullOrEmpty(id)) return;
                if (!Plugin.itemRegistry.TryGetValue(id, out var rsh) || rsh?.onSpawn == null) return;

                var gun = item.GetComponent(gunType);

                if (gun == null)
                {
                    // 还没有 GunScript：说明是自定义枪，先补初始化（只做一次）
                    if (item.gameObject.GetComponent<RshInitMark>() == null)
                        item.gameObject.AddComponent<RshInitMark>();

                    rsh.onSpawn(item.gameObject, "");       // 商店/容器路径：补一次初始化
                    gun = gunType != null ? item.GetComponent(gunType) : null;
                    if (gunType != null) BarrelFix.Apply(item.gameObject, gunType);
                }

                // 枪口对齐：子弹用的是 GunScript.barrel.position，火光用的是 muzzleParticle。
                // 枪包只设置了火光偏移，barrel 仍沿用模板预制体的位置，所以子弹从错误的点射出。
                try
                {
                    // 无论走哪条路径都要对齐：Utils.Create 建出来的枪同样有这个问题
                    if (gun != null)
                    {
                        var barrel = gunType.GetField("barrel")?.GetValue(gun) as Transform;
                        var muzzleObj = gunType.GetField("muzzleParticle")?.GetValue(gun) as Component;
                        if (barrel != null && muzzleObj != null)
                        {
                            // 关键：barrel 与火光原本挂在不同的父节点下，
                            // 只改 localPosition 世界坐标仍然不重合。
                            // 把 barrel 挪到火光的父节点下、用同一个局部坐标，
                            // 之后就天然同步，不需要每帧对齐。
                            barrel.SetParent(muzzleObj.transform.parent, true);
                            barrel.localPosition = muzzleObj.transform.localPosition;
                            barrel.localRotation = muzzleObj.transform.localRotation;

                            Plugin.Log?.LogInfo("[RshLib] 枪口已合并到火光节点: " + id
                                + " → " + muzzleObj.transform.localPosition
                                + "（父节点 " + (muzzleObj.transform.parent != null ? muzzleObj.transform.parent.name : "无") + "）");
                        }
                    }
                }
                catch { }

                Plugin.Log?.LogInfo("[RshLib] 补初始化枪械: " + id);
            }
            catch (Exception e) { Plugin.Log?.LogWarning("[RshLib] 枪械初始化失败: " + e.Message); }
        }
    }

    internal static class UtilsCreatePatch
    {
        /// <summary>
        /// Utils.Create 有两个重载，直接写 [HarmonyPatch(type, "Create")] 会因歧义抛异常，
        /// 必须显式指定参数表。之前这个类连特性都没有，等于从未生效过。
        /// </summary>
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Utils");
            if (t == null) { Plugin.Log?.LogWarning("[RshLib] 找不到 Utils 类型"); return null; }
            var m = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(Vector2), typeof(float) }, null);
            if (m == null) Plugin.Log?.LogWarning("[RshLib] 找不到 Utils.Create(string, Vector2, float)");
            return m;
        }

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

                var gt = AccessTools.TypeByName("GunScript");
                if (gt != null) BarrelFix.Apply(go, gt);

                // 克隆出来的物品耐久默认 0，而枪等物品 destroyAtZeroCondition=true → 立刻自毁。
                // 原版预制体自带耐久，CUCoreLib 实例化也会显式设置，只有我们这条路径没有。
                var it2 = go.GetComponent<Item>();
                if (it2 != null && it2.condition <= 0.001f)
                {
                    it2.SetCondition(1f);
                    Plugin.Log?.LogInfo("[RshLib] 物品耐久为 0，已置 100%: " + key);
                }

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
