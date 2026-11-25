using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using CUCoreLib.Registries;
using UnityEngine;
using HarmonyLib;

namespace AmmoPress
{
    /// <summary>
    /// 散装子弹装填 / 压子弹
    ///  1) 弹壳收集器：工业警戒色的 12 号霰弹盒，120 发，塑料块 ×1 合成
    ///  2) F 键标记的弹匣，附近有散装弹时自动压弹
    /// 不自动收集弹壳——弹壳要手动取出来做子弹。
    /// </summary>
    [BepInPlugin("com.local.ammopress", "散装子弹装填", "2.3.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal const string ShellCollector = "shellcollector";
        internal const int ShellCapacity = 120;

        /// <summary>
        /// 专属弹种：AmmoType 只有 Pistol/Rifle/Shotgun，枚举允许未定义值，
        /// 用 99 代表"空弹壳"，避免被霰弹枪（Shotgun=2）当成实弹吃进去。
        /// </summary>
        internal const int CasingAmmoType = 99;

        internal static GunScript.AmmoType Casing => (GunScript.AmmoType)CasingAmmoType;

        internal static Plugin Instance;
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static ConfigEntry<bool> AutoFill;
        internal static ConfigEntry<float> AutoFillRadius;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            AutoFill = Config.Bind("通用", "AutoFillMagazine", true,
                "自动把附近的散装子弹压进未满的弹匣（仅对 F 键标记收藏的弹匣生效）。");
            AutoFillRadius = Config.Bind("通用", "AutoFillRadius", 6f,
                "自动压弹的搜索半径（米）。");

            gameObject.AddComponent<AutoFiller>();
            try
            {
                var h = new Harmony("com.local.ammopress.patch");
                h.PatchAll(typeof(CollectorSetupPatch));
                h.PatchAll(typeof(CasingPatch));
                h.PatchAll(typeof(UnloadCasingPatch));
                Logger.LogInfo("补丁已应用: 收集器装配 / 弹壳弹药化 / 取出弹壳");
            }
            catch (Exception e) { Logger.LogWarning("补丁失败: " + e.Message); }
            StartCoroutine(Init());
        }

        private IEnumerator Init()
        {
            float waited = 0f;
            while (!HasItems())
            {
                waited += 1f;
                if (waited > 30f) { Logger.LogWarning("等待物品表超时，放弃注册"); yield break; }
                yield return new WaitForSecondsRealtime(1f);
            }
            Logger.LogInfo("物品表就绪（等待 " + Mathf.RoundToInt(waited) + " 秒），开始注册");

            try { RegisterCollector(); }
            catch (Exception e) { Logger.LogError("注册弹壳收集器失败: " + e); }
        }

        private static bool HasItems()
        {
            try { return Item.GlobalItems != null && Item.GlobalItems.Count > 0; }
            catch { return false; }
        }

        private void RegisterCollector()
        {
            if (Item.GlobalItems.ContainsKey(ShellCollector))
            {
                Logger.LogInfo("弹壳收集器已存在，跳过");
                return;
            }
            if (!Item.GlobalItems.ContainsKey("boxof12gauge"))
            {
                Logger.LogWarning("找不到 boxof12gauge 模板");
                return;
            }

            var src = Item.GlobalItems["boxof12gauge"];
            var info = new ItemInfo();
            foreach (var f in typeof(ItemInfo).GetFields(BindingFlags.Public |
                         BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try { f.SetValue(info, f.GetValue(src)); } catch { }
            }

            info.fullName = "弹壳收集器";
            info.description =
                "工业警戒色涂装的弹壳收纳盒，用来集中存放打空的弹壳。\n" +
                "<color=#ffd54f>┃容量 120 枚空弹壳（2.4u）</color>\n" +
                "<color=#ffcc80>┃弹壳可取出，用于复装子弹</color>";
            info.category = "tool";
            info.weight = 0.4f;
            info.value = 22;

            ItemRegistry.Register(ShellCollector, info, HazardSprite());
            Logger.LogInfo("已注册物品: " + ShellCollector + "（弹壳收集器）");

            RecipeRegistry.Register(new Recipe
            {
                INT = 3,
                category = Recipes.RecipeCategory.Tools,
                items = new List<RecipeItem> { Solid("plasticchunk") },
                result = new RecipeResult { id = ShellCollector, amount = 1, isLiquid = false, resultCondition = 1f }
            });
            Logger.LogInfo("配方：塑料块×1 → 弹壳收集器");
        }

        private static RecipeItem Solid(string id) =>
            new RecipeItem(0f) { specific = true, specificId = id };

        /// <summary>把霰弹盒贴图染成工业警戒色（黄黑斜条纹）</summary>
        internal Sprite HazardSpritePublic() => HazardSprite();

        private static Sprite HazardSprite()
        {
            try
            {
                var go = Utils.Create("boxof12gauge", Vector3.zero, 0f);
                if (go == null) return null;
                var sr = go.GetComponentInChildren<SpriteRenderer>();
                var baseSprite = sr != null ? sr.sprite : null;
                UnityEngine.Object.Destroy(go);
                if (baseSprite == null)
                {
                    Plugin.Log?.LogWarning("取不到 boxof12gauge 的贴图，图标会是空白");
                    return null;
                }

                var tex = baseSprite.texture;
                var r = baseSprite.textureRect;
                int w = Mathf.Max(1, (int)r.width), h = Mathf.Max(1, (int)r.height);

                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var px = tex.GetPixels((int)r.x, (int)r.y, w, h);
                for (int i = 0; i < px.Length; i++)
                {
                    int x = i % w;
                    int y = i / w;
                    float lum = px[i].r * 0.3f + px[i].g * 0.59f + px[i].b * 0.11f;

                    // 上半部分是盒盖 → 纯黑；下半部分是盒身 → 黄黑斜条纹
                    bool lid = y > h * 0.62f;
                    if (lid)
                    {
                        px[i] = new Color(lum * 0.22f, lum * 0.22f, lum * 0.22f, px[i].a);
                    }
                    else
                    {
                        px[i] = new Color(Mathf.Clamp01(lum * 1.55f),
                                          Mathf.Clamp01(lum * 1.15f),
                                          Mathf.Clamp01(lum * 0.12f), px[i].a);
                    }
                }
                copy.SetPixels(px);
                copy.Apply(false, false);
                copy.filterMode = tex.filterMode;
                copy.wrapMode = tex.wrapMode;

                return Sprite.Create(copy, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f),
                                     baseSprite.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("染色失败: " + e.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// 弹壳收集器只有 ItemInfo，没有实体组件。物品实例化后补上 AmmoScript，
    /// 否则它只是个"能拿在手上的图标"，既装不了弹壳也取不出来。
    /// </summary>
    [HarmonyPatch]
    internal static class CollectorSetupPatch
    {
        private static Sprite _hazard;

        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Item");
            return t?.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? t?.GetMethod("Start", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            try
            {
                var item = __instance as Component;
                if (item == null) return;

                var idf = item.GetType().GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if ((idf?.GetValue(item) as string) != Plugin.ShellCollector) return;

                // 收集器是「弹匣型」，装的是被我们改成 Round 的弹壳
                var ammo = item.GetComponent<AmmoScript>();
                if (ammo == null)
                {
                    ammo = item.gameObject.AddComponent<AmmoScript>();
                    ammo.itemType = AmmoScript.AmmoItemType.Magazine;
                    ammo.ammoType = Plugin.Casing;      // 专属弹种，不与霰弹枪互通
                    ammo.maxRounds = Plugin.ShellCapacity;
                    ammo.rounds = 0;
                    Plugin.Log?.LogInfo("已为弹壳收集器装配 AmmoScript（120 枚弹壳容量）");
                }

                // 重量强制 0.2u（Info 与实际可能不同步）
                try
                {
                    var statsF = item.GetType().GetField("Stats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var stats = statsF?.GetValue(item);
                    var wF = stats?.GetType().GetField("weight");
                    if (wF != null) wF.SetValue(stats, 0.2f);
                }
                catch { }

                // 贴图：首次取到后缓存，避免每次都算
                if (_hazard == null) _hazard = Plugin.Instance.HazardSpritePublic();
                var sr = item.GetComponent<SpriteRenderer>();
                if (sr != null && _hazard != null && sr.sprite != _hazard) sr.sprite = _hazard;
            }
            catch (Exception e) { Plugin.Log?.LogWarning("收集器装配失败: " + e.Message); }
        }
    }

    /// <summary>
    /// 让空弹壳具备弹药性质（Round），这样弹匣型的收集器才能把它 LoadRound 进去；
    /// 同时把收集器的「取出」改成生成空弹壳，而不是原版的实弹。
    /// </summary>
    [HarmonyPatch]
    internal static class CasingPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Item");
            return t?.GetMethod("Awake", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            try
            {
                var item = __instance as Component;
                if (item == null) return;
                var idf = item.GetType().GetField("id", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if ((idf?.GetValue(item) as string) != "casing") return;

                if (item.GetComponent<AmmoScript>() == null)
                {
                    var ammo = item.gameObject.AddComponent<AmmoScript>();
                    ammo.itemType = AmmoScript.AmmoItemType.Round;
                    ammo.ammoType = Plugin.Casing;       // 专属弹种
                    ammo.maxRounds = 1;
                    ammo.rounds = 1;
                }
            }
            catch { }
        }
    }

    [HarmonyPatch]
    internal static class UnloadCasingPatch
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("AmmoScript");
            return t?.GetMethod("UnloadRound", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            try
            {
                var ammo = __instance as Component;
                if (ammo == null) return true;
                var item = ammo.GetComponent<Item>();
                if (item == null || item.id != Plugin.ShellCollector) return true;   // 只管我们的盒子

                var roundsF = ammo.GetType().GetField("rounds");
                int rounds = roundsF != null ? Convert.ToInt32(roundsF.GetValue(ammo)) : 0;
                if (rounds <= 0) return false;   // 空盒，什么都不做

                var go = Utils.Create("casing", ammo.transform.position, 0f);
                if (go != null) PlayerCamera.main?.body?.AutoPickUpItem(go.GetComponent<Item>());
                roundsF.SetValue(ammo, rounds - 1);
                return false;                    // 拦掉原版（会生成实弹）
            }
            catch { return true; }
        }
    }

    /// <summary>
    /// 自动压弹：把附近散装子弹填进「F 键标记」且未装满的弹匣。
    /// 走原版 AmmoScript.LoadRound；弹壳收集器容量也在这里维持 120。
    /// </summary>
    internal class AutoFiller : MonoBehaviour
    {
        private float _next;

        private void Update()
        {
            if (!Plugin.AutoFill.Value) return;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1.5f;

            try
            {
                var body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                if (body == null) return;

                var all = UnityEngine.Object.FindObjectsOfType<AmmoScript>();
                if (all == null || all.Length == 0) return;

                float r = Plugin.AutoFillRadius.Value;
                var mags = new List<AmmoScript>();
                var rounds = new List<AmmoScript>();

                foreach (var a in all)
                {
                    if (a == null) continue;
                    var item = a.GetComponent<Item>();

                    // 弹壳收集器：容量拉到 120
                    if (item != null && item.id == Plugin.ShellCollector && a.maxRounds != Plugin.ShellCapacity)
                    {
                        a.maxRounds = Plugin.ShellCapacity;
                        a.rounds = Mathf.Min(a.rounds, a.maxRounds);
                    }

                    // 收集器重量固定 0.2u（Stats 可能在初始化后被覆盖）
                    if (item != null && item.id == Plugin.ShellCollector)
                    {
                        try
                        {
                            var sf = item.GetType().GetField("Stats", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            var st = sf?.GetValue(item);
                            var wf = st?.GetType().GetField("weight");
                            if (wf != null && Mathf.Abs(Convert.ToSingle(wf.GetValue(st)) - 0.2f) > 0.01f)
                                wf.SetValue(st, 0.2f);
                        }
                        catch { }
                    }

                    if (Vector3.Distance(a.transform.position, body.transform.position) > r) continue;

                    if (a.itemType == AmmoScript.AmmoItemType.Magazine)
                    {
                        if (a.rounds >= a.maxRounds) continue;
                        if (item == null || !item.favourited) continue;   // 只压标记过的弹匣
                        mags.Add(a);
                    }
                    else rounds.Add(a);
                }

                if (mags.Count == 0 || rounds.Count == 0) return;

                int loaded = 0;
                foreach (var mag in mags)
                {
                    foreach (var rd in rounds)
                    {
                        if (rd == null) continue;
                        if (mag == null || mag.rounds >= mag.maxRounds) break;
                        if (rd.ammoType != mag.ammoType) continue;

                        int before = mag.rounds;
                        mag.LoadRound(rd);
                        if (mag.rounds > before) loaded++;
                        if (loaded >= 30) break;
                    }
                    if (loaded >= 30) break;
                }

                if (loaded > 0)
                    Plugin.Log?.LogInfo("[自动压弹] 本次填入 " + loaded + " 发（半径 "
                        + r.ToString("F0") + " 米）");
            }
            catch (Exception e) { Plugin.Log?.LogWarning("自动压弹异常: " + e.Message); }
        }
    }
}
